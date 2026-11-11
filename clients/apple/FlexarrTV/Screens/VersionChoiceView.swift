import FlexarrKit
import SwiftUI

/// Which version of a title plays (Automatic first), from its page: a panel down the right, as the subtitle search is,
/// whose rows are Settings' choice rows, so the chosen one has its check and VoiceOver says it's the selected one.
struct VersionChoiceView: View {
    let versions: [TitleVersion]
    let choose: (String?) -> Void
    let close: () -> Void

    var body: some View {
        HStack(spacing: 0) {
            Spacer(minLength: 0)
            VStack(alignment: .leading, spacing: 18) {
                Text(t(Words.version))
                    .font(.marquee(48, weight: .black))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 40)
                ScrollView {
                    VStack(spacing: 12) {
                        SettingsRow(title: t(Words.automatic), accessory: versions.contains(where: \.chosen) ? .none : .checkmark) {
                            pick(nil)
                        }
                        ForEach(versions) { version in
                            SettingsRow(title: version.name(among: versions), accessory: version.chosen ? .checkmark : .none) {
                                pick(version.fileId)
                            }
                        }
                    }
                    .padding(.horizontal, 40)
                    .padding(.vertical, 20)
                }
                .scrollClipDisabled()
            }
            .padding(.top, 60)
            .frame(width: 900)
            .background(Color.room.opacity(0.94))
        }
        .ignoresSafeArea()
        .onExitCommand(perform: close)
    }

    private func pick(_ fileId: String?) {
        close()
        choose(fileId)
    }
}
