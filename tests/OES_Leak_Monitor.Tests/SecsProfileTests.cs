using System.IO;
using System.Text.Json;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The contract between the profile file and the program: the file owns the numbers, the
/// program owns the values, and <c>bind</c> names join them. A name that exists on one side
/// only is the failure this checks for — it would otherwise surface as a host reading nothing,
/// or as an exception when the interface starts on a customer's machine.
/// </summary>
public class SecsProfileTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oes-secs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void The_shipped_template_parses_and_declares_what_the_specification_asks_for()
    {
        var profile = LoadStamped(2);

        Assert.Equal("OES Leak Monitor (ss=27)", profile.Name);
        // 001-032 global, plus 10 ratio slots x 25 offsets.
        Assert.Equal(32 + 250, profile.RequiredBindings.Count());
        Assert.Equal(5, profile.Alarms.Count());
        // One command, and it informs rather than drives: the EAP forwards the main tool's
        // process events. Its parameter names are what the library checks before the app sees it.
        var command = Assert.Single(profile.RemoteCommands);
        Assert.Equal("TOOL_EVENT", command.Key);
        Assert.Equal(new[] { "CEID", "TIME" }, command.Value);
    }

    [Fact]
    public void Every_binding_the_profile_names_is_one_the_app_supplies()
    {
        // The library fails construction when a profile names a binding nobody registered, so
        // this is the same check moved to build time.
        var missing = LoadStamped(2).RequiredBindings
            .Except(SecsBridge.SuppliedBindNames, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(missing.Count == 0, "profile binds names the app does not supply: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The other direction, which nothing used to check and which an upgrade produces every
    /// time: a status variable the app can serve that the shipped profile never asks for reaches
    /// no host at all, and says nothing about it. A site's own profile is deliberately never
    /// overwritten, so the template is the only copy this can be enforced on — the running
    /// interface reports the gap for the rest (<c>SecsBridge.BindsNotInProfile</c>).
    /// </summary>
    [Fact]
    public void The_shipped_template_asks_for_every_binding_the_app_supplies()
    {
        var unlisted = SecsBridge.BindsNotInProfile(
            SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 2));

        Assert.True(unlisted.Count == 0,
            "the app serves status variables the shipped profile does not list, so no host can " +
            "read them: " + string.Join(", ", unlisted));
    }

    /// <summary>
    /// The upgrade case itself: a profile written by an older build, kept (as it must be), and
    /// therefore missing the status variables added since. Nothing breaks — which is the
    /// problem, so the gap has to be named rather than inferred from a host reading nothing.
    /// </summary>
    [Fact]
    public void A_profile_from_an_older_build_names_what_it_is_missing()
    {
        var older = SecsProfileTemplate.Json;
        foreach (var bind in new[] { "oes.processClass", "oes.processClassState", "oes.processStepIndex" })
            older = string.Join("\n", older.Split('\n').Where(l => !l.Contains(bind)));

        var unlisted = SecsBridge.BindsNotInProfile(older);

        Assert.Equal(
            new[] { "oes.processClass", "oes.processClassState", "oes.processStepIndex" },
            unlisted.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void An_existing_profile_is_never_overwritten()
    {
        // It carries site edits — SVID numbers a customer chose, alarm text in their language.
        var path = Path.Combine(_folder, SecsProfileTemplate.FolderName, "p.json");
        Assert.True(SecsProfileTemplate.EnsureExists(path));

        File.WriteAllText(path, "{ \"name\": \"edited on site\", \"statusVariables\": [] }");
        Assert.False(SecsProfileTemplate.EnsureExists(path));
        Assert.Contains("edited on site", File.ReadAllText(path));
    }

    // ---- the ratio slot rows (#47) -------------------------------------------

    [Fact]
    public void The_template_numbers_every_slot_offset_by_the_specification_formula()
    {
        using var doc = JsonDocument.Parse(SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 2));
        var byBind = doc.RootElement.GetProperty("statusVariables").EnumerateArray()
            .ToDictionary(sv => sv.GetProperty("bind").GetString()!, sv => sv);

        // Worked from the specification's own examples (§1.4(b)): Ratio 1 offset 01 = 101,
        // Ratio 2 offset 01 = 131, Ratio 10 offset 30 = 400.
        Assert.Equal(1022700101, byBind["oes.slot1.monitoredValue"].GetProperty("svid").GetInt64());
        Assert.Equal(1022700131, byBind["oes.slot2.monitoredValue"].GetProperty("svid").GetInt64());
        Assert.Equal(1022700118, byBind["oes.slot1.valueSemantics"].GetProperty("svid").GetInt64());
        Assert.Equal(1022700395, byBind["oes.slot10.key"].GetProperty("svid").GetInt64());

        for (int n = 1; n <= 10; n++)
        {
            var slot = byBind.Where(kv => kv.Key.StartsWith($"oes.slot{n}.", StringComparison.Ordinal))
                .Select(kv => kv.Value.GetProperty("svid").GetInt64())
                .OrderBy(x => x).ToArray();
            long first = 1022700000 + 100 + (n - 1) * 30;
            Assert.Equal(Enumerable.Range(1, 25).Select(o => first + o), slot);
        }
    }

    [Fact]
    public void The_template_gives_each_offset_the_format_the_specification_names()
    {
        using var doc = JsonDocument.Parse(SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 2));
        var formats = doc.RootElement.GetProperty("statusVariables").EnumerateArray()
            .Where(sv => sv.GetProperty("bind").GetString()!.StartsWith("oes.slot3.", StringComparison.Ordinal))
            .ToDictionary(sv => (int)(sv.GetProperty("svid").GetInt64() - 1022700160),
                          sv => sv.GetProperty("format").GetString());

        var u4 = new[] { 5, 15, 16, 17, 18, 19 };
        var ascii = new[] { 22, 23, 24, 25 };
        for (int o = 1; o <= 25; o++)
        {
            string expected = u4.Contains(o) ? "U4" : ascii.Contains(o) ? "A" : "F4";
            Assert.True(expected == formats[o], $"offset {o:00}: expected {expected}, got {formats[o]}");
        }
    }

    [Fact]
    public void The_template_lists_CEID_507()
    {
        using var doc = JsonDocument.Parse(SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, 2));
        var ceids = doc.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("ceid").GetInt64()).ToArray();

        Assert.Contains(10227507L, ceids);
    }

    [Fact]
    public void Every_supplied_bind_name_is_really_registered()
    {
        var bridge = new SecsBridge(null, _folder, _folder, () => default);
        var registered = bridge.CreateBindings();

        var missing = SecsBridge.SuppliedBindNames.Where(n => !registered.Contains(n)).ToList();
        var unlisted = registered.Names.Except(SecsBridge.SuppliedBindNames, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(missing.Count == 0, "listed but not registered: " + string.Join(", ", missing));
        Assert.True(unlisted.Count == 0, "registered but not listed: " + string.Join(", ", unlisted));
    }

    private IDeviceProfile LoadStamped(int chamber)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, $"stamped-{chamber}.json");
        File.WriteAllText(path, SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, chamber));
        return JsonDeviceProfile.Load(path);
    }
}
