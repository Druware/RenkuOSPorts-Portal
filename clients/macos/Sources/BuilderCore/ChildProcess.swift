import Foundation
import os

/// A child process with stdin from /dev/null and stdout and stderr merged into one pipe, read on its own thread
/// so the caller can poll for output, exit and Stop without blocking.
final class ChildProcess: @unchecked Sendable {
    let process = Process()
    private let output = OSAllocatedUnfairLock(initialState: (data: Data(), eof: false))

    /// Starts `argv` (argv[0] resolved by ``resolveExecutable(_:)``) with `env` added to the inherited environment.
    init(_ argv: [String], env: [String: String] = [:]) throws {
        guard let first = argv.first, let path = resolveExecutable(first) else {
            throw ApiError("cannot run \(argv.first ?? ""): not found (PATH \(childPath()))")
        }
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = Array(argv.dropFirst())
        process.environment = ProcessInfo.processInfo.environment.merging(env) { $1 }
            .merging(["PATH": childPath()]) { $1 }
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        process.standardInput = FileHandle.nullDevice
        try process.run()
        let reader = pipe.fileHandleForReading
        Thread.detachNewThread { [output] in
            while true {
                let chunk = reader.availableData
                output.withLock { if chunk.isEmpty { $0.eof = true } else { $0.data.append(chunk) } }
                if chunk.isEmpty { break }
            }
        }
    }

    /// The output read since the last call, and whether the pipe is closed.
    func take() -> (data: Data, eof: Bool) {
        output.withLock { state in
            defer { state.data = Data() }
            return (state.data, state.eof)
        }
    }

    /// The exit code, 128 + signal if it was killed.
    var exitCode: Int32 {
        process.terminationReason == .uncaughtSignal ? 128 + process.terminationStatus : process.terminationStatus
    }
}

/// Runs `argv` to the end and returns its exit code and output (127 and the reason if it cannot be started).
public func runCaptured(_ argv: [String]) async -> (code: Int32, output: String) {
    let child: ChildProcess
    do { child = try ChildProcess(argv) } catch { return (127, "\(error)") }
    var output = Data()
    var exitedAt: Date?
    while true {
        let (data, eof) = child.take()
        output.append(data)
        if !child.process.isRunning {
            exitedAt = exitedAt ?? Date()
            // A grandchild may keep the pipe open; do not wait for it long.
            if eof || Date().timeIntervalSince(exitedAt ?? Date()) > 2 { break }
        }
        try? await Task.sleep(for: .milliseconds(50))
    }
    output.append(child.take().data)
    return (child.exitCode, String(decoding: output, as: UTF8.self))
}
