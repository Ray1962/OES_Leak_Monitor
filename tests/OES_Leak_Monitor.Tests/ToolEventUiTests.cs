using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The EAP tab — the EAP link's settings and status, and the tool-event controls moved there
/// from the SECS tab (#58) — and the Leak Monitor banner (#34), at the view-model seam.
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

    private (EapViewModel Vm, List<ToolEventSettings> Saved) Tab(ToolEventSettings tools)
    {
        var saved = new List<ToolEventSettings>();
        var vm = new EapViewModel(_bridge, tools, saved.Add);
        vm.SetRole(isEngineerOrHigher: true);
        return (vm, saved);
    }

    /// <summary>A complete, distinct set of the nine EAP ids, as the vendor would assign them.</summary>
    private static void EnterIds(EapViewModel vm)
    {
        vm.WindowStateSvid = 6001; vm.WindowCeidSvid = 6002; vm.WindowStartSvid = 6003;
        vm.ToolLinkSvid = 6004; vm.LastCeidSvid = 6005; vm.LastTimeSvid = 6006; vm.SequenceSvid = 6007;
        vm.ForwardCeid = 6100; vm.LinkCeid = 6101;
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// The link's fields and the nine ids reach settings.json through the tab's Save, and the
    /// CEID table — which the tab does not edit — rides along untouched.
    /// </summary>
    [Fact]
    public void Saving_CarriesTheEapLinkFieldsAndIds()
    {
        var (vm, saved) = Tab(Pm3());

        vm.EapEnabled = true;
        vm.EapHost = "10.2.3.121";
        vm.EapPort = 20348;
        vm.EapDeviceId = 3;
        vm.EapSilenceTimeoutSeconds = 90;
        EnterIds(vm);
        vm.SaveCommand.Execute(null);

        var eap = Assert.Single(saved).Eap;
        Assert.True(eap.Enabled);
        Assert.Equal("10.2.3.121", eap.RemoteHost);
        Assert.Equal(20348, eap.RemotePort);
        Assert.Equal(3, eap.DeviceId);
        Assert.Equal(90, eap.SilenceTimeoutSeconds);
        Assert.Equal(new uint[] { 6001, 6002, 6003, 6004, 6005, 6006, 6007 }, eap.StatusSvids);
        Assert.Equal(6100u, eap.ForwardCeid);
        Assert.Equal(6101u, eap.LinkCeid);
        Assert.Equal(2, saved[0].Mappings.Count);
    }

    /// <summary>
    /// What cannot work is not saved, and the tab says why: a silence timeout under 35 s fires on
    /// a healthy 30 s poll, an enabled link needs an address and all nine ids, and S1–S7 and the
    /// two CEIDs must be distinct or a value would be read from the wrong variable.
    /// </summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("host")]
    [InlineData("id-missing")]
    [InlineData("svid-duplicate")]
    [InlineData("ceid-duplicate")]
    public void AnUnusableEapLink_IsNotSaved(string what)
    {
        var (vm, saved) = Tab(Pm3());
        vm.EapEnabled = true;
        EnterIds(vm);
        switch (what)
        {
            case "timeout": vm.EapSilenceTimeoutSeconds = 30; break;
            case "host": vm.EapHost = " "; break;
            case "id-missing": vm.SequenceSvid = 0; break;
            case "svid-duplicate": vm.WindowCeidSvid = 6001; break;
            case "ceid-duplicate": vm.LinkCeid = 6100; break;
        }
        vm.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.StartsWith("Not saved", vm.SaveMessage);
    }

    /// <summary>Switched off, the ids may stay unset: someone filling the tab in before the EAP
    /// has assigned them can still save the tool-event switches.</summary>
    [Fact]
    public void WithTheLinkOff_IdsAreNotRequired()
    {
        var (vm, saved) = Tab(Pm3());
        vm.ToolMaxWindowSeconds = 300;
        vm.SaveCommand.Execute(null);
        Assert.Single(saved);
    }

    [Fact]
    public void WithNoLink_TheStatusSaysNotInUse()
    {
        var (vm, _) = Tab(Pm3());
        Assert.Equal("EAP link: not in use", vm.EapLinkText);
        Assert.Equal("", vm.EapDetailText);
    }

    /// <summary>Switched on with the ids not set, the link does not start and the status line
    /// says so in words — not a red "cannot connect" that sends someone to check the network.</summary>
    [Fact]
    public void WithIdsMissing_TheStatusSaysTheLinkIsNotStarted()
    {
        using var link = new EapSecsLink(null, _dir, (_, _) => ToolEventAck.Accepted, _ => ToolEventAck.Accepted);
        link.Configure(new EapLinkSettings { Enabled = true }, 30);
        var vm = new EapViewModel(_bridge, Pm3(), _ => { }, link);

        Assert.Equal(EapLinkState.IdsMissing, link.Status.State);
        Assert.Contains("not started", vm.EapLinkText);
        Assert.Contains("ids", vm.EapLinkText);
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

        var tools = Assert.Single(saved);
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
        Assert.Contains("window", vm.SaveMessage);
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

        vm.SetToolEventsInForce(saved[^1]);             // the acquisition start adopts it
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
