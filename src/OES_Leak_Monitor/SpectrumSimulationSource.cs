using System;
using System.IO;
using System.Threading;
using Aqst.OesApp.Core;
using Aqst.OesSpectrometer.Models;

namespace OES_Leak_Monitor;

/// <summary>
/// Test-mode plasma-spectrum playback. When a full-spectrum CSV (the same wide format
/// the intensity logger writes — <c>WaveLength,&lt;wl…&gt;</c> header row, then one
/// <c>HH:mm:ss.fff,&lt;intensity…&gt;</c> row per frame) is loaded, <see cref="Map"/>
/// replaces a synthetic test-mode frame's intensities with the recorded spectrum,
/// looping back to the first frame once the file is exhausted. With no file loaded — or
/// when the incoming frame is from real hardware (<see cref="SpectrumSample.IsTestMode"/>
/// is false) — it is a transparent no-op, so the original synthetic generator is used.
///
/// <para>Used because the <c>Aqst.OesApp.Wpf</c> <c>DeviceViewModel</c> exposes no seam
/// to inject a custom spectrum source. The app intercepts every device frame at its
/// fan-out point (<c>MainViewModel</c>) and routes it through here. Crucially, the
/// recorded spectrum is <b>resampled onto the live frame's own wavelength axis and the
/// frame's intensity array is overwritten in place</b> — rather than handing back a new
/// sample on the CSV's axis. That is what lets the substitution also reach the
/// <c>DeviceViewModel</c>'s built-in live full-spectrum plot, which redraws from the same
/// frame object on a dispatcher callback queued <i>after</i> the synchronous
/// <c>SpectrumAvailable</c> invoke (so it observes the mutation). The intensity logger,
/// leak engine, and Monitor-tab trend receive the same overwritten frame, so every view
/// shows the CSV. The trade-off is the test-mode axis's resolution and range
/// (≈200–800 nm, 1000 points): wavelengths the device axis doesn't cover are dropped, and
/// any CSV detail finer than the device pixel spacing is interpolated away.</para>
/// </summary>
public sealed class SpectrumSimulationSource
{
    private readonly object _gate = new();
    private FullRecording? _recording;
    private string? _path;
    private int _index;

    /// <summary>Path of the loaded CSV, or null when none is loaded.</summary>
    public string? FilePath { get { lock (_gate) return _path; } }

    /// <summary>True when a CSV with at least one frame is loaded and driving playback.</summary>
    public bool IsLoaded { get { lock (_gate) return _recording is { FrameCount: > 0 }; } }

    /// <summary>Frame count of the loaded CSV (0 when none).</summary>
    public int FrameCount { get { lock (_gate) return _recording?.FrameCount ?? 0; } }

    /// <summary>
    /// Loads playback frames from a full-spectrum CSV. Throws <see cref="InvalidDataException"/>
    /// if the file cannot be parsed or contains no frames; on success the next <see cref="Map"/>
    /// call returns the first frame.
    /// </summary>
    public void Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var rec = RecordingCsvParser.ReadFull(path, CancellationToken.None)
                  ?? throw new InvalidDataException("Could not parse the spectrum CSV (unexpected format).");
        if (rec.FrameCount == 0 || rec.Wavelengths.Length == 0)
            throw new InvalidDataException("The spectrum CSV has no spectra to play back.");

        lock (_gate)
        {
            _recording = rec;
            _path = path;
            _index = 0;
        }
    }

    /// <summary>Drops the loaded file so playback reverts to the built-in synthetic generator.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _recording = null;
            _path = null;
            _index = 0;
        }
    }

    /// <summary>
    /// Maps a device frame to its effective spectrum. When a CSV is loaded and
    /// <paramref name="raw"/> is a test-mode frame, the next recorded frame is resampled onto
    /// <paramref name="raw"/>'s wavelength axis and written into its intensity array in place
    /// (advancing the playback cursor and wrapping at the end), and the same instance is
    /// returned — so every consumer, including the device's own live plot which redraws from
    /// this object, shows the recorded spectrum. Otherwise <paramref name="raw"/> is returned
    /// untouched. The frame's timestamp and metadata are kept so trends advance on the real
    /// wall clock.
    /// </summary>
    public SpectrumSample Map(SpectrumSample raw)
    {
        if (raw is null || !raw.IsTestMode) return raw!;

        var dstW = raw.Wavelengths;
        var dstI = raw.Intensities;
        if (dstW is null || dstI is null) return raw;

        lock (_gate)
        {
            var rec = _recording;
            if (rec is null || rec.FrameCount == 0) return raw;

            int i = _index;
            _index = (i + 1) % rec.FrameCount;
            Resample(rec.Wavelengths, rec.Intensities[i], dstW, dstI);
        }
        return raw;
    }

    /// <summary>
    /// Linearly resamples a source spectrum onto the destination wavelength axis, writing
    /// into <paramref name="dstI"/> in place. Both wavelength arrays are assumed ascending
    /// (the OES convention). Destination wavelengths outside the source's covered range are
    /// set to 0 (no signal there). O(n+m) via a single forward cursor since both axes rise
    /// monotonically.
    /// </summary>
    private static void Resample(float[] srcW, float[] srcI, float[] dstW, float[] dstI)
    {
        int m = Math.Min(srcW.Length, srcI.Length);
        int n = Math.Min(dstW.Length, dstI.Length);

        if (m == 0)
        {
            Array.Clear(dstI, 0, dstI.Length);
            return;
        }

        int cursor = 0;
        for (int k = 0; k < n; k++)
        {
            float w = dstW[k];
            if (w <= srcW[0])      { dstI[k] = w < srcW[0]     ? 0f : srcI[0];     continue; }
            if (w >= srcW[m - 1])  { dstI[k] = w > srcW[m - 1] ? 0f : srcI[m - 1]; continue; }

            // Advance so srcW[cursor] <= w < srcW[cursor + 1].
            while (cursor < m - 2 && srcW[cursor + 1] <= w) cursor++;

            float w0 = srcW[cursor], w1 = srcW[cursor + 1];
            float frac = w1 > w0 ? (w - w0) / (w1 - w0) : 0f;
            dstI[k] = srcI[cursor] + frac * (srcI[cursor + 1] - srcI[cursor]);
        }

        // Defensive: if the intensity array somehow outruns the wavelength axis, zero the tail.
        for (int k = n; k < dstI.Length; k++) dstI[k] = 0f;
    }
}
