@testable import BuilderCore
import Foundation
import os
import Security
import Testing

/// The fake docker of the end-to-end test.
private let fakeDocker = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
    .appendingPathComponent("../e2e/fake_docker.sh").standardized.path

/// Collects worker events and lets a handler act on the worker (the sink cannot capture it directly).
private final class Recorder: @unchecked Sendable {
    private let lines = OSAllocatedUnfairLock(initialState: [String]())
    nonisolated(unsafe) var worker: Worker?
    var output: String { lines.withLock { $0.joined() } }

    func sink(onEvent: @escaping @Sendable (WorkerEvent, Worker) -> Void) -> Worker.Sink {
        { [self] event in
            if case let .log(text, _) = event { lines.withLock { $0.append(text) } }
            if case let .result(text) = event { lines.withLock { $0.append("[result] \(text)\n") } }
            if let worker { onEvent(event, worker) }
        }
    }
}

/// A coordinator with one arm64 job (42) for `port`; `jobs/next` answers after `claimDelay`.
private func coordinator(port: String, claimDelay: TimeInterval = 0) -> (URLRequest, Data) -> (Int, Data) {
    var claimed = false
    return { request, _ in
        switch request.url?.path ?? "" {
        case "/api/client/token": return (200, StubbedNetworkTests.token)
        case "/api/client/me":
            return (
                200,
                Data(#"{"name":"n","platform":"arm64","portsRepo":{"url":"https://x/p.git","branch":"main"}}"#.utf8)
            )
        case "/api/client/jobs": return (200, Data(#"{"total":1}"#.utf8))
        case "/api/client/jobs/next":
            Thread.sleep(forTimeInterval: claimDelay)
            if claimed { return (204, Data()) }
            claimed = true
            return (200, Data(#"{"jobId":42,"portName":"\#(port)","version":"1.2","platform":"arm64","build":1}"#.utf8))
        case "/api/client/jobs/42/release": return (204, Data())
        case "/api/client/jobs/42/complete": return (200, Data(#"{"status":"Succeeded"}"#.utf8))
        default: return (200, Data("{}".utf8))
        }
    }
}

private func testSettings() throws -> Settings {
    var settings = Settings()
    settings.server = "http://stub"
    settings.clientId = "c1"
    settings.secret = "s"
    settings.dockerPath = fakeDocker
    settings.image = "renkuports-builder:test"
    settings.workDir = FileManager.default.temporaryDirectory.appendingPathComponent("WorkerTests-\(UUID().uuidString)")
        .path
    return settings
}

/// Starts a worker on the stub and waits for its loop to end.
private func runWorker(
    _ settings: Settings,
    busyRetryDelay: Duration = .seconds(30),
    onEvent: @escaping @Sendable (WorkerEvent, Worker) -> Void
) async -> Recorder {
    let recorder = Recorder()
    let worker = Worker(
        sink: recorder.sink(onEvent: onEvent),
        session: StubProtocol.session(),
        busyRetryDelay: busyRetryDelay
    )
    recorder.worker = worker
    await worker.start(settings)
    await worker.waitForExit()
    return recorder
}

private func jobPosts() -> [String] {
    StubProtocol.requests.map(\.path).filter { $0.hasPrefix("/api/client/jobs/42/") }.map { $0.replacingOccurrences(
        of: "/api/client/jobs/42/",
        with: ""
    ) }
}

extension StubbedNetworkTests {
    @Test func cancelAbortsADelayedRequest() async throws {
        StubProtocol.handler = { request, _ in
            if request.url?.path == "/api/client/token" { return (200, Self.token) }
            Thread.sleep(forTimeInterval: 1.5)
            return (200, Data(#"{"name":"n","platform":"arm64"}"#.utf8))
        }
        let api = StubProtocol.api()
        let started = ContinuousClock.now
        let request = Task { try await api.me() }
        try await Task.sleep(for: .milliseconds(200))
        await api.cancel()
        await #expect(throws: ApiError.self) { try await request.value }
        #expect(ContinuousClock.now - started < .seconds(1))
    }

    @Test func nonCancellableRequestsSurviveCancel() async throws {
        StubProtocol.handler = { request, _ in
            if request.url?.path == "/api/client/token" { return (200, Self.token) }
            Thread.sleep(forTimeInterval: 0.5)
            return (204, Data())
        }
        let api = StubProtocol.api()
        let request = Task { try await api.call("POST", "api/client/jobs/next", cancellable: false) }
        try await Task.sleep(for: .milliseconds(200))
        await api.cancel()
        #expect(try await request.value.isEmpty)
    }

    @Test func stopDuringTheClaimReleasesTheClaimedJob() async throws {
        StubProtocol.handler = coordinator(port: "libfoo", claimDelay: 0.5)
        var settings = try testSettings()
        settings.buildCommand = "/usr/bin/false"
        // The queue line comes right before the claim, so Stop lands while jobs/next is in flight.
        let recorder = await runWorker(settings) { event, worker in
            if case .queue = event { Task { await worker.stop() } }
        }
        #expect(jobPosts() == ["release"])
        let release = try JSONSerialization
            .jsonObject(with: StubProtocol.requests.last?.body ?? Data()) as? [String: String]
        #expect(release?["reason"] == "client stopped")
        #expect(!recorder.output.contains("running:"))
    }

    @Test func pauseDuringTheExit4WaitStillRetries() async throws {
        StubProtocol.handler = coordinator(port: "busyport")
        let recorder = try await runWorker(testSettings(), busyRetryDelay: .seconds(1)) { event, worker in
            if case let .log(text, _) = event, text.contains("retrying once") { Task { await worker.pause() } }
        }
        #expect(jobPosts() == ["packages", "complete"])
        #expect(recorder.output.components(separatedBy: "running: ").count == 3) // two docker runs
        #expect(recorder.output.contains("[result] busyport-1.2 (job 42): completed, 1 package(s), job Succeeded"))
    }

    @Test func stopDuringTheExit4WaitReleasesAtOnce() async throws {
        StubProtocol.handler = coordinator(port: "busyport")
        let started = ContinuousClock.now
        let recorder = try await runWorker(testSettings()) { event, worker in
            if case let .log(text, _) = event, text.contains("retrying once") { Task { await worker.stop() } }
        }
        #expect(ContinuousClock.now - started < .seconds(10)) // not the 30 s wait
        #expect(jobPosts() == ["release"])
        #expect(recorder.output.components(separatedBy: "running: ").count == 2) // one docker run
        #expect(recorder.output.contains("released: client stopped"))
    }
}

// MARK: - Keychain

private enum KeychainProbe {
    static let service = "org.renkuos.PortsBuilder.tests.\(ProcessInfo.processInfo.processIdentifier)"

    /// Whether this process can write the login keychain (a locked keychain or a headless runner cannot).
    static let available: Bool = {
        do {
            try Keychain.saveSecret("probe", service: service)
            try Keychain.deleteSecret(service: service)
            return true
        } catch {
            return false
        }
    }()
}

@Test(.enabled(if: KeychainProbe.available, "the login keychain is not writable from this test runner"))
func keychainSavesReadsOverwritesAndDeletesTheSecret() throws {
    let service = KeychainProbe.service
    defer { try? Keychain.deleteSecret(service: service) }
    #expect(Keychain.readSecret(service: service) == nil)
    try Keychain.saveSecret("first", service: service)
    #expect(Keychain.readSecret(service: service) == "first")
    try Keychain.saveSecret("second ünïcode", service: service)
    #expect(Keychain.readSecret(service: service) == "second ünïcode")
    try Keychain.deleteSecret(service: service)
    #expect(Keychain.readSecret(service: service) == nil)
    try Keychain.deleteSecret(service: service) // deleting nothing is fine
    #expect(Keychain.readSecret(service: Keychain.defaultService + ".absent") == nil)
}
