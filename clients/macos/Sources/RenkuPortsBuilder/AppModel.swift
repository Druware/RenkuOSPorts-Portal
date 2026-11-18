import BuilderCore
import Foundation
import Observation

/// The window's state: the settings being edited and what the worker reports.
@MainActor @Observable
final class AppModel {
    /// The log view keeps the last part of the output; the whole log is in `<work dir>/job-<id>/build.log`.
    static let maxLogCharacters = 512 * 1024

    var settings = Settings()
    let hasSavedSettings: Bool
    var settingsStatus = ""
    var checks: [Preflight.Check] = []

    var state = "not started"
    var job: String?
    var jobStarted: Date?
    var queue = "unknown"
    var log = ""
    var results: [String] = []
    var isRunning = false
    var isPaused = false
    /// `renkuports-job-*` containers found running at launch (a crash left them; they would block the cache volume).
    var leftoverContainers: [String] = []

    @ObservationIgnored private var worker: Worker!
    @ObservationIgnored private var quitReply: (() -> Void)?

    init() {
        let loaded = try? Settings.load()
        hasSavedSettings = loaded != nil
        settings = loaded ?? Settings()
        settings.secret = Keychain.readSecret() ?? ""
        settingsStatus = hasSavedSettings ? "" : "No settings saved yet."
        // The sink runs on the worker's executor; a stream keeps the events in order on the main actor.
        let (events, continuation) = AsyncStream.makeStream(of: WorkerEvent.self)
        worker = Worker { continuation.yield($0) }
        Task { for await event in events { self.handle(event) } }
        if settings.runOnStart { start() }
        Task { await findLeftoverContainers() }
    }

    // MARK: - Buttons

    func start() {
        isPaused = false
        isRunning = true
        let settings = settings
        Task { await worker.start(settings) }
    }

    func pause() {
        isPaused = true
        state = "pausing (finishing the current job)"
        Task { await worker.pause() }
    }

    func stop() {
        state = "stopping"
        Task { await worker.stop() }
    }

    func save() {
        do {
            try settings.save()
            try Keychain.saveSecret(settings.secret)
            settingsStatus = isRunning
                ? "Saved; the running builder uses the new settings after Stop and Start." : "Saved."
        } catch {
            settingsStatus = "Saving the settings failed: \(error.localizedDescription)"
        }
    }

    /// Gets a token and reads `api/client/me`.
    func testConnection() {
        settingsStatus = "Testing..."
        let settings = settings
        Task {
            let api = ApiClient(server: settings.server, clientId: settings.clientId, secret: settings.secret)
            do {
                settingsStatus = try await "Connected: \(api.summary())"
            } catch {
                settingsStatus = "Connection failed: \(error)"
            }
        }
    }

    /// The Docker pre-flight as a checklist, for the client's platform if the server can be reached.
    func checkDocker() {
        checks = []
        settingsStatus = "Checking Docker..."
        let settings = settings
        Task {
            let api = ApiClient(server: settings.server, clientId: settings.clientId, secret: settings.secret)
            let platform = try? await api.me().platform
            checks = await Preflight.run(settings: settings, platform: platform, fetch: false).checks
            settingsStatus = platform == nil ? "Checked without a platform (the server could not be reached)." : ""
        }
    }

    func stopLeftoverContainers() {
        let names = leftoverContainers
        leftoverContainers = []
        Task {
            guard let docker = resolveExecutable(settings.dockerPath) else { return }
            for name in names { _ = await runCaptured([docker, "stop", "-t", "90", name]) }
        }
    }

    /// With a build running, stop it first (it is given back to the queue), then let the app quit.
    func quit(reply: @escaping () -> Void) -> Bool {
        guard isRunning else { return true }
        quitReply = reply
        stop()
        state = "stopping before quit"
        return false
    }

    // MARK: - Worker events

    private func handle(_ event: WorkerEvent) {
        switch event {
        case let .state(text): state = text
        case let .queue(text): queue = text
        case let .job(name, started):
            job = name
            jobStarted = started
        case let .log(text, clear):
            if clear { log = "" }
            log += text
            if log.utf8.count > Self.maxLogCharacters { log = String(log.suffix(Self.maxLogCharacters * 3 / 4)) }
        case let .result(text):
            let stamp = Date().formatted(date: .numeric, time: .shortened)
            results.insert("\(stamp)  \(text)", at: 0)
            if results.count > 100 { results.removeLast() }
        case .exited:
            isRunning = false
            isPaused = false
            quitReply?()
        }
    }

    private func findLeftoverContainers() async {
        guard settings.buildCommand.isEmpty, let docker = resolveExecutable(settings.dockerPath) else { return }
        let ps = await runCaptured([docker, "ps", "--filter", "name=renkuports-job-", "--format", "{{.Names}}"])
        if ps.code == 0 { leftoverContainers = ps.output.split(whereSeparator: \.isNewline).map(String.init) }
    }
}
