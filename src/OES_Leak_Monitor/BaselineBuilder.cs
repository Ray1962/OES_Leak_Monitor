using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OES_Leak_Monitor;

/// <summary>One ratio's reading across a whole recording, recomputed with today's configuration.</summary>
public sealed class RatioTrace
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Monitored value per frame; NaN where the frame produced none.</summary>
    public required double[] Value { get; init; }

    /// <summary>Per frame: this frame may feed a baseline (gate open, lines present, SNR clear).</summary>
    public required bool[] Accepted { get; init; }

    /// <summary>Per frame: a value came out, but the SNR floor rejected it.</summary>
    public required bool[] LowSnr { get; init; }

    /// <summary>Reference-line reading per frame — the plasma floor is derived from it.</summary>
    public required double[] Reference { get; init; }

    /// <summary>Mean / σ over the accepted frames in <c>[from, to]</c>.</summary>
    public RunningStats StatsOver(double[] elapsed, double fromSec, double toSec)
    {
        var s = new RunningStats();
        for (int i = 0; i < Value.Length; i++)
            if (Accepted[i] && elapsed[i] >= fromSec && elapsed[i] <= toSec) s.Add(Value[i]);
        return s;
    }
}

/// <summary>A whole recording, re-extracted through the current ratio set.</summary>
public sealed class RecordingScan
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Local time of the recording's first frame — the CSV carries only a time of day,
    /// so the date comes from the file's own folder (<see cref="Recording.SessionStart"/>).</summary>
    public required DateTime StartLocal { get; init; }

    public required double[] ElapsedSec { get; init; }
    public required IReadOnlyDictionary<string, RatioTrace> Traces { get; init; }

    /// <summary>The wavelength-corrected ratio definitions this scan was made with — the same
    /// clones the engine builds, so a baseline built here means what the engine will read.</summary>
    public required IReadOnlyList<RatioDefinition> Definitions { get; init; }

    /// <summary>What the recording was taken under. The axis is recovered from the CSV header;
    /// integration time and averaging come from the sidecar written beside it, and are absent
    /// for a recording made before that existed — a partial fingerprint, deliberately, because
    /// a guessed one would make the mismatch check pass when it should not.</summary>
    public required AcquisitionFingerprint Acquisition { get; init; }

    public int FrameCount => ElapsedSec.Length;
    public double DurationSeconds => FrameCount == 0 ? 0 : ElapsedSec[^1] - ElapsedSec[0];
}

/// <summary>A window of one recording, offered as a candidate steady segment.</summary>
public sealed record SegmentSuggestion(double FromSec, double ToSec, int Frames, double Unsteadiness);

/// <summary>The window an operator picked out of one recording. One per recording, by design —
/// see <see cref="BaselineBuilder"/>.</summary>
public sealed record SteadyWindow(double FromSec, double ToSec);

/// <summary>A recording whose level disagrees with the others, and the ratio that says so.</summary>
public sealed record BuildOutlier(string Path, string RatioKey, string RatioDisplayName,
                                  double Mean, double PeerMedian, double Sigmas)
{
    public string Reason =>
        $"{RatioDisplayName} reads {Mean:G4} here against {PeerMedian:G4} in the other " +
        $"recordings — {Sigmas:0.#} σ away";
}

/// <summary>
/// How steady the chosen window actually is, per recording — the worst ratio in it.
///
/// <para>It answers the question the back-check cannot when the window is the whole recording:
/// there is then nothing outside it to compare, and the one guard that would have caught a window
/// containing a leak goes quiet. σ within the window does not, and neither does drift across it —
/// a slow rise inflates σ and shows up as drift, where noise inflates σ and does not.</para>
/// </summary>
public sealed record BuildSteadiness(string Path, string RatioKey, string RatioDisplayName,
                                     double RelativeSigma, double RelativeDrift,
                                     bool WindowCoversWholeRecording);

/// <summary>How far the finished baseline is exceeded *outside* the window it was built from.</summary>
public sealed record BuildBackCheck(string Path, string RatioKey, string RatioDisplayName,
                                    double MaxSigmas, double AtSeconds);

public sealed class BaselineBuildOptions
{
    public string RunName { get; set; } = "";

    /// <summary>Fewest accepted frames a ratio needs, pooled across every recording. Frames, not
    /// seconds: σ's reliability follows the sample count, and 120 s is 68 frames at 0.56 fps and
    /// 600 at 5 fps. The live capture counts seconds only because an operator is standing there.</summary>
    public int MinFrames { get; set; } = 60;

    /// <summary>Recordings the operator has put back in after the consistency check excluded them.</summary>
    public IReadOnlyCollection<string> ForceInclude { get; set; } = Array.Empty<string>();

    /// <summary>How far a recording's level may sit from its peers before it is set aside,
    /// in multiples of the typical within-recording σ.</summary>
    public double OutlierSigmas { get; set; } = 5.0;
}

public sealed class BaselineBuildResult
{
    public required GoldenRun Run { get; init; }

    /// <summary>False when no ratio got a usable baseline — nothing worth storing.</summary>
    public required bool Accepted { get; init; }

    public required IReadOnlyList<GoldenRunRatioRejection> Rejected { get; init; }

    /// <summary>Recordings left out by the consistency check. Already excluded from
    /// <see cref="Run"/>; put one back with <see cref="BaselineBuildOptions.ForceInclude"/>.</summary>
    public required IReadOnlyList<BuildOutlier> Outliers { get; init; }

    public required IReadOnlyList<BuildBackCheck> BackChecks { get; init; }

    /// <summary>Per recording, how steady its chosen window is — and whether that window swallowed
    /// the whole recording, which is what leaves <see cref="BackChecks"/> with nothing to say.</summary>
    public required IReadOnlyList<BuildSteadiness> Steadiness { get; init; }

    /// <summary>Non-empty when the build was refused outright.</summary>
    public string Error { get; init; } = "";
}

/// <summary>
/// Builds a Golden Run from recordings already on disk, instead of from a minute in front of the
/// tool.
///
/// <para>It exists for the processes the live capture cannot serve: one that ramps for its first
/// ten seconds (the steady segment starts late, and only a plot shows where), and one whose whole
/// run is barely longer than a capture window (no single run holds enough frames, so several have
/// to be pooled). Both are answered the same way — pick the steady segment afterwards, with the
/// trace visible, and pool across runs.</para>
///
/// <para><b>One window per recording, deliberately.</b> Two windows out of one run either sit at
/// the same operating point — in which case they are one window with a gap — or at different
/// ones, in which case averaging them together is exactly the error this whole exercise is about.
/// A recipe with several operating points needs several Golden Runs, which the engine cannot yet
/// hold; see the ADR.</para>
///
/// <para>Every gate the live capture applies is applied here, through the same code
/// (<see cref="RatioFrameSampling"/>): a baseline built here is stored in the same field and
/// judged by the same thresholds, so it has to be the same measurement.</para>
/// </summary>
public static class BaselineBuilder
{
    /// <summary>Mirrors LeakMonitorEngine's own floor: a baseline whose mean is not this many σ
    /// clear of zero makes every quantity derived from it the sign of the noise.</summary>
    public const double MinBaselineMeanToSigma = 10.0;

    /// <summary>Mirrors LeakMonitorEngine: too few of the SNR-evaluable frames clearing the floor
    /// means the survivors are a biased upward sliver.</summary>
    public const double MinAcceptFraction = 0.5;

    /// <summary>
    /// A baseline this close to <see cref="MinBaselineMeanToSigma"/> is worth saying out loud.
    /// Clearing the floor at 10.2 σ and clearing it at 97 σ look identical on screen — both are
    /// "has a baseline" — but the first has thresholds ten times wider, which is a monitor that
    /// looks configured and will not fire. Measured: a window that swallowed a whole recording,
    /// leak included, produced exactly 10.2.
    /// </summary>
    public const double MarginalMeanToSigma = 20.0;

    /// <summary>A window this fraction of a recording leaves nothing outside it to check.</summary>
    public const double WholeRecordingFraction = 0.95;

    /// <summary>Re-extracts one parsed recording through the current ratio set.</summary>
    public static RecordingScan Scan(string path, string displayName, DateTime startLocal,
        FullRecording rec, IReadOnlyList<RatioDefinition> correctedDefs, PlasmaGate? gate,
        AcquisitionFingerprint? sidecar, IProgress<double>? progress, CancellationToken ct)
    {
        if (rec is null) throw new ArgumentNullException(nameof(rec));
        var frames = new float[rec.FrameCount][];
        var el = new double[rec.FrameCount];
        for (int i = 0; i < rec.FrameCount; i++) { frames[i] = rec.Intensities[i]; el[i] = rec.ElapsedSec[i]; }
        return Scan(path, displayName, startLocal, rec.Wavelengths, frames, el,
                    correctedDefs, gate, sidecar, progress, ct);
    }

    /// <summary>
    /// Re-extracts one recording through the current ratio set. Takes the arrays rather than the
    /// parsed type so the computation has nothing to do with where the frames came from.
    /// </summary>
    public static RecordingScan Scan(string path, string displayName, DateTime startLocal,
        float[] wavelengths, IReadOnlyList<float[]> frames, IReadOnlyList<double> elapsedSec,
        IReadOnlyList<RatioDefinition> correctedDefs, PlasmaGate? gate,
        AcquisitionFingerprint? sidecar, IProgress<double>? progress, CancellationToken ct)
    {
        if (frames is null) throw new ArgumentNullException(nameof(frames));
        if (correctedDefs is null) throw new ArgumentNullException(nameof(correctedDefs));

        // A disabled ratio gets no baseline from a live capture — FinalizeCapture rejects it with
        // "the ratio was disabled for the whole capture" — so it gets none here either. Scanning
        // it anyway would produce a Golden Run the live path could not have produced.
        correctedDefs = correctedDefs.Where(d => d.Enabled).ToList();

        int n = frames.Count;
        var traces = correctedDefs.ToDictionary(d => d.Key, d => new RatioTrace
        {
            Key = d.Key,
            DisplayName = d.DisplayName,
            Value = new double[n],
            Accepted = new bool[n],
            LowSnr = new bool[n],
            Reference = new double[n],
        });

        var elapsed = new double[n];
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            elapsed[i] = elapsedSec[i];
            var inten = frames[i];
            bool? open = gate?.IsPlasmaPresent(wavelengths, inten);
            foreach (var def in correctedDefs)
            {
                // Floor 0: the plasma floors come out of this build, exactly as they come out of
                // a live capture, so an inherited one must not gate the frames producing them.
                var fs = RatioFrameSampling.Evaluate(def, wavelengths, inten, open, 0.0);
                var t = traces[def.Key];
                t.Value[i] = fs.Value;
                t.Accepted[i] = fs.Accepted;
                t.LowSnr[i] = fs.Evaluable && fs.LowSnr;
                t.Reference[i] = fs.Denominator.Value;
            }
            if (n > 0 && (i & 63) == 0) progress?.Report((double)i / n);
        }
        progress?.Report(1.0);

        var axis = wavelengths;
        var acquisition = sidecar?.Clone() ?? new AcquisitionFingerprint();
        acquisition.AxisLength = axis.Length;
        acquisition.AxisStartNm = axis.Length > 0 ? axis[0] : 0;
        acquisition.AxisEndNm = axis.Length > 0 ? axis[^1] : 0;

        return new RecordingScan
        {
            Path = path,
            DisplayName = displayName,
            StartLocal = startLocal,
            ElapsedSec = elapsed,
            Traces = traces,
            Definitions = correctedDefs,
            Acquisition = acquisition,
        };
    }

    /// <summary>
    /// Candidate steady windows, flattest first. Deliberately simple: its job is to put the
    /// operator's cursor near the right place, not to decide anything — it can see that a stretch
    /// is flat, never that it is leak-free, and only the second question matters in the end.
    /// </summary>
    public static IReadOnlyList<SegmentSuggestion> Suggest(RecordingScan scan, double windowSeconds,
                                                           int count = 3)
    {
        if (scan is null) throw new ArgumentNullException(nameof(scan));
        var elapsed = scan.ElapsedSec;
        int n = elapsed.Length;
        if (n < 4 || scan.Traces.Count == 0) return Array.Empty<SegmentSuggestion>();

        double span = Math.Min(windowSeconds, scan.DurationSeconds);
        if (span <= 0) return Array.Empty<SegmentSuggestion>();

        var found = new List<SegmentSuggestion>();
        double stride = Math.Max(span / 8.0, (elapsed[^1] - elapsed[0]) / 200.0);
        for (double from = elapsed[0]; from + span <= elapsed[^1] + 1e-9; from += stride)
        {
            double to = from + span;
            double worst = 0;
            int minFrames = int.MaxValue;
            bool usable = true;
            foreach (var t in scan.Traces.Values)
            {
                var s = t.StatsOver(elapsed, from, to);
                if (s.Count < 4 || s.Mean == 0) { usable = false; break; }
                minFrames = Math.Min(minFrames, s.Count);
                // A window is only as steady as its least steady ratio, so the worst one scores it.
                double rel = s.StdDev / Math.Abs(s.Mean);
                double drift = Math.Abs(Drift(t, elapsed, from, to)) / Math.Abs(s.Mean);
                worst = Math.Max(worst, rel + drift);
            }
            if (usable) found.Add(new SegmentSuggestion(from, to, minFrames, worst));
        }

        // Non-overlapping picks: the three flattest windows of one plateau are one answer.
        var picks = new List<SegmentSuggestion>();
        foreach (var c in found.OrderBy(f => f.Unsteadiness))
        {
            if (picks.Count >= count) break;
            if (picks.Any(p => c.FromSec < p.ToSec && p.FromSec < c.ToSec)) continue;
            picks.Add(c);
        }
        return picks;
    }

    /// <summary>Level difference between the second and first half of the window — a cheap slope
    /// that costs one pass and does not care about the frame interval.</summary>
    private static double Drift(RatioTrace t, double[] elapsed, double from, double to)
    {
        double mid = (from + to) / 2.0;
        var a = t.StatsOver(elapsed, from, mid);
        var b = t.StatsOver(elapsed, mid, to);
        return a.Count == 0 || b.Count == 0 ? 0 : b.Mean - a.Mean;
    }

    /// <summary>
    /// Pools the picked windows into one Golden Run.
    /// </summary>
    public static BaselineBuildResult Build(
        IReadOnlyList<(RecordingScan Scan, SteadyWindow Window)> picks,
        BaselineBuildOptions options)
    {
        if (picks is null) throw new ArgumentNullException(nameof(picks));
        options ??= new BaselineBuildOptions();

        if (picks.Count == 0) return Refused("No recordings selected.", options);

        // Two recordings on different wavelength axes are not two measurements of the same thing:
        // every extraction window falls on different pixels. Refuse rather than pool them.
        var axes = picks.Select(p => (p.Scan.Acquisition.AxisLength, p.Scan.Acquisition.AxisStartNm,
                                      p.Scan.Acquisition.AxisEndNm)).Distinct().ToList();
        if (axes.Count > 1)
            return Refused("The selected recordings were taken on different wavelength axes " +
                           string.Join(" and ", axes.Select(a => $"{a.AxisLength} points " +
                               $"{a.AxisStartNm:0.#}–{a.AxisEndNm:0.#} nm")) +
                           ". A baseline pooled across them would be meaningless.", options);

        var keys = picks[0].Scan.Traces.Keys.ToList();
        var outliers = FindOutliers(picks, keys, options);
        var excluded = outliers.Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var keep in options.ForceInclude) excluded.Remove(keep);
        var used = picks.Where(p => !excluded.Contains(p.Scan.Path)).ToList();
        if (used.Count == 0)
            return Refused("Every selected recording was set aside by the consistency check.",
                           options, outliers);

        var run = new GoldenRun
        {
            Name = options.RunName,
            CapturedUtc = DateTime.UtcNow,
            DurationSeconds = used.Sum(p => p.Window.ToSec - p.Window.FromSec),
            Acquisition = used[0].Scan.Acquisition.Clone(),
            Source = new GoldenRunSource
            {
                Kind = GoldenRunSource.OfflineBuild,
                Files = used.Select(p => new GoldenRunSourceWindow
                {
                    Path = p.Scan.Path,
                    FromUtc = p.Scan.StartLocal.AddSeconds(p.Window.FromSec).ToUniversalTime(),
                    ToUtc = p.Scan.StartLocal.AddSeconds(p.Window.ToSec).ToUniversalTime(),
                }).ToList(),
                Excluded = outliers.Where(o => excluded.Contains(o.Path))
                                   .Select(o => new GoldenRunSourceExclusion
                                   { Path = o.Path, Reason = o.Reason }).ToList(),
            },
        };

        var rejected = new List<GoldenRunRatioRejection>();
        var denomPools = new Dictionary<string, RunningStats>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var pooled = new RunningStats();
            int lowSnr = 0;
            RatioTrace? any = null;
            foreach (var (scan, window) in used)
            {
                if (!scan.Traces.TryGetValue(key, out var t)) continue;
                any = t;
                var el = scan.ElapsedSec;
                for (int i = 0; i < t.Value.Length; i++)
                {
                    if (el[i] < window.FromSec || el[i] > window.ToSec) continue;
                    if (t.LowSnr[i]) lowSnr++;
                    if (!t.Accepted[i]) continue;
                    pooled.Add(t.Value[i]);
                }
            }
            if (any is null) continue;

            string display = any.DisplayName;
            int evaluable = pooled.Count + lowSnr;
            if (pooled.Count == 0)
            {
                rejected.Add(Reject(key, display,
                    "no frame in the selected windows produced a usable value — check the plasma " +
                    "gate (the logger's trigger threshold) and that the line is on this axis"));
                continue;
            }
            if (pooled.Count < options.MinFrames)
            {
                rejected.Add(Reject(key, display,
                    $"only {pooled.Count} usable frames across the selected windows, fewer than " +
                    $"the {options.MinFrames} required — widen a window, or add another recording"));
                continue;
            }
            if (evaluable > 0 && pooled.Count < MinAcceptFraction * evaluable)
            {
                rejected.Add(Reject(key, display,
                    $"only {pooled.Count} of {evaluable} frames cleared the SNR floor; the line " +
                    "sat near the noise floor"));
                continue;
            }
            double mean = pooled.Mean, sd = pooled.StdDev;
            if (mean <= 0 || (sd > 0 && mean < MinBaselineMeanToSigma * sd))
            {
                rejected.Add(Reject(key, display,
                    $"mean {mean:G4} ± {sd:G3} is not clear of zero (needs mean > " +
                    $"{MinBaselineMeanToSigma:0} σ). Use a stronger line, or switch the " +
                    "extraction to Raw if there is no peak at this wavelength"));
                continue;
            }

            var def = FindDefinition(used, key);
            run.Baselines.Add(new GoldenRunRatioBaseline
            {
                Key = key,
                Mean = mean,
                Sigma = sd,
                SampleCount = pooled.Count,
                ExtractionRevision = LeakMonitorEngine.CurrentExtractionRevision,
                Mode = def?.MonitorMode ?? MonitorMode.Ratio,
                ReferenceLabel = def is null || def.MonitorMode == MonitorMode.AbsoluteIntensity
                    ? "" : def.Denominator.Label,
            });
            if (def is not null && def.MonitorMode != MonitorMode.AbsoluteIntensity)
            {
                if (!denomPools.TryGetValue(def.Denominator.MeasurementKey, out var pool))
                    denomPools[def.Denominator.MeasurementKey] = pool = new RunningStats();
                foreach (var (scan, window) in used)
                {
                    if (!scan.Traces.TryGetValue(key, out var t)) continue;
                    var el = scan.ElapsedSec;
                    for (int i = 0; i < t.Value.Length; i++)
                        if (t.Accepted[i] && el[i] >= window.FromSec && el[i] <= window.ToSec)
                            pool.Add(t.Reference[i]);
                }
            }
        }

        foreach (var kv in denomPools.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Value.Count == 0) continue;
            run.PlasmaFloors.Add(new PlasmaFloorEntry
            {
                ReferenceKey = kv.Key,
                ReferenceLabel = kv.Key.Split('|')[0],
                Floor = 0.2 * kv.Value.Mean,
            });
        }

        // Record the frame counts now that each ratio's pool is known: one number per window is
        // not meaningful per ratio, so the window carries the largest count any ratio drew from it.
        for (int i = 0; i < used.Count; i++)
        {
            var (scan, window) = used[i];
            int frames = scan.ElapsedSec.Count(e => e >= window.FromSec && e <= window.ToSec);
            run.Source!.Files[i].FramesAccepted = frames;
        }

        return new BaselineBuildResult
        {
            Run = run,
            Accepted = run.Baselines.Count > 0,
            Rejected = rejected,
            Outliers = outliers,
            BackChecks = BackCheck(used, run),
            Steadiness = Steadiness(used, run),
        };
    }

    /// <summary>
    /// How steady each recording's chosen window is, judged from inside it. Reported per recording
    /// as the worst ratio, so the panel stays readable — and reported even when a recording
    /// produced no baseline at all, because "the window is the whole recording" is worth saying
    /// either way.
    /// </summary>
    private static IReadOnlyList<BuildSteadiness> Steadiness(
        IReadOnlyList<(RecordingScan Scan, SteadyWindow Window)> used, GoldenRun run)
    {
        var rows = new List<BuildSteadiness>();
        foreach (var (scan, window) in used)
        {
            bool whole = scan.DurationSeconds > 0 &&
                         (window.ToSec - window.FromSec) >= WholeRecordingFraction * scan.DurationSeconds;
            BuildSteadiness? worst = null;
            foreach (var b in run.Baselines)
            {
                if (!scan.Traces.TryGetValue(b.Key, out var t)) continue;
                var s = t.StatsOver(scan.ElapsedSec, window.FromSec, window.ToSec);
                if (s.Count < 4 || s.Mean == 0) continue;
                double rel = s.StdDev / Math.Abs(s.Mean);
                double drift = Math.Abs(Drift(t, scan.ElapsedSec, window.FromSec, window.ToSec))
                             / Math.Abs(s.Mean);
                if (worst is null || rel > worst.RelativeSigma)
                    worst = new BuildSteadiness(scan.Path, b.Key, t.DisplayName, rel, drift, whole);
            }
            rows.Add(worst ?? new BuildSteadiness(scan.Path, "", "", double.NaN, double.NaN, whole));
        }
        return rows;
    }

    /// <summary>
    /// How far each recording goes outside its own window once this baseline is applied. It
    /// answers the one question the build itself cannot: does the baseline already flag the data
    /// it came from. Deliberately a single number per recording rather than a replayed state
    /// machine — the EMA, the confirmation timer and the latch live in the engine, and a second
    /// copy of them here would drift from the first.
    /// </summary>
    private static IReadOnlyList<BuildBackCheck> BackCheck(
        IReadOnlyList<(RecordingScan Scan, SteadyWindow Window)> used, GoldenRun run)
    {
        var results = new List<BuildBackCheck>();
        foreach (var (scan, window) in used)
        {
            BuildBackCheck? worst = null;
            foreach (var b in run.Baselines)
            {
                if (b.Sigma <= 0 || !scan.Traces.TryGetValue(b.Key, out var t)) continue;
                var el = scan.ElapsedSec;
                for (int i = 0; i < t.Value.Length; i++)
                {
                    if (!t.Accepted[i]) continue;
                    if (el[i] >= window.FromSec && el[i] <= window.ToSec) continue;
                    double sigmas = Math.Abs(t.Value[i] - b.Mean) / b.Sigma;
                    if (worst is null || sigmas > worst.MaxSigmas)
                        worst = new BuildBackCheck(scan.Path, b.Key, t.DisplayName, sigmas, el[i]);
                }
            }
            if (worst is not null) results.Add(worst);
        }
        return results;
    }

    /// <summary>
    /// Recordings whose level disagrees with the rest. Default-excluded, because the failure this
    /// guards against is silent and permanent: pool one run that was actually leaking and the leak
    /// becomes the baseline, after which nothing can ever detect it.
    /// </summary>
    private static IReadOnlyList<BuildOutlier> FindOutliers(
        IReadOnlyList<(RecordingScan Scan, SteadyWindow Window)> picks,
        IReadOnlyList<string> keys, BaselineBuildOptions options)
    {
        // With two recordings there is no majority to be the odd one out of.
        if (picks.Count < 3) return Array.Empty<BuildOutlier>();

        var outliers = new List<BuildOutlier>();
        foreach (var key in keys)
        {
            var per = new List<(string Path, string Display, double Mean, double Sigma)>();
            foreach (var (scan, window) in picks)
            {
                if (!scan.Traces.TryGetValue(key, out var t)) continue;
                var s = t.StatsOver(scan.ElapsedSec, window.FromSec, window.ToSec);
                if (s.Count >= 2) per.Add((scan.Path, t.DisplayName, s.Mean, s.StdDev));
            }
            if (per.Count < 3) continue;

            double median = Median(per.Select(p => p.Mean));
            double typical = Median(per.Select(p => p.Sigma));
            if (!(typical > 0)) continue;

            foreach (var p in per)
            {
                double sigmas = Math.Abs(p.Mean - median) / typical;
                if (sigmas <= options.OutlierSigmas) continue;
                if (outliers.Any(o => o.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                outliers.Add(new BuildOutlier(p.Path, key, p.Display, p.Mean, median, sigmas));
            }
        }
        return outliers;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
    }

    private static RatioDefinition? FindDefinition(
        IReadOnlyList<(RecordingScan Scan, SteadyWindow Window)> used, string key) =>
        used.Select(u => u.Scan).SelectMany(s => s.Definitions).FirstOrDefault(d => d.Key == key);

    private static GoldenRunRatioRejection Reject(string key, string display, string reason) =>
        new() { Key = key, DisplayName = display, Reason = reason };

    private static BaselineBuildResult Refused(string error, BaselineBuildOptions options,
        IReadOnlyList<BuildOutlier>? outliers = null) => new()
    {
        Run = new GoldenRun { Name = options.RunName },
        Accepted = false,
        Rejected = Array.Empty<GoldenRunRatioRejection>(),
        Outliers = outliers ?? Array.Empty<BuildOutlier>(),
        BackChecks = Array.Empty<BuildBackCheck>(),
        Steadiness = Array.Empty<BuildSteadiness>(),
        Error = error,
    };
}
