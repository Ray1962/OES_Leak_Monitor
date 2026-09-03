using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// What the batch record puts on disk.
///
/// <para>Two files, and the split is the point. The day file sits beside the recordings it
/// summarises so a day folder copied off the machine still explains itself, and it is wide
/// because that is what a person opening it wants. The index at the root is long — one row per
/// batch per ratio — because reading a trend means reading dozens of batches across as many day
/// folders, and a wide file cannot survive a ratio being added, renamed or re-scoped part way
/// through a year of history.</para>
/// </summary>
public class BatchCsvLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "oeslm-batch-" + Guid.NewGuid().ToString("N"));

    public BatchCsvLoggerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime Epoch = new(2026, 3, 4, 9, 0, 0, DateTimeKind.Local);

    private static LineRegion Raw(string label, double nm) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 0.5,
        BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
    };

    private static RatioDefinition Ratio(string key, string name, string cls) => new()
    {
        Key = key, DisplayName = name, Enabled = true, ProcessClass = cls,
        MonitorMode = MonitorMode.Ratio,
        Numerator = Raw("N2 337", 337.1), Denominator = Raw("CO 330", 329.6),
    };

    private static LeakMonitorSnapshot Frame(double t, int step, string cls, bool plasma,
                                             params (string Key, double Value)[] ratios) =>
        new()
        {
            Timestamp = Epoch.AddSeconds(t),
            ProcessStepIndex = step,
            ProcessClass = cls,
            PlasmaPresent = plasma,
            PlasmaGateAvailable = true,
            Ratios = ratios.Select(r => new RatioSnapshot(
                r.Key, r.Key, RatioState.Normal, r.Value, r.Value,
                true, 1, 0.1, 100, 2, 3, 0, plasma, 1, 1, 0, 10, 10,
                MonitorMode.Ratio, RatioRole.Alarm, false)).ToList(),
        };

    private sealed class Harness : IDisposable
    {
        public LeakMonitorEngine Engine { get; }
        public BatchTracker Tracker { get; }
        public BatchCsvLogger Logger { get; }

        public Harness(string root, params RatioDefinition[] ratios)
        {
            var settings = new LeakMonitorSettings { Enabled = true };
            foreach (var r in ratios) settings.Ratios.Add(r);
            settings.Batch = new BatchSettings
            {
                Enabled = true,
                WindowStartSeconds = 10, WindowEndSeconds = 30, MinWindowFrames = 5,
                BatchGapSeconds = 240,
            };
            Engine = new LeakMonitorEngine(settings);
            Tracker = new BatchTracker(settings.Batch);
            Logger = new BatchCsvLogger(Engine, root, Tracker);
            Logger.Configure(new LoggerSettings { BaseDirectory = root, FilePrefix = "P" });
        }

        public void Dispose() { Logger.Dispose(); Engine.Dispose(); }
    }

    /// <summary>Drives one step of gate-open frames at 2 s, the cadence this tool runs at.</summary>
    private static double Step(BatchTracker t, double t0, int index, string cls,
                               double seconds, params (string Key, double Value)[] ratios)
    {
        double s = 0;
        for (int f = 0; s <= seconds; f++, s = f * 2.0)
            t.Add(Frame(t0 + s, index, f >= 2 ? cls : "", true, ratios));
        return t0 + s;
    }

    private static void Gap(BatchTracker t, double from, double to, int lastIndex)
    {
        for (double s = from; s < to; s += 2.0) t.Add(Frame(s, lastIndex, "", false));
    }

    /// <summary>
    /// Reads a file the logger may still have open. <c>File.ReadAllLines</c> asks for
    /// <c>FileShare.Read</c>, which forbids the writer's existing write handle and throws — the
    /// same collision <c>SystemLogMirror</c> solves the same way. It is not only a test
    /// convenience: the day file is appended to for the whole acquisition, so anything that
    /// reads a batch trend while the tool is running has to open it like this.
    /// </summary>
    private static string[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    private string[] DayFileLines() =>
        ReadShared(Directory.EnumerateFiles(_root, "P_Batch_*.csv", SearchOption.AllDirectories).Single());

    private string[] IndexLines() =>
        ReadShared(Path.Combine(_root, BatchCsvLogger.IndexFileName));

    [Fact]
    public void A_completed_batch_becomes_one_row_in_the_day_folder()
    {
        using var h = new Harness(_root, Ratio("R_C", "N2 337 / CO 330 (C)", "C"));

        double t = Step(h.Tracker, 0, 1, "C", 84, ("R_C", 0.0195));
        Gap(h.Tracker, t, t + 60, 1);
        t = Step(h.Tracker, t + 60, 2, "C", 84, ("R_C", 0.0170));
        h.Tracker.Flush();

        var lines = DayFileLines();
        Assert.Equal(2, lines.Length);                     // header + one batch
        Assert.StartsWith("BatchStart,BatchEnd,DurationMin,Steps,Classes,", lines[0]);
        Assert.Contains("N2 337 / CO 330 (C) [R_C]", lines[0]);

        var cells = lines[1].Split(',');
        Assert.Equal("2", cells[3]);                        // two steps in the batch
        Assert.Equal("Cx2", cells[4]);
        // The sampling point is the batch's FIRST step of that class, not the last and not their
        // mean — the viewport dims measurably between them.
        Assert.Equal(0.0195, double.Parse(cells[5], System.Globalization.CultureInfo.InvariantCulture), 5);
    }

    /// <summary>
    /// The day file is written into the folder for the batch's own date, alongside the
    /// recordings it summarises — the property that lets a day folder be copied off the machine
    /// and still explain itself.
    /// </summary>
    [Fact]
    public void The_day_file_lands_beside_that_days_recordings()
    {
        using var h = new Harness(_root, Ratio("R_C", "c", "C"));
        Step(h.Tracker, 0, 1, "C", 84, ("R_C", 1.0));
        h.Tracker.Flush();

        var file = Directory.EnumerateFiles(_root, "P_Batch_*.csv", SearchOption.AllDirectories).Single();
        Assert.Equal(Path.Combine(_root, "202603", "04"), Path.GetDirectoryName(file));
    }

    /// <summary>
    /// The index is long-format, so adding or re-scoping a ratio never changes its schema — the
    /// whole reason it is not simply a copy of the wide day file.
    /// </summary>
    [Fact]
    public void The_index_is_one_row_per_batch_per_ratio()
    {
        using var h = new Harness(_root,
            Ratio("R_C", "n2/co (C)", "C"), Ratio("R_A", "n2/co (A)", "A"));

        double t = Step(h.Tracker, 0, 1, "C", 84, ("R_C", 0.02), ("R_A", 0.03));
        Gap(h.Tracker, t, t + 60, 1);
        t = Step(h.Tracker, t + 60, 2, "A", 40, ("R_C", 0.02), ("R_A", 0.03));
        h.Tracker.Flush();

        var lines = IndexLines();
        Assert.Equal("BatchStart,BatchEnd,Steps,Classes,RatioKey,RatioLabel,ProcessClass,Value", lines[0]);
        Assert.Equal(3, lines.Length);                      // header + one row per ratio
        Assert.Contains(lines, l => l.Contains(",R_C,") && l.Contains(",C,"));
        Assert.Contains(lines, l => l.Contains(",R_A,") && l.Contains(",A,"));
    }

    /// <summary>
    /// A ratio whose class never ran in the batch gets no value — a blank cell in the day file
    /// and no row at all in the index. Writing the number from another process's step would be a
    /// reading of a different plasma, and there is no honest placeholder for one.
    /// </summary>
    [Fact]
    public void A_ratio_whose_class_did_not_run_is_left_blank()
    {
        using var h = new Harness(_root,
            Ratio("R_C", "c", "C"), Ratio("R_B", "b", "B"));

        Step(h.Tracker, 0, 1, "C", 84, ("R_C", 0.02), ("R_B", 0.4));
        h.Tracker.Flush();

        var cells = DayFileLines()[1].Split(',');
        Assert.NotEqual("", cells[5]);                      // R_C ran
        Assert.Equal("", cells[6]);                         // R_B's class never did

        var index = IndexLines();
        Assert.Equal(2, index.Length);                      // header + R_C only
        Assert.DoesNotContain(index, l => l.Contains(",R_B,"));
    }

    /// <summary>
    /// The index is named with a leading underscore, which is what keeps the review tabs from
    /// listing it as a recording — the same convention <c>_config_*.json</c> and
    /// <c>_log_*.csv</c> already rely on.
    /// </summary>
    [Fact]
    public void The_index_is_not_mistaken_for_a_recording()
    {
        Assert.StartsWith("_", BatchCsvLogger.IndexFileName);
        var path = Path.Combine(_root, "202603", "04", BatchCsvLogger.IndexFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        Assert.Null(Recording.TryParse(path));
    }

    [Fact]
    public void Nothing_is_written_while_the_batch_record_is_disabled()
    {
        var settings = new LeakMonitorSettings { Enabled = true };
        settings.Ratios.Add(Ratio("R_C", "c", "C"));
        settings.Batch = new BatchSettings { Enabled = false };
        using var engine = new LeakMonitorEngine(settings);
        var tracker = new BatchTracker(settings.Batch);
        using var logger = new BatchCsvLogger(engine, _root, tracker);
        logger.Configure(new LoggerSettings { BaseDirectory = _root, FilePrefix = "P" });

        Step(tracker, 0, 1, "C", 84, ("R_C", 0.02));
        tracker.Flush();

        Assert.Empty(Directory.EnumerateFiles(_root, "*.csv", SearchOption.AllDirectories));
    }
}
