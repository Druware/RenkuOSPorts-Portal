import AppKit
import BuilderCore
import SwiftUI

@main
struct RenkuPortsBuilderApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        Window("RenkuPorts Builder", id: "main") {
            ContentView(model: delegate.model)
                .frame(minWidth: 720, minHeight: 540)
        }
    }
}

/// Owns the model so quitting can stop a running build first.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = AppModel()

    func applicationDidFinishLaunching(_: Notification) {
        // A bare SwiftPM executable starts as a background process; make it a regular app with a Dock icon.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_: NSApplication) -> Bool { true }

    func applicationShouldTerminate(_: NSApplication) -> NSApplication.TerminateReply {
        model.quit { NSApp.reply(toApplicationShouldTerminate: true) } ? .terminateNow : .terminateLater
    }
}

struct ContentView: View {
    @Bindable var model: AppModel
    @State private var tab: String

    init(model: AppModel) {
        self.model = model
        _tab = State(initialValue: model.hasSavedSettings ? "status" : "settings")
    }

    var body: some View {
        TabView(selection: $tab) {
            StatusView(model: model).tabItem { Text("Status") }.tag("status")
            SettingsView(model: model).tabItem { Text("Settings") }.tag("settings")
        }
        .padding()
        .alert("Build containers are still running", isPresented: .constant(!model.leftoverContainers.isEmpty)) {
            Button("Stop Them") { model.stopLeftoverContainers() }
            Button("Leave Running", role: .cancel) { model.leftoverContainers = [] }
        } message: {
            Text(
                "\(model.leftoverContainers.joined(separator: ", ")) hold the cache volume "
                    + "and would block the next job."
            )
        }
    }
}

struct StatusView: View {
    let model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Button("Start", action: model.start).disabled(model.isRunning && !model.isPaused)
                Button("Pause", action: model.pause).disabled(!model.isRunning || model.isPaused)
                Button("Stop", action: model.stop).disabled(!model.isRunning)
            }
            Text("State: \(model.state)")
            TimelineView(.periodic(from: .now, by: 1)) { context in
                Text("Job: \(model.job ?? "none")" + elapsed(at: context.date))
            }
            Text("Queue: \(model.queue)")
            ScrollViewReader { proxy in
                ScrollView([.vertical, .horizontal]) {
                    Text(model.log)
                        .font(.system(.body, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    Color.clear.frame(height: 1).id("end")
                }
                .background(.background.secondary)
                .onChange(of: model.log) { proxy.scrollTo("end") }
            }
            .frame(maxHeight: .infinity)
            .accessibilityLabel("Build output")
            Text("Recent results:")
            List(model.results, id: \.self) { Text($0) }
                .frame(height: 110)
        }
    }

    private func elapsed(at now: Date) -> String {
        guard model.job != nil, let started = model.jobStarted else { return "" }
        let s = Int(now.timeIntervalSince(started))
        return String(format: "   elapsed %d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
    }
}

struct SettingsView: View {
    @Bindable var model: AppModel

    var body: some View {
        Form {
            TextField("Server URL:", text: $model.settings.server)
            TextField("Client id:", text: $model.settings.clientId)
            SecureField("Secret:", text: $model.settings.secret)
            TextField("Jobs:", value: $model.settings.jobs, format: .number)
            TextField("Work directory:", text: $model.settings.workDir)
            TextField("Docker command:", text: $model.settings.dockerPath)
            TextField("Image:", text: $model.settings.image)
            Picker("KVM:", selection: $model.settings.kvm) {
                Text("Auto (never on macOS)").tag("auto")
                Text("On").tag("on")
                Text("Off").tag("off")
            }
            TextField("Cache volume:", text: $model.settings.cacheVolume)
            TextField("Guest memory (MB):", value: $model.settings.guestMB, format: .number)
            Picker("Pull image:", selection: $model.settings.pullPolicy) {
                Text("If missing").tag("if-missing")
                Text("Before every job").tag("always")
            }
            Toggle("Start building when the application starts", isOn: $model.settings.runOnStart)
            HStack {
                Button("Save", action: model.save)
                Button("Test Connection", action: model.testConnection)
                Button("Check Docker", action: model.checkDocker)
            }
            if !model.settingsStatus.isEmpty { Text(model.settingsStatus).textSelection(.enabled) }
            ForEach(Array(model.checks.enumerated()), id: \.offset) { _, check in
                Label(check.text, systemImage: symbol(check.status)).textSelection(.enabled)
            }
        }
    }

    private func symbol(_ status: Preflight.Status) -> String {
        switch status {
        case .ok: "checkmark.circle"
        case .warning: "exclamationmark.triangle"
        case .failed: "xmark.octagon"
        }
    }
}
