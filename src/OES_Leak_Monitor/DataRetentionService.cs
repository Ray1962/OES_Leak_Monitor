using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace OES_Leak_Monitor;

/// <summary>One day folder found under the data root.</summary>
public sealed record DayFolder(DateTime Date, string Path, long Bytes, int FileCount);

/// <summary>Read-only snapshot of the data tree and the drive holding it.</summary>
public sealed class DataFolderState
{
    public string BaseDirectory { get; init; } = "";
    public bool Exists { get; init; }
    public long TotalBytes { get; init; }
    public long ArchivedBytes { get; init; }
    public int DayFolderCount { get; init; }
    public int ArchiveCount { get; init; }
    public DateTime? OldestDay { get; init; }
    public long FreeBytes { get; init; }
    public long DriveBytes { get; init; }

    public double FreePercent => DriveBytes > 0 ? 100.0 * FreeBytes / DriveBytes : 100.0;
    public double TotalGB => TotalBytes / 1024.0 / 1024.0 / 1024.0;

    /// <summary>Tree is larger than the configured cap.</summary>
    public bool OverCap { get; init; }
    public bool LowFreeSpace { get; init; }
    public bool CriticalFreeSpace { get; init; }

    /// <summary>
    /// Operator-facing lines for the start-up / shutdown warning, empty when nothing is
    /// wrong. Each line says what is wrong and what to do about it.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Outcome of one archiving pass.</summary>
public sealed class RetentionResult
{
    public int ArchivedFolders { get; init; }
    public int SkippedFolders { get; init; }
    public long BytesBefore { get; init; }
    public long BytesAfter { get; init; }
    public bool StillOverCap { get; init; }
    public long BytesSaved => Math.Max(0, BytesBefore - BytesAfter);
}

/// <summary>
/// Compresses expired day folders under the logger's data root. Pure file-system work with
/// no UI or device dependencies; the caller decides when to run it and how to report.
/// <para/>
/// <b>Nothing is ever deleted before it is safely inside a verified archive.</b> A folder is
/// archived only if every file in it can be opened exclusively (nothing else is reading or
/// writing it), the zip is written to a temporary name, and its entry list matches the
/// folder's file list. Only then are the originals removed and the temp renamed into place.
/// Any failure leaves the folder exactly as it was.
/// </summary>
public static class DataRetentionService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Walk the tree and summarise it. Metadata only — no file contents are read.</summary>
    public static DataFolderState Inspect(string baseDirectory, DataRetentionSettings settings)
    {
        settings ??= new DataRetentionSettings();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return new DataFolderState { BaseDirectory = baseDirectory ?? "", Exists = false };
        }

        long total = 0, archived = 0;
        int dayFolders = 0, archives = 0;
        DateTime? oldest = null;

        foreach (var day in EnumerateDayFolders(baseDirectory))
        {
            total += day.Bytes;
            dayFolders++;
            if (oldest is null || day.Date < oldest) oldest = day.Date;
        }
        foreach (var zip in EnumerateArchives(baseDirectory))
        {
            var len = SafeLength(zip.Path);
            total += len;
            archived += len;
            archives++;
            if (oldest is null || zip.Date < oldest) oldest = zip.Date;
        }

        long free = 0, driveSize = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(baseDirectory));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) { free = drive.AvailableFreeSpace; driveSize = drive.TotalSize; }
            }
        }
        catch { /* network path or no permission — leave the drive figures at zero */ }

        var capBytes = (long)(settings.MaxTotalSizeGB * 1024 * 1024 * 1024);
        var overCap = capBytes > 0 && total > capBytes;
        var freePct = driveSize > 0 ? 100.0 * free / driveSize : 100.0;
        var low = driveSize > 0 && freePct < settings.WarnFreeSpacePercent;
        var critical = driveSize > 0 && freePct < settings.CriticalFreeSpacePercent;

        if (critical)
            warnings.Add($"Data drive is critically full: {freePct:0.#}% free ({Gb(free)} of {Gb(driveSize)}). " +
                         "Logging will fail when it runs out. Move or delete old data now.");
        else if (low)
            warnings.Add($"Data drive is running low: {freePct:0.#}% free ({Gb(free)} of {Gb(driveSize)}).");

        if (overCap)
            warnings.Add($"Data folder is {Gb(total)}, above the {settings.MaxTotalSizeGB:0.#} GB limit" +
                         (settings.Enabled
                             ? " — everything old enough is already compressed, so the limit can only be " +
                               "restored by moving archives off this machine."
                             : " — automatic compression is switched off in Configuration."));

        // Deliberately NOT a warning: compression is off by default, so "compression is off"
        // would pop a dialog on every open and every close of a perfectly healthy machine —
        // the nagging that trains people to dismiss this dialog without reading it. Only a
        // real disk condition (low free space, over the size limit) is worth interrupting
        // for; the switch's state is visible in the Configuration tab where it is set.

        return new DataFolderState
        {
            BaseDirectory = baseDirectory,
            Exists = true,
            TotalBytes = total,
            ArchivedBytes = archived,
            DayFolderCount = dayFolders,
            ArchiveCount = archives,
            OldestDay = oldest,
            FreeBytes = free,
            DriveBytes = driveSize,
            OverCap = overCap,
            LowFreeSpace = low,
            CriticalFreeSpace = critical,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Run one archiving pass. <paramref name="isInUse"/> is asked about every file before
    /// its folder is touched — the caller wires it to the paths its writers currently hold
    /// open. <paramref name="report"/> receives one line per action for the audit log.
    /// </summary>
    public static RetentionResult Run(string baseDirectory, DataRetentionSettings settings,
        Func<string, bool>? isInUse = null, Action<string, string, bool>? report = null,
        DateTime? today = null, CancellationToken token = default)
    {
        settings ??= new DataRetentionSettings();
        var now = (today ?? DateTime.Now).Date;
        var before = Inspect(baseDirectory, settings);
        if (!before.Exists || !settings.Enabled)
            return new RetentionResult { BytesBefore = before.TotalBytes, BytesAfter = before.TotalBytes };

        // Newest days are off limits whatever the rules say: an operator is most likely to be
        // reviewing them, and one of them may hold the open save session.
        var floor = now.AddDays(-Math.Max(0, settings.MinKeepDays));
        var folders = EnumerateDayFolders(baseDirectory)
                      .Where(d => d.Date < floor && d.FileCount > 0)
                      .OrderBy(d => d.Date)
                      .ToList();

        var byAge = settings.ArchiveAfterDays > 0
            ? folders.Where(d => d.Date < now.AddDays(-settings.ArchiveAfterDays)).ToList()
            : new List<DayFolder>();

        long running = before.TotalBytes;
        var capBytes = (long)(settings.MaxTotalSizeGB * 1024 * 1024 * 1024);
        int archivedCount = 0, skipped = 0;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in byAge)
        {
            token.ThrowIfCancellationRequested();
            if (ArchiveFolder(folder, isInUse, report, out var saved)) { archivedCount++; running -= saved; }
            else skipped++;
            done.Add(folder.Path);
        }

        // Size rule: keep going into newer folders, oldest first, until the tree fits.
        if (capBytes > 0 && running > capBytes)
        {
            foreach (var folder in folders.Where(f => !done.Contains(f.Path)))
            {
                token.ThrowIfCancellationRequested();
                if (running <= capBytes) break;
                report?.Invoke("DataRetentionOverCap",
                    $"Compressing {folder.Date:yyyy-MM-dd} ahead of its {settings.ArchiveAfterDays}-day age " +
                    $"because the data folder is over the {settings.MaxTotalSizeGB:0.#} GB limit.", false);
                if (ArchiveFolder(folder, isInUse, report, out var saved)) { archivedCount++; running -= saved; }
                else skipped++;
            }
        }

        var after = Inspect(baseDirectory, settings);
        return new RetentionResult
        {
            ArchivedFolders = archivedCount,
            SkippedFolders = skipped,
            BytesBefore = before.TotalBytes,
            BytesAfter = after.TotalBytes,
            StillOverCap = after.OverCap,
        };
    }

    /// <summary>
    /// Compress one day folder into a sibling <c>DD.zip</c>. Returns false (having changed
    /// nothing) when the folder is in use or anything goes wrong.
    /// </summary>
    private static bool ArchiveFolder(DayFolder folder, Func<string, bool>? isInUse,
        Action<string, string, bool>? report, out long bytesSaved)
    {
        bytesSaved = 0;
        string[] files;
        try { files = Directory.GetFiles(folder.Path); }
        catch (Exception ex)
        {
            report?.Invoke("DataArchiveFailed", $"Cannot list {folder.Path}: {ex.Message}", true);
            return false;
        }
        if (files.Length == 0) return false;

        // Refuse to touch anything still open — by our own writers or by anyone else
        // (an operator with the CSV open in Excel, a backup agent mid-copy).
        foreach (var f in files)
        {
            if (isInUse?.Invoke(f) == true || !CanOpenExclusively(f))
            {
                report?.Invoke("DataArchiveSkipped",
                    $"{folder.Date:yyyy-MM-dd} left alone — {Path.GetFileName(f)} is in use.", false);
                return false;
            }
        }

        var zipPath = UniqueZipPath(folder.Path);
        var tempPath = zipPath + ".tmp";
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                    zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
            }

            // Verify before removing anything: the archive must list exactly the files the
            // folder held. A truncated zip that passes unnoticed would destroy the data it
            // was meant to preserve.
            using (var check = ZipFile.OpenRead(tempPath))
            {
                var inZip = check.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var expected = files.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase!);
                if (!expected.SetEquals(inZip))
                {
                    File.Delete(tempPath);
                    report?.Invoke("DataArchiveFailed",
                        $"{folder.Date:yyyy-MM-dd} not archived — the archive did not contain every file " +
                        $"({inZip.Count} of {expected.Count}). The folder was left untouched.", true);
                    return false;
                }
            }

            File.Move(tempPath, zipPath);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            report?.Invoke("DataArchiveFailed",
                $"{folder.Date:yyyy-MM-dd} not archived: {ex.Message}. The folder was left untouched.", true);
            return false;
        }

        // The data is safely in the verified archive; now reclaim the space.
        var leftovers = 0;
        foreach (var f in files)
        {
            try { File.Delete(f); } catch { leftovers++; }
        }
        if (leftovers == 0)
        {
            try { Directory.Delete(folder.Path, recursive: false); } catch { /* not empty / locked */ }
        }

        var zipSize = SafeLength(zipPath);
        bytesSaved = Math.Max(0, folder.Bytes - zipSize);
        report?.Invoke("DataArchived",
            $"{folder.Date:yyyy-MM-dd} compressed: {folder.FileCount} file(s), " +
            $"{Gb(folder.Bytes)} → {Gb(zipSize)}" +
            (leftovers > 0 ? $" ({leftovers} file(s) could not be removed and remain on disk)" : "") +
            $". Data is preserved in {Path.GetFileName(zipPath)}.", false);
        return true;
    }

    /// <summary>Can this file be taken exclusively right now? Used as an "is anyone using it" probe.</summary>
    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch { return false; }
    }

    /// <summary><c>DD.zip</c>, or <c>DD_1.zip</c>… if a previous archive is already there.</summary>
    private static string UniqueZipPath(string dayFolder)
    {
        var parent = Path.GetDirectoryName(dayFolder) ?? dayFolder;
        var name = Path.GetFileName(dayFolder);
        var path = Path.Combine(parent, name + ".zip");
        for (int n = 1; (File.Exists(path) || File.Exists(path + ".tmp")) && n < 1000; n++)
            path = Path.Combine(parent, $"{name}_{n.ToString(Inv)}.zip");
        return path;
    }

    /// <summary>Day folders as <c>{baseDir}\YYYYMM\DD</c>, with their size and file count.</summary>
    public static IEnumerable<DayFolder> EnumerateDayFolders(string baseDirectory)
    {
        foreach (var monthDir in SafeDirectories(baseDirectory))
        {
            var month = Path.GetFileName(monthDir);
            if (month.Length != 6 || !int.TryParse(month, NumberStyles.Integer, Inv, out _)) continue;
            if (!int.TryParse(month.AsSpan(0, 4), NumberStyles.Integer, Inv, out var year)) continue;
            if (!int.TryParse(month.AsSpan(4, 2), NumberStyles.Integer, Inv, out var mm)) continue;

            foreach (var dayDir in SafeDirectories(monthDir))
            {
                var dd = Path.GetFileName(dayDir);
                if (dd.Length != 2 || !int.TryParse(dd, NumberStyles.Integer, Inv, out var day)) continue;
                DateTime date;
                try { date = new DateTime(year, mm, day); } catch { continue; }

                long bytes = 0; int count = 0;
                foreach (var f in SafeFiles(dayDir)) { bytes += SafeLength(f); count++; }
                yield return new DayFolder(date, dayDir, bytes, count);
            }
        }
    }

    /// <summary>Archives written by this service, as <c>{baseDir}\YYYYMM\DD[_n].zip</c>.</summary>
    public static IEnumerable<DayFolder> EnumerateArchives(string baseDirectory)
    {
        foreach (var monthDir in SafeDirectories(baseDirectory))
        {
            var month = Path.GetFileName(monthDir);
            if (month.Length != 6 || !int.TryParse(month, NumberStyles.Integer, Inv, out _)) continue;
            if (!int.TryParse(month.AsSpan(0, 4), NumberStyles.Integer, Inv, out var year)) continue;
            if (!int.TryParse(month.AsSpan(4, 2), NumberStyles.Integer, Inv, out var mm)) continue;

            foreach (var zip in SafeFiles(monthDir, "*.zip"))
            {
                var stem = Path.GetFileNameWithoutExtension(zip);
                var dayPart = stem.Split('_')[0];
                if (dayPart.Length != 2 || !int.TryParse(dayPart, NumberStyles.Integer, Inv, out var day)) continue;
                DateTime date;
                try { date = new DateTime(year, mm, day); } catch { continue; }
                yield return new DayFolder(date, zip, SafeLength(zip), 1);
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string path, string pattern = "*")
    {
        try { return Directory.EnumerateFiles(path, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>Human-readable size. Falls back to MB and KB so a small figure never reads "0 MB".</summary>
    public static string Gb(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
        >= 1024L * 1024        => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _                      => $"{bytes / 1024.0:0.#} KB",
    };
}
