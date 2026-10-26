using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The tool-event controls on the SECS tab and the Leak Monitor banner (#34), at the
/// view-model seam.
///
/// <para>What this guards: the section has two kinds of field. <c>Enabled</c> and the CEID
/// table are <b>staged</b> — saved now, adopted when acquisition next starts, because the table
/// names the classes the ratios refer to — while <c>MaxWindowSeconds</c> and
/// <c>ApplyInTestMode</c> are hot-applied. A screen that does not say which is which, or that
/// shows a saved table as if it were the one in force, is how someone edits the table, sees it
/// on screen, and wonders for a shift why nothing changed.</para>
/// </summary>
public sealed class ToolEventUiTests : IDisposable
{
    private const uint ProcessStart = 3100439529, ProcessEnd = 3100439530;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OesLeakToolUi-" + Guid.NewGuid().ToString("N"));
    private readonly SecsBridge _bridge;

    public ToolEventUiTests()
    {
        Directory.CreateDirectory(_dir);
        _bridge = new SecsBridge(null, _dir, _dir, () => default);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ToolEventSettings Pm3(bool enabled = true) => new()
    {
        Enabled = enabled,
        Mappings =
        {
            new ToolEventMapping { Ceid = ProcessStart, ClassName = "A", Edge = ToolEventEdge.Start,
                                   Name = "PM3 Chamber Process/precoat start" },
            new ToolEventMapping { Ceid = ProcessEnd, ClassName = "A", Edge = ToolEventEdge.End,
                                   Name = "PM3 Chamber Process/precoat end", PlasmaThreshold = 1200 },
        },
    };

    private (SecsViewModel Vm, List<SecsSettings> Saved) Tab(ToolEventSettings tools)
    {
        var saved = new List<SecsSettings>();
        var vm = new SecsViewModel(_bridge, new SecsSettings { ToolEvents = tools }, saved.Add);
        vm.SetRole(isEngineerOrHigher: true);
        return (vm, saved);
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// The EAP link's four fields reach settings.json through the same Save, and the CEID table
    /// rides along untouched.
    /// </summary>
    [Fact]
    public void Saving_CarriesTheEapLinkFields()
    {
        var (vm, saved) = Tab(Pm3());

        vm.EapEnabled = true;
        vm.EapHost = "10.2.3.121";
        vm.EapPort = 20348;
        vm.EapHeartbeatTimeoutSeconds = 90;
        vm.SaveCommand.Execute(null);

        var tools = Assert.Single(saved).ToolEvents;
        Assert.True(tools.Tcp.Enabled);
        Assert.Equal("10.2.3.121", tools.Tcp.RemoteHost);
        Assert.Equal(20348, tools.Tcp.RemotePort);
        Assert.Equal(90, tools.Tcp.HeartbeatTimeoutSeconds);
        Assert.Equal(2, tools.Mappings.Count);
    }

    /// <summary>
    /// A heartbeat timeout shorter than 35 s fires on a healthy link (the EAP sends one every
    /// 30 s), and an enabled link with no address has nowhere to go: neither is saved.
    /// </summary>
    [Theory]
    [InlineData(30, "10.2.3.120")]
    [InlineData(601, "10.2.3.120")]
    [InlineData(75, " ")]
    public void AnUnusableEapLink_IsNotSaved(double timeout, string host)
    {
        var (vm, saved) = Tab(Pm3());

        vm.EapEnabled = true;
        vm.EapHost = host;
        vm.EapHeartbeatTimeoutSeconds = timeout;
        vm.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.Contains(vm.Traffic, l => l.Contains("not saved") && l.Contains("EAP"));
    }

    [Fact]
    public void WithNoLink_TheStatusSaysNotInUse()
    {
        var (vm, _) = Tab(Pm3());
        Assert.Equal("EAP link: not in use", vm.EapLinkText);
        Assert.Equal("", vm.EapDetailText);
    }

    /// <summary>
    /// The three editable fields reach settings.json through the tab's own Save, and the CEID
    /// table — which the tab does not edit — rides along untouched.
    /// </summary>
    [Fact]
    public void Saving_CarriesTheToolEventFields_AndKeepsTheTable()
    {
        var (vm, saved) = Tab(Pm3(enabled: false));

        vm.ToolEventsEnabled = true;
        vm.ToolMaxWindowSeconds = 120;
        vm.ToolApplyInTestMode = true;
        vm.SaveCommand.Execute(null);

        var tools = Assert.Single(saved).ToolEvents;
        Assert.True(tools.Enabled);
        Assert.Equal(120, tools.MaxWindowSeconds);
        Assert.True(tools.ApplyInTestMode);
        Assert.Equal(new[] { ProcessStart, ProcessEnd }, tools.Mappings.Select(m => m.Ceid));
        Assert.Equal(1200, tools.Mappings[1].PlasmaThreshold);
    }

    /// <summary>
    /// A window limit outside 10 s – 24 h is refused with a traffic line, like every other field
    /// the tab validates: shorter than a process step closes every window mid-step, and the
    /// settings that reach the engine are the ones that were checked.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(100000)]
    [InlineData(double.NaN)]
    public void AWindowLimitOutOfRange_IsRefused(double seconds)
    {
        var (vm, saved) = Tab(Pm3());

        vm.ToolMaxWindowSeconds = seconds;
        vm.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.Contains(vm.Traffic, l => l.StartsWith("[CFG] not saved") && l.Contains("window"));
    }

    /// <summary>The table is shown as saved, one row per mapping, in the words the tool uses.</summary>
    [Fact]
    public void TheTable_IsShownAsSaved()
    {
        var (vm, _) = Tab(Pm3());

        var rows = vm.ToolEventMappings;
        Assert.Equal(2, rows.Count);
        Assert.Equal(ProcessStart, rows[0].Ceid);
        Assert.Equal("A", rows[0].ClassName);
        Assert.Equal("Start", rows[0].Edge);
        Assert.Equal("End", rows[1].Edge);
        Assert.Equal("1200", rows[1].PlasmaThreshold);
        Assert.Equal("inherit", rows[0].PlasmaThreshold);
    }

    /// <summary>
    /// The staged half is saved now and adopted at the next acquisition start, so between the two
    /// the screen says the saved table is not the one in force. Switched off on both sides is the
    /// same thing whatever the rows say; the hot-applied fields never make it pending.
    /// </summary>
    [Fact]
    public void ASavedTableNotYetAdopted_IsFlagged_UntilAcquisitionAdoptsIt()
    {
        var (vm, saved) = Tab(Pm3());

        vm.SetToolEventsInForce(Pm3());
        Assert.False(vm.ToolTablePending);

        vm.ToolMaxWindowSeconds = 300;                  // hot: never pending
        vm.ToolApplyInTestMode = true;
        vm.SaveCommand.Execute(null);
        Assert.False(vm.ToolTablePending);

        vm.ToolEventsEnabled = false;                   // staged
        vm.SaveCommand.Execute(null);
        Assert.True(vm.ToolTablePending);
        Assert.Contains("restart acquisition", vm.ToolTablePendingText);

        vm.SetToolEventsInForce(saved[^1].ToolEvents);  // the acquisition start adopts it
        Assert.False(vm.ToolTablePending);

        vm.SetToolEventsInForce(Pm3(enabled: false));   // off with rows == off without
        Assert.False(vm.ToolTablePending);
    }

    // ---------------------------------------------------------------- status

    /// <summary>
    /// The status lines, visible without a sign-in: which window, the last event, and how many
    /// have arrived this run — the number that answers "is the EAP forwarding anything at all".
    /// </summary>
    [Fact]
    public void TheStatusLines_ReportWindowLastEventAndCount()
    {
        var (vm, _) = Tab(Pm3());
        Assert.Contains("not in use", vm.ToolContextText);

        _bridge.OnToolStatus(new ToolEventStatus(ToolEventContext.InWindow, "A", ProcessStart,
            new DateTime(2026, 10, 23, 14, 3, 0), ReceivedCount: 5));

        Assert.Contains("in window A", vm.ToolContextText);
        Assert.Contains(ProcessStart.ToString(), vm.ToolLastEventText);
        Assert.Contains("14:03:00", vm.ToolLastEventText);
        Assert.Contains("5", vm.ToolEventCountText);
    }

    // ---------------------------------------------------------------- Leak Monitor banner

    /// <summary>
    /// Unknown context — tool events in use but no window edge yet, or the last window timed out —
    /// gets its own banner line on the Leak Monitor tab, so a screen judging "as without tool
    /// events" says so. It is quiet when the test-mode line already says the events are not
    /// applied (that is the same fact, said once), and gone once a window is in force.
    /// </summary>
    [Fact]
    public void TheUnknownContextBanner_ShowsOnlyWhenItSaysSomethingNew()
    {
        string live = LeakMonitorViewModel.FormatToolContextNote(ToolEventContext.Unknown, testMode: false, testModeToolEventsApplied: null);
        Assert.Contains("unknown", live, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("judged as without tool events", live);

        Assert.Equal("", LeakMonitorViewModel.FormatToolContextNote(ToolEventContext.InWindow, false, null));
        Assert.Equal("", LeakMonitorViewModel.FormatToolContextNote(ToolEventContext.OutsideWindow, false, null));
        Assert.Equal("", LeakMonitorViewModel.FormatToolContextNote(ToolEventContext.Disabled, false, null));
        Assert.Equal("", LeakMonitorViewModel.FormatToolContextNote(ToolEventContext.Unknown, true, false));
        Assert.NotEqual("", LeakMonitorViewModel.FormatToolContextNote(ToolEventContext.Unknown, true, true));
    }
}
