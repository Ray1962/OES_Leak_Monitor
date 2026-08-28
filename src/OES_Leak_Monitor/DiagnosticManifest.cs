using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>Why one thing the bundle would normally carry is not in it.</summary>
public enum BundleOmission
{
    /// <summary>It is present and complete.</summary>
    None = 0,
    /// <summary>Larger than the bundle's cap. The manifest names where the original is.</summary>
    TooLarge,
    /// <summary>Nothing of this kind exists yet — SECS never started, no recording today.</summary>
    NotPresent,
    /// <summary>It exists and could not be read. The message says what Windows said.</summary>
    Unreadable,
}

/// <summary>One entry in the bundle, present or not.</summary>
public sealed class BundleItem
{
    public required string Name { get; init; }
    public string SourcePath { get; init; } = "";
    public long Bytes { get; init; }
    public BundleOmission Omitted { get; init; } = BundleOmission.None;
    public string Note { get; init; } = "";

    /// <summary>
    /// The file was still being written when it was copied, so it ends mid-run. Set for the live
    /// recording, which is the one worth having and the one most likely to mislead: a truncated
    /// full-spectrum CSV re-baselines perfectly happily and gives an answer nobody can tell is wrong.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Rows copied, for a CSV. Lets the reader see where a truncated file stops.</summary>
    public long? Rows { get; init; }

    public bool Included => Omitted == BundleOmission.None;
}

/// <summary>
/// What the machine was, at the moment the bundle was taken. Everything here is read, nothing is
/// computed — the same discipline <see cref="SecsBridge"/> keeps, and for the same reason: a
/// number derived twice is a number that will eventually disagree with itself.
/// </summary>
public sealed class DiagnosticEnvironment
{
    public string MachineName { get; init; } = "";
    public string UserName { get; init; } = "";
    public string AppVersion { get; init; } = "";

    /// <summary>The commit this .exe was built from, out of AssemblyInformationalVersion.</summary>
    public string GitCommit { get; init; } = "";

    /// <summary>Package id → version, read off the loaded assemblies rather than the csproj.</summary>
    public Dictionary<string, string> Packages { get; init; } = new();

    public string ConfigDirectory { get; init; } = "";
    public string LogDirectory { get; init; } = "";
    public string DataDirectory { get; init; } = "";
    public string AppFolder { get; init; } = "";

    // --- the device, which is the half the load probe deliberately does not re-enter ---
    public bool DeviceConnected { get; init; }
    public bool IsTestMode { get; init; }
    public string SerialNumber { get; init; } = "";
    public string LastConnectionResult { get; init; } = "";
    public string ResolvedDllPath { get; init; } = "";
    public bool IsAcquiring { get; init; }

    // --- what decides whether anything is measured at all ---
    public string TriggerMode { get; init; } = "";
    public double SaveStartThreshold { get; init; }
    public bool LoggerEnabled { get; init; }
    public string LoggerState { get; init; } = "";
    public bool PlasmaGateUsable { get; init; }
    public string PlasmaGateDescription { get; init; } = "";

    // --- what any number in the ratio CSV is relative to ---
    public string ActiveGoldenRun { get; init; } = "";
    public DateTime? GoldenRunCapturedAt { get; init; }
    public string ActiveCalibration { get; init; } = "";

    // --- (b): the SECS half ---
    public bool SecsEnabled { get; init; }
    public int ChamberCode { get; init; }
    public string SecsState { get; init; } = "";
    public string SecsStatusText { get; init; } = "";
    public string SecsLastError { get; init; } = "";
}

/// <summary>
/// The bundle's own account of itself: what it is, what it holds, and — the part that earns the
/// file — what it does not hold and why.
///
/// <para>A manifest listing only what is present reads as complete whatever is missing. The two
/// ways this bundle can mislead are both omissions: a recording left out for size, and one copied
/// while it was still being written. Both are recorded here, or the bundle lies.</para>
/// </summary>
public sealed class DiagnosticManifest
{
    public const int SchemaVersion = 1;

    public int Schema { get; init; } = SchemaVersion;
    public DateTime CreatedLocal { get; init; }
    public string CreatedUtcOffset { get; init; } = "";
    public required DiagnosticEnvironment Environment { get; init; }
    public List<BundleItem> Items { get; init; } = new();

    public IEnumerable<BundleItem> Missing => Items.Where(i => !i.Included);

    /// <summary>
    /// The same content as a person can read without a JSON tool, generated from this same object
    /// — never assembled separately. Two renderings that can disagree are worse than one.
    /// </summary>
    public string ToReadme()
    {
        var sb = new StringBuilder();
        var e = Environment;

        sb.AppendLine("OES Leak Monitor - diagnostic bundle");
        sb.AppendLine(new string('=', 66));
        sb.AppendLine($"Taken      : {CreatedLocal:yyyy-MM-dd HH:mm:ss} {CreatedUtcOffset}");
        sb.AppendLine($"Machine    : {e.MachineName}   user {e.UserName}");
        sb.AppendLine($"App        : {e.AppVersion}  commit {Short(e.GitCommit)}");
        foreach (var (id, v) in e.Packages.OrderBy(p => p.Key))
            sb.AppendLine($"             {id} {v}");
        sb.AppendLine();

        sb.AppendLine("Paths as this machine actually resolved them");
        sb.AppendLine(new string('-', 66));
        sb.AppendLine($"  app     {e.AppFolder}");
        sb.AppendLine($"  config  {e.ConfigDirectory}");
        sb.AppendLine($"  logs    {e.LogDirectory}");
        sb.AppendLine($"  data    {e.DataDirectory}");
        sb.AppendLine("  (the data folder is wherever the operator pointed the logger; the other");
        sb.AppendLine("   three are under %AppData%. That split is why this bundle exists.)");
        sb.AppendLine();

        sb.AppendLine("Spectrometer");
        sb.AppendLine(new string('-', 66));
        sb.AppendLine($"  connected {e.DeviceConnected}   acquiring {e.IsAcquiring}");
        sb.AppendLine($"  serial    {e.SerialNumber}");
        if (e.IsTestMode)
        {
            sb.AppendLine("  TEST MODE - the frames are synthetic. Any CSV written during this");
            sb.AppendLine("  session carries the ordinary prefix and is NOT measurement.");
        }
        if (!string.IsNullOrWhiteSpace(e.LastConnectionResult))
            sb.AppendLine($"  connect   {e.LastConnectionResult}");
        if (!string.IsNullOrWhiteSpace(e.ResolvedDllPath))
            sb.AppendLine($"  dll       {e.ResolvedDllPath}");
        sb.AppendLine($"  See {OesLoadProbe.FileName} for whether the native DLLs load at all.");
        sb.AppendLine();

        sb.AppendLine("What was being recorded, and what it is measured against");
        sb.AppendLine(new string('-', 66));
        sb.AppendLine($"  recorder armed {e.LoggerEnabled}   state {e.LoggerState}");
        sb.AppendLine($"  trigger        {e.TriggerMode} above {e.SaveStartThreshold:0.#}");
        sb.AppendLine($"  plasma gate    {(e.PlasmaGateUsable ? e.PlasmaGateDescription : "UNUSABLE - absolute-intensity ratios run ungated")}");
        sb.AppendLine($"  golden run     {Or(e.ActiveGoldenRun, "none - nothing has a baseline")}"
                      + (e.GoldenRunCapturedAt is { } t ? $"  captured {t:yyyy-MM-dd HH:mm}" : ""));
        sb.AppendLine($"  calibration    {Or(e.ActiveCalibration, "none - leak rate is not estimated")}");
        sb.AppendLine();

        sb.AppendLine("SECS/GEM");
        sb.AppendLine(new string('-', 66));
        sb.AppendLine($"  enabled {e.SecsEnabled}   chamber code {(e.ChamberCode == 0 ? "00 (not configured)" : e.ChamberCode.ToString("00"))}");
        sb.AppendLine($"  state   {Or(e.SecsState, "never started")}  {e.SecsStatusText}");
        if (!string.IsNullOrWhiteSpace(e.SecsLastError))
            sb.AppendLine($"  error   {e.SecsLastError}");
        sb.AppendLine();

        sb.AppendLine("Contents");
        sb.AppendLine(new string('-', 66));
        foreach (var item in Items.Where(i => i.Included))
        {
            sb.AppendLine($"  {item.Name,-40} {item.Bytes,12:N0} bytes"
                          + (item.Rows is { } r ? $"  {r:N0} rows" : ""));
            if (item.Truncated)
            {
                sb.AppendLine("      TRUNCATED - copied while it was still being written. It ends");
                sb.AppendLine("      mid-run. Do not build a baseline from it without saying so.");
            }
            if (!string.IsNullOrWhiteSpace(item.Note))
                sb.AppendLine($"      {item.Note}");
        }
        sb.AppendLine();

        sb.AppendLine("NOT in this bundle");
        sb.AppendLine(new string('-', 66));
        var missing = Missing.ToList();
        if (missing.Count == 0)
        {
            sb.AppendLine("  Nothing was left out.");
        }
        else
        {
            foreach (var item in missing)
            {
                sb.AppendLine($"  {item.Name}");
                sb.AppendLine($"      {Describe(item)}");
                if (!string.IsNullOrWhiteSpace(item.SourcePath))
                    sb.AppendLine($"      original: {item.SourcePath}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Every settings file here has its whole access-control section removed, and");
        sb.AppendLine("every stored password hash stripped wherever it appeared - so no account,");
        sb.AppendLine("and no credential, is in the config/ folder at all. Usernames DO remain");
        sb.AppendLine("in the logs, deliberately: an audit entry that cannot say who cleared an");
        sb.AppendLine("alarm is not an audit entry.");
        return sb.ToString();
    }

    private static string Describe(BundleItem item) => item.Omitted switch
    {
        BundleOmission.TooLarge => $"Left out: {item.Bytes:N0} bytes, over this bundle's cap. "
                                 + "It was not truncated or sampled to fit -- a shortened "
                                 + "full-spectrum CSV re-baselines happily and answers wrongly. "
                                 + "Fetch the original if it is needed.",
        BundleOmission.NotPresent => Or(item.Note, "Does not exist on this machine."),
        BundleOmission.Unreadable => $"Could not be read: {item.Note}",
        _ => item.Note,
    };

    private static string Or(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Short(string commit) =>
        commit.Length > 12 ? commit[..12] : commit;
}
