import CryptoKit
import Foundation

/// A JSON string literal for raw bytes. Control characters are escaped and bytes that are not valid UTF-8 become
/// `?`, since the server's JSON parser rejects invalid UTF-8 (a log tail can start in the middle of a character).
public func jsonString(_ bytes: Data) -> String {
    var out = [UInt8]("\"".utf8)
    let s = [UInt8](bytes)
    var i = 0
    while i < s.count {
        let c = s[i]
        if c == UInt8(ascii: "\"") || c == UInt8(ascii: "\\") {
            out += [UInt8(ascii: "\\"), c]
        } else if c < 0x20 {
            out += Array(String(format: "\\u%04x", c).utf8)
        } else if c < 0x80 {
            out.append(c)
        } else {
            // shortcut: overlong 3/4-byte forms and surrogates are not rejected; add if the server ever refuses a log.
            let n = c >= 0xC2 && c < 0xE0 ? 2 : c >= 0xE0 && c < 0xF0 ? 3 : c >= 0xF0 && c < 0xF5 ? 4 : 0
            let valid = n > 0 && i + n <= s.count && s[i + 1 ..< i + n].allSatisfy { $0 & 0xC0 == 0x80 }
            if valid {
                out += s[i ..< i + n]
                i += n - 1
            } else {
                out.append(UInt8(ascii: "?"))
            }
        }
        i += 1
    }
    out.append(UInt8(ascii: "\""))
    return String(decoding: out, as: UTF8.self)
}

/// A JSON string literal for `text`.
public func jsonString(_ text: String) -> String { jsonString(Data(text.utf8)) }

/// The last 5 MB of the build log (the end is where the error is).
public func readLogTail(_ url: URL, limit: Int = 5 << 20) -> Data {
    guard let handle = try? FileHandle(forReadingFrom: url) else { return Data() }
    defer { try? handle.close() }
    let size = (try? handle.seekToEnd()) ?? 0
    try? handle.seek(toOffset: size > UInt64(limit) ? size - UInt64(limit) : 0)
    return (try? handle.readToEnd()) ?? Data()
}

/// The `*.hpkg` files to upload from `dir`, sorted; `-source.hpkg` is skipped.
public func collectPackages(in dir: URL) -> [URL] {
    let names = (try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []
    return names.filter { $0.hasSuffix(".hpkg") && !$0.hasSuffix("-source.hpkg") }.sorted()
        .map { dir.appendingPathComponent($0) }
}

/// SHA-256 of a file, read in 1 MB chunks, as lowercase hex.
public func sha256Hex(of url: URL) throws -> String {
    let handle = try FileHandle(forReadingFrom: url)
    defer { try? handle.close() }
    var hash = SHA256()
    while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hash.update(data: chunk) }
    return hash.finalize().map { String(format: "%02x", $0) }.joined()
}

/// Writes the multipart/form-data upload body for `file` to `body`: the `sha256` and `buildDate` fields first
/// (the server reads them before the file part), then the file, copied in 1 MB chunks.
/// - Returns: the `Content-Type` header value, with the boundary.
public func writeMultipartBody(
    file: URL,
    sha256: String,
    buildDate: String,
    boundary: String,
    to body: URL
) throws -> String {
    let head = "--\(boundary)\r\nContent-Disposition: form-data; name=\"sha256\"\r\n\r\n\(sha256)"
        + "\r\n--\(boundary)\r\nContent-Disposition: form-data; name=\"buildDate\"\r\n\r\n\(buildDate)"
        + "\r\n--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(file.lastPathComponent)\""
        + "\r\nContent-Type: application/octet-stream\r\n\r\n"
    let tail = "\r\n--\(boundary)--\r\n"
    guard FileManager.default.createFile(atPath: body.path, contents: Data(head.utf8)) else {
        throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: body.path])
    }
    let out = try FileHandle(forWritingTo: body)
    defer { try? out.close() }
    try out.seekToEnd()
    let input = try FileHandle(forReadingFrom: file)
    defer { try? input.close() }
    while let chunk = try input.read(upToCount: 1 << 20), !chunk.isEmpty { try out.write(contentsOf: chunk) }
    try out.write(contentsOf: Data(tail.utf8))
    return "multipart/form-data; boundary=\(boundary)"
}

/// `yyyy-MM-ddTHH:mm:ssZ` in UTC, the form the Haiku client sends as `buildDate`.
public func isoDate(_ date: Date) -> String {
    ISO8601DateFormatter().string(from: date)
}
