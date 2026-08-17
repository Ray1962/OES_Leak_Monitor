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
        Assert.Equal(26, profile.RequiredBindings.Count());
        Assert.Equal(5, profile.Alarms.Count());
        Assert.Empty(profile.RemoteCommands);   // this tool reports; it is not driven
    }

    [Fact]
    public void Every_binding_the_profile_names_is_one_the_app_supplies()
    {
        // The library fails construction when a profile names a binding nobody registered, so
        // this is the same check moved to build time. Kept as an explicit list rather than
        // reflection over SecsBridge: the point is that a rename has to be made in two places
        // deliberately, not that the two happen to agree.
        string[] supplied =
        {
            "oes.leakRate", "oes.leakRateSigma", "oes.leakRateConfidence", "oes.leakRateValid",
            "oes.outOfCalibratedRange", "oes.calibrationStatus", "oes.compositeLevel",
            "oes.enabledRatios", "oes.warningRatios", "oes.alarmRatios", "oes.lowSignalRatios",
            "oes.baselineAvailable", "oes.goldenRunName", "oes.calibrationName",
            "oes.acquisitionMismatch", "oes.testMode", "oes.captureActive", "oes.captureProgress",
            "oes.calCaptureActive", "oes.calCaptureProgress", "oes.plasmaPresent",
            "oes.plasmaGateAvailable", "oes.dropoutCount", "oes.integrationTime",
            "oes.averageCount", "oes.frameRate",
        };

        var missing = LoadStamped(2).RequiredBindings
            .Except(supplied, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(missing.Count == 0, "profile binds names the app does not supply: " + string.Join(", ", missing));
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
