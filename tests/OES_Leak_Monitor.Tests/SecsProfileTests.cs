using System.IO;
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
        Assert.Equal(29, profile.RequiredBindings.Count());
        Assert.Equal(5, profile.Alarms.Count());
        Assert.Empty(profile.RemoteCommands);   // this tool reports; it is not driven
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

    private IDeviceProfile LoadStamped(int chamber)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, $"stamped-{chamber}.json");
        File.WriteAllText(path, SecsChamberCoding.ApplyChamber(SecsProfileTemplate.Json, chamber));
        return JsonDeviceProfile.Load(path);
    }
}
