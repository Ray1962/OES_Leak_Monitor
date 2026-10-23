using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Move up / Move down on the Ratio Setup tab (#51). Slot N is row N, so moving a row is how a
/// site decides which ratio a host sees in which slot. What has to survive the move is
/// everything keyed on the ratio rather than on its position: the key itself, the Golden Run
/// baseline matched to it, and a latched alarm — deleting and re-adding a row would lose all
/// three, which is why the move had to exist at all.
/// </summary>
public class RatioSetupReorderTests
{
    // Catalog lines: the editor keeps a ratio's own region only while it still names a catalog
    // line, which every ratio built on the tab does.
    private const double Line = 337.1;
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private static SpectrumSample Frame(double seconds, double level)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - Line) <= 0.5) inten[i] = (float)level;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            IntegrationTime = 0, AverageCount = 0, SerialNumber = "T", IsSimulated = true,
        };
    }

    private static RatioDefinition Def(string key, MonitorMode mode = MonitorMode.AbsoluteIntensity) => new()
    {
        Key = key, DisplayName = key, Enabled = true,
        MonitorMode = mode, MinSnr = 0,
        SigmaWarn = 3, SigmaAlarm = 6, EmaTauSeconds = 0.1, ConfirmSeconds = 1.0,
        Numerator = new LineRegion
        {
            Label = "N2 337.1", CenterNm = Line, HalfWidthNm = 0.5,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
        },
        Denominator = new LineRegion
        {
            Label = "Ar 750.4", CenterNm = 750.4, HalfWidthNm = 0.5,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
        },
    };

    private sealed class Tab : IDisposable
    {
        public LeakMonitorSettings Settings { get; } = new()
        {
            Enabled = true, SuppressAlarmsInTestMode = false, RequireTwoForAlarm = false,
        };
        public LeakMonitorEngine Engine { get; }
        public RatioSetupViewModel Vm { get; }
        public int Saves { get; private set; }
        public List<RatioSlotTable> IdentityChanges { get; } = new();
        public LeakMonitorSnapshot? Last { get; private set; }

        public SystemLogger? Log { get; }

        public Tab(params string[] keys) : this(MonitorMode.AbsoluteIntensity, null, keys) { }

        public Tab(MonitorMode mode, SystemLogger? log, params string[] keys)
        {
            Log = log;
            foreach (var k in keys) Settings.Ratios.Add(Def(k, mode));
            Engine = new LeakMonitorEngine(Settings, log);
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.SpectrumPercentile,
                TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
            });
            Engine.SampleProcessed += (_, s) => Last = s;
            Engine.SlotIdentityChanged += t => IdentityChanges.Add(t);
            Vm = new RatioSetupViewModel(Engine, () => Saves++);
            Vm.SetRole(engineerOrHigher: true);
        }

        public string[] Rows => Vm.Ratios.Select(r => r.ToDefinition().Key).ToArray();

        public void Select(string key) => Vm.SelectedRatio = Vm.Ratios.First(r => r.ToDefinition().Key == key);

        public void Dispose() => Engine.Dispose();
    }

    [Fact]
    public void Guest_and_Operator_cannot_move_a_row()
    {
        using var tab = new Tab("A", "B", "C");
        tab.Vm.SetRole(engineerOrHigher: false);
        tab.Select("B");

        Assert.False(tab.Vm.MoveUpCommand.CanExecute(null));
        Assert.False(tab.Vm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void The_first_row_cannot_go_up_the_last_cannot_go_down_and_nothing_selected_moves_nothing()
    {
        using var tab = new Tab("A", "B", "C");

        tab.Select("A");
        Assert.False(tab.Vm.MoveUpCommand.CanExecute(null));
        Assert.True(tab.Vm.MoveDownCommand.CanExecute(null));

        tab.Select("C");
        Assert.True(tab.Vm.MoveUpCommand.CanExecute(null));
        Assert.False(tab.Vm.MoveDownCommand.CanExecute(null));

        tab.Vm.SelectedRatio = null;
        Assert.False(tab.Vm.MoveUpCommand.CanExecute(null));
        Assert.False(tab.Vm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void A_move_is_staged_until_Save_and_Revert_puts_the_order_back()
    {
        using var tab = new Tab("A", "B", "C");
        tab.Select("C");

        tab.Vm.MoveUpCommand.Execute(null);

        Assert.Equal(new[] { "A", "C", "B" }, tab.Rows);
        Assert.True(tab.Vm.IsDirty);
        Assert.Equal("C", tab.Vm.SelectedRatio!.ToDefinition().Key);      // the selection moves with it
        Assert.Equal(new[] { "A", "B", "C" }, tab.Settings.Ratios.Select(r => r.Key));
        Assert.Equal(0, tab.Saves);

        tab.Vm.RevertCommand.Execute(null);

        Assert.Equal(new[] { "A", "B", "C" }, tab.Rows);
        Assert.False(tab.Vm.IsDirty);
    }

    [Fact]
    public void Saving_a_move_reorders_the_slots_once_and_keeps_what_each_ratio_measures()
    {
        using var tab = new Tab("A", "B", "C");
        var before = tab.Settings.Ratios.ToDictionary(r => r.Key, r => r.Clone());
        tab.Select("A");

        tab.Vm.MoveDownCommand.Execute(null);
        tab.Vm.MoveDownCommand.Execute(null);
        tab.Vm.SaveCommand.Execute(null);
        tab.Engine.ReloadRatios();

        Assert.Equal(1, tab.Saves);
        Assert.Equal(new[] { "B", "C", "A" }, tab.Engine.SlotTable.Slots.Select(s => s.Key));
        Assert.Single(tab.IdentityChanges);
        // Same key, same lines, same mode: every baseline and calibration keyed on it still applies.
        Assert.All(tab.Settings.Ratios, r => Assert.True(before[r.Key].MeasuresSameAs(r), $"{r.Key} was redefined by the move"));
    }

    /// <summary>Both modes: a ratio-mode baseline is also matched on its reference label, so
    /// that is the one a move would most plausibly break.</summary>
    [Theory]
    [InlineData(MonitorMode.AbsoluteIntensity)]
    [InlineData(MonitorMode.Ratio)]
    public void A_latched_alarm_and_the_baseline_survive_the_move(MonitorMode mode)
    {
        var logDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oes-reorder-" + Guid.NewGuid().ToString("N"));
        using var log = new SystemLogger(logDir);
        using var tab = new Tab(mode, log, "A", "B");
        double t = 0;
        tab.Engine.BeginGoldenRunCapture("b", seconds: 2);
        for (; t <= 2.5; t += 0.5) tab.Engine.ProcessSample(Frame(t, 1000));
        for (; t <= 12.0; t += 0.5) tab.Engine.ProcessSample(Frame(t, 2000));
        Assert.Equal(LeakAlarmLevel.Alarm, tab.Last!.Overall);

        tab.Select("B");
        tab.Vm.MoveUpCommand.Execute(null);
        tab.Vm.SaveCommand.Execute(null);
        tab.Engine.ReloadRatios();
        tab.Engine.ProcessSample(Frame(t += 0.5, 1000));   // back to baseline: only a latch keeps it at Alarm

        Assert.Equal(new[] { "B", "A" }, tab.Engine.SlotTable.Slots.Select(s => s.Key));
        Assert.Equal(LeakAlarmLevel.Alarm, tab.Last!.Overall);
        Assert.All(tab.Last.Ratios, r =>
        {
            Assert.True(r.HasBaseline, $"{r.Key} lost its baseline");
            Assert.Equal(RatioState.Alarm, r.State);
        });
        Assert.DoesNotContain(log.Snapshot(), e =>
            e.Condition is "LeakMonitorBaselineMismatch" or "LeakMonitorAlarmLatchDropped");
        try { System.IO.Directory.Delete(logDir, recursive: true); } catch { /* best effort */ }
    }
}
