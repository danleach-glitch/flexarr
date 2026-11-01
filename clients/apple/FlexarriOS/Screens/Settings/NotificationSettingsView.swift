import FlexarrKit
import SwiftUI
import UserNotifications

struct NotificationSettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(PushNotifications.self) private var notifications
    @State private var device: NotificationDevice?
    @State private var authorization: UNAuthorizationStatus = .notDetermined
    @State private var isWorking = false
    @State private var problem: Problem?
    @State private var testSent: String?

    var body: some View {
        List {
            if authorization == .denied {
                Section {
                    Button("Open Settings") {
                        if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                    }
                    .listRowBackground(Color.seat)
                } footer: {
                    Text("Notifications are off for Flexarr in the Settings app. Allow them there first.")
                }
            }

            Section {
                Toggle("Notifications", isOn: Binding(get: { notifications.isOn }, set: { on in
                    Task { await turn(on) }
                }))
                .settingsRow()
                .disabled(isWorking)
            } footer: {
                if device?.relay == false {
                    Text("Your server has notifications turned off, so none will arrive. They can be turned on in its configuration.")
                } else if !notifications.isOn {
                    Text("Hear when something new is ready. This \(ThisDevice.name) gets them for one profile.")
                }
            }

            if notifications.isOn {
                Section {
                    LabeledContent("For", value: notifications.profileName ?? "Your profile")
                        .listRowBackground(Color.seat)
                    if let current = model.currentProfile, let profileId = notifications.profileId, current.id != profileId {
                        Button("Get notifications for \(current.name) instead") { Task { await move() } }
                            .disabled(isWorking)
                            .listRowBackground(Color.seat)
                    }
                }

                Section("Tell me about") {
                    toggle("Movies", \.films)
                    toggle("Shows", \.shows)
                    if model.podcastStatus?.available != false { toggle("Podcasts", \.podcasts) }
                    if model.hasLiveTv { toggle("Recordings", \.recordings) }
                    toggle("Requests", \.requests, detail: "When something you requested is ready")
                }

                Section {
                    toggle("Upcoming", \.upcoming, detail: "Each morning, what's airing today")
                    if notifications.settings.upcoming {
                        Picker("At", selection: setting(\.upcomingHour)) {
                            ForEach(0..<24, id: \.self) { Text(Self.hourLabel($0)).tag($0) }
                        }
                        .listRowBackground(Color.seat)
                    }
                }

                Section {
                    Button("Send test notification") { Task { await sendTest() } }
                        .disabled(isWorking)
                        .listRowBackground(Color.seat)
                }
            }
        }
        .settingsPage("Notifications")
        .task { await load() }
        .alert(problem?.title ?? "", isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(problem?.message ?? "")
        }
        .banner($testSent)
    }

    private func toggle(_ title: String, _ key: WritableKeyPath<NotificationSettings, Bool>, detail: String? = nil) -> some View {
        Toggle(isOn: setting(key)) {
            Text(title)
            if let detail { Text(detail) }
        }
        .settingsRow()
    }

    /// A setting, sent to the server as it's changed.
    private func setting<Value>(_ key: WritableKeyPath<NotificationSettings, Value>) -> Binding<Value> {
        Binding(get: { notifications.settings[keyPath: key] }, set: { value in
            var changed = notifications.settings
            changed[keyPath: key] = value
            Task { await save(changed) }
        })
    }

    /// "9 AM", or "09:00", as the phone tells the time.
    private static func hourLabel(_ hour: Int) -> String {
        guard let date = Calendar.current.date(from: DateComponents(hour: hour)) else { return "\(hour):00" }
        return date.formatted(date: .omitted, time: .shortened)
    }

    private func load() async {
        authorization = await notifications.authorization()
        guard let credentials = model.credentials else { return }
        device = try? await notifications.load(credentials)
    }

    private func turn(_ on: Bool) async {
        guard let credentials = model.credentials else { return }
        isWorking = true
        defer { isWorking = false }
        if on {
            do {
                device = try await notifications.turnOn(credentials)
            } catch is PushNotifications.Refused {
                authorization = .denied
            } catch {
                problem = Problem(error)
            }
            authorization = await notifications.authorization()
        } else {
            await notifications.turnOff(credentials)
            device = nil
        }
    }

    private func move() async {
        guard let credentials = model.credentials else { return }
        isWorking = true
        defer { isWorking = false }
        do {
            device = try await notifications.move(to: credentials)
        } catch {
            problem = Problem(error)
        }
    }

    private func save(_ settings: NotificationSettings) async {
        guard let credentials = model.credentials else { return }
        do {
            device = try await notifications.update(settings, signedIn: credentials)
        } catch {
            problem = Problem(error)
        }
    }

    private func sendTest() async {
        guard let credentials = model.credentials else { return }
        isWorking = true
        defer { isWorking = false }
        do {
            try await notifications.sendTest(credentials)
            testSent = "Test notification sent. It should arrive in a moment."
        } catch {
            problem = Problem(error, title: "Couldn't send a test notification")
        }
    }

    private struct Problem {
        var title = "Couldn't change notifications"
        var message: String

        init(_ error: Error, title: String? = nil) {
            if let title { self.title = title }
            message = ConnectionError(error).message
        }
    }
}
