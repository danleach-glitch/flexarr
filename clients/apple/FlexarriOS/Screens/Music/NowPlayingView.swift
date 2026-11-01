import FlexarrKit
import SwiftUI

/// The music that's playing, as a sheet: the cover, the track's name, a scrubber, the transport controls, AirPlay,
/// and what comes next. A Lyrics button, there when the track has any, swaps the cover and queue for the words,
/// which follow the song. A podcast episode gets a podcast's controls instead: back 15 seconds and forward 30, its
/// speed, a sleep timer and a menu of what can be done with it. The episode's page doesn't scroll: its Notes button
/// swaps the cover for a panel of its chapters (tapping one goes there), its notes (whose times jump there too) and
/// Up Next, with the controls staying put below.
struct NowPlayingView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(DownloadStore.self) private var downloads
    @Environment(\.dismiss) private var dismiss
    @Environment(\.horizontalSizeClass) private var sizeClass
    /// Where the thumb is while the viewer drags it; nil when the track's own position shows.
    @State private var scrubbing: Double?
    @State private var lyrics = LyricsLoader()
    /// Lyrics are wanted; they show while the track that's playing has some.
    @State private var showingLyrics = false
    @State private var playing = PlayingEpisode()
    /// The episode's chapters, notes and Up Next are showing in the cover's place.
    @State private var showingDetails = false
    /// Opened from the chapter's name: the details start at the chapters.
    @State private var openAtChapters = false
    /// The cover's own colour, for a glow when the server gives none.
    @State private var artworkGlow: Color?
    /// A book's speed is chosen on a sheet of its own (0.5× to 3.5× in 0.05 steps).
    @State private var showingSpeed = false
    /// A book's scrubber covers the chapter playing (the default, as in book apps) or the whole book; the Apple TV
    /// keeps the same choice under the same name.
    @AppStorage("books.timelineIsBook") private var wholeBook = false

    private var music: MusicPlayer { model.music }

    var body: some View {
        ZStack {
            RoomBackground(ambient: music.current.map(ambient), intensity: 0.45)
            if let track = music.current {
                if showingLyrics, let found = lyrics.lyrics {
                    lyricsPanel(track, found)
                } else if music.isPodcast || music.isBook {
                    // A book's page is an episode's, with its chapters, bookmarks and offers.
                    podcastPage(track)
                } else {
                    ScrollView(.vertical) {
                        VStack(spacing: 24) {
                            grabber
                            cover(track)
                                .padding(.top, 8)
                            titles(track)
                            scrubber
                            controls
                            extras
                            upNext
                        }
                        .padding(.horizontal, 28)
                        .padding(.bottom, 32)
                        .frame(maxWidth: 560)
                        .frame(maxWidth: .infinity)
                    }
                }
            } else {
                StatusView(state: .empty("Nothing is playing."))
            }
        }
        .task(id: music.current?.id) { await lyrics.load(music.isPodcast || music.isBook ? nil : music.current, client: model.client) }
        .sheet(isPresented: $showingSpeed) {
            BookSpeedSheet()
                .presentationDetents([.height(300)])
                .presentationBackground(Color.room)
        }
        .task(id: music.current?.id) { if music.isPodcast { await model.loadUpNext() } }
        .task(id: music.current?.id) {
            await playing.load(music.isPodcast ? music.current?.id : nil, client: model.client)
        }
        .overlay(alignment: .top) {
            if let notice = music.bookmarkNotice { NoticeCapsule(notice: notice).padding(.top, 28) }
        }
        .animation(.easeInOut(duration: 0.25), value: music.bookmarkNotice)
        .task(id: music.artwork) { artworkGlow = music.artwork?.averageColor.map { Color(rgb: $0.glow) } }
        .onChange(of: music.current == nil) { _, empty in if empty { dismiss() } }
    }

    /// The track's own colour, else its cover's.
    private func ambient(_ track: ItemSummary) -> Color {
        track.accentColor == nil ? artworkGlow ?? track.glowColor : track.glowColor
    }

    /// The episode's chapters, once they've come (and only for the episode playing), or the book's.
    private var chapters: [PodcastChapter] {
        if music.isBook { return music.chapters.count > 1 ? music.chapters.map { PodcastChapter(startSeconds: $0.startSeconds, title: $0.title) } : [] }
        return playing.episode?.id == music.current?.id ? playing.chapters : []
    }

    private var grabber: some View {
        Capsule().fill(Color.aisle).frame(width: 40, height: 5).padding(.top, 10)
    }

    /// An episode's page, fixed in place: the cover (or, with Notes, the episode's details in its place), then the title,
    /// the chapter, the scrubber and the controls.
    private func podcastPage(_ track: ItemSummary) -> some View {
        VStack(spacing: 20) {
            grabber
            if showingDetails {
                smallHeader(track) { withAnimation(.easeInOut(duration: 0.3)) { showingDetails = false } }
                    .padding(.top, 4)
                details
                    .frame(maxHeight: .infinity)
            } else {
                cover(track)
                    .frame(maxHeight: .infinity)
                titles(track)
            }
            bookOffers
            if !chapters.isEmpty { chapterStrip }
            scrubber
            controls
            extras
        }
        .padding(.horizontal, 28)
        .padding(.bottom, 24)
        .frame(maxWidth: 560)
        .frame(maxWidth: .infinity)
    }

    /// The episode's chapters, notes and Up Next, scrolling in the cover's place. Opening it from the chapter's name
    /// goes to the chapters.
    private var details: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 20) {
                    chapterList.id("chapters")
                    if music.isBook {
                        bookmarkList
                    } else {
                        notes
                        upNext
                    }
                }
                .padding(.bottom, 12)
            }
            .scrollIndicators(.hidden)
            .mask(LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: .black, location: 0.03),
                                         .init(color: .black, location: 0.94), .init(color: .clear, location: 1)],
                                 startPoint: .top, endPoint: .bottom))
            .onAppear { if openAtChapters { proxy.scrollTo("chapters", anchor: .top); openAtChapters = false } }
        }
        .transition(.opacity)
    }

    /// The track's cover kept small, beside its name; tapping it puts the big cover back.
    private func smallHeader(_ track: ItemSummary, close: @escaping () -> Void) -> some View {
        Button(action: close) {
            HStack(spacing: 14) {
                artwork(track)
                    .frame(width: 56, height: 56)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .font(.headline)
                        .foregroundStyle(Color.screen)
                    Text(track.seriesTitle ?? track.artistName ?? "")
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.usher)
                }
                .lineLimit(1)
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityHint("Shows the cover")
    }

    /// The words, with the track's name and cover kept small above them and the controls kept below.
    private func lyricsPanel(_ track: ItemSummary, _ found: Lyrics) -> some View {
        VStack(spacing: 20) {
            grabber
            HStack(spacing: 14) {
                artwork(track)
                    .frame(width: 56, height: 56)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .font(.headline)
                        .foregroundStyle(Color.screen)
                    if let artist = track.artistName {
                        Text(artist)
                            .font(.subheadline.weight(.medium))
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.top, 8)
            LyricsView(lyrics: found, position: music.position) { music.seek(to: $0) }
                .frame(maxHeight: .infinity)
            scrubber
            controls
            extras
        }
        .padding(.horizontal, 28)
        .padding(.bottom, 32)
        .frame(maxWidth: 560)
        .frame(maxWidth: .infinity)
    }

    private func artwork(_ track: ItemSummary) -> some View {
        ZStack {
            if let artwork = music.artwork {
                Image(uiImage: artwork).resizable().aspectRatio(contentMode: .fill)
            } else {
                SquareArtwork(item: track, imageWidth: 800)
            }
        }
    }

    private func cover(_ track: ItemSummary) -> some View {
        artwork(track)
            .aspectRatio(1, contentMode: .fit)
            .frame(maxWidth: sizeClass == .regular ? 420 : 320)
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .shadow(color: .black.opacity(0.5), radius: 24, y: 12)
            .shadow(color: track.glowColor.opacity(0.35), radius: 40)
            // Smaller while paused, as every music app does.
            .scaleEffect(music.isPlaying ? 1 : 0.9)
            .animation(.spring(response: 0.45, dampingFraction: 0.8), value: music.isPlaying)
            .animation(.easeOut(duration: 0.3), value: track.id)
    }

    private func titles(_ track: ItemSummary) -> some View {
        VStack(spacing: 4) {
            Text(track.title)
                .font(.title3.weight(.bold))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
                .multilineTextAlignment(.center)
            HStack(spacing: 6) {
                if music.isPodcast, let show = track.seriesTitle {
                    Button { goToPodcast() } label: { Text(show).lineLimit(1) }
                        .buttonStyle(.plain)
                        .accessibilityHint("Opens the podcast")
                } else if music.isBook {
                    Button { goToBook() } label: { Text(track.bookAuthors ?? "").lineLimit(1) }
                        .buttonStyle(.plain)
                        .accessibilityHint("Opens the book")
                } else {
                    Text([track.artistName, music.albumTitle(of: track)].compactMap { $0 }.joined(separator: "  –  "))
                        .lineLimit(1)
                }
                if music.isPlayingOffline {
                    DownloadedMark()
                }
            }
            .font(.subheadline.weight(.medium))
            .foregroundStyle(Color.usher)
        }
    }

    /// What the scrubber spans: a book's chapter playing (unless the whole book was chosen), else the whole track.
    private var scope: (start: Double, length: Double, isChapter: Bool) {
        if music.isBook, !wholeBook, let chapter = music.chapter, music.chapters.count > 1, chapter.durationSeconds > 0 {
            return (chapter.startSeconds, chapter.durationSeconds, true)
        }
        return (0, music.duration, false)
    }

    /// Where the scrubber's thumb is, on the whole timeline.
    private var shownPosition: Double { scrubbing.map { $0 + scope.start } ?? music.position }

    private var scrubber: some View {
        let scope = scope
        let into = max(0, (scrubbing ?? (music.position - scope.start)))
        // A book's time left is at the speed it plays.
        let left = max(0, scope.length - into) / (music.isBook ? max(0.5, music.speed) : 1)
        return VStack(spacing: 2) {
            PlayerScrubber(position: music.position - scope.start, duration: scope.length,
                           chapterStarts: scope.isChapter ? [] : chapters.map(\.startSeconds),
                           step: music.isBook ? music.skipForward : nil, scrubbing: $scrubbing) { music.seek(to: $0 + scope.start) }
            HStack {
                Text(Format.timestamp(into))
                    .frame(maxWidth: .infinity, alignment: .leading)
                if music.isPodcast, music.duration > 0 {
                    // When it'll finish, at the speed it's playing.
                    Text("Ends \(Date().addingTimeInterval(max(0, music.duration - music.position) / max(0.5, music.speed)).formatted(date: .omitted, time: .shortened))")
                } else if music.isBook, music.chapters.count > 1 {
                    // Which the scrubber spans: a tap switches.
                    Button(scope.isChapter ? "Chapter" : "Book") { wholeBook.toggle() }
                        .buttonStyle(.plain)
                        // A full-size target, overlapping the space around it so the row keeps its height.
                        .frame(minWidth: 64, minHeight: 44)
                        .contentShape(Rectangle())
                        .padding(.vertical, -6)
                        .accessibilityLabel(scope.isChapter ? "Showing the chapter" : "Showing the whole book")
                        .accessibilityHint("Switches between the chapter and the whole book")
                }
                Text(scope.length > 0 ? "-" + Format.timestamp(left) : "")
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .font(.caption.weight(.medium))
            .monospacedDigit()
            .foregroundStyle(Color.usher)
        }
    }

    /// The chapter playing, with the ones either side a tap away.
    private var chapterStrip: some View {
        let current = chapters.index(at: shownPosition)
        return HStack(spacing: 4) {
            Button {
                // Back to the start of this chapter, or (near its start already) to the one before.
                guard let current else { return music.seek(to: 0) }
                let start = chapters[current].startSeconds
                music.seek(to: music.position - start > 3 || current == 0 ? start : chapters[current - 1].startSeconds)
            } label: { Label("Previous chapter", systemImage: "chevron.backward") }
            .buttonStyle(ChapterStepStyle())
            Button {
                openAtChapters = !showingDetails
                withAnimation(.easeInOut(duration: 0.3)) { showingDetails = true }
            } label: {
                Text(current.map { chapters[$0].title } ?? chapters[0].title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, minHeight: 36)
                    .contentShape(Rectangle())
                    .contentTransition(.opacity)
                    .animation(.easeOut(duration: 0.2), value: current)
            }
            .buttonStyle(.plain)
            .accessibilityHint("Shows the chapters")
            Button {
                let next = (current ?? -1) + 1
                if chapters.indices.contains(next) { music.seek(to: chapters[next].startSeconds) }
            } label: { Label("Next chapter", systemImage: "chevron.forward") }
            .buttonStyle(ChapterStepStyle())
            .disabled((current ?? -1) + 1 >= chapters.count)
        }
        .padding(.horizontal, 4)
        .background(Capsule().fill(Color.screen.opacity(0.08)))
    }

    @ViewBuilder
    private var controls: some View {
        if music.isPodcast || music.isBook { podcastControls } else { musicControls }
    }

    /// Back and forward: a podcast's 15 and 30 seconds, a book's chosen steps.
    private var podcastControls: some View {
        let back = Int(music.skipBack), forward = Int(music.skipForward)
        return HStack(spacing: 0) {
            Spacer()
            Button { music.skip(by: -music.skipBack) } label: { Label("Back \(back) seconds", systemImage: "gobackward.\(back)") }
                .buttonStyle(TransportStyle(size: 56))
            Spacer()
            playButton
            Spacer()
            Button { music.skip(by: music.skipForward) } label: { Label("Forward \(forward) seconds", systemImage: "goforward.\(forward)") }
                .buttonStyle(TransportStyle(size: 56))
            Spacer()
        }
    }

    /// "Jump to 2:14:05?" when another device got further, and the series' next book once this one ends.
    @ViewBuilder
    private var bookOffers: some View {
        if let offer = music.jumpOffer {
            offerBar(text: "\(offer.device ?? "Elsewhere"): \(Format.timestamp(offer.positionSeconds))", action: "Jump", icon: "arrow.uturn.forward") {
                music.acceptJump()
            } dismiss: { music.dismissJump() }
        } else if let next = music.nextBookOffer {
            offerBar(text: next.title, action: "Play", icon: "play.fill") { music.playNextBook() } dismiss: { music.dismissNextBook() }
        }
    }

    private func offerBar(text: String, action: String, icon: String, act: @escaping () -> Void, dismiss: @escaping () -> Void) -> some View {
        HStack(spacing: 10) {
            Text(text)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Color.screen)
                .lineLimit(1)
                .monospacedDigit()
            Spacer(minLength: 0)
            Button(action: act) { Label(action, systemImage: icon).font(.subheadline.weight(.bold)) }
                .buttonStyle(.borderedProminent)
                .tint(Color.silver)
                .foregroundStyle(Color.room)
            Button(action: dismiss) {
                Image(systemName: "xmark")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.usher)
                    .frame(width: 44, height: 44)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Not now")
        }
        .padding(.leading, 16)
        .padding(.trailing, 2)
        .padding(.vertical, 2)
        .background(Capsule().fill(Color.screen.opacity(0.1)))
        .transition(.opacity)
    }

    private var playButton: some View {
        Button { music.togglePlayPause() } label: {
            if music.isLoading {
                ProgressView().tint(Color.screen)
            } else {
                Label(music.isPlaying ? "Pause" : "Play", systemImage: music.isPlaying ? "pause.fill" : "play.fill")
            }
        }
        .buttonStyle(TransportStyle(size: 76, filled: true))
    }

    private var musicControls: some View {
        HStack(spacing: 0) {
            Button { music.toggleShuffle() } label: { Label("Shuffle", systemImage: "shuffle") }
                .buttonStyle(TransportStyle(size: 44, active: music.isShuffled))
            Spacer()
            Button { music.previous() } label: { Label("Previous", systemImage: "backward.fill") }
                .buttonStyle(TransportStyle(size: 56))
            Spacer()
            playButton
            Spacer()
            Button { music.next() } label: { Label("Next", systemImage: "forward.fill") }
                .buttonStyle(TransportStyle(size: 56))
                .disabled(!music.hasNext)
            Spacer()
            Button { music.cycleRepeat() } label: {
                Label("Repeat", systemImage: music.repeatMode == .one ? "repeat.1" : "repeat")
            }
            .buttonStyle(TransportStyle(size: 44, active: music.repeatMode != .off))
        }
    }

    @ViewBuilder
    private var extras: some View {
        if music.isPodcast || music.isBook { podcastExtras } else { musicExtras }
    }

    /// AirPlay, and Lyrics when the track has some.
    private var musicExtras: some View {
        HStack(spacing: 36) {
            Spacer()
            if !GuestMode.isActive { RoutePicker().frame(width: 44, height: 44) }
            if lyrics.lyrics != nil {
                Button { showingLyrics.toggle() } label: { Label("Lyrics", systemImage: "quote.bubble") }
                    .buttonStyle(TransportStyle(size: 44, active: showingLyrics))
            }
            Spacer()
        }
    }

    /// Speed, the sleep timer, Notes, AirPlay and the episode's menu: five of a kind, evenly spaced, each lit while it's set.
    private var podcastExtras: some View {
        HStack(spacing: 0) {
            speedMenu.frame(maxWidth: .infinity)
            sleepMenu.frame(maxWidth: .infinity)
            Button {
                withAnimation(.easeInOut(duration: 0.3)) { showingDetails.toggle() }
            } label: {
                Image(systemName: "text.page")
                    .font(.system(size: 18, weight: .medium))
                    .modifier(ExtraLabel(active: showingDetails))
            }
            .buttonStyle(.plain)
            .accessibilityLabel(showingDetails ? "Hide notes" : music.isBook ? "Chapters and bookmarks" : "Notes")
            .frame(maxWidth: .infinity)
            if !GuestMode.isActive { RoutePicker().frame(width: 44, height: 44).frame(maxWidth: .infinity) }
            if music.isBook { bookMenu.frame(maxWidth: .infinity) } else { episodeMenu.frame(maxWidth: .infinity) }
        }
    }

    @ViewBuilder
    private var speedMenu: some View {
        if music.isBook {
            Button { showingSpeed = true } label: {
                Text(BookSpeed.label(music.speed))
                    .font(.system(size: 16, weight: .semibold))
                    .monospacedDigit()
                    .modifier(ExtraLabel(active: music.speed != 1))
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Speed, \(BookSpeed.label(music.speed))")
        } else {
            podcastSpeedMenu
        }
    }

    /// What can be done with the book: a bookmark here, its page, finished, its download.
    private var bookMenu: some View {
        Menu {
            if let track = music.current {
                Button { Task { await music.addBookmark() } } label: { Label("Add Bookmark", systemImage: "bookmark") }
                Button { goToBook() } label: { Label("Go to Book", systemImage: "book") }
                if !model.isKids {
                    Button {
                        Task {
                            await model.setBookFinished(track, true)
                            music.stop()
                        }
                    } label: { Label("Mark as Finished", systemImage: "checkmark.circle") }
                }
            }
        } label: {
            Image(systemName: "ellipsis")
                .font(.system(size: 18, weight: .semibold))
                .modifier(ExtraLabel(active: false))
        }
        .accessibilityLabel("More")
    }

    /// Closes Now Playing and opens the book's page.
    private func goToBook() {
        guard let track = music.current else { return }
        dismiss()
        router.push(.item(track))
    }

    /// The book's bookmarks: tapping one goes there; a long press deletes it.
    @ViewBuilder
    private var bookmarkList: some View {
        let marks = music.bookmarks.sorted { $0.positionSeconds < $1.positionSeconds }
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                sectionTitle("Bookmarks")
                Spacer()
                Button { Task { await music.addBookmark() } } label: { Label("Add", systemImage: "plus") }
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.usher)
                    .padding(.top, 8)
                    .padding(.trailing, 12)
            }
            ForEach(marks) { mark in
                Button { music.seek(to: mark.positionSeconds) } label: {
                    HStack(alignment: .firstTextBaseline, spacing: 12) {
                        Text(Format.timestamp(mark.positionSeconds))
                            .font(.caption.weight(.semibold))
                            .monospacedDigit()
                            .foregroundStyle(Color.usher)
                            .frame(minWidth: 52, alignment: .leading)
                        Text(mark.note ?? music.book?.timeline.chapter(at: mark.positionSeconds)?.title ?? "Bookmark")
                            .font(.subheadline)
                            .foregroundStyle(Color.screen.opacity(0.85))
                            .multilineTextAlignment(.leading)
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .contextMenu {
                    Button(role: .destructive) { Task { await music.deleteBookmark(mark) } } label: { Label("Delete", systemImage: "trash") }
                }
            }
        }
    }

    private var podcastSpeedMenu: some View {
        Menu {
            Picker("Speed", selection: Binding(get: { music.speed }, set: { music.setSpeed($0) })) {
                // A speed chosen elsewhere that isn't one of the choices is offered too.
                ForEach(BookSpeed.choices(with: music.speed), id: \.self) { Text(speedLabel($0)).tag($0) }
            }
        } label: {
            Text(speedLabel(music.speed))
                .font(.system(size: 16, weight: .semibold))
                .monospacedDigit()
                .modifier(ExtraLabel(active: music.speed != 1))
        }
        .accessibilityLabel("Speed, \(speedLabel(music.speed))")
    }

    private var sleepMenu: some View {
        Menu {
            Picker("Sleep timer", selection: Binding(get: { music.sleep.setting }, set: { music.sleep.set($0) })) {
                Text("Off").tag(SleepTimer.Setting?.none)
                ForEach(SleepTimer.minuteChoices, id: \.self) { Text(SleepTimer.Setting.minutes($0).label).tag(SleepTimer.Setting?.some(.minutes($0))) }
                let end: SleepTimer.Setting = music.isBook ? .endOfChapter : .endOfEpisode
                Text(end.label).tag(SleepTimer.Setting?.some(end))
            }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: "moon.zzz")
                if let end = music.sleep.endsAt {
                    // Minutes left, counting down.
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Text(Format.timestamp(max(0, end.timeIntervalSince(context.date))))
                            .font(.caption.weight(.semibold))
                            .monospacedDigit()
                    }
                }
            }
            .font(.system(size: 18, weight: .medium))
            .modifier(ExtraLabel(active: music.sleep.setting != nil))
        }
        .accessibilityLabel(music.sleep.setting == nil ? "Sleep timer" : "Sleep timer on")
    }

    /// What can be done with the episode: open its show, mark it played, download it, share the show.
    private var episodeMenu: some View {
        Menu {
            if let track = music.current {
                Button { goToPodcast() } label: { Label("Go to Podcast", systemImage: "square.stack") }
                Button {
                    Task {
                        await model.setWatched(track, true)
                        // Played: on to what comes next, as if it had finished.
                        music.seek(to: max(0, music.duration - 0.5))
                    }
                } label: { Label("Mark as Played", systemImage: "checkmark.circle") }
                if let download = downloads.entry(for: track.id) {
                    Button(role: .destructive) { downloads.remove(download.id) } label: {
                        Label(download.isDownloaded ? "Remove Download" : "Stop Download", systemImage: "trash")
                    }
                } else if !music.isPlayingOffline {
                    Button { Task { try? await downloads.download(itemId: track.id, quality: .original) } } label: {
                        Label("Download", systemImage: "arrow.down.circle")
                    }
                }
                if let link = playing.podcast?.link.flatMap(URL.init(string:)) {
                    ShareLink(item: link) { Label("Share Podcast", systemImage: "square.and.arrow.up") }
                }
            }
        } label: {
            Image(systemName: "ellipsis")
                .font(.system(size: 18, weight: .semibold))
                .modifier(ExtraLabel(active: false))
        }
        .accessibilityLabel("More")
    }

    /// Closes Now Playing and opens the show.
    private func goToPodcast() {
        guard let podcastId = music.current?.seriesId, let client = model.client else { return }
        Task {
            let podcast: Podcast? = if let loaded = playing.podcast, loaded.id == podcastId { loaded } else { try? await client.podcast(podcastId) }
            guard let podcast else { return }
            dismiss()
            router.push(.item(podcast.summary))
        }
    }

    /// The episode's chapters: tapping one goes there; the one playing is lit, and those heard are dimmed.
    @ViewBuilder
    private var chapterList: some View {
        if !chapters.isEmpty {
            let current = chapters.index(at: music.position)
            VStack(alignment: .leading, spacing: 2) {
                sectionTitle("Chapters")
                ForEach(Array(chapters.enumerated()), id: \.offset) { offset, chapter in
                    Button { music.seek(to: chapter.startSeconds) } label: {
                        HStack(alignment: .firstTextBaseline, spacing: 12) {
                            Text(Format.timestamp(chapter.startSeconds))
                                .font(.caption.weight(.semibold))
                                .monospacedDigit()
                                .foregroundStyle(Color.usher)
                                .frame(minWidth: 44, alignment: .leading)
                            Text(chapter.title)
                                .font(.subheadline.weight(offset == current ? .bold : .regular))
                                .foregroundStyle(offset == current ? Color.screen : Color.screen.opacity((current ?? -1) > offset ? 0.55 : 0.85))
                                .multilineTextAlignment(.leading)
                            Spacer(minLength: 0)
                            if offset == current {
                                Image(systemName: music.isPlaying ? "waveform" : "pause.fill")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(Color.silver)
                                    .symbolEffect(.variableColor.iterative, isActive: music.isPlaying)
                            }
                        }
                        .padding(.horizontal, 12)
                        .padding(.vertical, 8)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(chapter.title), at \(Format.timestamp(chapter.startSeconds))")
                    .accessibilityAddTraits(offset == current ? .isSelected : [])
                }
            }
        }
    }

    /// The episode's notes: when it came out and how long it is, then the notes, whose times jump there and whose web
    /// addresses open.
    @ViewBuilder
    private var notes: some View {
        if let episode = playing.episode, episode.id == music.current?.id {
            VStack(alignment: .leading, spacing: 8) {
                sectionTitle("Episode Notes")
                FactsLine(facts: [episode.dateLine, episode.durationSeconds.flatMap(Format.runtime(seconds:)), episode.explicit ? "Explicit" : nil].compactMap { $0 })
                    .font(.caption)
                    .padding(.horizontal, 12)
                if let text = episode.overview {
                    Text(episodeNotes(text, duration: episode.durationSeconds))
                        .font(.subheadline)
                        .foregroundStyle(Color.screen.opacity(0.85))
                        .tint(Color.silver)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                        .seeksFromNotes { music.seek(to: $0) }
                        .padding(.horizontal, 12)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func sectionTitle(_ title: String) -> some View {
        Text(title)
            .font(.marquee(22, weight: .bold))
            .foregroundStyle(Color.screen)
            .padding(.horizontal, 12)
            .padding(.top, 8)
    }

    @ViewBuilder
    private var upNext: some View {
        if music.isPodcast { podcastUpNext } else { musicUpNext }
    }

    /// A podcast's Up Next, as the server keeps it: tapping one plays it.
    @ViewBuilder
    private var podcastUpNext: some View {
        let next = model.upNext.filter { $0.id != music.current?.id }
        if !next.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                sectionTitle("Up Next")
                ForEach(next.prefix(20)) { episode in
                    Button { model.playEpisode(episode.summary) } label: {
                        HStack(spacing: 12) {
                            PodcastArtwork(path: episode.artwork.thumb ?? episode.artwork.poster, title: episode.podcastTitle,
                                           accent: episode.accentColor, imageWidth: 185, cornerRadius: 6)
                                .frame(width: 44, height: 44)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(episode.title)
                                    .font(.subheadline.weight(.semibold))
                                    .foregroundStyle(Color.screen)
                                Text(episode.podcastTitle)
                                    .font(.caption)
                                    .foregroundStyle(Color.usher)
                            }
                            .lineLimit(1)
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    @ViewBuilder
    private var musicUpNext: some View {
        let next = Array(music.upNext.prefix(20))
        if !next.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                Text("Up next")
                    .font(.marquee(22, weight: .bold))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 12)
                    .padding(.top, 8)
                ForEach(Array(next.enumerated()), id: \.element.id) { offset, track in
                    TrackRow(track: track, number: offset + 1, isCurrent: false, isPlaying: false) {
                        music.skip(to: music.index + 1 + offset)
                    }
                }
            }
        }
    }
}

/// Transport controls: an icon alone, or on a projected-white disc for Play. `active` lights shuffle and repeat.
struct TransportStyle: ButtonStyle {
    var size: CGFloat
    var filled = false
    var active = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .labelStyle(.iconOnly)
            .font(.system(size: size * (filled ? 0.42 : 0.5), weight: .semibold))
            .foregroundStyle(filled ? Color.room : (active ? Color.silver : Color.screen))
            .frame(width: size, height: size)
            .background {
                if filled { Circle().fill(Color.screen) }
            }
            .opacity(configuration.isPressed ? 0.7 : 1)
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// The podcast row's controls (speed, sleep timer, the menu): the same size and weight, lit with a soft pill while set.
struct ExtraLabel: ViewModifier {
    var active: Bool

    func body(content: Content) -> some View {
        content
            .foregroundStyle(active ? Color.room : Color.screen.opacity(0.85))
            .padding(.horizontal, 10)
            .frame(minWidth: 44, minHeight: 34)
            .background { if active { Capsule().fill(Color.silver) } }
            .frame(minHeight: 44)
            .contentShape(Rectangle())
    }
}

/// The chevrons either side of the chapter's name.
private struct ChapterStepStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .labelStyle(.iconOnly)
            .font(.system(size: 14, weight: .semibold))
            .foregroundStyle(Color.screen.opacity(isEnabled ? 0.85 : 0.3))
            .frame(width: 40, height: 36)
            .contentShape(Rectangle())
            .opacity(configuration.isPressed ? 0.6 : 1)
    }
}
