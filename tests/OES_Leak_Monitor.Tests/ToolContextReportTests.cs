using System;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// <c>LeakMonitorEngine.ReportToolContext</c> — the EAP heartbeat's statement of where the tool's
/// window <i>is</i>, as opposed to an edge that moves it.
///
/// <para>What this guards: the heartbeat is what lets a push-only link recover. After an OES
/// restart it puts the window back without waiting for the tool's next edge; when an edge was
/// lost it corrects the window and says so; when the EAP itself cannot vouch for the window it
/// sets it to unknown. A report that agrees must change nothing and write nothing — one arrives
/// every 30 s — and a report must never pose as an event the EAP sent (VID 032).</para>
/// </summary>
public sealed class ToolContextReportTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private const uint ProcessStart = 3100439529, ProcessEnd = 3100439530;
    private const uint CleanStart = 3099591660;
    private const uint LeakCheckStart = 3100167145;
    private const uint SlitDoorOpen = 2697019492;   // not in the table at all

    private const double O777 = 777.4, Sig337 = 337.1;

    private static SpectrumSample On(double seconds)
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
        };
    }

    private static LineRegion Raw(string label, double nm) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 0.5,
        BaselineGapNm = 1.0, BaselineWidthNm = 1.0,
        Mode = LineExtractMode.RawMean,
    };

    private static ToolEventSettings Pm3() => new()
    {
        Enabled = true,
        Mappings =
        {
            new ToolEventMapping { Ceid = ProcessStart, ClassName = "A", Edge = ToolEventEdge.Start, Name = "PM3 ProcessStart" },
            new ToolEventMapping { Ceid = ProcessEnd, ClassName = "A", Edge = ToolEventEdge.End, Name = "PM3 ProcessEnd" },
            new ToolEventMapping { Ceid = CleanStart, ClassName = "B", Edge = ToolEventEdge.Start, Name = "PM3 Clean start" },
            new ToolEventMapping { Ceid = LeakCheckStart, ClassName = "", Edge = ToolEventEdge.Start, Name = "PM3 Auto Leak check" },
        },
    };

    private sealed class Harness : IDisposable
    {
        private double _t;
        public LeakMonitorEngine Engine { get; }
        public LeakMonitorSnapshot? Last { get; private set; }

        public Harness(SystemLogger? log = null, bool configure = true)
        {
            var settings = new LeakMonitorSettings { Enabled = true, SuppressAlarmsInTestMode = false };
            settings.Ratios.Add(new RatioDefinition
            {
                Key = "R_A", DisplayName = "R_A", Enabled = true, ProcessClass = "A",
                MonitorMode = MonitorMode.AbsoluteIntensity, MinSnr = 0,
                Numerator = Raw("Sig 337", Sig337), Denominator = Raw("O 777", O777),
            });
            Engine = new LeakMonitorEngine(settings, log);
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.Wavelength, TriggerWavelength = (float)O777,
                WavelengthToleranceNm = 1f, SaveStartThresholdIntensity = 500,
            });
            Engine.SampleProcessed += (_, s) => Last = s;
            if (configure) Engine.ConfigureToolEvents(Pm3());
        }

        /// <summary>Reports are applied at the start of the next frame, like edges.</summary>
        public LeakMonitorSnapshot Frame()
        {
            Engine.ProcessSample(On(_t));
            _t += 0.5;
            return Last!;
        }

        public ToolEventAck Report(ToolContextReportState state, uint ceid = 0) =>
            Engine.ReportToolContext(new ToolContextReport(state, ceid, "test heartbeat"));

        public void Dispose() => Engine.Dispose();
    }

    // ---------------------------------------------------------------- restoring

    /// <summary>
    /// The case the heartbeat exists for: the OES restarts in the middle of a process step. The
    /// window is unknown until an edge arrives — which, without a heartbeat, is the step's End —
    /// and the first heartbeat puts it back on the very next frame.
    /// </summary>
    [Fact]
    public void AnInWindowReport_RestoresTheWindow_OnTheNextFrame()
    {
        using var h = new Harness();
        Assert.Equal(ToolEventContext.Unknown, h.Frame().ToolEventState);

        Assert.Equal(ToolEventAck.Accepted, h.Report(ToolContextReportState.InWindow, ProcessStart));
        var snap = h.Frame();

        Assert.Equal(ToolEventContext.InWindow, snap.ToolEventState);
        Assert.Equal("A", snap.ToolWindowClass);
    }

    [Fact]
    public void AnOutsideReport_RestoresOutside()
    {
        using var h = new Harness();
        h.Report(ToolContextReportState.Outside);
        Assert.Equal(ToolEventContext.OutsideWindow, h.Frame().ToolEventState);
    }

    // ---------------------------------------------------------------- correcting

    /// <summary>
    /// The End was lost: the OES still holds window A while the EAP says the tool is outside any
    /// window. The heartbeat closes it, and the log says an event went missing — which is the only
    /// evidence an EAP that drops events ever leaves.
    /// </summary>
    [Fact]
    public void ADisagreeingReport_CorrectsTheWindow_AndLogsAResync()
    {
        using var log = new LogCapture();
        using (var h = new Harness(log.Logger))
        {
            h.Engine.ReceiveToolEvent(ProcessStart, Epoch);
            Assert.Equal("A", h.Frame().ToolWindowClass);

            h.Report(ToolContextReportState.Outside);
            Assert.Equal(ToolEventContext.OutsideWindow, h.Frame().ToolEventState);
        }

        var lines = log.Lines();
        var resync = Assert.Single(lines, l => l.Contains("ToolEventResync"));
        Assert.Contains("window A", resync);
        Assert.Contains("outside any window", resync);
    }

    /// <summary>One window replaced by another is corrected the same way.</summary>
    [Fact]
    public void AReportNamingAnotherClass_SwitchesTheWindow()
    {
        using var h = new Harness();
        h.Engine.ReceiveToolEvent(ProcessStart, Epoch);
        h.Frame();

        h.Report(ToolContextReportState.InWindow, CleanStart);
        Assert.Equal("B", h.Frame().ToolWindowClass);
    }

    /// <summary>
    /// A heartbeat every 30 s that agrees with the window must leave no trace in the audit log —
    /// a line per heartbeat would bury the corrections that matter. Becoming known after a
    /// start-up is logged once, as information: it is the normal course, not a lost event.
    /// </summary>
    [Fact]
    public void AnAgreeingReport_WritesNothing()
    {
        using var log = new LogCapture();
        using (var h = new Harness(log.Logger))
        {
            for (int i = 0; i < 5; i++)
            {
                h.Report(ToolContextReportState.InWindow, ProcessStart);
                h.Frame();
            }
        }

        var lines = log.Lines();
        Assert.Single(lines, l => l.Contains("ToolEventContextRestored"));
        Assert.DoesNotContain(lines, l => l.Contains("ToolEventResync"));
    }

    // ---------------------------------------------------------------- the CEID table

    /// <summary>
    /// The tool is in a step the table records but scopes no ratio to (Auto Leak check). That is
    /// outside any window — and it closes an A window whose End was lost when the tool moved on,
    /// which an edge-by-edge reading of the same events never would.
    /// </summary>
    [Fact]
    public void ARecordOnlyStart_MeansOutsideAnyWindow()
    {
        using var h = new Harness();
        h.Engine.ReceiveToolEvent(ProcessStart, Epoch);
        h.Frame();

        Assert.Equal(ToolEventAck.Accepted, h.Report(ToolContextReportState.InWindow, LeakCheckStart));
        Assert.Equal(ToolEventContext.OutsideWindow, h.Frame().ToolEventState);
    }

    [Fact]
    public void AnUnmappedStart_MeansOutsideAnyWindow()
    {
        using var h = new Harness();
        h.Report(ToolContextReportState.InWindow, SlitDoorOpen);
        Assert.Equal(ToolEventContext.OutsideWindow, h.Frame().ToolEventState);
    }

    /// <summary>An End named as the event that opened the window is a contradiction: refused,
    /// nothing changes.</summary>
    [Fact]
    public void AnEndNamedAsTheOpeningEvent_IsRefused_AndChangesNothing()
    {
        using var h = new Harness();
        h.Engine.ReceiveToolEvent(ProcessStart, Epoch);
        h.Frame();

        Assert.Equal(ToolEventAck.Invalid, h.Report(ToolContextReportState.InWindow, ProcessEnd));
        Assert.Equal("A", h.Frame().ToolWindowClass);
    }

    // ---------------------------------------------------------------- unknown

    [Fact]
    public void AnUnknownReport_SetsUnknown_AndSaysWhy()
    {
        using var log = new LogCapture();
        using (var h = new Harness(log.Logger))
        {
            h.Engine.ReceiveToolEvent(ProcessStart, Epoch);
            h.Frame();

            h.Engine.ReportToolContext(new ToolContextReport(ToolContextReportState.Unknown, 0,
                "the EAP reports no SECS link to the tool"));
            var snap = h.Frame();
            Assert.Equal(ToolEventContext.Unknown, snap.ToolEventState);
            Assert.Equal("", snap.ToolWindowClass);
        }

        var line = Assert.Single(log.Lines(), l => l.Contains("ToolEventContextUnknown"));
        Assert.Contains("no SECS link to the tool", line);
    }

    // ---------------------------------------------------------------- order and identity

    /// <summary>
    /// Edges and reports between two frames are applied in the order they arrived: a heartbeat
    /// states the window as of the events before it, so a stale one must not undo a newer edge.
    /// </summary>
    [Fact]
    public void EdgesAndReports_ApplyInArrivalOrder()
    {
        using var h = new Harness();
        h.Report(ToolContextReportState.Outside);
        h.Engine.ReceiveToolEvent(ProcessStart, Epoch);
        Assert.Equal("A", h.Frame().ToolWindowClass);

        h.Engine.ReceiveToolEvent(CleanStart, Epoch);
        h.Report(ToolContextReportState.Outside);
        Assert.Equal(ToolEventContext.OutsideWindow, h.Frame().ToolEventState);
    }

    /// <summary>A report is not an event the EAP sent: VID 032 and the received count are left
    /// to the events.</summary>
    [Fact]
    public void AReport_DoesNotCountAsAReceivedEvent()
    {
        using var h = new Harness();
        h.Engine.ReceiveToolEvent(ProcessEnd, Epoch);
        h.Report(ToolContextReportState.InWindow, ProcessStart);
        h.Frame();

        Assert.Equal(ProcessEnd, h.Engine.ToolStatus.LastCeid);
        Assert.Equal(1, h.Engine.ToolStatus.ReceivedCount);
    }

    [Fact]
    public void WithToolEventsOff_AReportIsRefused()
    {
        using var h = new Harness(configure: false);
        Assert.Equal(ToolEventAck.Disabled, h.Report(ToolContextReportState.InWindow, ProcessStart));
        Assert.Equal(ToolEventContext.Disabled, h.Frame().ToolEventState);
    }
}

/// <summary>A real <see cref="SystemLogger"/> over a temporary folder, read back as lines.</summary>
internal sealed class LogCapture : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "oeslm-log-" + Guid.NewGuid().ToString("N"));
    public SystemLogger Logger { get; }

    public LogCapture()
    {
        System.IO.Directory.CreateDirectory(_dir);
        Logger = new SystemLogger(_dir);
    }

    /// <summary>Every line written so far. Flushes by disposing, so call it once, last.</summary>
    public string[] Lines()
    {
        Logger.Dispose();
        return System.IO.Directory.GetFiles(_dir, "*.csv")
            .SelectMany(System.IO.File.ReadAllLines).ToArray();
    }

    public void Dispose()
    {
        Logger.Dispose();
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
