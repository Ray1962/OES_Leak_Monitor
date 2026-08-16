using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>
/// The SECS traffic log: every line the equipment reports, appended to a day file.
/// <para/>
/// Separate from <c>SystemLogger</c> on purpose. That log is the audit record — a few
/// entries a shift, each one something a person needs to read afterwards. This one is a
/// protocol trace: every message in and out, with its full body, thousands of lines an
/// hour once a host starts polling. Merging them would bury the audit record in the
/// trace, and the trace is exactly what a customer asks for at acceptance, so it cannot
/// simply be dropped either.
/// <para/>
/// Writes are serialised and best-effort: a logging failure must not take the interface
/// down, so an I/O error is swallowed after being surfaced once.
/// </summary>
public sealed class SecsLogFile : IDisposable
{
    private readonly object _gate = new();
    private readonly string _folder;
    private readonly int _retentionDays;

    private StreamWriter? _writer;
    private DateTime _openDay = DateTime.MinValue;
    private bool _failed;

    /// <summary>Raised once when writing starts failing, with the reason.</summary>
    public event Action<string>? WriteFailed;

    public SecsLogFile(string folder, int retentionDays)
    {
        _folder = folder;
        _retentionDays = retentionDays;
    }

    /// <summary>Path of the file currently being written, or "" before the first line.</summary>
    public string CurrentPath { get; private set; } = "";

    /// <summary>Appends one line, stamped with the local time it was received.</summary>
    public void Write(string line)
    {
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }
            try
            {
                var now = DateTime.Now;
                Roll(now.Date);
                _writer!.WriteLine(
                    now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line);
            }
            catch (Exception ex)
            {
                _failed = true;
                Close();
                WriteFailed?.Invoke(ex.Message);
            }
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private void Roll(DateTime day)
    {
        if (_writer is not null && day == _openDay)
        {
            return;
        }
        Close();
        Directory.CreateDirectory(_folder);
        CurrentPath = Path.Combine(_folder, $"secs_{day:yyyyMMdd}.log");
        _writer = new StreamWriter(CurrentPath, append: true, Encoding.UTF8) { AutoFlush = true };
        _openDay = day;
        Prune(day);
    }

    /// <summary>
    /// Deletes day files older than the retention window. Only files this class writes
    /// (<c>secs_YYYYMMDD.log</c>) and only ones whose stamp parses — a folder the operator
    /// keeps other things in is not ours to tidy.
    /// </summary>
    private void Prune(DateTime today)
    {
        if (_retentionDays <= 0)
        {
            return;
        }
        var cutoff = today.AddDays(-_retentionDays);
        foreach (var path in SafeEnumerate(_folder, "secs_*.log"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length != 13 ||
                !DateTime.TryParseExact(stem.Substring(5), "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                continue;
            }
            if (day < cutoff)
            {
                try { File.Delete(path); } catch { /* a locked old log is not worth failing over */ }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string folder, string pattern)
    {
        try { return Directory.EnumerateFiles(folder, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose()
    {
        lock (_gate) { Close(); }
    }
}
