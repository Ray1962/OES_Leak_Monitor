using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Where the process window the main tool's events put the monitor in is written down: the ratio
/// CSV, the recording's acquisition sidecar, and the system log (#33).
///
/// <para>What this guards: a row of a ratio CSV read a year later has to say which process it was
/// judged in, or nobody can tell a leak during process A from an excursion during a clean — and an
/// installation that never uses tool events must get exactly the files it always got.</para>
/// </summary>
public sealed class ToolEventOutputTests : IDisposable
{
    private static readonly DateTime Epoch = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Local);
    private const uint ProcessStart = 3100439529, ProcessEnd = 3100439530;
    private const double O777 = 777.4, Sig337 = 337.1;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oes-toolout-" + Guid.NewGuid().ToString("N"));

    public ToolEventOutputTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- fixtures

    private static SpectrumSample Frame(double seconds)
    {
        const int n = 1100;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = 10;
            if (Math.Abs(wl[i] - O777) <= 1.5) inten[i] = 800;
            if (Math.Abs(wl[i] - Sig337) <= 1.5) inten[i] = 500;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds),
            Wavelengths = wl,
            Intensities = inten,
            SerialNumber = "TEST",
            // A hardware frame: the windows these files record are the chamber's, and tool events
            // are not applied to synthetic spectra unless asked (#31).
            IsSimulated = false,
        };
    }

    private static ToolEventSettings Pm3() => new()
    {
        Enabled = true,
        Mappings =
        {
            new ToolEventMapping { Ceid = ProcessStart, ClassName = "A", Edge = ToolEventEdge.Start, Name = "PM3 ProcessStart" },
            new ToolEventMapping { Ceid = ProcessEnd, ClassName = "A", Edge = ToolEventEdge.End, Name = "PM3 ProcessEnd" },
        },
    };

    private LeakMonitorEngine Engine()
    {
        var settings = new LeakMonitorSettings { Enabled = true, RequireTwoForAlarm = false };
        settings.Ratios.Add(new RatioDefinition
        {
            Key = "R_A", DisplayName = "Sig A", Enabled = true, ProcessClass = "A",
            MonitorMode = MonitorMode.AbsoluteIntensity, MinSnr = 0,
            Numerator = new LineRegion
            {
                Label = "Sig 337", CenterNm = Sig337, HalfWidthNm = 0.5,
                BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
            },
            Denominator = new LineRegion
            {
                Label = "O 777", CenterNm = O777, HalfWidthNm = 0.5,
                BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
            },
        });
        var engine = new LeakMonitorEngine(settings);
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.Wavelength,
            TriggerWavelength = (float)O777,
            WavelengthToleranceNm = 1f,
            SaveStartThresholdIntensity = 500,
        });
        return engine;
    }

    /// <summary>Runs frames through a ratio logger and returns the file's lines.</summary>
    private string[] RatioCsv(LeakMonitorEngine engine, Action<int> beforeFrame, int frames)
    {
        string path;
        using (var logger = new RatioCsvLogger(engine, _root))
        {
            logger.Configure(new LoggerSettings { BaseDirectory = _root, FilePrefix = "T" });
            for (int i = 0; i < frames; i++)
            {
                beforeFrame(i);
                engine.ProcessSample(Frame(i * 0.5));
            }
            path = logger.CurrentFile;
            Assert.False(string.IsNullOrEmpty(path), "the ratio logger opened no file");
        }
        return File.ReadAllLines(path);
    }

    // ---------------------------------------------------------------- ratio CSV

    /// <summary>
    /// Never configured, the ratio CSV's columns are exactly the ones it has always had — the
    /// header still ends at the leak-rate pair, so nothing downstream of an unconfigured install
    /// sees a change.
    /// </summary>
    [Fact]
    public void WithoutToolEvents_TheRatioCsvHasNoToolColumns()
    {
        using var engine = Engine();
        var lines = RatioCsv(engine, _ => { }, frames: 3);

        Assert.EndsWith(",OverallState,LeakRate,LeakRateSigma", lines[0]);
        Assert.DoesNotContain("ToolContext", lines[0]);
    }

    /// <summary>
    /// In use, every row says which window the frame was judged in — before the first edge
    /// (Unknown), inside A, and after it closes — and the class is blank outside a window.
    /// </summary>
    [Fact]
    public void WithToolEvents_EveryRowSaysWhichWindowItWasJudgedIn()
    {
        using var engine = Engine();
        engine.ConfigureToolEvents(Pm3());

        var lines = RatioCsv(engine, i =>
        {
            if (i == 2) engine.ReceiveToolEvent(ProcessStart, Epoch.AddSeconds(i));
            if (i == 4) engine.ReceiveToolEvent(ProcessEnd, Epoch.AddSeconds(i));
        }, frames: 6);

        var header = lines[0].Split(',');
        Assert.Equal("ToolWindowClass", header[^1]);
        Assert.Equal("ToolContext", header[^2]);

        string[] Tail(int row) => lines[row + 1].Split(',')[^2..];
        Assert.Equal(new[] { "Unknown", "" }, Tail(0));
        Assert.Equal(new[] { "Unknown", "" }, Tail(1));
        Assert.Equal(new[] { "InWindow", "A" }, Tail(2));
        Assert.Equal(new[] { "InWindow", "A" }, Tail(3));
        Assert.Equal(new[] { "OutsideWindow", "" }, Tail(4));
        Assert.Equal(new[] { "OutsideWindow", "" }, Tail(5));
    }

    /// <summary>
    /// Ratio Review reads a file carrying the new columns exactly as it reads one without them:
    /// the reader locates columns by name, so the trailing pair is ignored rather than taken for
    /// another ratio.
    /// </summary>
    [Fact]
    public void RatioReview_ReadsAFileWithToolColumns_AsBefore()
    {
        using var engine = Engine();
        engine.ConfigureToolEvents(Pm3());
        var lines = RatioCsv(engine, i =>
        {
            if (i == 1) engine.ReceiveToolEvent(ProcessStart, Epoch.AddSeconds(i));
        }, frames: 4);

        var data = RatioCsvReader.Read(new StringReader(string.Join("\n", lines)));

        Assert.Equal(new[] { "R_A" }, data.RatioKeys);
        Assert.Equal(4, data.RowCount);
        Assert.Equal(4, data.States.Count);
        Assert.All(data.States, s => Assert.DoesNotContain("Window", s));
    }

    // ---------------------------------------------------------------- acquisition sidecar

    private static AcquisitionFingerprint Fingerprint() => new()
    {
        IntegrationTimeMs = 150, AverageCount = 4, AxisLength = 1100, AxisStartNm = 300, AxisEndNm = 849.5,
    };

    private string RecordingFile()
    {
        var day = Path.Combine(_root, "202610", "23");
        Directory.CreateDirectory(day);
        var path = Path.Combine(day, "P_OES1_1023100000.csv");
        File.WriteAllText(path, "WaveLength,300\n10:00:00.000,1\n");
        return path;
    }

    /// <summary>
    /// The sidecar records the window the recording opened in, so a baseline built from it later
    /// can know which process it was — and the fingerprint beside it still reads as before.
    /// </summary>
    [Fact]
    public void TheSidecar_RecordsTheWindowTheRecordingOpenedIn()
    {
        var path = RecordingFile();
        var status = new ToolEventStatus(ToolEventContext.InWindow, "A", ProcessStart, Epoch);

        Assert.Null(AcquisitionSidecar.TryWrite(path, Fingerprint(), status));

        var rec = Recording.TryParse(path)!;
        var tool = AcquisitionSidecar.TryReadToolContext(rec);
        Assert.NotNull(tool);
        Assert.Equal(ToolEventContext.InWindow, tool!.Context);
        Assert.Equal("A", tool.WindowClass);
        Assert.Equal(150, AcquisitionSidecar.TryRead(rec)!.IntegrationTimeMs);
    }

    /// <summary>
    /// Not in use, the sidecar carries no tool fields at all — the file means what it always
    /// meant — and a sidecar written before this existed reads as "no context", not as a guess.
    /// </summary>
    [Fact]
    public void WithoutToolEvents_OrFromBefore_TheSidecarHasNoToolContext()
    {
        var path = RecordingFile();

        Assert.Null(AcquisitionSidecar.TryWrite(path, Fingerprint(), ToolEventStatus.Disabled));
        var json = File.ReadAllText(AcquisitionSidecar.PathFor(path));
        Assert.DoesNotContain("tool", json, StringComparison.OrdinalIgnoreCase);

        var rec = Recording.TryParse(path)!;
        Assert.Null(AcquisitionSidecar.TryReadToolContext(rec));
        Assert.Equal(150, AcquisitionSidecar.TryRead(rec)!.IntegrationTimeMs);

        // An old two-argument write (the shape every existing sidecar has).
        Assert.Null(AcquisitionSidecar.TryWrite(path, Fingerprint()));
        Assert.Null(AcquisitionSidecar.TryReadToolContext(Recording.TryParse(path)!));
    }

    // ---------------------------------------------------------------- system log

    /// <summary>
    /// A forwarded CEID that is not in the table reaches the audit log, not only the SECS traffic
    /// log — once per CEID, because the EAP is invited to forward the whole tool's events
    /// unfiltered, and one line per slit-valve cycle would bury the log it is written to.
    /// </summary>
    [Fact]
    public async Task AnIgnoredCeid_IsLoggedOnce_PerCeid()
    {
        var logDir = Path.Combine(_root, "Logs");
        using var log = new SystemLogger(logDir);
        await using var h = await SecsHarness.StartAsync(log: log);
        h.Bridge.ToolEventSink = (_, _) => ToolEventAck.Ignored;

        await h.Host.SendRemoteCommandAsync("TOOL_EVENT", ("CEID", "2697019492"));
        await h.Host.SendRemoteCommandAsync("TOOL_EVENT", ("CEID", "2697019492"));
        await h.Host.SendRemoteCommandAsync("TOOL_EVENT", ("CEID", "2697019493"));

        Assert.True(await SecsTestPort.WaitAsync(
            () => log.Snapshot().Count(e => e.Condition == "ToolEventIgnored") >= 2, 10),
            "no ToolEventIgnored entry reached the system log");
        await Task.Delay(500);   // a third, duplicate entry would have arrived by now

        var ignored = log.Snapshot().Where(e => e.Condition == "ToolEventIgnored").ToList();
        Assert.Equal(2, ignored.Count);
        Assert.Contains(ignored, e => e.Description.Contains("2697019492") || e.RelatedParameters.Contains("2697019492"));
        Assert.Contains(ignored, e => e.Description.Contains("2697019493") || e.RelatedParameters.Contains("2697019493"));
    }
}
