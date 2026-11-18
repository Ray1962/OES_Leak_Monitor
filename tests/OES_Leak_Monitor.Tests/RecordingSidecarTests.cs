using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aqst.OesApp.Core;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Each recording's <c>.acq.json</c> says what that recording was taken at. On 2026-11-18 one
/// recording held 199 rows at 1 ms and 252 at 0.1 ms, and its sidecar said 0.1 for all of them:
/// the logger kept one file across the change, and the sidecar was written again when that file
/// closed — the logger still reports a closed file's path — with whatever was current by then.
/// </summary>
public sealed class RecordingSidecarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oes-sidecar-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Epoch = new(2026, 11, 18, 22, 48, 0, DateTimeKind.Local);
    private static readonly float[] Axis = Enumerable.Range(0, 64).Select(i => 300f + i * 0.5f).ToArray();

    public RecordingSidecarTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private LoggerSettings Settings() => new()
    {
        Enabled = true,
        BaseDirectory = _root,
        FilePrefix = "P",
        TriggerMode = TriggerMode.Wavelength,
        TriggerWavelength = 310f,
        WavelengthToleranceNm = 2f,
        SaveStartThresholdIntensity = 1000f,
        StartConfirmSeconds = 0,
        StopConfirmSeconds = 0,
        MinSaveTimeSeconds = 0,
        IncludeFullSpectrum = true,
        MonitoredWavelengths = Array.Empty<float>(),
        AnalysisMemoryBudgetMB = 500,
    };

    private static SpectrumSample Frame(int ms, float integrationMs, float level = 5000f) => new()
    {
        Timestamp = Epoch.AddMilliseconds(ms),
        Wavelengths = Axis,
        Intensities = Enumerable.Repeat(level, Axis.Length).ToArray(),
        IntegrationTime = integrationMs,
        AverageCount = 100,
        SerialNumber = "T",
        IsSimulated = false,
    };

    private static AcquisitionFingerprint At(double integrationMs) => new()
    {
        IntegrationTimeMs = integrationMs, AverageCount = 100, AxisLength = Axis.Length, AxisStartNm = 300, AxisEndNm = 331.5,
    };

    private IReadOnlyList<string> Recordings() =>
        Directory.GetFiles(_root, "P_OES1_*.csv", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".summary.csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f).ToList();

    private static double SidecarIntegration(string recording) =>
        AcquisitionSidecar.TryRead(Recording.TryParse(recording)!)!.IntegrationTimeMs;

    [Fact]
    public void A_change_of_integration_time_mid_save_gives_each_file_a_sidecar_of_its_own_exposure()
    {
        using var logger = new DualIntensityLogger(new[] { "OES1" }, _root);
        logger.Configure(Settings());
        var sidecars = new RecordingSidecars();
        var current = At(1);
        logger.FilesChanged += (_, _) => sidecars.Write(logger.CurrentFiles, current, tool: null);

        for (int ms = 0; ms < 1000; ms += 140) logger.ProcessSample(0, Frame(ms, 1f));
        current = At(0.1);   // what ApplyAll does before the device takes the new value
        for (int ms = 1000; ms < 2000; ms += 140) logger.ProcessSample(0, Frame(ms, 0.1f));
        for (int ms = 2000; ms < 2600; ms += 140) logger.ProcessSample(0, Frame(ms, 0.1f, level: 100f));

        var files = Recordings();
        Assert.Equal(2, files.Count);
        Assert.Equal(1, SidecarIntegration(files.Single(f => !f.EndsWith("_1.csv"))));
        Assert.Equal(0.1, SidecarIntegration(files.Single(f => f.EndsWith("_1.csv"))), 6);
    }

    [Fact]
    public void Closing_a_recording_does_not_rewrite_its_sidecar()
    {
        using var logger = new DualIntensityLogger(new[] { "OES1" }, _root);
        logger.Configure(Settings());
        var sidecars = new RecordingSidecars();
        var current = At(1);
        logger.FilesChanged += (_, _) => sidecars.Write(logger.CurrentFiles, current, tool: null);

        for (int ms = 0; ms < 1000; ms += 140) logger.ProcessSample(0, Frame(ms, 1f));
        current = At(150);   // settings moved on while the file was open; nothing it holds was taken at 150
        logger.Stop();

        Assert.Equal(1, SidecarIntegration(Assert.Single(Recordings())));
    }
}
