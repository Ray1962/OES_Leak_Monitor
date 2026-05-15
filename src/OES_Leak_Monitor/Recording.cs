using System;
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

    /// <summary>Strips the device tag from the key. In this single-OES app every group
    /// holds at most one Recording, but the key shape is preserved for forward / backward
    /// compatibility with the dual-OES file layout.</summary>
    public string GroupKey => $"{Prefix}|{SessionStart:o}|{RotationIndex}";

    public string FileSizeText => FormatSize(FileSizeBytes);

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

            var stem = Path.GetFileNameWithoutExtension(info.Name);
            if (stem.EndsWith(".summary", StringComparison.OrdinalIgnoreCase)) return null;

            var dayFolder   = info.Directory?.Name;
            var monthFolder = info.Directory?.Parent?.Name;
            if (dayFolder is null || monthFolder is null) return null;
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
                FilePath      = fullPath,
                FileName      = info.Name,
                FileSizeBytes = info.Length,
                Prefix        = prefix,
                DeviceTag     = tag,
                SessionStart  = start,
                RotationIndex = rot,
            };
        }
        catch
        {
            return null;
        }
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
