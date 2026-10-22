using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OES_Leak_Monitor;

/// <summary>
/// The acquisition conditions a recording was taken under, written beside it as
/// <c>{recording}.acq.json</c>.
///
/// <para><b>Why beside it and not in it.</b> A full-spectrum CSV carries a wavelength axis and
/// frames, and nothing about the integration time, averaging or correction switches the
/// spectrometer was set to — so a Golden Run built from one afterwards cannot answer the question
/// <c>LeakMonitorAcquisitionMismatch</c> exists to ask, and absolute-intensity readings scale with
/// exactly those settings. Putting them in the CSV header means changing
/// <c>IntensityCsvWriter</c> <em>and</em> <c>RecordingCsvParser</c>, both of which live in the
/// Aqst.OesApp.Core package and are read by four code paths here; a sidecar is this repo's own
/// file, costs nothing to ignore, and a recording made before it existed behaves exactly as it
/// does today — fingerprint absent, comparison skipped.</para>
///
/// <para>The cost is that the two can be separated: copy the CSV alone and the conditions are
/// gone. Compression keeps them together (<see cref="DataRetentionService"/> archives whatever
/// the day folder holds), and losing it is no worse than not having written it.</para>
/// </summary>
public static class AcquisitionSidecar
{
    /// <summary>Suffix appended to the recording's path.</summary>
    public const string Extension = ".acq.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Sidecar path for a recording's CSV path.</summary>
    public static string PathFor(string recordingPath) =>
        System.IO.Path.ChangeExtension(recordingPath, null) + Extension;

    /// <summary>
    /// Writes the sidecar for a freshly opened recording. Failures are returned, not thrown: the
    /// recording itself is the thing that matters, and a missing sidecar degrades to the
    /// behaviour that existed before it did.
    /// </summary>
    public static string? TryWrite(string recordingPath, AcquisitionFingerprint? acquisition) =>
        TryWrite(recordingPath, acquisition, tool: null);

    /// <summary>
    /// As above, also recording the process window the main tool's events had put the monitor in
    /// when the recording opened (<see cref="ToolContextField"/> / <see cref="ToolWindowClassField"/>),
    /// so a baseline built from the recording later can know which process it was.
    /// <para>The fields sit beside the fingerprint's own, not inside it: the fingerprint is
    /// compared field by field against a Golden Run's (<c>AcquisitionFingerprint.Differences</c>),
    /// and which process ran is not an acquisition setting — a recording of process A and one of
    /// process B taken at the same exposure are not a mismatch. Readers that only want the
    /// fingerprint skip the extra properties. With tool events not in use (null or
    /// <see cref="ToolEventContext.Disabled"/>) nothing is added, so the file is the one this
    /// method always wrote.</para>
    /// <para>Still written only when there is a fingerprint: a sidecar holding nothing but the
    /// window would read back as a fingerprint of zeros, which is a wrong answer where no file is
    /// the honest one.</para>
    /// </summary>
    public static string? TryWrite(string recordingPath, AcquisitionFingerprint? acquisition, ToolEventStatus? tool)
    {
        if (string.IsNullOrWhiteSpace(recordingPath) || acquisition is null) return null;
        try
        {
            var node = JsonSerializer.SerializeToNode(acquisition, Json)!.AsObject();
            if (tool is not null && tool.State != ToolEventContext.Disabled)
            {
                node[ToolContextField] = tool.State.ToString();
                node[ToolWindowClassField] = tool.WindowClass ?? "";
            }
            File.WriteAllText(PathFor(recordingPath), node.ToJsonString(Json));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Sidecar property: the tool-event context the recording opened in (enum name).</summary>
    public const string ToolContextField = "toolEventContext";

    /// <summary>Sidecar property: the window's class when the context was InWindow, else empty.</summary>
    public const string ToolWindowClassField = "toolWindowClass";

    /// <summary>
    /// Reads the sidecar for a recording, loose on disk or inside the day's archive. Null when
    /// there is none — which is the ordinary case for anything recorded before this existed.
    /// </summary>
    public static AcquisitionFingerprint? TryRead(Recording recording)
    {
        var text = TryReadText(recording);
        if (text is null) return null;
        try
        {
            return JsonSerializer.Deserialize<AcquisitionFingerprint>(text, Json);
        }
        catch
        {
            // A malformed or unreadable sidecar is the same as none: the fingerprint stays partial
            // and the mismatch check compares what it can.
            return null;
        }
    }

    /// <summary>
    /// The process window the recording opened in, from its sidecar. Null when the sidecar has
    /// none — written before this existed, or while tool events were not in use — and null is
    /// "not known", never "outside any window".
    /// </summary>
    public static RecordingToolContext? TryReadToolContext(Recording recording)
    {
        var text = TryReadText(recording);
        if (text is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty(ToolContextField, out var ctx) || ctx.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<ToolEventContext>(ctx.GetString(), ignoreCase: true, out var context))
                return null;
            string cls = root.TryGetProperty(ToolWindowClassField, out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
            return new RecordingToolContext(context, cls);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The sidecar's text, loose on disk or inside the day's archive; null if none.</summary>
    private static string? TryReadText(Recording recording)
    {
        if (recording is null) return null;
        try
        {
            if (!recording.IsArchived)
            {
                var path = PathFor(recording.FilePath);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }

            using var archive = ZipFile.OpenRead(recording.ArchivePath);
            var name = System.IO.Path.ChangeExtension(recording.EntryName, null) + Extension;
            var entry = archive.GetEntry(name);
            if (entry is null) return null;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The process window a recording opened in, as its sidecar recorded it: the tool-event context
/// and, when that was <see cref="ToolEventContext.InWindow"/>, the window's class.
/// </summary>
public sealed record RecordingToolContext(ToolEventContext Context, string WindowClass);
