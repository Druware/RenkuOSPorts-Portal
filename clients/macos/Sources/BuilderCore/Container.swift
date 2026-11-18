import Foundation

/// The builder container's contract (docs/linux-mac-build-clients.md section A).
public enum Container {
    /// The container's name, which Stop and the pre-flight use to find it.
    public static func name(jobId: Int64) -> String { "renkuports-job-\(jobId)" }

    /// `RENKUPORTS_ACCEL` for the `kvm` setting.
    public static func accel(kvm: String) -> String { kvm == "on" ? "kvm" : kvm == "off" ? "tcg" : "auto" }

    /// `--device /dev/kvm` only for `kvm=on`: `auto` never adds it on macOS (Docker Desktop's VM has no KVM).
    public static func deviceArgs(kvm: String) -> [String] { kvm == "on" ? ["--device", "/dev/kvm"] : [] }

    /// The `docker run` arguments (after the docker executable) for one job, exactly as in section A.
    public static func runArguments(
        job: Job,
        settings: Settings,
        outDir: String,
        portsURL: String,
        portsRef: String
    ) -> [String] {
        ["run", "--rm", "--init", "--name", name(jobId: job.jobId)] + deviceArgs(kvm: settings.kvm) + [
            "-v", "\(settings.cacheVolume):/cache", "-v", "\(outDir):/out",
            "-e", "RENKUPORTS_JOB_ID=\(job.jobId)", "-e", "RENKUPORTS_PLATFORM=\(job.platform)",
            "-e", "RENKUPORTS_RECIPE_PATH=\(job.recipePath ?? "")",
            "-e", "RENKUPORTS_PORTS_URL=\(portsURL)", "-e", "RENKUPORTS_PORTS_REF=\(portsRef)",
            "-e", "RENKUPORTS_JOBS=\(settings.jobs)", "-e", "RENKUPORTS_GUEST_MB=\(settings.guestMB)",
            "-e", "RENKUPORTS_ACCEL=\(accel(kvm: settings.kvm))",
            settings.image, "build", job.platform, job.portName, job.version,
        ]
    }

    /// What the client does with a container's exit code.
    public enum Action: Equatable, Sendable {
        case upload
        /// Terminal: `POST jobs/{id}/fail`.
        case fail(String)
        /// Exit 2 is a client bug: fail the job and pause the client.
        case failAndPause(String)
        /// Infrastructure or Stop: `POST jobs/{id}/release` (falls back to fail on an old server).
        case release(String)
        /// Exit 4 (cache volume busy): wait and run once more.
        case retry
    }

    /// The exit code table of section A, with the release endpoint for infrastructure failures and Stop.
    /// - Parameters:
    ///   - stopped: the user pressed Stop (wins over the exit code).
    ///   - retried: this was already the retry after an exit 4.
    public static func action(exitCode: Int32, stopped: Bool, retried: Bool) -> Action {
        if stopped { return .release("client stopped") }
        switch exitCode {
        case 0: return .upload
        case 1: return .fail("build failed (exit 1)")
        case 2: return .failAndPause("bad arguments or environment (exit 2); see the log")
        case 3: return .release("infrastructure failure (exit 3)")
        case 4: return retried ? .release("cache volume busy (exit 4)") : .retry
        case 5: return .release("guest image missing or bad (exit 5); run Prepare")
        case 75: return .release("client stopped")
        case 125 ... 127, 137: return .release("docker failed (exit \(exitCode))")
        default: return .fail("container exited with code \(exitCode)")
        }
    }

    /// `info --json`.
    public struct Info: Decodable, Sendable {
        public let version: String?
        public let imageArch: String
        public let targets: [String]
        public let accel: [String: String]
        public let guestImages: [String: String]
    }

    /// Docker and `uname -m` spell architectures differently.
    static func normalizedArch(_ arch: String) -> String {
        switch arch.trimmingCharacters(in: .whitespacesAndNewlines) {
        case "aarch64", "arm64": "arm64"
        case "x86_64", "amd64": "amd64"
        case let other: other
        }
    }
}

/// Where GUI apps find docker: they do not inherit the shell's PATH.
public let extraSearchPaths = [
    "/usr/local/bin",
    "/opt/homebrew/bin",
    "/Applications/Docker.app/Contents/Resources/bin",
    FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".orbstack/bin")
        .path,
]

/// PATH for child processes: the inherited one plus ``extraSearchPaths`` (docker needs its credential helpers).
public func childPath() -> String {
    let inherited = ProcessInfo.processInfo.environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin"
    return ([inherited] + extraSearchPaths).joined(separator: ":")
}

/// The executable for `command`: a path is used as given (with `~` expanded), a bare name is searched in
/// ``childPath()``. Nil if nothing executable is found.
public func resolveExecutable(_ command: String) -> String? {
    let expanded = (command as NSString).expandingTildeInPath
    if expanded.contains("/") { return FileManager.default.isExecutableFile(atPath: expanded) ? expanded : nil }
    return childPath().split(separator: ":").map { "\($0)/\(expanded)" }
        .first { FileManager.default.isExecutableFile(atPath: $0) }
}
