import Foundation

/// The Docker checks before Start (docs/linux-mac-build-clients.md section E, step 2), also shown in Settings.
public struct Preflight: Sendable {
    /// A warning does not stop Start; a failure does.
    public enum Status: Sendable { case ok, warning, failed }
    /// One line of the checklist.
    public struct Check: Sendable {
        public let status: Status
        public let text: String
    }

    /// The checks in the order they ran; a failure ends the list early.
    public private(set) var checks: [Check] = []
    /// The image's `info --json`, once it ran.
    public private(set) var info: Container.Info?
    /// The first failed check, or nil when Start may go on.
    public var failure: String? { checks.first { $0.status == .failed }?.text }

    /// Runs a command; `stream` asks for the output in the build log (pull, fetch-images) instead of captured.
    public typealias Runner = @Sendable (_ argv: [String], _ stream: Bool) async -> (code: Int32, output: String)

    /// - Parameters:
    ///   - platform: the client's platform from `api/client/me`, nil if unknown (Settings without a connection).
    ///   - fetch: run `fetch-images` for a missing guest image (Start) rather than only report it (Settings).
    public static func run(
        settings: Settings,
        platform: String?,
        fetch: Bool,
        runner: Runner = { argv, _ in await runCaptured(argv) }
    ) async -> Preflight {
        var result = Preflight()
        func add(_ status: Status, _ text: String) { result.checks.append(Check(status: status, text: text)) }

        if platform == "riscv64" {
            add(.failed, "riscv64 is not supported: no riscv64 builder guest exists yet")
            return result
        }
        guard let docker = resolveExecutable(settings.dockerPath) else {
            add(
                .failed,
                "docker not found (\(settings.dockerPath); also searched \(extraSearchPaths.joined(separator: ", ")))"
            )
            return result
        }
        let version = await runner([docker, "version", "--format", "{{.Server.Arch}}"], false)
        guard version.code == 0 else {
            add(.failed, "Docker daemon not reachable (is Docker Desktop running?): \(lastLine(version.output))")
            return result
        }
        let daemonArch = Container.normalizedArch(version.output)
        add(.ok, "Docker \(docker), daemon \(daemonArch)")

        let image = settings.image
        if await runner([docker, "image", "inspect", "--format", "{{.Id}}", image], false).code == 0 {
            add(.ok, "image \(image) present")
        } else {
            let pull = await runner([docker, "pull", image], true)
            guard pull.code == 0 else {
                add(.failed, "image \(image) missing and docker pull failed: \(lastLine(pull.output))")
                return result
            }
            add(.ok, "image \(image) pulled")
        }

        let cache = ["-v", "\(settings.cacheVolume):/cache"]
        let device = Container.deviceArgs(kvm: settings.kvm)
        let infoRun = await runner([docker, "run", "--rm"] + device + cache + [image, "info", "--json"], false)
        guard infoRun.code == 0,
              let info = try? JSONDecoder().decode(Container.Info.self, from: Data(infoRun.output.utf8))
        else {
            add(.failed, "\(image) info --json failed (exit \(infoRun.code)): \(lastLine(infoRun.output))")
            return result
        }
        result.info = info
        if Container.normalizedArch(info.imageArch) != daemonArch {
            add(.failed, "image arch \(info.imageArch) does not match the Docker daemon (\(daemonArch))")
        }
        if let platform {
            if !info.targets.contains(platform) {
                add(
                    .failed,
                    "image \(image) cannot build \(platform) (targets: \(info.targets.joined(separator: ", ")))"
                )
            } else {
                let accel = info.accel[platform] == "kvm" ? "KVM" : "TCG (slow)"
                add(.ok, "\(platform) builds with \(accel)")
                if info.guestImages[platform] == "cached" {
                    add(.ok, "guest image for \(platform) cached")
                } else if fetch {
                    let fetched = await runner(
                        [docker, "run", "--rm"] + cache + [image, "fetch-images", platform],
                        true
                    )
                    add(
                        fetched.code == 0 ? .ok : .failed,
                        fetched.code == 0 ? "guest image for \(platform) downloaded"
                            : "fetch-images \(platform) failed (exit \(fetched.code)); run Prepare again"
                    )
                } else {
                    add(.warning, "guest image for \(platform) not downloaded yet; Start downloads it first")
                }
            }
        }

        let memory = await runner([docker, "info", "--format", "{{.MemTotal}}"], false)
        let needed = Int64(settings.guestMB + 1024) << 20
        if let total = Int64(memory.output.trimmingCharacters(in: .whitespacesAndNewlines)), total < needed {
            add(
                .warning,
                "Docker has \(total >> 20) MiB of memory, a guest needs \(needed >> 20) MiB: "
                    + "raise Memory in Docker Desktop"
            )
        }

        let busy = await runner([
            docker,
            "ps",
            "--filter",
            "name=renkuports-job-",
            "--filter",
            "volume=\(settings.cacheVolume)",
            "--format",
            "{{.Names}}",
        ], false)
        let names = busy.output.split(whereSeparator: \.isNewline).joined(separator: " ")
        if busy.code == 0, !names.isEmpty {
            add(
                .failed,
                "volume \(settings.cacheVolume) is used by running \(names) (left over? docker stop -t 90 \(names))"
            )
        }
        return result
    }

    private static func lastLine(_ text: String) -> String {
        text.split(whereSeparator: \.isNewline).last.map(String.init) ?? ""
    }
}
