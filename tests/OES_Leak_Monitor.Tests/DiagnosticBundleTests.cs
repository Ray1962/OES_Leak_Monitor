using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OES_Leak_Monitor;
using System.Threading.Tasks;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The one-click diagnostic bundle.
///
/// <para>The easy test — "the zip contains these entries" — is the one worth least: it only shows
/// that the good path is good. What can actually hurt someone is an <b>omission the manifest does
/// not confess</b>. A recording dropped for size, or copied while it was still being written,
/// leaves a bundle that looks complete; a baseline built from it comes back with an answer nobody
/// downstream can tell is wrong. So three of the four facts here are negative ones.</para>
/// </summary>
public class DiagnosticBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "oeslm-diagbundle-" + Guid.NewGuid().ToString("N"));

    private readonly DateTime _now = new(2026, 8, 28, 14, 30, 00, DateTimeKind.Local);

    public DiagnosticBundleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // --- fixture -------------------------------------------------------------

    private string Folder(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private string DayFolder() => Folder("data", _now.ToString("yyyyMM"), _now.ToString("dd"));

    private static AppSettings WithAUser()
    {
        var s = new AppSettings();
        s.AccessControl.Users.Add(new UserAccount
        {
            Username = "engineer1",
            PasswordHash = "PLEASE-DO-NOT-COPY-ME",
            Role = UserRole.Engineer,
        });
        return s;
    }

    /// <summary>A full-spectrum CSV of roughly the requested size, in the wide format.</summary>
    private string WriteSpectrum(string name, int rows)
    {
        var path = Path.Combine(DayFolder(), name);
        using var w = new StreamWriter(path);
        w.WriteLine("WaveLength," + string.Join(",", Enumerable.Range(0, 200).Select(i => 200 + i * 3)));
        for (int r = 0; r < rows; r++)
            w.WriteLine($"14:30:{r % 60:00}.000," +
                        string.Join(",", Enumerable.Range(0, 200).Select(i => 1000 + i)));
        return path;
    }

    private DiagnosticInputs Inputs(Func<string>? probe = null, long cap = 200L * 1024 * 1024) => new()
    {
        Environment = new DiagnosticEnvironment { MachineName = "FAB-PC-01", ChamberCode = 3 },
        Settings = WithAUser(),
        DataDirectory = Folder("data"),
        ConfigDirectory = Folder("config"),
        LogDirectory = Folder("logs"),
        Probe = probe,
        MaxBundleBytes = cap,
    };

    private DiagnosticBundleResult Run(DiagnosticInputs inputs) =>
        DiagnosticBundle.Write(Path.Combine(_root, "out", "diag.zip"), inputs, _now);

    private static string[] EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    // --- 1. an omission the manifest confesses -------------------------------

    /// <summary>
    /// Over the cap the recording is left out <b>whole</b>, and the manifest says so and says
    /// where the original is. It is never shortened or sampled to fit: the EMA, the EWMA variance
    /// and the σ/min slope are all dt-bound, so a trimmed full-spectrum CSV re-baselines happily
    /// and answers wrongly, with nothing in the file to warn the reader.
    /// </summary>
    [Fact]
    public void AnOversizedRecordingIsLeftOutWholeAndNamed()
    {
        var source = WriteSpectrum("P_OES1_0828143000.csv", rows: 4000);
        // A cap far below anything this file can compress to.
        var result = Run(Inputs(cap: 4 * 1024));

        Assert.DoesNotContain(EntryNames(result.Path), n => n.EndsWith("P_OES1_0828143000.csv"));

        var dropped = Assert.Single(result.Manifest.Missing,
            i => i.Omitted == BundleOmission.TooLarge);
        Assert.Equal(source, dropped.SourcePath);
        Assert.True(dropped.Bytes > 0, "the manifest has to say how big the thing it skipped was");

        // And the human-readable half must carry it too — a reader who never opens the JSON is
        // exactly the reader who would otherwise assume the bundle is complete.
        var readme = result.Manifest.ToReadme();
        Assert.Contains("NOT in this bundle", readme);
        Assert.Contains("P_OES1_0828143000.csv", readme);
        Assert.Contains(source, readme);
    }

    // --- 2. an omission of a different kind: the file that is still growing ---

    /// <summary>
    /// The newest recording is usually the one the logger has open, which is precisely why it is
    /// worth taking — it covers the problem being reported — and precisely why it has to be
    /// marked. Copied, not skipped; labelled, not silently trusted.
    /// </summary>
    [Fact]
    public void ARecordingStillBeingWrittenIsCopiedAndMarkedTruncated()
    {
        var path = WriteSpectrum("P_OES1_0828143000.csv", rows: 50);

        // Hold it the way DualIntensityLogger does: readable and writable by others.
        using var held = new FileStream(path, FileMode.Open, FileAccess.Write,
                                        FileShare.ReadWrite | FileShare.Delete);

        var result = Run(Inputs());

        var item = Assert.Single(result.Manifest.Items,
            i => i.Name.EndsWith("P_OES1_0828143000.csv"));
        Assert.True(item.Included, "a locked file must be copied, not skipped");
        Assert.True(item.Truncated, "a file another handle holds open ends mid-run and must say so");
        Assert.Equal(51, item.Rows);   // header + 50 rows, so a reader can see where it stops
        Assert.Contains("TRUNCATED", result.Manifest.ToReadme());
    }

    // --- 3. nothing that must not leave the machine --------------------------

    /// <summary>
    /// Scans <b>every entry</b>, not just <c>settings.json</c>. The backups beside it are whole
    /// copies of the same file and carry the same secret; they are redacted through the same rule
    /// rather than by a second one written for them.
    /// </summary>
    [Fact]
    public void NoEntryAnywhereInTheBundleCarriesACredential()
    {
        var config = Folder("config");
        File.WriteAllText(Path.Combine(config, "settings.json.bak-20260810"),
            """{"accessControl":{"users":[{"username":"engineer1","passwordHash":"LEAKED"}]}}""");
        // A backup an older build wrote that no longer deserialises into AppSettings. It must
        // still be redacted, or left out — never carried raw.
        File.WriteAllText(Path.Combine(config, "settings.json.bak-20260701"),
            """{"legacy":true,"device1":{"passwordHash":"ALSO-LEAKED"}}""");

        var result = Run(Inputs());

        using var zip = ZipFile.OpenRead(result.Path);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LEAKED", text, StringComparison.Ordinal);
        }

        // The usernames stay: the audit log's whole value is knowing who acknowledged what.
        Assert.Contains(zip.Entries, e => e.FullName == "config/settings.json");
    }

    // --- 4. the probe failing is a finding, not a dead end -------------------

    /// <summary>
    /// A probe that throws must not take the bundle with it. The machine being diagnosed is
    /// already misbehaving — abandoning the diagnostic path when something fails is the same
    /// mistake that made the 2026-08-17 session unexplainable.
    /// </summary>
    [Fact]
    public void AProbeThatThrowsStillLeavesACompleteBundle()
    {
        var result = Run(Inputs(probe: () => throw new DllNotFoundException("UserApplication.dll")));

        Assert.Contains(EntryNames(result.Path), n => n == OesLoadProbe.FileName);
        Assert.Contains(EntryNames(result.Path), n => n == DiagnosticBundle.ManifestName);
        Assert.Contains(EntryNames(result.Path), n => n == DiagnosticBundle.ReadmeName);

        using var zip = ZipFile.OpenRead(result.Path);
        using var reader = new StreamReader(zip.GetEntry(OesLoadProbe.FileName)!.Open());
        var text = reader.ReadToEnd();
        Assert.Contains("DllNotFoundException", text);
        Assert.Contains("UserApplication.dll", text);
    }

    // --- the filename, which is the only thing seen before the zip is opened --

    /// <summary>
    /// The machine name leads because it cannot lie. A spectrometer serial reads
    /// <c>TEST_MODE_SIMULATOR</c> in exactly the failure this bundle is for, so it is evidence
    /// inside the manifest and never a label on the outside.
    /// </summary>
    [Fact]
    public void TheFileNameIdentifiesTheMachineAndSaysWhenThereIsNoChamberCode()
    {
        Assert.Equal("diag_FAB-PC-01_cc03_20260828_143000.zip",
            DiagnosticBundle.FileNameFor("FAB-PC-01", 3, _now));

        // Chamber code 0 is "not configured", so its absence from the name is itself information.
        Assert.Equal("diag_FAB-PC-01_20260828_143000.zip",
            DiagnosticBundle.FileNameFor("FAB-PC-01", 0, _now));
    }

    // --- the button, not just the builder -----------------------------------

    /// <summary>
    /// End to end through the view-model, because everything above this tests the builder and
    /// nothing tests the thing an operator actually presses. Covers the two steps that only exist
    /// here: the bundle is named and placed by itself (no dialog — the operator is on the phone),
    /// and the path reaches the audit log as well as the screen, since the Explorer window gets
    /// closed and "where is it" is always the next sentence.
    /// </summary>
    [Fact]
    public async Task PressingTheButtonWritesABundleAndRecordsWhereItWent()
    {
        var appData = Folder("appdata");
        var logDir = Folder("appdata", "Logs");
        using var log = new SystemLogger(logDir);
        WriteSpectrum("P_OES1_0828143000.csv", rows: 10);

        string? revealed = null;
        var vm = new DiagnosticsViewModel(appData, () => Inputs(probe: () => "probe ok"),
            log, System.Windows.Threading.Dispatcher.CurrentDispatcher,
            reveal: (file, _) => revealed = file);

        await vm.RunAsync();

        Assert.False(vm.IsBusy);
        Assert.True(File.Exists(vm.LastBundlePath), "the bundle should be where the view-model says");
        Assert.Equal(vm.LastBundlePath, revealed);

        // Named for the machine, dropped in the app's own folder — nothing was asked of the user.
        Assert.Equal(Path.Combine(appData, DiagnosticsViewModel.FolderName),
            Path.GetDirectoryName(vm.LastBundlePath));
        Assert.StartsWith("diag_FAB-PC-01_cc03_", Path.GetFileName(vm.LastBundlePath));

        log.Dispose();   // flush the queue before reading the file back
        var written = Directory.GetFiles(logDir, "*.csv").SelectMany(File.ReadAllLines).ToArray();
        Assert.Contains(written, l => l.Contains("DiagnosticBundleCreated") &&
                                      l.Contains(vm.LastBundlePath));
    }

    /// <summary>Old bundles are dropped as new ones arrive; this folder is on the system disk.</summary>
    [Fact]
    public void OnlyTheNewestFewBundlesAreKept()
    {
        var folder = Folder("out");
        foreach (var day in Enumerable.Range(1, 8))
            File.WriteAllText(Path.Combine(folder, $"diag_PC_202608{day:00}_120000.zip"), "x");

        DiagnosticBundle.Prune(folder, keep: 5);

        var left = Directory.GetFiles(folder, "diag_*.zip").Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(5, left.Length);
        Assert.Contains("diag_PC_20260808_120000.zip", left);
        Assert.DoesNotContain("diag_PC_20260801_120000.zip", left);
    }
}
