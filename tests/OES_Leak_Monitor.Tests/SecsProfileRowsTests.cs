using System.IO;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// "Add missing rows" (#48): a site profile is never overwritten, so everything a later build
/// adds — status variables, events, alarms, remote commands — reaches an existing machine only
/// if someone puts it there. This appends what the shipped template has and the site's file
/// lacks, and touches nothing else: not a renumbered SVID, not a renamed row, not a comment.
/// </summary>
public class SecsProfileRowsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oes-secs-rows-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static readonly System.Text.RegularExpressions.Regex EmptySection =
        new(@":\s*(\[\s*\]|\{\s*\})\s*,?\s*$");

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>True when every line of <paramref name="before"/> is still in
    /// <paramref name="after"/>, unchanged and in the same order — rows, comments, all of it,
    /// except an empty section (<c>"remoteCommands": {}</c>), which has to be opened to hold a row.</summary>
    private static bool KeepsEveryLine(string before, string after)
    {
        var rest = Lines(after);
        int i = 0;
        foreach (var line in Lines(before).Where(l => !EmptySection.IsMatch(l)))
        {
            while (i < rest.Length && rest[i] != line) i++;
            if (i == rest.Length) return false;
            i++;
        }
        return true;
    }

    private IDeviceProfile Load(string json)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, SecsChamberCoding.ApplyChamber(json, 2));
        return JsonDeviceProfile.Load(path);
    }

    [Fact]
    public void A_profile_from_before_tool_events_is_missing_what_has_been_added_since()
    {
        var missing = SecsProfileRows.Missing(SecsProfileFixtures.Before20261015, SecsProfileTemplate.Json);

        Assert.Equal(3 + 250, missing.Count(r => r.Section == "statusVariables"));
        Assert.Contains(missing, r => r.Section == "statusVariables" && r.Key == "oes.toolEventState");
        Assert.Contains(missing, r => r.Section == "statusVariables" && r.Key == "oes.slot10.key");
        Assert.Equal(new[] { "10027502", "10027507", "10027508", "10027509" },
            missing.Where(r => r.Section == "events").Select(r => r.Key));
        Assert.DoesNotContain(missing, r => r.Section == "alarms");
        Assert.Equal(new[] { "TOOL_EVENT" }, missing.Where(r => r.Section == "remoteCommands").Select(r => r.Key));
    }

    [Fact]
    public void Adding_keeps_every_existing_line_and_leaves_nothing_missing()
    {
        var before = SecsProfileFixtures.Before20261015;

        var result = SecsProfileRows.AddMissing(before, SecsProfileTemplate.Json);

        Assert.Equal(3 + 250 + 4 + 1, result.Added.Count);
        Assert.Empty(result.Rejected);
        Assert.True(KeepsEveryLine(before, result.Json), "an existing line was changed or removed");
        Assert.Contains("// No remote commands: this tool reports, it is not driven from the host.", result.Json);
        Assert.Empty(SecsProfileRows.Missing(result.Json, SecsProfileTemplate.Json));

        var profile = Load(result.Json);
        Assert.Equal(32 + 250, profile.RequiredBindings.Count());
        Assert.Equal(new[] { "CEID", "TIME" }, profile.RemoteCommands["TOOL_EVENT"]);
    }

    [Fact]
    public void A_row_renumbered_or_renamed_on_site_is_left_as_it_is()
    {
        var site = SecsProfileFixtures.Before20261015.Replace(
            "{ \"svid\": 1002700001, \"name\": \"Leak rate\",",
            "{ \"svid\": 1002700901, \"name\": \"洩漏率\",");

        var result = SecsProfileRows.AddMissing(site, SecsProfileTemplate.Json);

        Assert.True(KeepsEveryLine(site, result.Json));
        Assert.DoesNotContain(result.Added, r => r.Key == "oes.leakRate");
        Assert.Single(Lines(result.Json), l => l.Contains("\"oes.leakRate\""));
    }

    [Fact]
    public void A_row_deleted_on_site_comes_back()
    {
        var site = string.Join("\n", Lines(SecsProfileTemplate.Json).Where(l => !l.Contains("\"oes.dropoutCount\"")));

        var result = SecsProfileRows.AddMissing(site, SecsProfileTemplate.Json);

        var added = Assert.Single(result.Added);
        Assert.Equal("oes.dropoutCount", added.Key);
        Assert.True(KeepsEveryLine(site, result.Json));
        Assert.Equal(32 + 250, Load(result.Json).RequiredBindings.Count());
    }

    [Fact]
    public void A_row_whose_number_is_taken_on_site_is_refused_and_the_rest_still_go_in()
    {
        // The site used 030 for a constant of its own before this build claimed it.
        var site = SecsProfileFixtures.Before20261015.Replace(
            "\"bind\": \"oes.processStepIndex\" }",
            "\"bind\": \"oes.processStepIndex\" },\n    { \"svid\": 1002700030, \"name\": \"Machine id\", \"format\": \"A\", \"value\": \"M-01\" }");

        var result = SecsProfileRows.AddMissing(site, SecsProfileTemplate.Json);

        var refused = Assert.Single(result.Rejected);
        Assert.Equal("oes.toolEventState", refused.Row.Key);
        Assert.Contains("1002700030", refused.Reason);
        Assert.Contains(result.Added, r => r.Key == "oes.toolWindowClass");
        Assert.Contains(result.Added, r => r.Key == "TOOL_EVENT");
        Assert.True(KeepsEveryLine(site, result.Json));
        Load(result.Json);   // and the result still loads
    }

    private const string Tiny = """
        {
          "name": "site",
          "statusVariables": [
            { "svid": 1002700001, "format": "F4", "bind": "oes.leakRate" }
          ],
          "hostActions": [] // the last one
        }
        """;

    /// <summary>A template of one SV and one remote command, so the edge cases below read
    /// without 280 rows in the way.</summary>
    private const string TinyTemplate = """
        {
          "statusVariables": [
            { "svid": 1002700001, "format": "F4", "bind": "oes.leakRate" },
            { "svid": 1002700002, "format": "F4", "bind": "oes.leakRateSigma" }
          ],
          "remoteCommands": { "TOOL_EVENT": [ "CEID", "TIME" ] }
        }
        """;

    private static void Parses(string json) =>
        System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }).Dispose();

    [Fact]
    public void A_section_appended_after_a_commented_last_property_leaves_that_line_alone()
    {
        var result = SecsProfileRows.AddMissing(Tiny, TinyTemplate);

        Parses(result.Json);
        Assert.Contains("  \"hostActions\": [] // the last one\n", result.Json.Replace("\r\n", "\n"));
        Assert.True(KeepsEveryLine(Tiny, result.Json));
        Assert.Equal(2, result.Added.Count);
    }

    [Fact]
    public void A_trailing_comma_after_the_last_property_does_not_become_two()
    {
        var site = Tiny.Replace("\"hostActions\": [] // the last one", "\"hostActions\": [], // the last one");

        var result = SecsProfileRows.AddMissing(site, TinyTemplate);

        Parses(result.Json);
        Assert.True(KeepsEveryLine(site, result.Json));
    }

    [Fact]
    public void A_CRLF_profile_stays_CRLF()
    {
        var site = Tiny.Replace("\r\n", "\n").Replace("\n", "\r\n");

        var result = SecsProfileRows.AddMissing(site, TinyTemplate);

        Parses(result.Json);
        Assert.DoesNotMatch("[^\r]\n", result.Json);
    }

    [Fact]
    public void A_section_that_is_not_a_list_is_refused_rather_than_written_twice()
    {
        var site = Tiny.Replace("\"hostActions\": []", "\"remoteCommands\": null,\n  \"hostActions\": []");

        var result = SecsProfileRows.AddMissing(site, TinyTemplate);

        var refused = Assert.Single(result.Rejected);
        Assert.Equal("TOOL_EVENT", refused.Row.Key);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Json, "\"remoteCommands\""));
        Parses(result.Json);
    }

    [Fact]
    public void A_complete_profile_gains_nothing()
    {
        var result = SecsProfileRows.AddMissing(SecsProfileTemplate.Json, SecsProfileTemplate.Json);

        Assert.Empty(result.Added);
        Assert.Equal(SecsProfileTemplate.Json, result.Json);
    }
}
