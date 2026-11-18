import Foundation

/// What the worker tells the window (or the CLI).
public enum WorkerEvent: Sendable {
    /// connecting, idle, building, paused, stopped or the last error.
    case state(String)
    /// The job being built (nil when none) and when it started.
    case job(String?, started: Date?)
    /// Build output or client messages; `clear` starts a new job.
    case log(String, clear: Bool)
    /// One line for the recent results list.
    case result(String)
    /// The queue line from `api/client/me`.
    case queue(String)
    case exited
}

/// The poll, build and report loop: claim a job, run the builder container (or the test build command), upload
/// the packages and complete the job, or fail or release it with the log. Start, Pause and Stop behave as in the
/// Haiku client.
public actor Worker {
    /// Receives the worker's events.
    public typealias Sink = @Sendable (WorkerEvent) -> Void

    /// How the running child is stopped.
    private enum StopMode { case none, interrupt, container(String) }

    private let sink: Sink
    private var settings = Settings()
    private var api = ApiClient(server: "", clientId: "", secret: "")
    private var loop: Task<Void, Never>?
    private var stopping = false, pausing = false
    private var pauseReason: String?
    private var info: Container.Info?
    private var logHandle: FileHandle?
    private var pending = Data()
    private var flushedAt = Date()
    private let stampFormat = {
        let format = DateFormatter()
        format.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return format
    }()

    private let session: URLSession?
    /// How long to wait before running a container again after exit 4 (cache volume busy).
    private let busyRetryDelay: Duration

    /// - Parameter sink: receives the events, on the worker's executor; it must not block.
    public init(sink: @escaping Sink) {
        self.init(sink: sink, session: nil, busyRetryDelay: .seconds(30))
    }

    /// For tests: a `URLProtocol` stub session and a short exit-4 wait.
    init(sink: @escaping Sink, session: URLSession?, busyRetryDelay: Duration) {
        self.sink = sink
        self.session = session
        self.busyRetryDelay = busyRetryDelay
    }

    /// Whether the loop runs (also while paused, until the current job ends).
    public var isRunning: Bool { loop != nil }

    /// Starts the loop, or un-pauses it.
    public func start(_ settings: Settings) {
        pausing = false
        guard loop == nil else { return }
        self.settings = settings
        stopping = false
        pauseReason = nil
        info = nil
        api = ApiClient(
            server: settings.server,
            clientId: settings.clientId,
            secret: settings.secret,
            session: session
        )
        loop = Task { await self.run() }
    }

    /// Finish the current job, then stop claiming.
    public func pause() { pausing = true }

    /// Stop the running container (`docker stop -t 90`), abort the HTTP request in flight (an upload, say), give
    /// the job back (release) and stop. The claim and the job reports are not aborted, so no claim goes unreported.
    public func stop() async {
        guard !stopping else { return }
        stopping = true
        await api.cancel()
    }

    /// Returns when the loop has ended.
    public func waitForExit() async { await loop?.value }

    // MARK: - Loop

    private func run() async {
        let fake = !settings.buildCommand.isEmpty
        log("RenkuPorts Builder \(appVersion): polling \(settings.server) every \(settings.pollSeconds) s"
            + (fake ? " (test build command)" : ", image \(settings.image)"))
        while !stopping && !pausing {
            state("connecting")
            if let summary = try? await api.summary() { sink(.queue(summary)) }
            if !fake, info == nil {
                // Pre-flight once per Start; a failure before a claim pauses the client.
                do {
                    let me = try await api.me()
                    state("checking Docker")
                    let report = await Preflight
                        .run(settings: settings, platform: me.platform, fetch: true) { argv, stream in
                            await self.runChild(argv, capture: !stream)
                        }
                    for check in report.checks where check.status != .ok { log("pre-flight: \(check.text)") }
                    if let failure = report.failure {
                        pauseReason = failure
                        break
                    }
                    info = report.info
                } catch {
                    state("error: \(error)")
                    log("error: \(error)")
                    await sleep(settings.pollSeconds)
                    continue
                }
            }
            if stopping { break }
            let data: Data
            do {
                // Not cancellable: a claim the server made but whose reply was dropped would stay unreported.
                data = try await api.call("POST", "api/client/jobs/next", cancellable: false)
            } catch {
                if stopping { break }
                state("error: \(error)")
                log("error: \(error)")
                await sleep(settings.pollSeconds)
                continue
            }
            if data.isEmpty {
                state("connected, idle (no job queued)")
                await sleep(settings.pollSeconds)
            } else if let job = try? JSONDecoder().decode(Job.self, from: data) {
                if stopping {
                    // Stop arrived while the claim was in flight: give the job back without building it.
                    await release(job.jobId, "client stopped", log: Data())
                } else {
                    await process(job)
                }
                sink(.job(nil, started: nil))
            } else {
                state("error: unreadable job")
                log("error: unreadable reply to jobs/next: \(String(decoding: data.prefix(300), as: UTF8.self))")
                await sleep(settings.pollSeconds)
            }
        }
        let final = stopping ? "stopped" : pauseReason.map { "paused: \($0)" } ?? "paused"
        state(final)
        log(final)
        loop = nil
        sink(.exited)
    }

    // MARK: - Job

    private func process(_ job: Job) async {
        let id = job.jobId, name = "\(job.portName)-\(job.version)"
        sink(.job("\(name) (job \(id))", started: Date()))
        sink(.log("", clear: true))
        state("building \(name)")

        let fm = FileManager.default
        let dir = URL(fileURLWithPath: (settings.workDir as NSString).expandingTildeInPath)
            .appendingPathComponent("job-\(id)")
        let outDir = dir.appendingPathComponent("out"), logURL = dir.appendingPathComponent("build.log")
        try? fm.removeItem(at: dir)
        do {
            try fm.createDirectory(at: outDir, withIntermediateDirectories: true)
            fm.createFile(atPath: logURL.path, contents: nil)
            logHandle = try FileHandle(forWritingTo: logURL)
        } catch {
            log("warning: cannot create \(dir.path): \(error)")
        }
        log("job \(id): building \(name) for \(job.platform) (recipe \(job.recipePath ?? "")"
            + (job.build.map { ", build \($0)" } ?? "") + ")")

        var action = Container.Action.fail("build produced no .hpkg")
        if !settings.buildCommand.isEmpty {
            let argv = [settings.buildCommand, job.portName, job.version, outDir.path]
            log("running: \(argv.joined(separator: " "))")
            let code = await runChild(argv, env: [
                "RENKUPORTS_PLATFORM": job.platform,
                "RENKUPORTS_JOB_ID": String(id),
                "RENKUPORTS_RECIPE_PATH": job.recipePath ?? "",
            ], stop: .interrupt)
                .code
            action = code == 0 ? .upload : .fail("build command exited with code \(code)")
        } else {
            action = await runContainer(job, name: name, outDir: outDir)
        }
        if stopping { action = .release("client stopped") }

        var files: [URL] = []
        if action == .upload {
            files = collectPackages(in: outDir)
            action = files.isEmpty ? .fail("build produced no .hpkg") : await upload(files, jobId: id, dir: dir)
        }
        if stopping { action = .release("client stopped") }

        let logTail = { readLogTail(logURL) }
        var result: String
        switch action {
        case .upload:
            do {
                let reply = try await api.call(
                    "POST",
                    "api/client/jobs/\(id)/complete",
                    body: .json("{\"log\":\(jsonString(logTail()))}"),
                    cancellable: false
                )
                // Succeeded, or Verifying while the server waits for a second matching build.
                struct Done: Decodable { let status: String? }
                let status = (try? JSONDecoder().decode(Done.self, from: reply))?.status ?? "?"
                log("job \(id): completed, \(files.count) package(s) uploaded, job is \(status)")
                result = "completed, \(files.count) package(s), job \(status)"
            } catch {
                result = await fail(id, "complete failed: \(error)", log: logTail())
            }
        case let .fail(summary):
            result = await fail(id, summary, log: logTail())
        case let .failAndPause(summary):
            result = await fail(id, summary, log: logTail())
            pausing = true
            pauseReason = summary
        case let .release(reason):
            await release(id, reason, log: logTail())
            result = "released: \(reason)"
        case .retry:
            result = "internal error: retry not resolved" // runContainer never returns .retry
        }
        sink(.result("\(name) (job \(id)): \(result)"))
        flush()
        try? logHandle?.close()
        logHandle = nil
        try? fm.removeItem(at: outDir) // hpkgs can be large; build.log stays
    }

    /// Pull if asked, run the container (once more after an exit 4) and map its exit code.
    private func runContainer(_ job: Job, name: String, outDir: URL) async -> Container.Action {
        let portsRepo: ClientSelf.PortsRepo
        do {
            guard let repo = try await api.me().portsRepo else {
                return .fail(
                    "the server has no ports repository (PortsTree:GitUrl), so the container cannot clone the tree"
                )
            }
            portsRepo = repo
        } catch {
            return .release("cannot read api/client/me: \(error)")
        }
        guard let docker = resolveExecutable(settings.dockerPath) else { return .release("docker not found") }
        if settings.pullPolicy == "always" {
            let code = await runChild([docker, "pull", settings.image]).code
            if code != 0 { return .release("docker pull \(settings.image) failed (exit \(code))") }
        }
        let accel = info?.accel[job.platform] == "kvm" ? "KVM" : "TCG (slow)"
        let line = "building \(name): \(job.platform), \(accel), \(settings.image)"
        let argv = [docker] + Container.runArguments(
            job: job,
            settings: settings,
            outDir: outDir.path,
            portsURL: portsRepo.url,
            portsRef: portsRepo.branch ?? "main"
        )
        var retried = false
        while true {
            state("\(line), guest booting")
            log("running: \(argv.joined(separator: " "))")
            let code = await runChild(argv, stop: .container(Container.name(jobId: job.jobId)), guestLine: line).code
            let action = Container.action(exitCode: code, stopped: stopping, retried: retried)
            guard action == .retry else { return action }
            log("cache volume busy (exit 4); retrying once in \(busyRetryDelay)")
            // Pause lets the job take its normal path (wait, then retry); only Stop cuts the wait short.
            await sleep(busyRetryDelay, untilPause: false)
            if stopping { return .release("client stopped") }
            retried = true
        }
    }

    /// Uploads each package as multipart (sha256 and buildDate first), streamed from a temporary body file.
    private func upload(_ files: [URL], jobId: Int64, dir: URL) async -> Container.Action {
        let body = dir.appendingPathComponent("upload.body")
        defer { try? FileManager.default.removeItem(at: body) }
        for file in files {
            do {
                let sha = try sha256Hex(of: file)
                let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
                let date = isoDate(attributes[.modificationDate] as? Date ?? Date())
                let type = try writeMultipartBody(
                    file: file,
                    sha256: sha,
                    buildDate: date,
                    boundary: "renkuports-\(UUID().uuidString)",
                    to: body
                )
                log("uploading \(file.lastPathComponent) (\(attributes[.size] ?? 0) bytes, sha256 \(sha))")
                try await api.call("POST", "api/client/jobs/\(jobId)/packages", body: .file(body, contentType: type))
                log("uploaded \(file.lastPathComponent)")
            } catch {
                return .fail("upload failed: \(error)")
            }
        }
        return .upload
    }

    /// Gives the job back to the queue; an older server without the endpoint gets a fail instead.
    private func release(_ id: Int64, _ reason: String, log tail: Data) async {
        log("job \(id): giving it back: \(reason)")
        do {
            switch try await api.release(jobId: id, reason: reason, log: tail) {
            case .released: log("job \(id): released to the queue")
            case .failedInstead: log(
                    "job \(id): the server has no release endpoint; failed as infrastructure: \(reason)"
                )
            }
        } catch {
            log("job \(id): releasing it failed (\(error)); the job stays claimed until its lease expires")
        }
    }

    /// Reports the failure. Runs after Stop too, so a stopped client does not leave its job claimed.
    private func fail(_ id: Int64, _ summary: String, log tail: Data) async -> String {
        log("job \(id): failed: \(summary)")
        do {
            try await api.fail(jobId: id, summary: summary, log: tail)
        } catch {
            log("job \(id): reporting the failure failed (\(error)); the job stays claimed until its lease expires")
        }
        return "failed: \(summary)"
    }

    // MARK: - Child processes

    /// Runs `argv` with output into the build log and the window (or, with `capture`, returned only).
    /// Stop interrupts it per `stop`. With `guestLine`, the state drops "guest booting" at the first line that
    /// is not prefixed `[host] `.
    private func runChild(
        _ argv: [String],
        env: [String: String] = [:],
        capture: Bool = false,
        stop: StopMode = .none,
        guestLine: String? = nil
    ) async -> (code: Int32, output: String) {
        let child: ChildProcess
        do {
            child = try ChildProcess(argv, env: env)
        } catch {
            if !capture { log("\(error)") }
            return (127, "\(error)")
        }
        var captured = Data()
        var stopAt: Date?, exitedAt: Date?
        var stoppers: [ChildProcess] = [] // keeps docker stop / kill alive
        var killed = false
        var guestSeen = guestLine == nil
        while true {
            let (data, eof) = child.take()
            if capture {
                captured.append(data)
            } else if !data.isEmpty {
                logHandle?.write(data)
                pending.append(data)
                if !guestSeen, let guestLine {
                    // shortcut: a [host] line split across two reads can count as guest output;
                    // harmless (status text only).
                    let lines = String(decoding: data, as: UTF8.self).split(whereSeparator: \.isNewline)
                    if lines.contains(where: { !$0.hasPrefix("[host] ") }) {
                        guestSeen = true
                        state(guestLine)
                    }
                }
            }
            if !capture, pending.count > 32768 || Date().timeIntervalSince(flushedAt) > 0.3 { flush() }
            if !child.process.isRunning {
                exitedAt = exitedAt ?? Date()
                if eof || Date().timeIntervalSince(exitedAt ?? Date()) > 2 { break }
            } else if stopping {
                let now = Date()
                switch stop {
                case .none:
                    break
                case .interrupt:
                    if stopAt == nil {
                        log("stopping the build")
                        child.process.interrupt()
                        stopAt = now
                    } else if let stopAt, now.timeIntervalSince(stopAt) > 60 {
                        kill(child.process.processIdentifier, SIGKILL)
                    }
                case let .container(name):
                    // docker stop's 90 s grace exceeds the entrypoint's 60 s, so docker kill is the exception.
                    if stopAt == nil {
                        log("stopping the build: docker stop -t 90 \(name)")
                        launch([argv[0], "stop", "-t", "90", name], into: &stoppers)
                        stopAt = now
                    } else if let stopAt, now.timeIntervalSince(stopAt) > 100, !killed {
                        log("container still running after 100 s: docker kill \(name)")
                        launch([argv[0], "kill", name], into: &stoppers)
                        killed = true
                    }
                }
            }
            try? await Task.sleep(for: .milliseconds(100))
        }
        let rest = child.take().data
        if capture { captured.append(rest) } else { logHandle?.write(rest); pending.append(rest); flush() }
        return (child.exitCode, String(decoding: captured, as: UTF8.self))
    }

    /// Starts docker stop or kill without waiting for it; its own output is not shown.
    private func launch(_ argv: [String], into children: inout [ChildProcess]) {
        do { try children.append(ChildProcess(argv)) } catch { log("\(error)") }
    }

    // MARK: - Messages

    private func flush() {
        flushedAt = Date()
        guard !pending.isEmpty else { return }
        sink(.log(String(decoding: pending, as: UTF8.self), clear: false))
        pending = Data()
    }

    /// A timestamped client message, into the current job's build log and the window.
    private func log(_ text: String) {
        let stamp = stampFormat.string(from: Date())
        let line = Data("\(stamp) \(text)\n".utf8)
        logHandle?.write(line)
        pending.append(line)
        flush()
    }

    private func state(_ text: String) { sink(.state(text)) }

    /// Sleeps in steps of at most 250 ms until the time is up or Stop (and, with `untilPause`, Pause) is pressed.
    private func sleep(_ duration: Duration, untilPause: Bool = true) async {
        let end = ContinuousClock.now + duration
        while ContinuousClock.now < end, !stopping, !(untilPause && pausing) {
            try? await Task.sleep(for: min(.milliseconds(250), end - ContinuousClock.now))
        }
    }

    private func sleep(_ seconds: Int) async { await sleep(.seconds(seconds)) }
}
