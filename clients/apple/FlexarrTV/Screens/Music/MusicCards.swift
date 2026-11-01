import FlexarrKit
import SwiftUI

/// Album covers and artist pictures are square; the title stands in on a wash of the item's colour until one arrives.
struct SquareArtwork: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var size: CGFloat
    var circular = false

    var body: some View {
        RemoteImage(url: model.client?.imageURL(item.artwork.poster, width: size > 400 ? 800 : 500)) {
            ZStack {
                LinearGradient(colors: [item.glowColor.opacity(0.4), Color.seat], startPoint: .top, endPoint: .bottom)
                Text(circular ? initials : item.title)
                    .font(.marquee(circular ? size * 0.3 : max(24, size * 0.11), weight: .bold))
                    .foregroundStyle(Color.screen.opacity(0.8))
                    .multilineTextAlignment(.center)
                    .padding(size * 0.08)
            }
        }
        .frame(width: size, height: size)
        .clipShape(circular ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous)))
    }

    private var initials: String {
        item.title.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
    }
}

/// An album: square cover with its title and year beneath.
struct AlbumCard: View {
    let item: ItemSummary
    var width: CGFloat = Layout.posterWidth
    /// The artist under the title, where the albums are from several; the year on an artist's own page.
    var showsArtist = false
    /// Shared focus state, so the row can steer focus to this card (see `RowFocus`).
    var focus: FocusState<String?>.Binding? = nil
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: item.glowColor, focus: focus, focusID: item.id, action: action) {
            SquareArtwork(item: item, size: width)
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(item.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                    .lineLimit(1)
                if let subtitle {
                    Text(subtitle)
                        .font(.system(size: 21))
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            .frame(width: width, alignment: .leading)
        }
    }

    private var subtitle: String? {
        if showsArtist, let artist = item.artistName { return artist }
        return item.releaseYear.map(String.init)
    }
}

/// A track in a Home row: its cover with the name and artist beneath, and a play mark since pressing it plays.
struct TrackCard: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var width: CGFloat = Layout.posterWidth
    /// Shared focus state, so the row can steer focus to this card (see `RowFocus`).
    var focus: FocusState<String?>.Binding? = nil
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: item.glowColor, focus: focus, focusID: item.id, action: action) {
            SquareArtwork(item: item, size: width)
                .overlay(alignment: .bottomTrailing) {
                    Image(systemName: model.music.current?.id == item.id && model.music.isPlaying ? "speaker.wave.2.fill" : "play.fill")
                        .font(.system(size: 20, weight: .bold))
                        .foregroundStyle(Color.room)
                        .frame(width: 44, height: 44)
                        .background(Circle().fill(Color.screen.opacity(0.92)))
                        .padding(12)
                }
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(item.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                    .lineLimit(1)
                if let artist = item.artistName {
                    Text(artist)
                        .font(.system(size: 21))
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            .frame(width: width, alignment: .leading)
        }
    }
}

/// An artist: a round picture with the name beneath.
struct ArtistCard: View {
    let item: ItemSummary
    var size: CGFloat = 220
    /// Shared focus state, so the row can steer focus to this card (see `RowFocus`).
    var focus: FocusState<String?>.Binding? = nil
    let action: () -> Void

    var body: some View {
        VStack(spacing: 18) {
            Button(action: action) {
                SquareArtwork(item: item, size: size, circular: true)
            }
            .buttonStyle(CircleCardStyle())
            .modifier(SharedFocus(binding: focus, id: item.id))
            VStack(spacing: 2) {
                Text(item.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(Color.screen)
                if item.childCount > 0 {
                    Text(item.childCount == 1 ? "1 album" : "\(item.childCount) albums")
                        .font(.system(size: 21))
                        .foregroundStyle(Color.usher)
                }
            }
            .lineLimit(1)
            .frame(width: size + 30)
        }
    }
}

/// One line of an album's tracklist: number (or a speaker while it plays), title and length. Focused it lights up
/// like the other TV buttons.
struct TrackRow: View {
    let track: ItemSummary
    let number: Int
    let isCurrent: Bool
    let isPlaying: Bool
    /// Under the title: the track's own artist, where it isn't the album's.
    var subtitle: String? = nil
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 28) {
                Group {
                    if isCurrent {
                        Image(systemName: isPlaying ? "speaker.wave.2.fill" : "speaker.fill")
                            .font(.system(size: 24, weight: .semibold))
                    } else {
                        Text(String(number))
                            .font(.system(size: 26, weight: .medium))
                            .monospacedDigit()
                    }
                }
                .frame(width: 56, alignment: .trailing)
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .font(.system(size: 28, weight: isCurrent ? .bold : .medium))
                    if let subtitle {
                        Text(subtitle)
                            .font(.system(size: 21, weight: .medium))
                            .opacity(0.7)
                    }
                }
                .lineLimit(1)
                Spacer(minLength: 20)
                if let duration = track.durationSeconds, duration > 0 {
                    Text(Format.timestamp(duration))
                        .font(.system(size: 24, weight: .medium))
                        .monospacedDigit()
                }
            }
        }
        .buttonStyle(TrackRowStyle(isCurrent: isCurrent))
    }
}

struct TrackRowStyle: ButtonStyle {
    var isCurrent: Bool

    func makeBody(configuration: Configuration) -> some View {
        RowBody(configuration: configuration, isCurrent: isCurrent)
    }

    private struct RowBody: View {
        let configuration: Configuration
        let isCurrent: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .padding(.horizontal, 30)
                .frame(height: 80)
                .foregroundStyle(isFocused ? Color.room : (isCurrent ? Color.screen : Color.screen.opacity(0.82)))
                .background {
                    RoundedRectangle(cornerRadius: Radius.panel, style: .continuous)
                        .fill(isFocused ? AnyShapeStyle(Color.screen) : AnyShapeStyle(isCurrent ? Color.screen.opacity(0.12) : Color.clear))
                }
                .scaleEffect(configuration.isPressed ? 0.98 : (isFocused ? 1.02 : 1))
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isFocused)
        }
    }
}

/// A round transport control (previous, play, next, shuffle, repeat): an icon in a circle that fills with projected
/// white on focus. `active` keeps shuffle and repeat lit while they're on.
struct RoundControlStyle: ButtonStyle {
    var size: CGFloat = 96
    var active = false

    func makeBody(configuration: Configuration) -> some View {
        ControlBody(configuration: configuration, size: size, active: active)
    }

    private struct ControlBody: View {
        let configuration: Configuration
        let size: CGFloat
        let active: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .labelStyle(.iconOnly)
                .font(.system(size: size * 0.38, weight: .semibold))
                .foregroundStyle(isFocused ? Color.room : (active ? Color.screen : Color.screen.opacity(0.85)))
                .frame(width: size, height: size)
                .background {
                    Circle().fill(isFocused ? AnyShapeStyle(Color.screen) : AnyShapeStyle(Color.screen.opacity(active ? 0.28 : 0.14)))
                }
                .shadow(color: isFocused ? Color.silver.opacity(0.5) : .clear, radius: 24)
                .scaleEffect(configuration.isPressed ? 0.95 : (isFocused ? 1.08 : 1))
                .animation(.spring(response: 0.3, dampingFraction: 0.7), value: isFocused)
                .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
        }
    }
}

/// Opens the right music screen for an artist, album or track (a track opens its album).
struct MusicDestination: View {
    let item: ItemSummary

    var body: some View {
        switch item.kind {
        case .artist:
            ArtistView(id: item.id, preview: item)
        case .album:
            AlbumView(id: item.id, preview: item)
        default:
            if let album = item.parentId {
                AlbumView(id: album, preview: nil, highlighted: item.id)
            } else {
                StatusView(state: .empty("This track isn't part of an album."))
            }
        }
    }
}
