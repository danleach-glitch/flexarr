import FlexarrKit
import SwiftUI

struct ContentView: View {
    @AppStorage("serverURL") private var serverURL = "http://flexarr.local:8383"
    @AppStorage("apiKey") private var apiKey = ""
    @State private var status: Result<SystemStatus, Error>?

    var body: some View {
        NavigationStack {
            Form {
                Section("Server") {
                    TextField("Server URL", text: $serverURL)
                        .textContentType(.URL)
                    SecureField("API key", text: $apiKey)
                    Button("Connect") { Task { await connect() } }
                }

                Section("Status") {
                    switch status {
                    case .none:
                        Text("Not connected").foregroundStyle(.secondary)
                    case .success(let status):
                        LabeledContent("Version", value: status.version)
                        LabeledContent("Runtime", value: status.runtimeVersion)
                    case .failure(let error):
                        Text(error.localizedDescription).foregroundStyle(.red)
                    }
                }
            }
            .navigationTitle("Flexarr")
        }
        .task { await connect() }
    }

    private func connect() async {
        guard let url = URL(string: serverURL) else { return }
        let client = FlexarrClient(baseURL: url, apiKey: apiKey.isEmpty ? nil : apiKey)
        do {
            status = .success(try await client.systemStatus())
        } catch {
            status = .failure(error)
        }
    }
}
