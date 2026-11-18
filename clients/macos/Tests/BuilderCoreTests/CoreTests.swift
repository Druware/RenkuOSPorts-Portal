@testable import BuilderCore
import Foundation
import Testing

private func temporaryDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("BuilderCoreTests-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func settingsRoundTripKeepsSecretOutOfTheFile() throws {
    let url = try temporaryDirectory().appendingPathComponent("settings")
    var settings = Settings()
    settings.server = "https://ports.example.org/"
    settings.clientId = "c1"
    settings.secret = "s3cret"
    settings.jobs = 8
    settings.buildCommand = "/tmp/mock-build.sh"
    settings.pollSeconds = 30
    settings.dockerPath = "/opt/homebrew/bin/docker"
    settings.kvm = "off"
    settings.cacheVolume = "renkuports-cache-b"
    settings.guestMB = 12288
    settings.pullPolicy = "always"
    try settings.save(to: url)

    let mode = try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int
    #expect(mode == 0o600)
    let text = try String(contentsOf: url, encoding: .utf8)
    #expect(!text.contains("s3cret"))
    #expect(text.contains("cache_volume=renkuports-cache-b\n"))

    var loaded = try Settings.load(from: url)
    #expect(loaded.secret == "")
    loaded.secret = settings.secret
    #expect(loaded == settings)
}

@Test func settingsLoadClampsAndIgnoresUnknownKeys() throws {
    let url = try temporaryDirectory().appendingPathComponent("settings")
    try "# comment\njobs=0\npoll_seconds=1\nsecret=ignored\nbogus=1\nserver=http://a=b/\n".write(
        to: url,
        atomically: true,
        encoding: .utf8
    )
    let settings = try Settings.load(from: url)
    #expect(settings.jobs == 1)
    #expect(settings.pollSeconds == 5)
    #expect(settings.secret == "")
    #expect(settings.server == "http://a=b/")
    #expect(throws: (any Error).self) { try Settings.load(from: url.appendingPathExtension("missing")) }
}

@Test func jsonStringEscapesAndReplacesInvalidUTF8() {
    #expect(jsonString("a\"b\\c\nd\t\u{1}") == #""a\"b\\c\u000ad\u0009\u0001""#)
    #expect(jsonString("Grüße €") == "\"Grüße €\"")
    // A lone continuation byte, a truncated 3-byte sequence at the end, 0xff, and a valid 4-byte emoji.
    #expect(jsonString(Data([0x80, 0x41, 0xFF, 0xF0, 0x9F, 0x98, 0x80, 0xE2, 0x82])) == "\"?A?😀??\"")
}

@Test func multipartBodyPutsFieldsBeforeTheFile() throws {
    let dir = try temporaryDirectory()
    let file = dir.appendingPathComponent("libfoo-1.2-1-arm64.hpkg"), body = dir.appendingPathComponent("body")
    try Data("PKG\r\nbytes".utf8).write(to: file)
    let type = try writeMultipartBody(
        file: file,
        sha256: "abc",
        buildDate: "2026-11-18T10:00:00Z",
        boundary: "B",
        to: body
    )
    #expect(type == "multipart/form-data; boundary=B")
    let expected = "--B\r\nContent-Disposition: form-data; name=\"sha256\"\r\n\r\nabc\r\n"
        + "--B\r\nContent-Disposition: form-data; name=\"buildDate\"\r\n\r\n2026-11-18T10:00:00Z\r\n"
        + "--B\r\nContent-Disposition: form-data; name=\"file\"; filename=\"libfoo-1.2-1-arm64.hpkg\"\r\n"
        + "Content-Type: application/octet-stream\r\n\r\nPKG\r\nbytes\r\n--B--\r\n"
    #expect(try String(contentsOf: body, encoding: .utf8) == expected)
    #expect(try sha256Hex(of: file) == "fdcbddfecacef0abef5fe122d9a4564525ccd8679ce38eda9ee78d012f735ec1")
    #expect(isoDate(Date(timeIntervalSince1970: 0)) == "1970-01-01T00:00:00Z")
}

@Test func collectPackagesSkipsSourcePackages() throws {
    let dir = try temporaryDirectory()
    for name in ["b-1-1-arm64.hpkg", "a-1-1-arm64.hpkg", "a-1-1-source.hpkg", "notes.txt"] {
        try Data().write(to: dir.appendingPathComponent(name))
    }
    #expect(collectPackages(in: dir).map(\.lastPathComponent) == ["a-1-1-arm64.hpkg", "b-1-1-arm64.hpkg"])
}

@Test(arguments: [
    (Int32(0), false, false, Container.Action.upload),
    (1, false, false, .fail("build failed (exit 1)")),
    (2, false, false, .failAndPause("bad arguments or environment (exit 2); see the log")),
    (3, false, false, .release("infrastructure failure (exit 3)")),
    (4, false, false, .retry),
    (4, false, true, .release("cache volume busy (exit 4)")),
    (5, false, false, .release("guest image missing or bad (exit 5); run Prepare")),
    (75, false, false, .release("client stopped")),
    (125, false, false, .release("docker failed (exit 125)")),
    (126, false, false, .release("docker failed (exit 126)")),
    (127, false, false, .release("docker failed (exit 127)")),
    (137, false, false, .release("docker failed (exit 137)")),
    (9, false, false, .fail("container exited with code 9")),
    (0, true, false, .release("client stopped")),
    (1, true, false, .release("client stopped")),
])
func exitCodeMapping(code: Int32, stopped: Bool, retried: Bool, expected: Container.Action) {
    #expect(Container.action(exitCode: code, stopped: stopped, retried: retried) == expected)
}

@Test func dockerRunArgumentsMatchTheContract() throws {
    let job = try JSONDecoder().decode(Job.self, from: Data("""
    {"jobId":42,"portName":"libfoo","version":"1.2","platform":"arm64",
     "recipePath":"dev-libs/libfoo/libfoo-1.2.recipe","build":1}
    """.utf8))
    var settings = Settings()
    settings.image = "renkuports-builder:dev"
    let argv = Container.runArguments(
        job: job,
        settings: settings,
        outDir: "/w/job-42/out",
        portsURL: "https://git.example/ports.git",
        portsRef: "main"
    )
    #expect(argv == [
        "run",
        "--rm",
        "--init",
        "--name",
        "renkuports-job-42",
        "-v",
        "renkuports-cache:/cache",
        "-v",
        "/w/job-42/out:/out",
        "-e",
        "RENKUPORTS_JOB_ID=42",
        "-e",
        "RENKUPORTS_PLATFORM=arm64",
        "-e",
        "RENKUPORTS_RECIPE_PATH=dev-libs/libfoo/libfoo-1.2.recipe",
        "-e",
        "RENKUPORTS_PORTS_URL=https://git.example/ports.git",
        "-e",
        "RENKUPORTS_PORTS_REF=main",
        "-e",
        "RENKUPORTS_JOBS=4",
        "-e",
        "RENKUPORTS_GUEST_MB=8192",
        "-e",
        "RENKUPORTS_ACCEL=auto",
        "renkuports-builder:dev",
        "build",
        "arm64",
        "libfoo",
        "1.2",
    ])
    settings.kvm = "on"
    let kvm = Container.runArguments(job: job, settings: settings, outDir: "/o", portsURL: "u", portsRef: "r")
    #expect(Array(kvm[5 ... 6]) == ["--device", "/dev/kvm"])
    #expect(kvm.contains("RENKUPORTS_ACCEL=kvm"))
    settings.kvm = "off"
    #expect(Container.runArguments(job: job, settings: settings, outDir: "/o", portsURL: "u", portsRef: "r")
        .contains("RENKUPORTS_ACCEL=tcg"))
}

@Test func preflightRefusesRiscv64() async {
    let report = await Preflight.run(settings: Settings(), platform: "riscv64", fetch: false) { _, _ in
        Issue.record("no command may run for riscv64")
        return (0, "")
    }
    #expect(report.failure?.contains("riscv64 is not supported") == true)
}

// MARK: - API client against a URLProtocol stub

/// Answers requests from `handler` and records them; the suite using it is serialized.
final class StubProtocol: URLProtocol {
    nonisolated(unsafe) static var handler: (URLRequest, Data) -> (Int, Data) = { _, _ in (500, Data()) }
    nonisolated(unsafe) static var requests: [(path: String, body: Data)] = []

    override static func canInit(with _: URLRequest) -> Bool { true }
    override static func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func stopLoading() {}
    override func startLoading() {
        var body = request.httpBody ?? Data()
        if let stream = request.httpBodyStream {
            stream.open()
            var buffer = [UInt8](repeating: 0, count: 65536)
            while case let n = stream.read(&buffer, maxLength: buffer.count), n > 0 { body.append(buffer, count: n) }
            stream.close()
        }
        Self.requests.append((request.url?.path ?? "", body))
        let (status, data) = Self.handler(request, body)
        guard let url = request.url,
              let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: nil, headerFields: nil)
        else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }

    /// A session answered by this stub; clears the recorded requests.
    static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        requests = []
        return URLSession(configuration: configuration)
    }

    static func api() -> ApiClient {
        ApiClient(server: "http://stub", clientId: "c1", secret: "s", session: session())
    }
}

/// Every test that uses ``StubProtocol`` (its handler is shared), including the worker tests in WorkerTests.swift.
@Suite(.serialized) struct StubbedNetworkTests {
    static let token = Data(#"{"accessToken":"t"}"#.utf8)

    @Test func a401GetsANewTokenAndOneRetry() async throws {
        var meCalls = 0
        StubProtocol.handler = { request, _ in
            if request.url?.path == "/api/client/token" { return (200, Self.token) }
            meCalls += 1
            return meCalls == 1 ? (401, Data()) : (200, Data(#"{"name":"n","platform":"arm64"}"#.utf8))
        }
        let me = try await StubProtocol.api().me()
        #expect(me.platform == "arm64")
        #expect(StubProtocol.requests.map(\.path) == [
            "/api/client/token",
            "/api/client/me",
            "/api/client/token",
            "/api/client/me",
        ])
    }

    @Test func aSecond401IsAnError() async {
        StubProtocol.handler = { request, _ in request.url?.path == "/api/client/token" ? (200, Self.token) : (
            401,
            Data()
        ) }
        await #expect(throws: ApiError.self) { try await StubProtocol.api().me() }
        #expect(StubProtocol.requests.count == 4)
    }

    @Test func releaseFallsBackToFailOn404() async throws {
        StubProtocol.handler = { request, _ in
            switch request.url?.path {
            case "/api/client/token": (200, Self.token)
            case "/api/client/jobs/7/release": (404, Data())
            default: (200, Data("{}".utf8))
            }
        }
        let outcome = try await StubProtocol.api().release(jobId: 7, reason: "client stopped", log: Data("tail\n".utf8))
        #expect(outcome == .failedInstead)
        #expect(StubProtocol.requests.map(\.path) == [
            "/api/client/token",
            "/api/client/jobs/7/release",
            "/api/client/jobs/7/fail",
        ])
        let release = try JSONSerialization.jsonObject(with: StubProtocol.requests[1].body) as? [String: String]
        #expect(release == ["reason": "client stopped", "log": "tail\n"])
        let fail = try JSONSerialization.jsonObject(with: StubProtocol.requests[2].body) as? [String: String]
        #expect(fail == ["summary": "infrastructure: client stopped", "log": "tail\n"])
    }

    @Test func releaseReturnsReleasedOn204() async throws {
        StubProtocol.handler = { request, _ in request.url?.path == "/api/client/token" ? (200, Self.token) : (
            204,
            Data()
        ) }
        #expect(try await StubProtocol.api().release(jobId: 7, reason: "r", log: Data()) == .released)
        #expect(StubProtocol.requests.count == 2)
    }

    @Test func releaseOtherErrorsThrowWithoutFailing() async {
        StubProtocol.handler = { request, _ in request.url?.path == "/api/client/token" ? (200, Self.token) : (
            500,
            Data()
        ) }
        await #expect(throws: ApiError.self) { try await StubProtocol.api().release(jobId: 7, reason: "r", log: Data())
        }
        #expect(StubProtocol.requests.map(\.path).last == "/api/client/jobs/7/release")
    }
}
