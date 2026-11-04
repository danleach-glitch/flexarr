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
                    Button(t("Open Settings")) {
                        if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                    }
                    .listRowBackground(Color.seat)
                } footer: {
                    Text(t("Notifications are off for Flexarr in the Settings app. Allow them there first."))
                }
            }

            Section {
                Toggle(t("Notifications"), isOn: Binding(get: { notifications.isOn }, set: { on in
                    Task { await turn(on) }
                }))
                .settingsRow()
                .disabled(isWorking)
            } footer: {
                if device?.relay == false {
                    Text(t("Your server has notifications turned off, so none will arrive. They can be turned on in its configuration."))
                } else if !notifications.isOn {
                    Text(t("Hear when something new is ready. This {device} gets them for one profile.", ["device": ThisDevice.name]))
                }
            }

            if notifications.isOn {
                Section {
                    LabeledContent(t("For"), value: notifications.profileName ?? t("Your profile"))
                        .listRowBackground(Color.seat)
                    if let current = model.currentProfile, let profileId = notifications.profileId, current.id != profileId {
                        Button(t("Get notifications for {profile} instead", ["profile": current.name])) { Task { await move() } }
                            .disabled(isWorking)
                            .listRowBackground(Color.seat)
                    }
                }

                Section(t("Tell me about")) {
                    toggle(t(Words.movies), \.films)
                    toggle(t(Words.shows), \.shows)
                    if model.podcastStatus?.available != false { toggle(t(Words.podcasts), \.podcasts) }
                    if model.hasLiveTv { toggle(t("Recordings"), \.recordings) }
                    toggle(t("Requests"), \.requests, detail: t("When something you requested is ready"))
                }

                Section {
                    toggle(t("Upcoming"), \.upcoming, detail: t("Each morning, what's airing today"))
                    if notifications.settings.upcoming {
                        Picker(t("At"), selection: setting(\.upcomingHour)) {
                            ForEach(0..<24, id: \.self) { Text(Self.hourLabel($0)).tag($0) }
                        }
                        .listRowBackground(Color.seat)
                    }
                }

                Section {
                    Button(t("Send test notification")) { Task { await sendTest() } }
                        .disabled(isWorking)
                        .listRowBackground(Color.seat)
                }
            }
        }
        .settingsPage(t("Notifications"))
        .task { await load() }
        .alert(problem?.title ?? "", isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
            Button(t("OK"), role: .cancel) {}
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
        return date.formatted(Date.FormatStyle(date: .omitted, time: .shortened, locale: Localization.locale))
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
            testSent = t("Test notification sent. It should arrive in a moment.")
        } catch {
            problem = Problem(error, title: t("Couldn't send a test notification"))
        }
    }

    private struct Problem {
        var title: String
        var message: String

        init(_ error: Error, title: String? = nil) {
            self.title = title ?? t("Couldn't change notifications")
            message = ConnectionError(error).message
        }
    }
}
