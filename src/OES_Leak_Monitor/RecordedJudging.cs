using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace OES_Leak_Monitor;

/// <summary>
/// How each ratio of a recorded run was judged, read from the <c>_config_HHmmss.json</c>
/// snapshot <see cref="ConfigSnapshot"/> left beside it (#107). The ratio CSV says what was
/// measured; only the snapshot says which paths judged it and at what thresholds — and the
/// settings may have changed since, so the review must not use today's.
/// </summary>
public static class RecordedJudging
{
    /// <summary>
    /// The snapshot that describes a recorded run: the newest whose time of day is not after
    /// the run's last row (<paramref name="end"/>). Not "not after its first row": the snapshot
    /// is written when the intensity recording opens, which on 2026-11-19 trailed the ratio
    /// CSV by 2–68 s, so a run's own snapshot is usually stamped after its first row. Snapshots
    /// are only written when the settings change, so an earlier one still applies; one written
    /// after the run ended belongs to a later run. Ratio definitions only reach the engine on an
    /// acquisition restart, which starts a new ratio file, so one run has one definition set.
    /// Null when none qualifies. Names that are not snapshots are ignored.
    /// </summary>
    public static string? Choose(IEnumerable<string> fileNames, DateTime end)
    {
        string? best = null;
        TimeSpan bestTime = TimeSpan.MinValue;
        foreach (var path in fileNames)
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(ConfigSnapshot.Prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var stamp = name.Substring(ConfigSnapshot.Prefix.Length, name.Length - ConfigSnapshot.Prefix.Length - 5);
            if (!DateTime.TryParseExact(stamp, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            var tod = t.TimeOfDay;
            if (tod > end.TimeOfDay || tod <= bestTime) continue;
            best = path; bestTime = tod;
        }
        return best;
    }

    /// <summary>The ratio definitions of a snapshot's text, by key. Throws on malformed JSON.</summary>
    public static IReadOnlyDictionary<string, RatioDefinition> Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions) ?? new AppSettings();
        var map = new Dictionary<string, RatioDefinition>();
        foreach (var r in settings.LeakMonitor.Ratios) map[r.Key] = r;
        return map;
    }

    /// <summary>
    /// Loads the definitions in force for <paramref name="file"/>, looking beside it on disk or,
    /// for an archived recording, inside the same archive. Null when there is no snapshot or it
    /// cannot be read; <paramref name="source"/> names the snapshot used.
    /// </summary>
    public static IReadOnlyDictionary<string, RatioDefinition>? TryLoad(Recording file, DateTime end, out string? source)
    {
        source = null;
        try
        {
            if (file.IsArchived)
            {
                using var zip = ZipFile.OpenRead(file.ArchivePath);
                var dir = Path.GetDirectoryName(file.EntryName.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                var names = zip.Entries.Where(e => (Path.GetDirectoryName(e.FullName)?.Replace('\\', '/') ?? "") == dir)
                                       .Select(e => e.FullName);
                var pick = Choose(names, end);
                if (pick is null) return null;
                using var reader = new StreamReader(zip.GetEntry(pick)!.Open());
                var defs = Parse(reader.ReadToEnd());
                source = Path.GetFileName(pick);
                return defs;
            }
            else
            {
                var dir = Path.GetDirectoryName(file.FilePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                var pick = Choose(Directory.EnumerateFiles(dir, ConfigSnapshot.Prefix + "*.json"), end);
                if (pick is null) return null;
                var defs = Parse(File.ReadAllText(pick));
                source = Path.GetFileName(pick);
                return defs;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
