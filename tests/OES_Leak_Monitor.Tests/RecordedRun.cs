using System.IO;
using System.Threading;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// A recorded plasma run, replayed frame by frame into the leak-monitor engine.
///
/// <para>The default is a real measurement: <c>P_OES1_0814220358.csv</c>, ten minutes of a
/// chamber on 2026-08-14 that the tool itself took from Normal through Warning to Alarm
/// (its ratio CSV, <c>P_Ratio_0814220355.csv</c>, is the record of that). Synthetic spectra
/// cannot stand in for it: what the engine does is decided by the continuum under each line,
/// the noise on it, and the axis the spectrometer actually produced — 1904 points over
/// 179.8–850.2 nm — none of which a sine wave has.</para>
///
/// <para><b>The recording is not committed.</b> It is 5.5 MB (2.5 MB compressed) against a
/// repository whose entire history is 5 MB and whose largest file is 128 KB, so the test reads
/// it from the data folder and <b>skips</b> when it is not there. Point
/// <c>OES_TEST_RECORDING</c> at another full-spectrum CSV to run it against a different run.</para>
/// </summary>
internal static class RecordedRun
{
    /// <summary>Environment variable that overrides which recording is replayed.</summary>
    public const string PathVariable = "OES_TEST_RECORDING";

    /// <summary>Where the 2026-08-14 run lives on the machine it was measured on.</summary>
    public const string DefaultPath = @"C:\DualOES\202608\14\P_OES1_0814220358.csv";

    /// <summary>The recording to replay, or null when there is none to be had.</summary>
    public static string? ResolvePath()
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }
        return File.Exists(DefaultPath) ? DefaultPath : null;
    }

    /// <summary>Reason to skip, for a test that cannot find a recording.</summary>
    public static string SkipReason =>
        $"no recorded run available — set {PathVariable} to a full-spectrum CSV, " +
        $"or put one at {DefaultPath}";

    public static Loaded Load(string path)
    {
        var parsed = RecordingCsvParser.ReadFull(path, CancellationToken.None)
            ?? throw new InvalidDataException($"{path} did not parse as a full-spectrum recording");
        return new Loaded(parsed);
    }

    internal sealed class Loaded
    {
        private readonly FullRecording _data;

        public Loaded(FullRecording data) => _data = data;

        public int FrameCount => _data.FrameCount;

        public float[] Wavelengths => _data.Wavelengths;

        /// <summary>Wall-clock span the recording covers, seconds.</summary>
        public double DurationSeconds =>
            _data.FrameCount < 2 ? 0 : _data.ElapsedSec[_data.FrameCount - 1] - _data.ElapsedSec[0];

        /// <summary>
        /// One frame, on the recording's own axis and at its own interval. Marked
        /// <c>IsTestMode</c> because that is what it is — replayed, not measured; the tests
        /// that need alarm transitions switch the engine's suppression off explicitly rather
        /// than pretending otherwise.
        /// </summary>
        public SpectrumSample Frame(int i, DateTime epoch) => new()
        {
            Timestamp = epoch.AddSeconds(_data.ElapsedSec[i]),
            Wavelengths = _data.Wavelengths,
            Intensities = _data.Intensities[i],
            IntegrationTime = 0,
            AverageCount = 0,
            SerialNumber = "REPLAY",
            IsTestMode = true,
        };
    }
}
