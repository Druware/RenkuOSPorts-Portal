import Foundation

/// The app version; keep it in step with the Haiku client's APP_VERSION and the git tag.
public let appVersion = "0.1.2"

/// Settings, stored as key=value lines (mode 0600) in `~/Library/Application Support/RenkuPortsBuilder/settings`.
/// The secret is not in the file: it lives in the login Keychain (see ``Keychain``).
/// Each property is the settings key of the same meaning (`client_id`, `work_dir`, ...), see the README.
public struct Settings: Sendable, Equatable {
    public var server = "http://localhost:8080/"
    public var clientId = ""
    public var secret = ""
    public var jobs = 4
    public var workDir = Settings.supportDirectory.appendingPathComponent("work").path
    public var runOnStart = false
    /// Hidden (no field in the window): `<cmd> <port> <version> <outdir>` instead of the container, for testing.
    public var buildCommand = ""
    public var pollSeconds = 60
    public var dockerPath = "docker"
    public var image = "ghcr.io/druware/renkuports-builder:\(appVersion)"
    /// `auto` (never on macOS), `on` (adds `--device /dev/kvm`) or `off`.
    public var kvm = "auto"
    public var cacheVolume = "renkuports-cache"
    public var guestMB = 8192
    /// `if-missing` or `always` (`docker pull` before each job).
    public var pullPolicy = "if-missing"

    /// The defaults.
    public init() {}

    /// `~/Library/Application Support/RenkuPortsBuilder`.
    public static let supportDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/RenkuPortsBuilder")
    /// The settings file.
    public static let defaultURL = supportDirectory.appendingPathComponent("settings")

    /// Reads the file (the secret is not in it). Throws if the file does not exist.
    public static func load(from url: URL = defaultURL) throws -> Settings {
        var settings = Settings()
        let text = try String(contentsOf: url, encoding: .utf8)
        for line in text.split(whereSeparator: \.isNewline) where !line.hasPrefix("#") {
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = String(line[..<eq]), value = String(line[line.index(after: eq)...])
            switch key {
            case "server": settings.server = value
            case "client_id": settings.clientId = value
            case "jobs": settings.jobs = Int(value) ?? settings.jobs
            case "work_dir": settings.workDir = value
            case "run_on_start": settings.runOnStart = value == "true"
            case "build_command": settings.buildCommand = value
            case "poll_seconds": settings.pollSeconds = Int(value) ?? settings.pollSeconds
            case "docker_path": settings.dockerPath = value
            case "image": settings.image = value
            case "kvm": settings.kvm = value
            case "cache_volume": settings.cacheVolume = value
            case "guest_mb": settings.guestMB = Int(value) ?? settings.guestMB
            case "pull_policy": settings.pullPolicy = value
            default: break
            }
        }
        settings.jobs = max(1, settings.jobs)
        settings.pollSeconds = max(5, settings.pollSeconds)
        return settings
    }

    /// Writes a 0600 temporary file and renames it over the old one. The secret is not written.
    public func save(to url: URL = defaultURL) throws {
        var lines: [(String, String)] = [
            ("server", server), ("client_id", clientId), ("jobs", String(jobs)), ("work_dir", workDir),
            ("run_on_start", runOnStart ? "true" : "false"), ("docker_path", dockerPath), ("image", image),
            ("kvm", kvm), ("cache_volume", cacheVolume), ("guest_mb", String(guestMB)), ("pull_policy", pullPolicy),
        ]
        if !buildCommand.isEmpty { lines.append(("build_command", buildCommand)) }
        if pollSeconds != 60 { lines.append(("poll_seconds", String(pollSeconds))) }
        // Values are single lines; strip newlines a paste may have brought in.
        let text = lines.map { "\($0.0)=\($0.1.filter { !$0.isNewline })\n" }.joined()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let tmp = url.appendingPathExtension("tmp")
        guard FileManager.default.createFile(
            atPath: tmp.path,
            contents: Data(text.utf8),
            attributes: [.posixPermissions: 0o600]
        ) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: tmp.path])
        }
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: tmp.path)
        guard rename(tmp.path, url.path) == 0 else {
            try? FileManager.default.removeItem(at: tmp)
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
    }
}
