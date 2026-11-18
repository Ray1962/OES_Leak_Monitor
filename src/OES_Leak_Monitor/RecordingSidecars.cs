using System;
using System.Collections.Generic;

namespace OES_Leak_Monitor;

/// <summary>
/// Writes each recording's <c>.acq.json</c> once: when the intensity logger first reports the
/// file. The logger raises <c>FilesChanged</c> on open, on a roll and on close, and its
/// <c>CurrentFiles</c> still names a file after closing it — so writing on every event rewrote a
/// recording's sidecar at close with whatever the settings had become by then. On 2026-11-18 that
/// labelled a file holding 199 rows at 1 ms and 252 at 0.1 ms as 0.1 ms throughout.
/// <para>A recording holds one exposure since Core 0.1.11 (the logger rolls on a change of
/// integration time), so the conditions at the moment a file appears are the file's conditions.</para>
/// </summary>
public sealed class RecordingSidecars
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>
    /// Writes a sidecar for every path in <paramref name="files"/> not seen before. Returns the
    /// paths whose sidecar could not be written, with why; those are not retried — a later write
    /// would carry later conditions, which is the error this class exists to prevent.
    /// </summary>
    public IReadOnlyList<(string Path, string Failure)> Write(
        IEnumerable<string> files, AcquisitionFingerprint? acquisition, ToolEventStatus? tool)
    {
        var failures = new List<(string, string)>();
        lock (_gate)
        {
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f) || !_seen.Add(f)) continue;
                var failure = AcquisitionSidecar.TryWrite(f, acquisition, tool);
                if (failure is not null) failures.Add((f, failure));
            }
        }
        return failures;
    }
}
