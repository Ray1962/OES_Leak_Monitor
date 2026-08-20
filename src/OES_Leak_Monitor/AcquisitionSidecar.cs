using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

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
    public static string? TryWrite(string recordingPath, AcquisitionFingerprint? acquisition)
    {
        if (string.IsNullOrWhiteSpace(recordingPath) || acquisition is null) return null;
        try
        {
            File.WriteAllText(PathFor(recordingPath), JsonSerializer.Serialize(acquisition, Json));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Reads the sidecar for a recording, loose on disk or inside the day's archive. Null when
    /// there is none — which is the ordinary case for anything recorded before this existed.
    /// </summary>
    public static AcquisitionFingerprint? TryRead(Recording recording)
    {
        if (recording is null) return null;
        try
        {
            if (!recording.IsArchived)
            {
                var path = PathFor(recording.FilePath);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<AcquisitionFingerprint>(File.ReadAllText(path), Json)
                    : null;
            }

            using var archive = ZipFile.OpenRead(recording.ArchivePath);
            var name = System.IO.Path.ChangeExtension(recording.EntryName, null) + Extension;
            var entry = archive.GetEntry(name);
            if (entry is null) return null;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<AcquisitionFingerprint>(reader.ReadToEnd(), Json);
        }
        catch
        {
            // A malformed or unreadable sidecar is the same as none: the fingerprint stays partial
            // and the mismatch check compares what it can.
            return null;
        }
    }
}
