using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>
/// A copy of the day's system-log files, kept in the day folder beside the recordings they
/// describe.
///
/// <para>Same reason as <see cref="ConfigSnapshot"/>, different half of the problem: the settings
/// say what the tool was configured to do, the log says what it actually did — when acquisition
/// started, when a Golden Run was captured and what it rejected, when the acquisition parameters
/// changed, when an alarm rose and who acknowledged it. Analysing a day's recordings without it
/// means guessing at the timeline, and the log lives under <c>%AppData%</c> where nobody copying
/// a data folder will find it.</para>
///
/// <para>Copied rather than moved, and re-copied whenever the source has grown: the live file is
/// still being appended to, so an early copy is a partial one and the next recording refreshes
/// it. The last refresh happens when the app closes. Opened with
/// <see cref="FileShare.ReadWrite"/> because <c>SystemLogger</c> holds its own handle.</para>
///
/// <para>Cheap: an hour of ordinary logging is 6–15 KB against recordings of several megabytes.</para>
/// </summary>
public static class SystemLogMirror
{
    /// <summary>Filename prefix, so the copies sort together and read as what they are.</summary>
    public const string Prefix = "_log_";

    /// <summary>
    /// Copies every system-log file belonging to <paramref name="localDay"/> into
    /// <paramref name="dayFolder"/>, skipping any that is already there at the same length.
    /// </summary>
    /// <returns>How many files were written. Failures are collected in
    /// <paramref name="errors"/> rather than thrown.</returns>
    public static int Sync(string dayFolder, string logDirectory, DateTime localDay,
                           out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        errors = problems;
        if (string.IsNullOrWhiteSpace(dayFolder) || string.IsNullOrWhiteSpace(logDirectory)) return 0;
        if (!Directory.Exists(dayFolder) || !Directory.Exists(logDirectory)) return 0;

        int written = 0;
        // SystemLogger names its files yyMMddHH.csv, one per hour.
        var pattern = localDay.ToString("yyMMdd") + "??.csv";
        foreach (var source in Directory.EnumerateFiles(logDirectory, pattern).OrderBy(f => f))
        {
            var target = Path.Combine(dayFolder, Prefix + Path.GetFileName(source));
            try
            {
                var src = new FileInfo(source);
                var dst = new FileInfo(target);
                // The live hour keeps growing; anything shorter than the source is stale.
                if (dst.Exists && dst.Length >= src.Length) continue;
                CopyShared(source, target);
                written++;
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }
        return written;
    }

    /// <summary>Copies a file that someone else has open for writing.</summary>
    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
        input.CopyTo(output);
    }
}
