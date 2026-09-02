using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>How a <see cref="ProcessClassRule"/> compares its discriminant to its threshold.</summary>
public enum ComparisonOp
{
    GreaterThan,
    LessThan,
}

/// <summary>
/// One test in the classifier's ordered decision list: a line ratio compared to a threshold,
/// naming the process class a frame belongs to when the test passes.
///
/// <para>Deliberately a <em>ratio</em> of two lines rather than an absolute intensity. The
/// three processes on the measured chamber differ in brightness by a factor of ten and the
/// viewport dims by 30 % within one batch, so any absolute level is a moving target; a ratio
/// of two lines in the same frame is not. On the 2026-08-20/21 recordings the two rules
/// <c>Ar 750.4 / O 777.4 &gt; 0.5</c> and <c>Hα 656.3 / O 777.4 &lt; 0.07</c> separated all
/// 226 recordings with no errors and two orders of magnitude of clearance — see
/// <c>docs/process-classification-20260820-21-zh-TW.html</c>.</para>
/// </summary>
public sealed class ProcessClassRule
{
    /// <summary>Class assigned when this test passes. Matched against
    /// <see cref="RatioDefinition.ProcessClass"/>, so the spelling has to agree.</summary>
    public string ClassName { get; set; } = "";

    /// <summary>Human-readable name for the discriminant, used as its column header in the
    /// ratio CSV and in the log. Falls back to the two line labels when empty.</summary>
    public string DisplayName { get; set; } = "";

    public LineRegion Numerator { get; set; } = new();
    public LineRegion Denominator { get; set; } = new();

    public ComparisonOp Op { get; set; } = ComparisonOp.GreaterThan;
    public double Threshold { get; set; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName)
        ? $"{Numerator.Label}/{Denominator.Label}"
        : DisplayName;

    public ProcessClassRule Clone() => new()
    {
        ClassName = ClassName,
        DisplayName = DisplayName,
        Numerator = Numerator.Clone(),
        Denominator = Denominator.Clone(),
        Op = Op,
        Threshold = Threshold,
    };
}

/// <summary>
/// One process class the classifier can name, plus the plasma-gate threshold that class runs at.
///
/// <para><see cref="PlasmaThreshold"/> exists because a single brightness threshold cannot serve
/// processes an order of magnitude apart. On the measured chamber the whole-frame mean runs
/// 240–540 counts in one process, 600–1700 in another and 1670–4300 in a third: a threshold set
/// where the brightest one needs it holds the dimmest one's gate shut for its entire step — the
/// failure recorded in <c>docs/leak-test-20260819-analysis.md</c>, where nothing was evaluated
/// until the leak itself brightened the discharge. Zero or negative means "use the logger's own
/// save-start threshold", which is the behaviour every existing installation already has.</para>
/// </summary>
public sealed class ProcessClassDefinition
{
    public string Name { get; set; } = "";

    /// <summary>Trigger-metric level that counts as plasma-on for this class. ≤ 0 = inherit the
    /// logger's <c>SaveStartThresholdIntensity</c>.</summary>
    public double PlasmaThreshold { get; set; }

    public ProcessClassDefinition Clone() => new() { Name = Name, PlasmaThreshold = PlasmaThreshold };
}

/// <summary>
/// Site configuration for <see cref="ProcessClassifier"/>. Serialized inside
/// <see cref="LeakMonitorSettings"/>.
///
/// <para><see cref="Enabled"/> is false by default and everything downstream is a no-op while it
/// is: a ratio with no <see cref="RatioDefinition.ProcessClass"/> applies to every class, and with
/// no classifier running there is only one class. An upgrade therefore changes nothing until a
/// site configures this.</para>
/// </summary>
public sealed class ProcessClassifierSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// How many gate-open frames to wait before deciding the step's class, then lock it for the
    /// rest of the step. Three frames is ≈ 6 s at the measured 2 s cadence, which the ignition
    /// transient fits inside: the discriminants over a step's first 10 s scatter by 8.7 %, and
    /// over 10–30 s by 1.6 %. Locking is deliberate — a plasma step does not change process
    /// half way through, so letting the verdict move can only add jitter.
    /// </summary>
    public int DecideAfterFrames { get; set; } = 3;

    /// <summary>Ordered decision list; the first rule that passes names the class.</summary>
    public List<ProcessClassRule> Rules { get; set; } = new();

    /// <summary>Class for a step no rule matched. Empty means such a step is
    /// <see cref="ProcessClassifier.Unknown"/> and nothing is judged during it.</summary>
    public string FallbackClass { get; set; } = "";

    /// <summary>The classes this site runs, with their plasma-gate thresholds.</summary>
    public List<ProcessClassDefinition> Classes { get; set; } = new();

    public ProcessClassifierSettings Clone() => new()
    {
        Enabled = Enabled,
        DecideAfterFrames = DecideAfterFrames,
        FallbackClass = FallbackClass,
        Rules = Rules.Select(r => r.Clone()).ToList(),
        Classes = Classes.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>What one frame said about which process is running.</summary>
public readonly record struct ProcessClassReading(
    string ClassName,
    bool Measurable,
    IReadOnlyList<ProcessDiscriminant> Discriminants);

/// <summary>One rule's measured value on one frame. Recorded whatever the verdict was, because
/// the first time a step lands near a threshold the number is what says whether the threshold
/// still fits this chamber — a verdict on its own cannot answer that.</summary>
public readonly record struct ProcessDiscriminant(string Label, double Value, string ClassName);

/// <summary>
/// Names the process a plasma step is running, from the spectrum alone.
///
/// <para>Pure and stateless: <see cref="Evaluate"/> reads one frame and returns a verdict plus
/// every rule's measured value. The per-step state machine that decides <em>when</em> to evaluate
/// and then locks the answer lives in <see cref="LeakMonitorEngine"/>, next to the plasma gate
/// whose transitions define a step.</para>
///
/// <para>It exists because this chamber interleaves three processes on a cycle of about three
/// minutes — <c>B(156 s) → (A → C) × N</c> — and the leak monitor holds one active Golden Run.
/// Judging every step against one baseline compares three different plasmas to whichever of them
/// happened to be captured. Reading the class off the spectrum keeps that self-contained: no host
/// integration, and the answer is derived from the same frame the ratios are.</para>
///
/// <para>A step no rule matched is <see cref="Unknown"/> and nothing is judged during it. That is
/// the same rule <c>PlasmaGate</c> follows for an unusable gate: "we cannot tell" is not a
/// measurement, and guessing produces an alarm nobody can attribute.</para>
/// </summary>
public sealed class ProcessClassifier
{
    /// <summary>Class of a step no rule matched and no fallback named. Never matches a
    /// <see cref="RatioDefinition.ProcessClass"/>, so class-scoped ratios stand down.</summary>
    public const string Unknown = "Unknown";

    private readonly ProcessClassRule[] _rules;
    private readonly string _fallback;
    private readonly Dictionary<string, double> _thresholds;

    public ProcessClassifier(ProcessClassifierSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        _rules = settings.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.ClassName))
            .Select(r => r.Clone())
            .ToArray();
        _fallback = string.IsNullOrWhiteSpace(settings.FallbackClass)
            ? Unknown
            : settings.FallbackClass.Trim();
        DecideAfterFrames = Math.Max(1, settings.DecideAfterFrames);
        _thresholds = settings.Classes
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().PlasmaThreshold,
                          StringComparer.OrdinalIgnoreCase);
        ClassNames = _thresholds.Keys
            .Concat(_rules.Select(r => r.ClassName.Trim()))
            .Concat(_fallback == Unknown ? Array.Empty<string>() : new[] { _fallback })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int DecideAfterFrames { get; }

    /// <summary>Every class this configuration can name, for the log and the UI.</summary>
    public IReadOnlyList<string> ClassNames { get; }

    /// <summary>
    /// False when no rule survived validation — the classifier would name every step the
    /// fallback, which is worse than not running at all because it looks like it worked.
    /// </summary>
    public bool IsUsable => _rules.Length > 0;

    /// <summary>One-line description for the system log.</summary>
    public string Description => IsUsable
        ? string.Join("; ", _rules.Select(r =>
              $"{r.Label} {(r.Op == ComparisonOp.GreaterThan ? ">" : "<")} {r.Threshold:0.###} → {r.ClassName}"))
          + $"; else {_fallback}"
        : "no rules configured";

    /// <summary>
    /// This class's plasma-gate threshold, or <paramref name="fallback"/> when the class is
    /// unknown to the configuration or set to inherit.
    /// </summary>
    public double PlasmaThresholdFor(string? className, double fallback) =>
        className is not null &&
        _thresholds.TryGetValue(className, out var t) && t > 0
            ? t
            : fallback;

    /// <summary>
    /// The lowest plasma threshold any class runs at, never above <paramref name="fallback"/>.
    ///
    /// <para>Step boundaries are detected with this rather than with a per-class threshold,
    /// because the class is not known until several frames into the step — and a boundary
    /// detector using the brightest class's threshold would never see the dimmest class's step
    /// begin, so that class could never be classified and would stay dark for ever. The
    /// per-class threshold then refines the gate once the class is known.</para>
    /// </summary>
    public double BoundaryThreshold(double fallback)
    {
        double lowest = fallback;
        foreach (var t in _thresholds.Values)
            if (t > 0 && t < lowest) lowest = t;
        return lowest;
    }

    /// <summary>
    /// Names the class this frame belongs to, and reports every rule's measured value.
    /// <see cref="ProcessClassReading.Measurable"/> is false when no rule could be evaluated at
    /// all (lines off the axis, blank frame); the caller must treat that as
    /// <see cref="Unknown"/> rather than falling through to the fallback class.
    /// </summary>
    public ProcessClassReading Evaluate(float[]? wavelengths, float[]? intensities)
    {
        if (!IsUsable) return new ProcessClassReading(Unknown, false, Array.Empty<ProcessDiscriminant>());

        var values = new List<ProcessDiscriminant>(_rules.Length);
        string? verdict = null;
        bool anyMeasured = false;

        foreach (var rule in _rules)
        {
            double v = Discriminant(wavelengths, intensities, rule);
            values.Add(new ProcessDiscriminant(rule.Label, v, rule.ClassName.Trim()));
            if (double.IsNaN(v)) continue;
            anyMeasured = true;
            if (verdict is not null) continue;   // first match wins; keep measuring for the record
            bool pass = rule.Op == ComparisonOp.GreaterThan
                ? v > rule.Threshold
                : v < rule.Threshold;
            if (pass) verdict = rule.ClassName.Trim();
        }

        if (!anyMeasured)
            return new ProcessClassReading(Unknown, false, values);

        return new ProcessClassReading(verdict ?? _fallback, true, values);
    }

    /// <summary>
    /// One rule's ratio on one frame, or NaN when either line could not be measured.
    ///
    /// <para>Extracted through the same <see cref="LineIntensityExtractor"/> the ratios use, so a
    /// discriminant and a monitored ratio built on the same line always agree about what that
    /// line reads. A non-positive denominator gives NaN rather than a signed infinity: the
    /// reference lines a discriminant divides by are bright by construction, so a denominator at
    /// or below zero means the frame is blank, not that the ratio is enormous.</para>
    /// </summary>
    private static double Discriminant(float[]? wl, float[]? inten, ProcessClassRule rule)
    {
        var num = LineIntensityExtractor.Extract(wl!, inten!, rule.Numerator);
        var den = LineIntensityExtractor.Extract(wl!, inten!, rule.Denominator);
        if (!num.HasValue || !den.HasValue || den.Value <= 0) return double.NaN;
        return num.Value / den.Value;
    }
}
