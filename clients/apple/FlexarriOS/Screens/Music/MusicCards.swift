import AVKit
import FlexarrKit
import SwiftUI

/// Album covers and artist pictures are square; the title stands in on a wash of the item's colour until one arrives.
/// Fills the width it's given.
struct SquareArtwork: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var circular = false
    var imageWidth = 500

    var body: some View {
        Color.seat
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                RemoteImage(url: model.client?.imageURL(item.artwork.poster, width: imageWidth)) {
                    ZStack {
                        LinearGradient(colors: [item.glowColor.opacity(0.4), Color.seat], startPoint: .top, endPoint: .bottom)
                        Text(circular ? initials : item.title)
                            .font(.marquee(circular ? 28 : 16, weight: .bold, scales: false))
                            .foregroundStyle(Color.screen.opacity(0.8))
                            .multilineTextAlignment(.center)
                            .padding(8)
                    }
                }
            }
            .clipShape(circular ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous)))
    }

    private var initials: String {
        item.title.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
    }
}

/// An album: square cover with its title and year (or artist) beneath, marked when it's downloaded; tapping opens it.
struct AlbumCard: View {
    @Environment(Router.self) private var router
    @Environment(DownloadStore.self) private var downloads
    let item: ItemSummary
    var width: CGFloat?
    /// The artist under the title, where the albums are from several; the year on an artist's own page.
    var showsArtist = false

    var body: some View {
        Button { router.push(.item(item)) } label: {
            VStack(alignment: .leading, spacing: 6) {
                SquareArtwork(item: item)
                    .overlay(alignment: .bottomTrailing) {
                        if downloads.isDownloaded(collectionId: item.id) {
                            DownloadedMark()
                                .padding(4)
                                .background(Circle().fill(Color.room.opacity(0.85)))
                                .padding(5)
                        }
                    }
                VStack(alignment: .leading, spacing: 1) {
                    Text(item.title)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    if let subtitle {
                        Text(subtitle)
                            .font(.caption)
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
    }

    private var subtitle: String? {
        if showsArtist, let artist = item.artistName { return artist }
        return item.releaseYear.map(String.init)
    }
}

/// A track in a Home row: its cover with a play mark, the name and the artist beneath. Tapping plays.
struct TrackCard: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var width: CGFloat?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 6) {
                SquareArtwork(item: item)
                    .overlay(alignment: .bottomTrailing) {
                        Image(systemName: model.music.current?.id == item.id && model.music.isPlaying ? "speaker.wave.2.fill" : "play.fill")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(Color.room)
                            .frame(width: 28, height: 28)
                            .background(Circle().fill(Color.screen.opacity(0.92)))
                            .padding(6)
                    }
                VStack(alignment: .leading, spacing: 1) {
                    Text(item.title)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    if let artist = item.artistName {
                        Text(artist)
                            .font(.caption)
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
    }
}

/// An artist: a round picture with the name beneath; tapping opens them.
struct ArtistCard: View {
    @Environment(Router.self) private var router
    let item: ItemSummary
    var width: CGFloat?

    var body: some View {
        Button { router.push(.item(item)) } label: {
            VStack(spacing: 8) {
                SquareArtwork(item: item, circular: true)
                    .padding(.horizontal, 6)
                VStack(spacing: 1) {
                    Text(item.title)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen)
                    if item.childCount > 0 {
                        Text(item.childCount == 1 ? "1 album" : "\(item.childCount) albums")
                            .font(.caption)
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
                .multilineTextAlignment(.center)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
    }
}

/// One line of an album's tracklist: number (or a speaker while it plays), title, a mark if it's downloaded, and length.
struct TrackRow: View {
    let track: ItemSummary
    let number: Int
    let isCurrent: Bool
    let isPlaying: Bool
    var isDownloaded = false
    /// Not playable here (a track of a download that hasn't arrived).
    var isDimmed = false
    /// Under the title: the track's own artist, where it isn't the album's.
    var subtitle: String? = nil
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 14) {
                Group {
                    if isCurrent {
                        Image(systemName: isPlaying ? "speaker.wave.2.fill" : "speaker.fill")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Color.screen)
                    } else {
                        Text(String(number))
                            .font(.subheadline.weight(.medium))
                            .monospacedDigit()
                            .foregroundStyle(Color.usher)
                    }
                }
                .frame(width: 28, alignment: .trailing)
                VStack(alignment: .leading, spacing: 1) {
                    Text(track.title)
                        .font(.body.weight(isCurrent ? .semibold : .regular))
                        .foregroundStyle(Color.screen)
                    if let subtitle {
                        Text(subtitle)
                            .font(.caption)
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
                Spacer(minLength: 12)
                if isDownloaded {
                    DownloadedMark()
                }
                if let duration = track.durationSeconds, duration > 0 {
                    Text(Format.timestamp(duration))
                        .font(.subheadline)
                        .monospacedDigit()
                        .foregroundStyle(Color.usher)
                }
            }
            .padding(.vertical, 12)
            .padding(.horizontal, 12)
            .background {
                if isCurrent {
                    RoundedRectangle(cornerRadius: Radius.panel, style: .continuous).fill(Color.screen.opacity(0.1))
                }
            }
            .opacity(isDimmed ? 0.45 : 1)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
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
                AlbumView(id: album, preview: nil)
            } else {
                StatusView(state: .empty("This track isn't part of an album."))
            }
        }
    }
}

/// The system's AirPlay and output picker, as a plain icon button.
struct RoutePicker: UIViewRepresentable {
    func makeUIView(context: Context) -> AVRoutePickerView {
        let picker = AVRoutePickerView()
        picker.tintColor = UIColor(Color.screen)
        picker.activeTintColor = UIColor(Color.silver)
        picker.backgroundColor = .clear
        return picker
    }

    func updateUIView(_ uiView: AVRoutePickerView, context: Context) {}
}
