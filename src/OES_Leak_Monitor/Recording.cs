using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OES_Leak_Monitor;

/// <summary>
/// One CSV file produced by <see cref="IntensityCsvWriter"/>. Filename shape
/// <c>{prefix}_{tag}_{MMddHHmmss}[_N].csv</c> nested under
/// <c>{baseDir}\YYYYMM\DD\</c>; the year is recovered from the parent folder.
/// </summary>
public sealed class Recording
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string   FilePath      { get; init; } = "";
    public string   FileName      { get; init; } = "";
    public long     FileSizeBytes { get; init; }
    public string   Prefix        { get; init; } = "";
    public string   DeviceTag     { get; init; } = "";
    public DateTime SessionStart  { get; init; }
    public int      RotationIndex { get; init; }

    /// <summary>
    /// Archive this CSV lives inside (<c>{baseDir}\YYYYMM\DD.zip</c>), or empty for a loose
    /// file. Data-folder housekeeping compresses expired day folders, and a recording that
    /// disappeared from the list the moment it was archived would be a poor trade — so the
    /// review tabs list archived CSVs too and read them straight out of the zip.
    /// </summary>
    public string ArchivePath { get; init; } = "";

    /// <summary>Entry name within <see cref="ArchivePath"/>; empty for a loose file.</summary>
    public string EntryName { get; init; } = "";

    public bool IsArchived => ArchivePath.Length > 0;

    /// <summary>Opens the CSV for reading, whether it is loose on disk or inside an archive.</summary>
    public StreamReader OpenText()
    {
        if (!IsArchived) return new StreamReader(FilePath);

        // The ZipArchive owns the entry stream, so the reader has to keep both alive; wrap
        // them in one reader whose Dispose closes the chain.
        var archive = System.IO.Compression.ZipFile.OpenRead(ArchivePath);
        var entry = archive.GetEntry(EntryName)
                    ?? throw new FileNotFoundException($"{EntryName} is no longer inside {ArchivePath}.");
        return new ArchiveEntryReader(archive, entry.Open());
    }

    /// <summary>A <see cref="StreamReader"/> that also disposes the archive it came from.</summary>
    private sealed class ArchiveEntryReader : StreamReader
    {
        private readonly IDisposable _archive;
        public ArchiveEntryReader(IDisposable archive, Stream stream) : base(stream) => _archive = archive;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _archive.Dispose();
        }
    }

    /// <summary>Strips the device tag from the key. In this single-OES app every group
    /// holds at most one Recording, but the key shape is preserved for forward / backward
    /// compatibility with the dual-OES file layout.</summary>
    public string GroupKey => $"{Prefix}|{SessionStart:o}|{RotationIndex}";

    public string DateText => SessionStart.ToString("yyyy-MM-dd");
    public string TimeText => SessionStart.ToString("HH:mm:ss");

    public string FileSizeText => FormatSize(FileSizeBytes);

    /// <summary>"zip" when this CSV is read out of a compressed day folder, else empty.</summary>
    public string ArchivedText => IsArchived ? "zip" : "";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    /// <summary>
    /// Parse a Recording out of a full file path; returns null if the path doesn't
    /// match the IntensityCsvWriter naming scheme or sits outside the expected
    /// YYYYMM/DD folder shape.
    /// </summary>
    public static Recording? TryParse(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;

            var dayFolder   = info.Directory?.Name;
            var monthFolder = info.Directory?.Parent?.Name;
            if (dayFolder is null || monthFolder is null) return null;

            return TryParseName(info.Name, monthFolder, dayFolder, info.Length, fullPath, "", "");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a Recording for one entry inside an archived day folder
    /// (<c>{baseDir}\YYYYMM\DD[_n].zip</c>). The month comes from the parent folder as
    /// usual; the day comes from the archive's own name, since the <c>DD</c> folder it
    /// replaced no longer exists.
    /// </summary>
    public static Recording? TryParseArchived(string archivePath, string entryName, long uncompressedLength)
    {
        try
        {
            var monthFolder = new FileInfo(archivePath).Directory?.Name;
            if (monthFolder is null) return null;
            // "08.zip" -> "08"; "08_1.zip" -> "08" (a re-archived day gets a suffix).
            var dayFolder = Path.GetFileNameWithoutExtension(archivePath).Split('_')[0];

            return TryParseName(entryName, monthFolder, dayFolder, uncompressedLength,
                                filePath: archivePath, archivePath: archivePath, entryName: entryName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shared filename logic for both loose files and archive entries: the name must match
    /// <c>{prefix}_{tag}_{MMddHHmmss}[_N].csv</c> and its embedded month/day must agree with
    /// the folder it was found under.
    /// </summary>
    private static Recording? TryParseName(string fileName, string monthFolder, string dayFolder,
        long sizeBytes, string filePath, string archivePath, string entryName)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (stem.EndsWith(".summary", StringComparison.OrdinalIgnoreCase)) return null;

            if (monthFolder.Length != 6 || !int.TryParse(monthFolder, NumberStyles.Integer, Inv, out _)) return null;
            if (dayFolder.Length   != 2 || !int.TryParse(dayFolder,   NumberStyles.Integer, Inv, out _)) return null;

            int year  = int.Parse(monthFolder.Substring(0, 4), Inv);
            int month = int.Parse(monthFolder.Substring(4, 2), Inv);
            int day   = int.Parse(dayFolder, Inv);

            var parts = stem.Split('_');
            if (parts.Length < 3) return null;
            var prefix = parts[0];
            var tag    = parts[1];
            var ts     = parts[2];
            int rot = 0;
            if (parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.Integer, Inv, out var r)) rot = r;
            if (ts.Length != 10) return null;
            if (!int.TryParse(ts.AsSpan(0, 2), NumberStyles.Integer, Inv, out var mmFile)) return null;
            if (!int.TryParse(ts.AsSpan(2, 2), NumberStyles.Integer, Inv, out var ddFile)) return null;
            if (!int.TryParse(ts.AsSpan(4, 2), NumberStyles.Integer, Inv, out var hh))     return null;
            if (!int.TryParse(ts.AsSpan(6, 2), NumberStyles.Integer, Inv, out var mi))     return null;
            if (!int.TryParse(ts.AsSpan(8, 2), NumberStyles.Integer, Inv, out var ss))     return null;
            if (mmFile != month || ddFile != day) return null;

            DateTime start;
            try { start = new DateTime(year, month, day, hh, mi, ss); }
            catch { return null; }

            return new Recording
            {
                FilePath      = filePath,
                FileName      = fileName,
                FileSizeBytes = sizeBytes,
                Prefix        = prefix,
                DeviceTag     = tag,
                SessionStart  = start,
                RotationIndex = rot,
                ArchivePath   = archivePath,
                EntryName     = entryName,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every CSV inside one day archive, as Recordings. Only the zip's central directory is
    /// read, so listing an archive costs about as much as listing a folder.
    /// </summary>
    public static IEnumerable<Recording> FromArchive(string archivePath)
    {
        List<Recording> found = new();
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                if (!entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                var rec = TryParseArchived(archivePath, entry.FullName, entry.Length);
                if (rec is not null) found.Add(rec);
            }
        }
        catch { /* unreadable or half-written archive — treat as holding nothing */ }
        return found;
    }
}

/// <summary>
/// One recording session keyed by prefix + timestamp + rotation index. The class is named
/// "Group" and the device file is held in <see cref="Oes1"/> for compatibility with the
/// upstream dual-OES file layout (filenames carry an "OES1" tag and live next to any
/// historical "OES2" sibling, which this single-OES app simply ignores at scan time).
/// </summary>
public sealed class RecordingGroup
{
    public string   Prefix        { get; init; } = "";
    public DateTime SessionStart  { get; init; }
    public int      RotationIndex { get; init; }
    public Recording? Oes1 { get; set; }

    public string GroupKey => $"{Prefix}|{SessionStart:o}|{RotationIndex}";

    public string DateText     => SessionStart.ToString("yyyy-MM-dd");
    public string TimeText     => SessionStart.ToString("HH:mm:ss");
    public string RotationText => RotationIndex == 0 ? "" : $"#{RotationIndex}";

    /// <summary>
    /// Marks a session whose CSV now lives inside a compressed day folder. Shown in the list
    /// so "why is this one slower to open" has a visible answer — it is decompressed on the
    /// way in — and so the archive is discoverable at all.
    /// </summary>
    public string ArchivedText => Oes1?.IsArchived == true ? "zip" : "";
    public long   TotalBytes   => Oes1?.FileSizeBytes ?? 0;
    public string SizeText
    {
        get
        {
            var b = TotalBytes;
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
            return $"{b / 1024.0 / 1024.0:F1} MB";
        }
    }
}
