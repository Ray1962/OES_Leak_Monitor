using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OES_Leak_Monitor;

/// <summary>Everything the bundle needs, so the writing has no idea where any of it came from.</summary>
public sealed class DiagnosticInputs
{
    public required DiagnosticEnvironment Environment { get; init; }

    /// <summary>Live settings, redacted on the way in. Same object the Save button would write.</summary>
    public required AppSettings Settings { get; init; }

    /// <summary>Resolved data folder — the configured one, not the AppData fallback.</summary>
    public required string DataDirectory { get; init; }

    public required string ConfigDirectory { get; init; }
    public required string LogDirectory { get; init; }

    /// <summary>Where <see cref="SecsBridge"/> put the two profiles; empty when it never started.</summary>
    public string SecsProfileTemplatePath { get; init; } = "";
    public string SecsEffectiveProfilePath { get; init; } = "";

    /// <summary>
    /// The load probe, injected. Under test it is a stub — including one that throws, because a
    /// bundle whose probe died is exactly the bundle someone will be holding.
    /// </summary>
    public Func<string>? Probe { get; init; }

    /// <summary>Days of system and SECS log to carry. A week is about 60 KB.</summary>
    public int LogDays { get; init; } = 7;

    /// <summary>Cap on the finished .zip. Past it the recording is dropped, never shortened.</summary>
    public long MaxBundleBytes { get; init; } = 200L * 1024 * 1024;
}

/// <summary>What came of one attempt.</summary>
public sealed class DiagnosticBundleResult
{
    public required string Path { get; init; }
    public required DiagnosticManifest Manifest { get; init; }
    public long Bytes { get; init; }
}

/// <summary>
/// The one-click bundle: everything needed to answer "it silently stopped measuring" or "the host
/// says it never got that alarm", in one file an operator can attach to an email.
///
/// <para>It exists because the two questions are answered from four places that no one copies
/// together — the data folder the operator chose, the logs and configuration under
/// <c>%AppData%</c>, the SECS profile and traffic log, and one fact about the native DLLs that
/// lives nowhere at all until something probes for it. <see cref="ConfigSnapshot"/> and
/// <see cref="SystemLogMirror"/> already fixed the narrower version of this for a day folder;
/// this is the same argument for a whole machine, at the moment somebody is on the phone.</para>
///
/// <para>Deliberately not configurable. Every knob here would be one more thing to have set wrong
/// on the machine you are trying to diagnose.</para>
/// </summary>
public static class DiagnosticBundle
{
    public const string ManifestName = "manifest.json";
    public const string ReadmeName = "README.txt";

    /// <summary>Bundles kept in the output folder. Older ones are deleted as new ones arrive.</summary>
    public const int KeepBundles = 5;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Name of the bundle. The machine name leads because it is the one identifier that cannot
    /// lie: a spectrometer serial reads <c>TEST_MODE_SIMULATOR</c> in precisely the failure this
    /// bundle is for, so it goes in the manifest as evidence, never in the filename as a label.
    /// The chamber code is appended only when it is set, so its absence says "no MES here".
    /// </summary>
    public static string FileNameFor(string machineName, int chamberCode, DateTime nowLocal)
    {
        var machine = Sanitize(string.IsNullOrWhiteSpace(machineName) ? "unknown" : machineName);
        var cc = chamberCode > 0 ? $"_cc{chamberCode:00}" : "";
        return $"diag_{machine}{cc}_{nowLocal:yyyyMMdd_HHmmss}.zip";
    }

    /// <summary>
    /// Writes one bundle. Never throws for anything it collects — a folder that cannot be read
    /// becomes an entry in the manifest saying so, because the bundle is most needed on a machine
    /// where things are already failing.
    /// </summary>
    public static DiagnosticBundleResult Write(string targetPath, DiagnosticInputs inputs,
                                               DateTime nowLocal)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var items = new List<BundleItem>();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // Pass one: everything small. The recording is added afterwards so the finished size can
        // decide its fate without having to guess a compression ratio in advance.
        using (var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create))
        {
            AddProbe(zip, items, inputs);
            AddSettings(zip, items, inputs);
            AddSettingsBackups(zip, items, inputs);
            AddLogs(zip, items, inputs, nowLocal);
            AddSecsProfiles(zip, items, inputs);
            AddRatioCsvs(zip, items, inputs, nowLocal);
        }

        var recording = NewestSpectrum(inputs, nowLocal);
        string? recordingEntry = null;
        if (recording is null)
        {
            items.Add(new BundleItem
            {
                Name = "recording (full spectrum)",
                Omitted = BundleOmission.NotPresent,
                Note = "No full-spectrum CSV in today's folder. Either nothing crossed the save "
                     + "threshold today, or the recorder is disarmed - see the trigger above.",
            });
        }
        else
        {
            using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Update);
            recordingEntry = "data/" + recording.Name;
            var item = CopyInto(zip, recordingEntry, recording.FullName, countRows: true);
            items.Add(item);
        }

        // Now the size is known. Over the cap, the recording comes back out whole: it is never
        // shortened to fit, because a truncated full-spectrum CSV re-baselines perfectly happily
        // and gives an answer no one downstream can tell is wrong.
        var length = new FileInfo(targetPath).Length;
        if (recordingEntry is not null && length > inputs.MaxBundleBytes)
        {
            using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Update);
            zip.GetEntry(recordingEntry)?.Delete();
            var dropped = items.Single(i => i.Name == recordingEntry);
            items.Remove(dropped);
            items.Add(new BundleItem
            {
                Name = dropped.Name,
                SourcePath = dropped.SourcePath,
                Bytes = dropped.Bytes,
                Omitted = BundleOmission.TooLarge,
            });
        }

        var manifest = new DiagnosticManifest
        {
            CreatedLocal = nowLocal,
            CreatedUtcOffset = FormatOffset(TimeZoneInfo.Local.GetUtcOffset(nowLocal)),
            Environment = inputs.Environment,
            Items = items.OrderBy(i => i.Included ? 0 : 1).ThenBy(i => i.Name).ToList(),
        };

        using (var zip = ZipFile.Open(targetPath, ZipArchiveMode.Update))
        {
            WriteText(zip, ManifestName, JsonSerializer.Serialize(manifest, Json));
            WriteText(zip, ReadmeName, manifest.ToReadme());
        }

        return new DiagnosticBundleResult
        {
            Path = targetPath,
            Manifest = manifest,
            Bytes = new FileInfo(targetPath).Length,
        };
    }

    /// <summary>Deletes all but the newest <see cref="KeepBundles"/> bundles in the folder.</summary>
    public static IReadOnlyList<string> Prune(string folder, int keep = KeepBundles)
    {
        var removed = new List<string>();
        if (!Directory.Exists(folder)) return removed;
        try
        {
            var stale = new DirectoryInfo(folder).GetFiles("diag_*.zip")
                                                 .OrderByDescending(f => f.Name)
                                                 .Skip(Math.Max(keep, 1));
            foreach (var f in stale)
            {
                try { f.Delete(); removed.Add(f.FullName); }
                catch { /* a bundle someone still has open is not worth failing over */ }
            }
        }
        catch { /* the folder is the caller's, not ours to insist on */ }
        return removed;
    }

    // ------------------------------------------------------------------ collectors

    private static void AddProbe(ZipArchive zip, List<BundleItem> items, DiagnosticInputs inputs)
    {
        if (inputs.Probe is null)
        {
            items.Add(new BundleItem
            {
                Name = OesLoadProbe.FileName,
                Omitted = BundleOmission.NotPresent,
                Note = "No probe was supplied to this bundle.",
            });
            return;
        }
        string text;
        try { text = inputs.Probe(); }
        catch (Exception ex)
        {
            // The probe failing is a finding about the machine, not a reason to abandon the
            // bundle -- which is the same mistake (bail on the diagnostic path) that made the
            // 2026-08-17 session unexplainable in the first place.
            text = $"The load probe threw before it could report:\n  "
                 + $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n";
        }
        items.Add(WriteText(zip, OesLoadProbe.FileName, text));
    }

    private static void AddSettings(ZipArchive zip, List<BundleItem> items, DiagnosticInputs inputs)
    {
        try
        {
            items.Add(WriteText(zip, "config/settings.json", ConfigSnapshot.Redact(inputs.Settings)));
        }
        catch (Exception ex)
        {
            items.Add(Unreadable("config/settings.json", "", ex));
        }
    }

    /// <summary>
    /// The <c>settings.json.bak-*</c> files. They carry the same secrets as the live one and are
    /// redacted the same way -- through <see cref="ConfigSnapshot.RedactJsonText"/> rather than by
    /// deserialising, since a backup from an older build need not round-trip any more.
    /// </summary>
    private static void AddSettingsBackups(ZipArchive zip, List<BundleItem> items,
                                           DiagnosticInputs inputs)
    {
        foreach (var file in SafeFiles(inputs.ConfigDirectory, "settings.json.bak-*"))
        {
            var name = "config/" + file.Name;
            try
            {
                var redacted = ConfigSnapshot.RedactJsonText(File.ReadAllText(file.FullName));
                if (redacted is null)
                {
                    items.Add(new BundleItem
                    {
                        Name = name,
                        SourcePath = file.FullName,
                        Bytes = file.Length,
                        Omitted = BundleOmission.Unreadable,
                        Note = "did not parse as JSON, so it could not be redacted and was left out",
                    });
                    continue;
                }
                items.Add(WriteText(zip, name, redacted));
            }
            catch (Exception ex)
            {
                items.Add(Unreadable(name, file.FullName, ex));
            }
        }
    }

    private static void AddLogs(ZipArchive zip, List<BundleItem> items, DiagnosticInputs inputs,
                                DateTime nowLocal)
    {
        var cutoff = nowLocal.Date.AddDays(-Math.Max(inputs.LogDays, 1) + 1);
        var any = false;
        foreach (var file in SafeFiles(inputs.LogDirectory, "*"))
        {
            if (LogDayOf(file.Name) is not { } day || day < cutoff) continue;
            any = true;
            items.Add(CopyInto(zip, "logs/" + file.Name, file.FullName, countRows: false));
        }
        if (!any)
            items.Add(new BundleItem
            {
                Name = "logs",
                SourcePath = inputs.LogDirectory,
                Omitted = BundleOmission.NotPresent,
                Note = $"No system or SECS log in the last {inputs.LogDays} days.",
            });
    }

    /// <summary>
    /// The day a log file belongs to: <c>yyMMddHH.csv</c> for the audit log,
    /// <c>secs_yyyyMMdd.log</c> for the traffic log. Anything else in the folder is somebody
    /// else's and is left alone -- the same rule <c>SecsLogFile.Prune</c> keeps.
    /// </summary>
    internal static DateTime? LogDayOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && stem.Length == 8 &&
            DateTime.TryParseExact(stem[..6], "yyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var audit))
            return audit.Date;
        if (fileName.StartsWith("secs_", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && stem.Length == 13 &&
            DateTime.TryParseExact(stem[5..], "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var secs))
            return secs.Date;
        return null;
    }

    private static void AddSecsProfiles(ZipArchive zip, List<BundleItem> items,
                                        DiagnosticInputs inputs)
    {
        // Both, unredacted. The chamber code stamped into every id is the whole subject of a
        // "the host never got it" dispute, so a redacted profile would be an empty gesture.
        Add(inputs.SecsProfileTemplatePath, "secs/profile-template.json");
        Add(inputs.SecsEffectiveProfilePath, "secs/profile-effective.json");

        void Add(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                items.Add(new BundleItem
                {
                    Name = name,
                    SourcePath = path,
                    Omitted = BundleOmission.NotPresent,
                    Note = "SECS has not started on this machine, so no profile was ever written.",
                });
                return;
            }
            items.Add(CopyInto(zip, name, path, countRows: false));
        }
    }

    /// <summary>
    /// Today's ratio CSVs -- the leak history itself. Cheap (~200 B a row against ~22 KB for a
    /// full-spectrum row) and, since <see cref="RatioCsvLogger"/> stopped following the threshold
    /// recorder, present whenever the tool was acquiring at all. That is what makes them worth
    /// taking unconditionally while the full spectrum has to earn its place.
    /// </summary>
    private static void AddRatioCsvs(ZipArchive zip, List<BundleItem> items,
                                     DiagnosticInputs inputs, DateTime nowLocal)
    {
        var folder = DayFolder(inputs.DataDirectory, nowLocal);
        var any = false;
        foreach (var file in SafeFiles(folder, "*_Ratio_*.csv"))
        {
            any = true;
            items.Add(CopyInto(zip, "data/" + file.Name, file.FullName, countRows: true));
        }
        // The day folder's own context files, which is what makes the recordings readable.
        foreach (var file in SafeFiles(folder, "_config_*.json"))
            items.Add(CopyInto(zip, "data/" + file.Name, file.FullName, countRows: false));

        if (!any)
            items.Add(new BundleItem
            {
                Name = "ratio CSV",
                SourcePath = folder,
                Omitted = BundleOmission.NotPresent,
                Note = "No ratio CSV today. The ratio recorder writes for as long as the OES is "
                     + "acquiring, so its absence means acquisition never started.",
            });
    }

    private static FileInfo? NewestSpectrum(DiagnosticInputs inputs, DateTime nowLocal) =>
        SafeFiles(DayFolder(inputs.DataDirectory, nowLocal), "*_OES1_*.csv")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

    private static string DayFolder(string dataDirectory, DateTime day) =>
        Path.Combine(dataDirectory, day.ToString("yyyyMM"), day.ToString("dd"));

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Copies one file in, tolerating that the logger may hold it open.
    ///
    /// <para><see cref="FileShare.ReadWrite"/> is the same choice <see cref="SystemLogMirror"/>
    /// makes, and for the same reason: the file being written right now is the one covering the
    /// problem. Skipping it would drop exactly the evidence worth having. But a file someone else
    /// has open ends mid-run, so it is marked -- see <see cref="BundleItem.Truncated"/>.</para>
    /// </summary>
    private static BundleItem CopyInto(ZipArchive zip, string entryName, string sourcePath,
                                       bool countRows)
    {
        try
        {
            var truncated = IsHeldOpen(sourcePath);
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete);
            using var output = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();

            long bytes = 0, rows = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                bytes += read;
                if (countRows)
                    for (int i = 0; i < read; i++)
                        if (buffer[i] == (byte)'\n') rows++;
            }

            return new BundleItem
            {
                Name = entryName,
                SourcePath = sourcePath,
                Bytes = bytes,
                Truncated = truncated,
                Rows = countRows ? rows : null,
            };
        }
        catch (Exception ex)
        {
            return Unreadable(entryName, sourcePath, ex);
        }
    }

    /// <summary>
    /// Whether something else has this file open, by trying to take it exclusively -- the probe
    /// <see cref="DataRetentionService"/> already uses, asking the same question for the opposite
    /// purpose. There it decides to leave a folder alone; here it decides only how to label what
    /// was copied.
    /// </summary>
    private static bool IsHeldOpen(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    private static BundleItem WriteText(ZipArchive zip, string entryName, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var stream = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        stream.Write(bytes, 0, bytes.Length);
        return new BundleItem { Name = entryName, Bytes = bytes.Length };
    }

    private static BundleItem Unreadable(string name, string source, Exception ex) => new()
    {
        Name = name,
        SourcePath = source,
        Omitted = BundleOmission.Unreadable,
        Note = $"{ex.GetType().Name}: {ex.Message}",
    };

    private static IEnumerable<FileInfo> SafeFiles(string folder, string pattern)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<FileInfo>();
        try { return new DirectoryInfo(folder).GetFiles(pattern).OrderBy(f => f.Name).ToList(); }
        catch { return Array.Empty<FileInfo>(); }
    }

    /// <summary>
    /// "+08:00", not TimeSpan's "08:00:00" — which sits beside the timestamp in the header and
    /// reads as a second clock reading rather than the zone the first one is in.
    /// </summary>
    private static string FormatOffset(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        return sb.ToString();
    }
}
