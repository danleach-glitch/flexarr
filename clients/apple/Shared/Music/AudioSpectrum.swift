import Accelerate
import AVFoundation
import MediaToolbox
import os

/// How loud the bass, middle and treble of the music are as it plays (each 0 to 1, on a decibel scale), for the
/// visualiser. An audio tap on the player item hands over each stretch of sound as it's played; the newest 1024
/// samples go through an FFT on that same audio thread (a few microseconds) and the bands are left for the screen to
/// read when it next draws.
///
/// Only a file can be tapped: a stream the server converts (HLS) has no audio track to tap, so it gets none and the
/// visualiser drifts on its own.
final class AudioSpectrum: Sendable {
    struct Bands: Sendable {
        var bass: Float
        var mid: Float
        var high: Float
    }

    /// Whether the visualiser is on, a setting of this device's own. On until turned off.
    static var isOn: Bool {
        get { UserDefaults.standard.object(forKey: "FlexarrMusicVisualiser") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "FlexarrMusicVisualiser") }
    }

    private let latest = OSAllocatedUnfairLock<Bands?>(initialState: nil)

    /// The bands of what was played last; nil before a tapped item has played anything.
    var bands: Bands? { latest.withLock { $0 } }

    /// Taps `item`'s audio, replacing what the last item left. Does nothing for an item with no audio track to tap.
    @MainActor
    func tap(_ item: AVPlayerItem) async {
        latest.withLock { $0 = nil }
        guard let track = try? await item.asset.loadTracks(withMediaType: .audio).first else { return }
        let analyser = Analyser { [latest] bands in latest.withLock { $0 = bands } }
        var callbacks = MTAudioProcessingTapCallbacks(
            version: kMTAudioProcessingTapCallbacksVersion_0,
            clientInfo: Unmanaged.passRetained(analyser).toOpaque(),
            init: { _, clientInfo, storage in storage.pointee = clientInfo },
            finalize: { tap in Unmanaged<Analyser>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).release() },
            prepare: { tap, _, format in
                Unmanaged<Analyser>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue().prepare(format.pointee)
            },
            unprepare: nil,
            process: { tap, frames, _, buffers, framesOut, flagsOut in
                guard MTAudioProcessingTapGetSourceAudio(tap, frames, buffers, flagsOut, nil, framesOut) == noErr else { return }
                Unmanaged<Analyser>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                    .process(UnsafeMutableAudioBufferListPointer(buffers), frames: Int(framesOut.pointee))
            }
        )
        var made: MTAudioProcessingTap?
        guard MTAudioProcessingTapCreate(kCFAllocatorDefault, &callbacks, kMTAudioProcessingTapCreationFlag_PostEffects, &made) == noErr,
              let made else {
            Unmanaged.passUnretained(analyser).release() // The tap never took it, so finalize won't let it go.
            return
        }
        let parameters = AVMutableAudioMixInputParameters(track: track)
        parameters.audioTapProcessor = made
        let mix = AVMutableAudioMix()
        mix.inputParameters = [parameters]
        item.audioMix = mix
    }
}

/// The FFT and its buffers, made once per tap and used only on the audio thread: nothing here allocates as it runs.
private final class Analyser: @unchecked Sendable {
    private static let size = 1024
    private let publish: (AudioSpectrum.Bands) -> Void
    private let fft = vDSP.FFT(log2n: 10, radix: .radix2, ofType: DSPSplitComplex.self)
    private let window = vDSP.window(ofType: Float.self, usingSequence: .hanningDenormalized, count: size, isHalfWindow: false)
    private let samples = UnsafeMutableBufferPointer<Float>.allocate(capacity: size)
    private let real = UnsafeMutableBufferPointer<Float>.allocate(capacity: size / 2)
    private let imaginary = UnsafeMutableBufferPointer<Float>.allocate(capacity: size / 2)
    private let magnitudes = UnsafeMutableBufferPointer<Float>.allocate(capacity: size / 2)
    private var sampleRate: Double = 44100
    private var readable = false

    init(publish: @escaping (AudioSpectrum.Bands) -> Void) {
        self.publish = publish
        samples.initialize(repeating: 0)
        real.initialize(repeating: 0)
        imaginary.initialize(repeating: 0)
        magnitudes.initialize(repeating: 0)
    }

    deinit {
        samples.deallocate()
        real.deallocate()
        imaginary.deallocate()
        magnitudes.deallocate()
    }

    func prepare(_ format: AudioStreamBasicDescription) {
        sampleRate = format.mSampleRate
        // AVPlayer hands taps 32-bit float, a buffer per channel; anything else is left alone rather than misread.
        readable = format.mFormatFlags & kAudioFormatFlagIsFloat != 0 && format.mBitsPerChannel == 32
            && format.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
    }

    func process(_ buffers: UnsafeMutableAudioBufferListPointer, frames: Int) {
        guard readable, frames > 0, let fft, let first = buffers.first?.mData?.assumingMemoryBound(to: Float.self) else { return }
        let size = Self.size
        // The newest `size` samples of the first channel (enough for the bands), the older ones moved along.
        let fresh = min(frames, size)
        let kept = size - fresh
        if kept > 0 { samples.baseAddress!.update(from: samples.baseAddress! + fresh, count: kept) }
        (samples.baseAddress! + kept).update(from: first + (frames - fresh), count: fresh)

        var windowed = samples // The same memory: vDSP windows it in place.
        vDSP.multiply(windowed, window, result: &windowed)
        var split = DSPSplitComplex(realp: real.baseAddress!, imagp: imaginary.baseAddress!)
        samples.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: size / 2) {
            vDSP_ctoz($0, 2, &split, 1, vDSP_Length(size / 2))
        }
        fft.forward(input: split, output: &split)
        var levels = magnitudes
        vDSP.absolute(split, result: &levels)

        let hzPerBin = sampleRate / Double(size)
        func band(_ low: Double, _ high: Double) -> Float {
            let from = max(1, Int(low / hzPerBin))
            let to = min(size / 2 - 1, Int((high / hzPerBin).rounded(.up)))
            guard to >= from else { return 0 }
            var sum: Float = 0
            for i in from...to {
                // As a browser's analyser scales it: -100 dB is nothing, -30 dB is full.
                let db = 20 * log10(max(magnitudes[i] / Float(size), 1e-9))
                sum += min(1, max(0, (db + 100) / 70))
            }
            return sum / Float(to - from + 1)
        }
        publish(AudioSpectrum.Bands(bass: band(30, 160), mid: band(300, 2000), high: band(3000, 10000)))
    }
}
