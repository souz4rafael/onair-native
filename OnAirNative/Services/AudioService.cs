using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace OnAirNative.Services;

/// <summary>
/// Manages audio capture via WASAPI.
/// Supports microphone (WasapiCapture) and system-audio loopback (WasapiLoopbackCapture).
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

    private IWaveIn?      _capture;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private bool          _recording;

    public bool IsRecording => _recording;

    /// <param name="source">"microphone" | "system" | "both" (both = mic only for now, loopback mix TODO)</param>
    public Task StartRecordingAsync(string source = "microphone", string? deviceId = null)
    {
        if (_recording) return Task.CompletedTask;

        StopVoiceMonitor();

        if (source == "system")
        {
            _capture = new WasapiLoopbackCapture();
        }
        else
        {
            _capture = CreateCapture(deviceId);
        }

        _buffer = new MemoryStream();
        _writer = new WaveFileWriter(_buffer, _capture.WaveFormat);

        _capture.DataAvailable += (_, e) =>
        {
            if (_writer is not null && e.BytesRecorded > 0)
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
        };

        _capture.RecordingStopped += (_, _) => { /* handled in StopRecordingAsync */ };

        _recording = true;
        _capture.StartRecording();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops recording and returns the captured audio as a WAV byte array.
    /// Returns an empty array if nothing was recorded.
    /// </summary>
    public async Task<byte[]> StopRecordingAsync()
    {
        if (!_recording || _capture is null) return [];

        _capture.StopRecording();
        _recording = false;

        // Give the device a moment to flush the last buffer
        await Task.Delay(150);

        _writer?.Flush();
        var data = _buffer?.ToArray() ?? [];

        CleanupCapture();
        return data;
    }

    private void CleanupCapture()
    {
        _writer?.Dispose();
        _capture?.Dispose();
        _buffer?.Dispose();
        _writer  = null;
        _capture = null;
        _buffer  = null;
    }

    // ── Voice monitor (RMS-based, for voice-activated scroll) ─────────────────

    private WasapiCapture? _monitor;
    private Action<float>? _rmsCallback;

    /// <summary>
    /// Starts continuous microphone monitoring. The <paramref name="rmsCallback"/>
    /// is invoked on the audio thread with the RMS level (0–100) of each buffer.
    /// The caller must dispatch UI updates to the UI thread.
    /// </summary>
    public void StartVoiceMonitor(Action<float> rmsCallback, string? deviceId = null)
    {
        if (_recording) return;
        StopVoiceMonitor();

        _rmsCallback = rmsCallback;
        _monitor = CreateCapture(deviceId);
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

    public void StopVoiceMonitor()
    {
        if (_monitor is null) return;
        _monitor.StopRecording();
        _monitor.DataAvailable -= OnMonitorDataAvailable;
        _monitor.Dispose();
        _monitor = null;
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
            _recording = false;
        }
        CleanupCapture();
        StopVoiceMonitor();
    }
}
