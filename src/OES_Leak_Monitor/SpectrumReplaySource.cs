using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Aqst.OesApp.Core;
using Aqst.OesSpectrometer.Models;

namespace OES_Leak_Monitor;

/// <summary>What the replay is doing. Only <see cref="Playing"/> and <see cref="Paused"/>
/// take the spectrum stream over from the device's synthetic generator.</summary>
public enum ReplayState
{
    /// <summary>No CSV loaded — the built-in synthetic generator drives test mode.</summary>
    NoFile,
    /// <summary>A CSV is loaded but playback has not been started.</summary>
    Ready,
    Playing,
    Paused,
    /// <summary>The last recorded frame has been delivered. The stream stays frozen there
    /// (rather than handing the synthetic generator back) until Stop or Restart.</summary>
    Finished,
}

/// <summary>Result of one device tick: the frames the replay wants delivered, plus the frame
/// the live plot should show. <see cref="Handled"/> is false when the replay is not driving
/// the stream at all, in which case the caller uses the device's own frame unchanged.</summary>
public readonly struct ReplayTick
{
    public bool Handled { get; init; }
    /// <summary>New frames due this tick, oldest first. May be empty (the device ticks faster
    /// than the recording, or playback is paused) — never a repeat of an already-delivered frame.</summary>
    public IReadOnlyList<SpectrumSample> Frames { get; init; }
    /// <summary>Frame the live full-spectrum plot should render. The most recent delivered
    /// frame, held between ticks so the plot never falls back to the synthetic spectrum.</summary>
    public SpectrumSample? Display { get; init; }
    /// <summary>True on the tick that delivered the recording's last frame. Reported here
    /// rather than as an event raised inside <c>Advance</c>, because the caller has not fanned
    /// <see cref="Frames"/> out yet at that point: an event let the host tear the replay's
    /// recorder session down first, so the final frames opened a session of their own — under
    /// the ordinary file prefix once the teardown had restored it. Acting on this flag after
    /// the fan-out keeps the whole run in one correctly-marked file.</summary>
    public bool Finished { get; init; }
}

/// <summary>Immutable snapshot of the replay for the UI, taken under the lock.</summary>
public readonly struct ReplayStatus
{
    public ReplayState State { get; init; }
    public string? FilePath { get; init; }
    public int FrameIndex { get; init; }
    public int FrameCount { get; init; }
    /// <summary>Playback position in recording seconds.</summary>
    public double PositionSeconds { get; init; }
    /// <summary>Length of the recording in seconds (last frame's elapsed time).</summary>
    public double DurationSeconds { get; init; }
    public double Speed { get; init; }
    /// <summary>Speed actually achieved over the last couple of seconds. Below
    /// <see cref="Speed"/> when the machine cannot keep up with the requested fast-forward.</summary>
    public double AchievedSpeed { get; init; }
    public SpectrumSample? LastFrame { get; init; }
}

/// <summary>
/// Test-mode playback of a recorded plasma spectrum, for validating the leak-monitor
/// algorithms against real data with no spectrometer attached. Reads a full-spectrum CSV in
/// the wide format <c>IntensityCsvWriter</c> writes (<c>WaveLength,&lt;wl…&gt;</c> header, then
/// one <c>HH:mm:ss.fff,&lt;intensity…&gt;</c> row per frame) and delivers those frames
/// <b>on the recording's own wavelength axis</b>.
///
/// <para><b>Why the axis matters.</b> The predecessor of this class resampled the recording
/// onto the synthetic test-mode axis (≈200–800 nm, 1000 points) because it could only overwrite
/// the incoming frame's fixed-length intensity array. Every line above 800 nm — Ar 811.5 nm
/// among them — was dropped, and everything finer than the device pixel spacing was interpolated
/// away, so ratios, σ, SNR and leak rates all differed from what the same data produces on real
/// hardware. Replacing the frame outright (via <c>DeviceViewModel.SpectrumMapper</c>) keeps the
/// recording's axis, which is what makes replay usable as algorithm validation rather than a
/// UI demo.</para>
///
/// <para><b>The clock.</b> Every judgement the leak monitor makes is tied to the interval
/// between frames — the EMA's <c>α = 1 − exp(−dt/τ)</c>, the sustained-confirmation seconds, the
/// σ/min slope, the leak-rate EWMA variance. So playback runs on a virtual clock: it starts when
/// Play is pressed and advances by the recording's own <c>ElapsedSec</c>, not by how often the
/// device happens to tick. Frame timestamps are <c>playStart + elapsed</c>, so the produced CSVs
/// land in today's folder while the algorithm sees exactly the intervals the plasma was measured
/// at. <see cref="Speed"/> scales how fast the recording is consumed, and because the timestamps
/// scale with it the algorithm's behaviour is unchanged — 20× just delivers the same evidence in
/// a twentieth of the wall time. Pausing freezes both clocks, leaving no gap in the timestamps.</para>
///
/// <para><b>Play once.</b> Unlike the looping demo playback it replaces, the recording is
/// played through exactly once and then holds on its last frame (<see cref="ReplayState.Finished"/>).
/// Looping stitched the file's end to its beginning, which every baseline and alarm downstream
/// read as a real step change.</para>
///
/// <para><see cref="Advance"/> runs on the acquisition thread; the transport commands come from
/// the UI thread. One lock guards the lot.</para>
/// </summary>
public sealed class SpectrumReplaySource
{
    /// <summary>Ceiling on how many recorded frames one device tick may deliver. Fast-forward
    /// asks for a burst per tick; this bounds it so a stall (or a slow disk) cannot turn into an
    /// unbounded catch-up that blocks the acquisition thread. When it bites, the virtual clock is
    /// re-anchored to what was actually delivered and <see cref="ReplayStatus.AchievedSpeed"/>
    /// reports the shortfall instead of the replay silently running fast.</summary>
    private const int MaxFramesPerTick = 400;

    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 20;

    private readonly object _gate = new();

    private FullRecording? _rec;
    private string? _path;
    private ReplayState _state = ReplayState.NoFile;
    private double _speed = 1;

    private int _index;                 // next recorded frame to deliver
    private double _virtualSec;         // playback position, in recording seconds
    private DateTime _lastWallUtc;      // wall clock at the previous tick
    private DateTime _frameEpoch;       // local time stamped onto elapsed-second 0
    private SpectrumSample? _lastFrame;

    // Achieved-speed measurement over a short window, so the UI can say "asked for 20×,
    // getting 6×" rather than leaving the operator to infer it from the progress bar.
    private DateTime _rateMarkWall;
    private double _rateMarkVirtual;
    private double _achievedSpeed;

    public string? FilePath { get { lock (_gate) return _path; } }
    public bool IsLoaded { get { lock (_gate) return _rec is { FrameCount: > 0 }; } }
    public int FrameCount { get { lock (_gate) return _rec?.FrameCount ?? 0; } }

    /// <summary>True while the replay owns the spectrum stream, so the caller knows the
    /// device's own frames must not reach the consumers.</summary>
    public bool IsActive
    {
        get { lock (_gate) return _state is ReplayState.Playing or ReplayState.Paused or ReplayState.Finished; }
    }

    /// <summary>Playback rate. Clamped to <see cref="MinSpeed"/>..<see cref="MaxSpeed"/>.</summary>
    public double Speed
    {
        get { lock (_gate) return _speed; }
        set { lock (_gate) _speed = Math.Clamp(value, MinSpeed, MaxSpeed); }
    }

    /// <summary>
    /// Loads a full-spectrum CSV, leaving playback stopped at its first frame.
    /// Throws <see cref="InvalidDataException"/> if the file cannot be parsed or holds no
    /// spectra; the previously loaded recording is then left untouched.
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
            _rec = rec;
            _path = path;
            _state = ReplayState.Ready;
            ResetPositionLocked();
        }
    }

    /// <summary>Drops the loaded recording; test mode reverts to the synthetic generator.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rec = null;
            _path = null;
            _state = ReplayState.NoFile;
            ResetPositionLocked();
        }
    }

    /// <summary>
    /// Starts playback, or resumes it after a pause. Starting from
    /// <see cref="ReplayState.Ready"/> or <see cref="ReplayState.Finished"/> rewinds to the
    /// first frame, so a second run of the same file is a clean repeat rather than a
    /// continuation. No-op with no recording loaded.
    /// </summary>
    public void Play()
    {
        lock (_gate)
        {
            if (_rec is null || _rec.FrameCount == 0) return;
            if (_state is ReplayState.Ready or ReplayState.Finished) ResetPositionLocked();

            var now = DateTime.UtcNow;
            _lastWallUtc = now;
            _rateMarkWall = now;
            _rateMarkVirtual = _virtualSec;
            // The epoch is only set when starting from the top; resuming keeps the original
            // one so the delivered timestamps carry on with no gap across the pause.
            if (_index == 0) _frameEpoch = DateTime.Now;
            _state = ReplayState.Playing;
        }
    }

    /// <summary>Freezes playback and the stream. The live plot keeps the last delivered
    /// frame; no consumer sees anything until Play resumes.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_state == ReplayState.Playing) _state = ReplayState.Paused;
        }
    }

    /// <summary>Ends playback and hands the stream back to the synthetic generator, rewound
    /// to the first frame.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_rec is null) { _state = ReplayState.NoFile; return; }
            _state = ReplayState.Ready;
            ResetPositionLocked();
        }
    }

    /// <summary>Rewinds and plays again from the first frame.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            if (_rec is null || _rec.FrameCount == 0) return;
            ResetPositionLocked();
            var now = DateTime.UtcNow;
            _lastWallUtc = now;
            _rateMarkWall = now;
            _rateMarkVirtual = 0;
            _frameEpoch = DateTime.Now;
            _state = ReplayState.Playing;
        }
    }

    /// <summary>Everything the UI polls, read in one lock so the numbers agree with each other.</summary>
    public ReplayStatus Snapshot()
    {
        lock (_gate)
        {
            var count = _rec?.FrameCount ?? 0;
            return new ReplayStatus
            {
                State = _state,
                FilePath = _path,
                FrameIndex = _index,
                FrameCount = count,
                PositionSeconds = _virtualSec,
                DurationSeconds = count > 0 ? _rec!.ElapsedSec[count - 1] : 0,
                Speed = _speed,
                AchievedSpeed = _achievedSpeed,
                LastFrame = _lastFrame,
            };
        }
    }

    /// <summary>
    /// Advances the virtual clock by this tick's wall time (scaled by <see cref="Speed"/>) and
    /// returns every recorded frame that has come due, the frame to display, and whether this
    /// tick emptied the recording.
    /// <see cref="ReplayTick.Handled"/> is false — and the caller should use the device's own
    /// frame — when no recording is loaded, playback has not been started, or the frame came
    /// from real hardware (<see cref="SpectrumSample.IsTestMode"/> false), which always wins.
    /// </summary>
    public ReplayTick Advance(SpectrumSample raw)
    {
        if (raw is null || !raw.IsTestMode) return default;

        List<SpectrumSample>? frames = null;
        SpectrumSample? display;
        bool finished = false;

        lock (_gate)
        {
            if (_rec is null) return default;
            if (_state is ReplayState.NoFile or ReplayState.Ready) return default;

            var now = DateTime.UtcNow;
            if (_state == ReplayState.Playing)
            {
                var dt = (now - _lastWallUtc).TotalSeconds;
                _lastWallUtc = now;
                if (dt > 0) _virtualSec += dt * _speed;

                while (_index < _rec.FrameCount && _rec.ElapsedSec[_index] <= _virtualSec)
                {
                    (frames ??= new List<SpectrumSample>()).Add(BuildFrameLocked(_index, raw));
                    _index++;
                    if (frames.Count >= MaxFramesPerTick)
                    {
                        // Delivered all we are willing to in one tick — pull the clock back to
                        // the last frame actually delivered so it cannot run away from the data.
                        _virtualSec = _rec.ElapsedSec[_index - 1];
                        break;
                    }
                }

                if (_index >= _rec.FrameCount)
                {
                    _state = ReplayState.Finished;
                    finished = true;
                }

                var window = (now - _rateMarkWall).TotalSeconds;
                if (window >= 2)
                {
                    _achievedSpeed = (_virtualSec - _rateMarkVirtual) / window;
                    _rateMarkWall = now;
                    _rateMarkVirtual = _virtualSec;
                }
            }
            else
            {
                // Paused or finished: hold the clock still so resuming doesn't skip ahead by
                // however long the operator was thinking about it.
                _lastWallUtc = now;
                _achievedSpeed = 0;
            }

            if (frames is { Count: > 0 }) _lastFrame = frames[^1];
            display = _lastFrame;
        }

        return new ReplayTick
        {
            Handled = true,
            Frames = frames ?? (IReadOnlyList<SpectrumSample>)Array.Empty<SpectrumSample>(),
            Display = display,
            Finished = finished,
        };
    }

    /// <summary>
    /// Builds one delivered frame. The wavelength axis is shared with the recording (nothing
    /// downstream writes to it), the intensities are copied so a consumer that scales or
    /// filters in place cannot corrupt a later replay of the same file. Acquisition metadata
    /// is carried over from the device frame — the exposure the recording was actually taken
    /// at is not in the CSV, which is why a Golden Run captured live will report an
    /// acquisition mismatch against replayed data.
    /// </summary>
    private SpectrumSample BuildFrameLocked(int i, SpectrumSample raw)
    {
        var src = _rec!.Intensities[i];
        var intensities = new float[src.Length];
        Array.Copy(src, intensities, src.Length);

        return new SpectrumSample
        {
            Timestamp       = _frameEpoch.AddSeconds(_rec.ElapsedSec[i]),
            Wavelengths     = _rec.Wavelengths,
            Intensities     = intensities,
            IntegrationTime = raw.IntegrationTime,
            AverageCount    = raw.AverageCount,
            SerialNumber    = raw.SerialNumber,
            IsTestMode      = true,
        };
    }

    private void ResetPositionLocked()
    {
        _index = 0;
        _virtualSec = 0;
        _lastFrame = null;
        _achievedSpeed = 0;
        _rateMarkVirtual = 0;
    }
}
