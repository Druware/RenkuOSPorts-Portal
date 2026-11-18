import Foundation

/// A failed request, with a message for the log and the window.
public struct ApiError: Error, CustomStringConvertible {
    public let description: String
    init(_ description: String) { self.description = description }
}

/// A job from `POST api/client/jobs/next`.
public struct Job: Decodable, Sendable {
    public let jobId: Int64
    public let portName: String
    public let version: String
    public let platform: String
    public let recipePath: String?
    /// The build number, from servers that verify each job with two matching builds.
    public let build: Int?
}

/// `GET api/client/me`, the parts the client uses.
public struct ClientSelf: Decodable, Sendable {
    public struct PortsRepo: Decodable, Sendable { public let url: String; public let branch: String? }
    public struct CurrentJob: Decodable, Sendable {
        let id: Int64?, portName: String?, version: String?, attempts: Int?, unblocks: Int?
    }

    public let name: String
    public let platform: String
    public let owner: String?
    public let currentJob: CurrentJob?
    public let portsRepo: PortsRepo?
}

/// What ``ApiClient/release(jobId:reason:log:)`` did.
public enum ReleaseOutcome: Equatable, Sendable {
    case released
    /// The server has no release endpoint (404); the job was failed with the `infrastructure:` prefix instead.
    case failedInstead
}

/// The coordinator's client API, with the same token handling as the .NET and Haiku clients: a token is fetched
/// on first use and refreshed before its 12 h lifetime ends; a 401 gets a new token and the request is retried once.
public actor ApiClient {
    /// A request body.
    public enum Body: Sendable {
        case json(String)
        /// Streamed from disk with `upload(for:fromFile:)`.
        case file(URL, contentType: String)
    }

    /// Tokens live 12 h on the server; get a new one an hour before.
    static let tokenRefresh: TimeInterval = 11 * 3600

    private let server: String, clientId: String, secret: String
    private let session: URLSession
    private var token: String?
    private var tokenAt = Date.distantPast
    /// The cancellable request in flight, if any (see ``cancel()``).
    private var inFlight: Task<(Int, Data), Error>?

    /// - Parameter session: the session to use; tests pass one with a `URLProtocol` stub.
    public init(server: String, clientId: String, secret: String, session: URLSession? = nil) {
        var server = server.trimmingCharacters(in: .whitespaces)
        if !server.hasSuffix("/") { server += "/" }
        self.server = server
        self.clientId = clientId.trimmingCharacters(in: .whitespaces)
        self.secret = secret.trimmingCharacters(in: .whitespaces)
        self.session = session ?? URLSession(configuration: .ephemeral)
    }

    /// Aborts the cancellable request in flight, if any (Stop). Requests sent with `cancellable: false` (the claim
    /// and the job reports) are never aborted, so a claimed job is always reported.
    public func cancel() {
        inFlight?.cancel()
    }

    /// One exchange with any status. Redirects are refused: one would turn a POST into a GET without its body.
    private func send(
        _ method: String,
        _ path: String,
        bearer: String?,
        body: Body?,
        cancellable: Bool
    ) async throws -> (Int, Data) {
        guard let url = URL(string: server + path) else { throw ApiError("bad server URL \(server)") }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("renkuports-builder/\(appVersion)", forHTTPHeaderField: "User-Agent")
        if let bearer { request.setValue("Bearer \(bearer)", forHTTPHeaderField: "Authorization") }
        if case .json? = body { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if case let .file(_, type)? = body { request.setValue(type, forHTTPHeaderField: "Content-Type") }
        if body == nil, method == "POST" { request.httpBody = Data() }
        let task = Task { [session, request] in try await Self.perform(request, body: body, session: session) }
        if cancellable { inFlight = task }
        defer { if cancellable { inFlight = nil } }
        do {
            return try await task.value
        } catch {
            throw ApiError("\(method) \(path): \(error.localizedDescription)")
        }
    }

    private static func perform(_ request: URLRequest, body: Body?, session: URLSession) async throws -> (Int, Data) {
        let data: Data, response: URLResponse
        switch body {
        case let .json(json)?:
            (data, response) = try await session.upload(for: request, from: Data(json.utf8), delegate: NoRedirect())
        case let .file(file, _)?:
            (data, response) = try await session.upload(for: request, fromFile: file, delegate: NoRedirect())
        case nil:
            (data, response) = try await session.data(for: request, delegate: NoRedirect())
        }
        return ((response as? HTTPURLResponse)?.statusCode ?? 0, data)
    }

    private func currentToken(cancellable: Bool) async throws -> String {
        if let token, Date().timeIntervalSince(tokenAt) < Self.tokenRefresh { return token }
        let json = "{\"clientId\":\(jsonString(clientId)),\"secret\":\(jsonString(secret))}"
        let (status, data): (Int, Data)
        do {
            (status, data) = try await send(
                "POST",
                "api/client/token",
                bearer: nil,
                body: .json(json),
                cancellable: cancellable
            )
        } catch {
            throw ApiError("cannot reach \(server): \(error)")
        }
        struct Reply: Decodable { let accessToken: String }
        guard status == 200, let reply = try? JSONDecoder().decode(Reply.self, from: data) else {
            throw ApiError("token request failed: \(status) \(String(decoding: data.prefix(300), as: UTF8.self))")
        }
        token = reply.accessToken
        tokenAt = Date()
        return reply.accessToken
    }

    /// Sends a request with the bearer token, retrying once on 401.
    /// - Parameter cancellable: whether ``cancel()`` may abort it; false for the claim and the job reports.
    /// - Returns: the status and body, whatever the status.
    /// - Throws: ``ApiError`` when no response arrived (or the request was cancelled).
    public func exchange(
        _ method: String,
        _ path: String,
        body: Body? = nil,
        cancellable: Bool = true
    ) async throws -> (status: Int, body: Data) {
        var result = try await send(
            method,
            path,
            bearer: currentToken(cancellable: cancellable),
            body: body,
            cancellable: cancellable
        )
        if result.0 == 401 {
            token = nil
            result = try await send(
                method,
                path,
                bearer: currentToken(cancellable: cancellable),
                body: body,
                cancellable: cancellable
            )
        }
        return result
    }

    /// As ``exchange(_:_:body:cancellable:)``, but a status outside 2xx throws.
    /// - Returns: the response body.
    @discardableResult
    public func call(
        _ method: String,
        _ path: String,
        body: Body? = nil,
        cancellable: Bool = true
    ) async throws -> Data {
        let (status, data) = try await exchange(method, path, body: body, cancellable: cancellable)
        guard (200 ..< 300).contains(status) else {
            throw ApiError("\(method) \(path): \(status) \(String(decoding: data.prefix(500), as: UTF8.self))")
        }
        return data
    }

    /// The client's own record.
    /// - Throws: ``ApiError`` on a network error, a non-2xx status or an unreadable reply.
    public func me() async throws -> ClientSelf {
        let data = try await call("GET", "api/client/me")
        guard let me = try? JSONDecoder().decode(ClientSelf.self, from: data) else {
            throw ApiError("api/client/me: unreadable reply")
        }
        return me
    }

    /// `POST jobs/{id}/fail` with a summary and the log tail. Not cancellable.
    /// - Throws: ``ApiError`` if the failure could not be reported.
    public func fail(jobId: Int64, summary: String, log: Data) async throws {
        let json = "{\"summary\":\(jsonString(summary)),\"log\":\(jsonString(log))}"
        try await call("POST", "api/client/jobs/\(jobId)/fail", body: .json(json), cancellable: false)
    }

    /// Gives the job back to the queue (`POST jobs/{id}/release`), for infrastructure failures and Stop.
    /// A server without the endpoint answers 404; the job is then failed with `infrastructure: <reason>`.
    /// Not cancellable.
    /// - Returns: whether the job was released or failed instead.
    /// - Throws: ``ApiError`` on any other status or a network error (the job then stays claimed).
    public func release(jobId: Int64, reason: String, log: Data) async throws -> ReleaseOutcome {
        let path = "api/client/jobs/\(jobId)/release"
        let json = "{\"reason\":\(jsonString(reason)),\"log\":\(jsonString(log))}"
        let (status, data) = try await exchange("POST", path, body: .json(json), cancellable: false)
        if (200 ..< 300).contains(status) { return .released }
        guard status == 404 else {
            throw ApiError("POST \(path): \(status) \(String(decoding: data.prefix(500), as: UTF8.self))")
        }
        try await fail(jobId: jobId, summary: "infrastructure: \(reason)", log: log)
        return .failedInstead
    }

    /// One line about this client and the queue (`api/client/me` and `api/client/jobs`).
    public func summary() async throws -> String {
        let me = try await me()
        var text = "\(me.name) (\(me.platform), owner \(me.owner ?? "?"))"
        struct Page: Decodable { let total: Int }
        if let data = try? await call("GET", "api/client/jobs?status=Queued&pageSize=1&platform=\(me.platform)"),
           let page = try? JSONDecoder().decode(Page.self, from: data)
        {
            text += ": \(page.total) job(s) queued for \(me.platform)"
        }
        if let job = me.currentJob {
            text += "; building \(job.portName ?? "?")-\(job.version ?? "?")"
                + " (job \(job.id ?? 0), attempt \(job.attempts ?? 0)"
            if let unblocks = job.unblocks { text += ", unblocks \(unblocks) queued" }
            text += ")"
        } else {
            text += "; no job claimed"
        }
        return text
    }
}

/// Refuses redirects so the caller sees the 3xx (e.g. http:// given for an https site).
private final class NoRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(
        _: URLSession,
        task _: URLSessionTask,
        willPerformHTTPRedirection _: HTTPURLResponse,
        newRequest _: URLRequest
    ) async -> URLRequest? { nil }
}
