using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;

namespace OnAirNative.Services;

/// <summary>
/// Manages audio capture via WASAPI.
/// Supports microphone (WasapiCapture), system-audio loopback (WasapiLoopbackCapture)
/// and a real-time mix of both.
/// Captured audio is buffered in memory and returned as a WAV byte array.
/// Also provides a lightweight voice-monitor mode for voice-activated scroll (RMS callback).
/// </summary>
public sealed class AudioService : IDisposable
{
    // ── Device enumeration ────────────────────────────────────────────────────

    public record AudioDeviceInfo(string Id, string Name);

    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
                .ToList();
        }
        catch { return []; }
    }

    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
                .ToList();
        }
        catch { return []; }
    }

    // ── Recording state ───────────────────────────────────────────────────────

    // Whisper expects 16 kHz mono; the mixer renders straight to that format so no
    // downstream resampling is needed.
    private const int MixSampleRate = 16000;

    // Summing two live sources can clip. Attenuate each leg before mixing.
    private const float MixLegGain = 0.8f;

    private IWaveIn?      _capture;
    private IWaveIn?      _loopbackCapture;   // second leg, only used by source = "both"
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private bool          _recording;

    // Guards _writer/_buffer: written from the NAudio callback thread (mic-only path)
    // or the mixer pump thread (StartMixedRecording), and now also read concurrently
    // by PeekRecordedAudio (called from a UI-thread timer for the live preview feature)
    // without stopping the recording. MemoryStream itself is not safe for concurrent
    // read+write, so every touch of _writer/_buffer takes this lock.
    private readonly object _bufferLock = new();

    private CancellationTokenSource? _mixPumpCts;
    private Task?                    _mixPumpTask;

    public bool IsRecording => _recording;

    /// <param name="source">"microphone" | "system" | "both" (mic + system audio mixed)</param>
    /// <param name="deviceId">Input (microphone) device ID — ignored for source="system".</param>
    /// <param name="outputDeviceId">Which playback/render device "system" loopback listens to —
    /// e.g. a virtual audio cable/mixer channel (Elgato Wave Link, VoiceMeeter, etc.) so only
    /// THAT device's audio is captured instead of whatever the OS default playback device is.
    /// Empty/null falls back to the OS default render device.</param>
    public Task StartRecordingAsync(string source = "microphone", string? deviceId = null, string? outputDeviceId = null)
    {
        if (_recording) return Task.CompletedTask;

        StopVoiceMonitor();

        if (source == "both")
        {
            StartMixedRecording(deviceId, outputDeviceId);
            return Task.CompletedTask;
        }

        _capture = source == "system"
            ? CreateLoopbackCapture(outputDeviceId)
            : CreateCapture(deviceId);

        _buffer = new MemoryStream();
        _writer = new WaveFileWriter(_buffer, _capture.WaveFormat);

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;
            lock (_bufferLock) { _writer?.Write(e.Buffer, 0, e.BytesRecorded); }
        };

        _capture.RecordingStopped += (_, _) => { /* handled in StopRecordingAsync */ };

        _recording = true;
        _capture.StartRecording();
        return Task.CompletedTask;
    }

    // ── Mixed capture (mic + system loopback) ─────────────────────────────────

    /// <summary>
    /// Captures the microphone and the system loopback simultaneously and mixes them
    /// into a single 16 kHz mono 16-bit PCM stream.
    ///
    /// Each capture feeds a <see cref="BufferedWaveProvider"/>; both are downmixed to
    /// mono, resampled to 16 kHz and summed by a <see cref="MixingSampleProvider"/>.
    /// A background pump drains the mixer at real-time pace — WASAPI loopback delivers
    /// no buffers while nothing is playing, so we cannot pace off buffered bytes;
    /// <c>ReadFully</c> pads the silent leg with zeros instead of stalling the mix.
    /// </summary>
    private void StartMixedRecording(string? deviceId, string? outputDeviceId)
    {
        _capture         = CreateCapture(deviceId);
        _loopbackCapture = CreateLoopbackCapture(outputDeviceId);

        var micBuffer  = CreateLegBuffer(_capture.WaveFormat);
        var loopBuffer = CreateLegBuffer(_loopbackCapture.WaveFormat);

        var mixer = new MixingSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, 1))
        {
            // Keep producing samples even when one leg has no data queued
            ReadFully = true,
        };
        mixer.AddMixerInput(ToMixFormat(micBuffer));
        mixer.AddMixerInput(ToMixFormat(loopBuffer));

        var pcm = new SampleToWaveProvider16(mixer);

        _buffer = new MemoryStream();
        _writer = new WaveFileWriter(_buffer, pcm.WaveFormat);

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
        _loopbackCapture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) loopBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        _recording = true;
        _capture.StartRecording();
        _loopbackCapture.StartRecording();

        _mixPumpCts  = new CancellationTokenSource();
        _mixPumpTask = Task.Run(() => PumpMixer(pcm, _mixPumpCts.Token));
    }

    private static BufferedWaveProvider CreateLegBuffer(WaveFormat format) => new(format)
    {
        BufferDuration        = TimeSpan.FromSeconds(10),
        DiscardOnBufferOverflow = true,
    };

    /// <summary>Downmixes to mono and resamples to <see cref="MixSampleRate"/>.</summary>
    private static ISampleProvider ToMixFormat(BufferedWaveProvider source)
    {
        ISampleProvider sp = source.ToSampleProvider();

        if (sp.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (sp.WaveFormat.Channels > 2)
            sp = new MultiplexingSampleProvider([sp], 1);

        if (sp.WaveFormat.SampleRate != MixSampleRate)
            sp = new WdlResamplingSampleProvider(sp, MixSampleRate);

        return new VolumeSampleProvider(sp) { Volume = MixLegGain };
    }

    /// <summary>
    /// Drains the mixer at wall-clock pace and writes the result to the WAV writer.
    /// Reading faster than real time would burn through the buffers and pad the take
    /// with silence, so the amount read is derived from elapsed time.
    /// </summary>
    private void PumpMixer(IWaveProvider pcm, CancellationToken token)
    {
        var bytesPerSecond = pcm.WaveFormat.AverageBytesPerSecond;
        var block          = pcm.WaveFormat.BlockAlign;
        var chunk          = new byte[bytesPerSecond / 5]; // 200 ms
        var clock          = Stopwatch.StartNew();
        long written       = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                long target = (long)(clock.Elapsed.TotalSeconds * bytesPerSecond);
                target -= target % block;

                while (written < target && !token.IsCancellationRequested)
                {
                    int want = (int)Math.Min(chunk.Length, target - written);
                    want -= want % block;
                    if (want <= 0) break;

                    int read = pcm.Read(chunk, 0, want);
                    if (read <= 0) break;

                    lock (_bufferLock) { _writer?.Write(chunk, 0, read); }
                    written += read;
                }

                Thread.Sleep(50);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] Mix pump stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops recording and returns the captured audio as a WAV byte array.
    /// Returns an empty array if nothing was recorded.
    /// </summary>
    public async Task<byte[]> StopRecordingAsync()
    {
        if (!_recording || _capture is null) return [];

        _capture.StopRecording();
        _loopbackCapture?.StopRecording();
        _recording = false;

        // Give the device a moment to flush the last buffer
        await Task.Delay(150);

        // Let the mix pump drain what the captures just flushed, then stop it
        if (_mixPumpCts is not null)
        {
            _mixPumpCts.Cancel();
            try { if (_mixPumpTask is not null) await _mixPumpTask; } catch { /* pump is best-effort */ }
        }

        byte[] data;
        lock (_bufferLock)
        {
            _writer?.Flush();
            data = _buffer?.ToArray() ?? [];
        }

        CleanupCapture();
        return data;
    }

    /// <summary>
    /// Returns a snapshot of everything captured so far, as a standalone valid WAV byte
    /// array — without stopping the recording. Used for the live transcript preview (Q&amp;A
    /// recording + local Whisper only; see <see cref="WhisperService.IsLocalModelLoaded"/>).
    /// Returns an empty array if not currently recording or if nothing has been captured yet.
    ///
    /// Deliberately NOT windowed to a trailing slice: an earlier version trimmed this to the
    /// last few seconds to keep re-transcription cheap on long recordings, but re-transcribing
    /// a disjoint slice from scratch every tick made the live preview jump between unrelated
    /// fragments and erase whatever was shown a moment before, instead of reading like a
    /// transcript that grows as you talk. Re-transcribing the whole buffer each tick is
    /// simpler and matches that expectation, and stays fast enough for the short Q&amp;A
    /// questions this feature targets — each tick does get slower as the recording runs
    /// longer, which would matter for long recordings on a slow model, but not here.
    /// </summary>
    public byte[] PeekRecordedAudio()
    {
        if (!_recording) return [];

        try
        {
            lock (_bufferLock)
            {
                // _recording can flip false and StopRecordingAsync can dispose _writer/_buffer
                // concurrently between the check above and here — guard against the resulting
                // ObjectDisposedException instead of letting it escape as an unhandled exception
                // on the caller's (UI thread, async void) timer tick.
                _writer?.Flush();
                return _buffer?.ToArray() ?? [];
            }
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    private void CleanupCapture()
    {
        _mixPumpCts?.Dispose();
        _mixPumpCts  = null;
        _mixPumpTask = null;

        _writer?.Dispose();
        _capture?.Dispose();
        _loopbackCapture?.Dispose();
        _buffer?.Dispose();
        _writer          = null;
        _capture         = null;
        _loopbackCapture = null;
        _buffer          = null;
    }

    // ── Voice monitor (RMS-based, for voice-activated scroll AND the Settings mic test) ──────

    private IWaveIn?       _monitor;
    private IWaveIn?       _monitorLoopback; // second leg, only used by source = "both"
    private Action<float>? _rmsCallback;
    private float          _monitorMicRms;
    private float          _monitorLoopbackRms;

    /// <summary>
    /// Starts continuous audio-level monitoring from whichever source is configured
    /// (<paramref name="source"/>: "microphone" | "system" | "both", same values as
    /// <see cref="StartRecordingAsync"/>). The <paramref name="rmsCallback"/> is invoked on the
    /// audio thread with the RMS level (0–100). The caller must dispatch UI updates to the UI
    /// thread.
    ///
    /// Previously this ALWAYS opened a microphone capture regardless of the configured source —
    /// selecting "System audio (loopback)" (or "Both") had no effect on the Settings mic test or
    /// on Voice scroll mode's activation, both of which route through here; either would keep
    /// reacting to the physical microphone even with a silent/no mic selected. For "both", each
    /// leg's own RMS is tracked independently and the callback reports whichever is louder at
    /// that instant — good enough for a level meter / activation threshold, unlike the recording
    /// path's sample-accurate mixer (which exists to produce one coherent WAV for transcription,
    /// not just "is there sound").
    /// </summary>
    /// <param name="outputDeviceId">Which playback/render device "system"/"both" loopback
    /// listens to (see <see cref="StartRecordingAsync"/>'s param doc) — empty/null falls back
    /// to the OS default render device.</param>
    public void StartVoiceMonitor(string source, Action<float> rmsCallback, string? deviceId = null, string? outputDeviceId = null)
    {
        if (_recording) return;
        StopVoiceMonitor();

        _rmsCallback        = rmsCallback;
        _monitorMicRms      = 0f;
        _monitorLoopbackRms = 0f;

        if (source == "both")
        {
            _monitor         = CreateCapture(deviceId);
            _monitorLoopback = CreateLoopbackCapture(outputDeviceId);

            _monitor.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                _monitorMicRms = CalculateRms(e.Buffer, e.BytesRecorded, _monitor?.WaveFormat);
                _rmsCallback?.Invoke(Math.Max(_monitorMicRms, _monitorLoopbackRms));
            };
            _monitorLoopback.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                _monitorLoopbackRms = CalculateRms(e.Buffer, e.BytesRecorded, _monitorLoopback?.WaveFormat);
                _rmsCallback?.Invoke(Math.Max(_monitorMicRms, _monitorLoopbackRms));
            };

            _monitor.StartRecording();
            _monitorLoopback.StartRecording();
            return;
        }

        _monitor = source == "system"
            ? CreateLoopbackCapture(outputDeviceId)
            : CreateCapture(deviceId);
        _monitor.DataAvailable += OnMonitorDataAvailable;
        _monitor.StartRecording();
    }

    private static WasapiCapture CreateCapture(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return new WasapiCapture();
        try
        {
            var device = new MMDeviceEnumerator().GetDevice(deviceId);
            return new WasapiCapture(device);
        }
        catch { return new WasapiCapture(); }
    }

    /// <summary>
    /// Creates a loopback capture that listens to a SPECIFIC render/playback device (e.g. a
    /// virtual audio cable/mixer channel such as Elgato Wave Link or VoiceMeeter) instead of
    /// whatever the OS default playback device happens to be. Previously every
    /// <c>new WasapiLoopbackCapture()</c> call site used the parameterless constructor, which
    /// ALWAYS captures the default render device — the "Output device" picker in Settings was
    /// saved to config but never actually consulted anywhere, so selecting e.g. an Elgato
    /// virtual channel there had zero effect on what "System audio (loopback)" actually heard.
    /// </summary>
    private static WasapiLoopbackCapture CreateLoopbackCapture(string? outputDeviceId)
    {
        if (string.IsNullOrEmpty(outputDeviceId)) return new WasapiLoopbackCapture();
        try
        {
            var device = new MMDeviceEnumerator().GetDevice(outputDeviceId);
            return new WasapiLoopbackCapture(device);
        }
        catch { return new WasapiLoopbackCapture(); }
    }

    public void StopVoiceMonitor()
    {
        if (_monitor is null) return;
        _monitor.StopRecording();
        _monitor.DataAvailable -= OnMonitorDataAvailable;
        _monitor.Dispose();
        _monitor = null;

        if (_monitorLoopback is not null)
        {
            _monitorLoopback.StopRecording();
            _monitorLoopback.Dispose();
            _monitorLoopback = null;
        }

        _rmsCallback = null;
    }

    private void OnMonitorDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_rmsCallback is null || e.BytesRecorded == 0) return;
        var fmt = _monitor?.WaveFormat;
        _rmsCallback(CalculateRms(e.Buffer, e.BytesRecorded, fmt));
    }

    /// <summary>Returns RMS amplitude scaled to 0–100.</summary>
    private static float CalculateRms(byte[] buffer, int bytes, WaveFormat? fmt)
    {
        if (fmt is null || bytes == 0) return 0f;

        double sumSq = 0;
        int count = 0;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            for (int i = 0; i + 3 < bytes; i += 4)
            {
                float s = BitConverter.ToSingle(buffer, i);
                sumSq += s * s;
                count++;
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < bytes; i += 2)
            {
                float s = BitConverter.ToInt16(buffer, i) / 32768f;
                sumSq += s * s;
                count++;
            }
        }

        if (count == 0) return 0f;
        return (float)(Math.Sqrt(sumSq / count) * 100.0);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_recording)
        {
            _capture?.StopRecording();
            _loopbackCapture?.StopRecording();
            _recording = false;
        }
        _mixPumpCts?.Cancel();
        try { _mixPumpTask?.Wait(500); } catch { /* pump is best-effort */ }
        CleanupCapture();
        StopVoiceMonitor();
    }
}
