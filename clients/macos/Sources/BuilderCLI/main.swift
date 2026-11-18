// Headless runner for the build worker: the same loop as the app's Start button, output on stdout.
//   renkuports-builder-cli [--settings <file>] [--once]
// The secret comes from RENKUPORTS_SECRET, else the Keychain. --once pauses after the first job (for tests).
// SIGINT and SIGTERM are the Stop button.
import BuilderCore
import Foundation

var arguments = CommandLine.arguments.dropFirst()
var settingsURL = Settings.defaultURL
var once = false
while let argument = arguments.popFirst() {
    switch argument {
    case "--settings": settingsURL = URL(fileURLWithPath: arguments.popFirst() ?? "")
    case "--once": once = true
    default:
        FileHandle.standardError.write(Data("usage: renkuports-builder-cli [--settings <file>] [--once]\n".utf8))
        exit(2)
    }
}

var settings: Settings
do {
    settings = try Settings.load(from: settingsURL)
} catch {
    FileHandle.standardError.write(Data("cannot read \(settingsURL.path): \(error)\n".utf8))
    exit(1)
}

settings.secret = ProcessInfo.processInfo.environment["RENKUPORTS_SECRET"] ?? Keychain.readSecret() ?? ""

/// The sink cannot capture the worker it is passed to, so it reaches it through this.
nonisolated(unsafe) var sinkWorker: Worker?
let pauseAfterJob = once
let worker = Worker { event in
    let write = { (text: String) in FileHandle.standardOutput.write(Data(text.utf8)) }
    switch event {
    case let .log(text, _): write(text)
    case let .state(text): write("[state] \(text)\n")
    case let .result(text): write("[result] \(text)\n")
    case let .queue(text): write("[queue] \(text)\n")
    case let .job(name, _):
        // --once: pause as soon as a job starts, so the loop ends after it is reported.
        if pauseAfterJob, name != nil { Task { await sinkWorker?.pause() } }
    case .exited: break
    }
}

sinkWorker = worker

var signalSources: [DispatchSourceSignal] = []
for signo in [SIGINT, SIGTERM] {
    signal(signo, SIG_IGN)
    let source = DispatchSource.makeSignalSource(signal: signo, queue: .global())
    source.setEventHandler { @Sendable [worker] in Task { await worker.stop() } }
    source.resume()
    signalSources.append(source)
}

await worker.start(settings)
await worker.waitForExit()
