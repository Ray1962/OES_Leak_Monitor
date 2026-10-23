using System.IO;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The SECS tab's "Add missing rows" (#48) at the view-model seam, plus the one check that
/// matters on the wire: a machine whose profile predates <c>TOOL_EVENT</c> stops answering it
/// HCACK 1 once the rows are added and the interface restarted.
/// </summary>
public sealed class SecsProfileRowsButtonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OesSecsRows-" + Guid.NewGuid().ToString("N"));
    private readonly SystemLogger _log;
    private readonly SecsBridge _bridge;

    public SecsProfileRowsButtonTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new SystemLogger(Path.Combine(_dir, "Logs"));
        _bridge = new SecsBridge(_log, _dir, _dir, () => default);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string ProfilePath => Path.Combine(_dir, SecsProfileTemplate.FolderName, new SecsSettings().ProfileFileName);

    private void WriteProfile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, json);
    }

    private SecsViewModel Tab(bool engineer = true)
    {
        var vm = new SecsViewModel(_bridge, new SecsSettings(), _ => { });
        vm.SetRole(engineer);
        return vm;
    }

    [Fact]
    public void Guest_and_Operator_cannot_press_it()
    {
        WriteProfile(SecsProfileFixtures.Before20261015);

        Assert.False(Tab(engineer: false).AddMissingRowsCommand.CanExecute(null));
        Assert.True(Tab(engineer: true).AddMissingRowsCommand.CanExecute(null));
    }

    [Fact]
    public void With_nothing_missing_it_is_off_and_says_why()
    {
        WriteProfile(SecsProfileTemplate.Json);
        var vm = Tab();

        Assert.False(vm.AddMissingRowsCommand.CanExecute(null));
        Assert.Contains("every row", vm.MissingRowsText);
    }

    [Fact]
    public void It_says_what_is_missing_and_that_deleted_rows_come_back_too()
    {
        WriteProfile(SecsProfileFixtures.Before20261015);
        var vm = Tab();

        Assert.Contains("258", vm.MissingRowsText);            // 3 + 250 SVs, 4 events, 1 command
        Assert.Contains("deleted", vm.MissingRowsText);
    }

    [Fact]
    public void Pressing_it_backs_the_profile_up_appends_the_rows_and_logs_what_it_did()
    {
        WriteProfile(SecsProfileFixtures.Before20261015);
        var vm = Tab();

        vm.AddMissingRowsCommand.Execute(null);

        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ProfilePath)!, "*.bak-*"));
        Assert.Matches(@"\.json\.bak-\d{14}$", backup);
        Assert.Equal(SecsProfileFixtures.Before20261015, File.ReadAllText(backup));
        Assert.Empty(SecsProfileRows.Missing(File.ReadAllText(ProfilePath), SecsProfileTemplate.Json));

        Assert.Contains("258", vm.AddMissingRowsResultText);
        Assert.Contains("next time", vm.AddMissingRowsResultText);
        Assert.False(vm.AddMissingRowsCommand.CanExecute(null));   // nothing left to add

        var entry = Assert.Single(_log.Snapshot(), e => e.Condition == "SecsProfileRowsAdded");
        Assert.Contains("253 statusVariables", entry.Value + entry.Description);
    }

    [Fact]
    public void A_refused_row_is_named_on_the_tab()
    {
        WriteProfile(SecsProfileFixtures.Before20261015.Replace(
            "\"bind\": \"oes.processStepIndex\" }",
            "\"bind\": \"oes.processStepIndex\" },\n    { \"svid\": 1002700030, \"name\": \"Machine id\", \"format\": \"A\", \"value\": \"M-01\" }"));
        var vm = Tab();

        vm.AddMissingRowsCommand.Execute(null);

        Assert.Contains("refused 1", vm.AddMissingRowsResultText);
        Assert.Contains("1002700030", vm.AddMissingRowsResultText);

        // What is left can never go in, so the button has nothing to do — and says why.
        Assert.False(vm.AddMissingRowsCommand.CanExecute(null));
        Assert.Contains("1002700030", vm.MissingRowsText);
    }

    [Fact]
    public void A_profile_saved_with_a_BOM_keeps_it()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, SecsProfileFixtures.Before20261015, new System.Text.UTF8Encoding(true));

        Tab().AddMissingRowsCommand.Execute(null);

        var bytes = File.ReadAllBytes(ProfilePath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.NotEqual(0xEF, bytes[3]);
    }

    [Fact]
    public void A_second_backup_in_the_same_second_does_not_replace_the_first()
    {
        WriteProfile(SecsProfileFixtures.Before20261015);
        var vm = Tab();
        vm.AddMissingRowsCommand.Execute(null);
        // A row deleted again straight away, and the button pressed again within the second.
        var afterFirst = File.ReadAllText(ProfilePath);
        WriteProfile(string.Join("\n", afterFirst.Split('\n').Where(l => !l.Contains("\"oes.slot1.key\""))));
        vm.AddMissingRowsCommand.Execute(null);

        var backups = Directory.GetFiles(Path.GetDirectoryName(ProfilePath)!, "*.bak-*");
        Assert.Equal(2, backups.Length);
        Assert.Contains(backups, b => File.ReadAllText(b) == SecsProfileFixtures.Before20261015);
    }
}

/// <summary>The same button's effect, on the wire.</summary>
[Collection("network")]
public class SecsProfileRowsWireTests
{
    private static async Task<string> ToolEventReplyAsync(SecsHarness h)
    {
        var lines = new List<string>();
        void Capture(string line) { if (line.Contains("S2F41")) lock (lines) lines.Add(line); }
        h.Host.Log += Capture;
        try
        {
            await h.Host.SendRemoteCommandAsync("TOOL_EVENT", ("CEID", "3100439529"));
            Assert.True(await SecsTestPort.WaitAsync(() => { lock (lines) return lines.Count > 0; }, 10),
                "the host logged no S2F41 reply");
            lock (lines) return lines[^1];
        }
        finally
        {
            h.Host.Log -= Capture;
        }
    }

    [Fact]
    public async Task A_profile_from_before_tool_events_takes_TOOL_EVENT_once_its_rows_are_added()
    {
        await using var h = await SecsHarness.StartAsync(
            prepare: folder =>
            {
                var path = Path.Combine(folder, SecsProfileTemplate.FolderName, new SecsSettings().ProfileFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, SecsProfileFixtures.Before20261015);
            });
        Assert.Contains("HCACK=1", await ToolEventReplyAsync(h));

        h.Bridge.AddMissingProfileRows();
        await h.RestartAsync();

        // Nothing is wired to take the event here, so the honest answer is "cannot perform now".
        var reply = await ToolEventReplyAsync(h);
        Assert.DoesNotContain("HCACK=1", reply);
        Assert.Contains("HCACK=2", reply);
    }
}
