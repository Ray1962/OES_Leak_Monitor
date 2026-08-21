using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The two files that make a day folder readable on its own: a redacted copy of the configuration
/// and a copy of the day's system log.
///
/// <para>They exist because the data folder is wherever the operator pointed the logger and
/// everything needed to interpret it is under <c>%AppData%</c>. Copying a day folder off a fab
/// machine takes the first and leaves the second — which on 2026-08-19 meant the save threshold
/// had to be recovered by arithmetic and the acquisition mode inferred from the frame interval,
/// both of them sitting in a file nobody thought to copy.</para>
/// </summary>
public class DayFolderContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "oeslm-daycontext-" + Guid.NewGuid().ToString("N"));

    public DayFolderContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Folder(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static AppSettings WithAUser()
    {
        var s = new AppSettings();
        s.AccessControl.Users.Add(new UserAccount
        {
            Username = "engineer1",
            PasswordHash = "PLEASE-DO-NOT-COPY-ME",
            Role = UserRole.Engineer,
        });
        return s;
    }

    // --- redaction -----------------------------------------------------------

    /// <summary>The one thing that must never travel with the data.</summary>
    [Fact]
    public void TheSnapshotCarriesNoCredentials()
    {
        var json = ConfigSnapshot.Redact(WithAUser());

        Assert.DoesNotContain("PLEASE-DO-NOT-COPY-ME", json);
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessControl", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And everything else does. The snapshot is a copy with holes cut in it, not a list of
    /// fields somebody has to remember to extend — the settings that mattered on 2026-08-19 were
    /// the logger's trigger and threshold, which no hand-written list would have thought to keep.
    /// </summary>
    [Fact]
    public void TheSnapshotCarriesWhatIsNeededToReadTheData()
    {
        var settings = WithAUser();
        settings.Logger.SaveStartThresholdIntensity = 2345;
        settings.Logger.TriggerMode = TriggerMode.SpectrumPercentile;

        var json = ConfigSnapshot.Redact(settings);
        using var doc = JsonDocument.Parse(json);
        var logger = doc.RootElement.GetProperty("logger");

        Assert.Equal(2345, logger.GetProperty("saveStartThresholdIntensity").GetSingle());
        Assert.Equal("SpectrumPercentile", logger.GetProperty("triggerMode").GetString());
        Assert.True(doc.RootElement.TryGetProperty("leakMonitor", out _));
        Assert.True(doc.RootElement.TryGetProperty("devices", out _));
    }

    // --- writing -------------------------------------------------------------

    [Fact]
    public void TheFirstSnapshotIsWrittenAndTheIdenticalNextOneIsNot()
    {
        var folder = Folder("write");
        var settings = WithAUser();
        var at = new DateTime(2026, 8, 19, 17, 55, 18);

        var first = ConfigSnapshot.TryWrite(folder, settings, at, out var e1);
        var second = ConfigSnapshot.TryWrite(folder, settings, at.AddMinutes(6), out var e2);

        Assert.Null(e1);
        Assert.Null(e2);
        Assert.NotNull(first);
        Assert.Null(second);                 // nothing changed, so nothing written
        Assert.Single(Directory.GetFiles(folder, ConfigSnapshot.Prefix + "*.json"));
        Assert.EndsWith("_config_175518.json", first!);
    }

    /// <summary>
    /// A settings change mid-session is exactly what the timestamps are for: on 2026-08-19 the
    /// integration time went from 200 to 100 partway through the afternoon, and nothing recorded
    /// when.
    /// </summary>
    [Fact]
    public void AChangedSettingWritesASecondSnapshot()
    {
        var folder = Folder("changed");
        var settings = WithAUser();
        var at = new DateTime(2026, 8, 19, 17, 34, 9);

        ConfigSnapshot.TryWrite(folder, settings, at, out _);
        settings.Devices[0].IntegrationTimeMs = 100;
        var second = ConfigSnapshot.TryWrite(folder, settings, at.AddMinutes(2), out _);

        Assert.NotNull(second);
        Assert.Equal(2, Directory.GetFiles(folder, ConfigSnapshot.Prefix + "*.json").Length);
    }

    // --- log mirror ----------------------------------------------------------

    [Fact]
    public void OnlyTheDaysLogsAreCopied()
    {
        var day = Folder("logs-day");
        var logs = Folder("logs-src");
        File.WriteAllText(Path.Combine(logs, "26081917.csv"), "a,b\n1,2\n");
        File.WriteAllText(Path.Combine(logs, "26081918.csv"), "a,b\n3,4\n");
        File.WriteAllText(Path.Combine(logs, "26082010.csv"), "a,b\n5,6\n");   // another day
        File.WriteAllText(Path.Combine(logs, "secs_20260819.log"), "not a system log");

        int n = SystemLogMirror.Sync(day, logs, new DateTime(2026, 8, 19), out var errors);

        Assert.Empty(errors);
        Assert.Equal(2, n);
        Assert.True(File.Exists(Path.Combine(day, "_log_26081917.csv")));
        Assert.True(File.Exists(Path.Combine(day, "_log_26081918.csv")));
        Assert.False(File.Exists(Path.Combine(day, "_log_26082010.csv")));
    }

    /// <summary>
    /// The live hour keeps growing, so an early copy is a partial one and the next sync has to
    /// replace it — while an unchanged one must not be rewritten on every recording.
    /// </summary>
    [Fact]
    public void AGrowingLogIsRecopiedAndAnUnchangedOneIsNot()
    {
        var day = Folder("grow-day");
        var logs = Folder("grow-src");
        var source = Path.Combine(logs, "26081917.csv");
        File.WriteAllText(source, "a,b\n1,2\n");

        Assert.Equal(1, SystemLogMirror.Sync(day, logs, new DateTime(2026, 8, 19), out _));
        Assert.Equal(0, SystemLogMirror.Sync(day, logs, new DateTime(2026, 8, 19), out _));

        File.AppendAllText(source, "3,4\n");
        Assert.Equal(1, SystemLogMirror.Sync(day, logs, new DateTime(2026, 8, 19), out _));
        Assert.Equal(File.ReadAllText(source),
                     File.ReadAllText(Path.Combine(day, "_log_26081917.csv")));
    }

    /// <summary>SystemLogger holds its own handle on the current hour's file.</summary>
    [Fact]
    public void ALogFileThatIsOpenForWritingIsStillCopied()
    {
        var day = Folder("open-day");
        var logs = Folder("open-src");
        var source = Path.Combine(logs, "26081917.csv");

        using (var held = new FileStream(source, FileMode.Create, FileAccess.Write,
                                         FileShare.ReadWrite | FileShare.Delete))
        using (var writer = new StreamWriter(held) { AutoFlush = true })
        {
            writer.Write("a,b\n1,2\n");
            int n = SystemLogMirror.Sync(day, logs, new DateTime(2026, 8, 19), out var errors);
            Assert.Empty(errors);
            Assert.Equal(1, n);
        }

        Assert.Equal("a,b\n1,2\n", File.ReadAllText(Path.Combine(day, "_log_26081917.csv")));
    }

    /// <summary>
    /// Neither companion may be mistaken for a recording: the review tabs walk the same folder.
    /// </summary>
    [Fact]
    public void TheCompanionsAreNotParsedAsRecordings()
    {
        var month = Folder("202608");
        var day = Path.Combine(month, "19");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, "_config_175518.json"), "{}");
        File.WriteAllText(Path.Combine(day, "_log_26081917.csv"), "a,b\n");

        Assert.Null(Recording.TryParse(Path.Combine(day, "_log_26081917.csv")));
        Assert.Empty(Recording.EnumerateSpectra(_root, new DateTime(2026, 8, 1),
                                                new DateTime(2026, 8, 31)));
    }
}
