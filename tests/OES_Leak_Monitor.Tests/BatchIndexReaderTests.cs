using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Reading the cross-batch record back: what the Batch Trend page is built on.
///
/// <para>Two properties are load-bearing here and neither is obvious from the code. The index is
/// parsed <em>by header name</em>, because it is appended to for as long as the tool runs and a
/// column added next year must not make a year of history unreadable. And the band a new batch is
/// judged against is a <em>robust</em> sigma — the excursion it exists to catch must not be able to
/// widen it, which is exactly what a standard deviation would let happen.</para>
/// </summary>
public class BatchIndexReaderTests : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "oeslm-index-" + Guid.NewGuid().ToString("N"));

    public BatchIndexReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string Header =
        "BatchStart,BatchEnd,Steps,Classes,RatioKey,RatioLabel,ProcessClass,Value";

    private static string Row(int day, string key, string label, string cls, double value) =>
        string.Format(Inv, "2026-08-{0:00} 09:00:00,2026-08-{0:00} 09:40:00,17,Cx8 Ax8 Bx1,{1},{2},{3},{4}",
                      day, key, label, cls, value.ToString("G6", Inv));

    private static IReadOnlyList<BatchPoint> Parse(params string[] lines) =>
        BatchIndexReader.Read(new StringReader(string.Join("\n", lines)));

    // ----------------------------------------------------------------

    [Fact]
    public void An_absent_index_reads_as_no_batches_rather_than_throwing()
    {
        Assert.Empty(BatchIndexReader.Read(Path.Combine(_root, BatchCsvLogger.IndexFileName)));
        Assert.Empty(BatchIndexReader.Read(new StringReader("")));
        Assert.Empty(Parse(Header));            // header only: the tool ran, no batch completed
    }

    /// <summary>
    /// Columns are found by name, so a column appended on the end later is ignored instead of
    /// shifting every field by one — the rule <c>RatioCsvReader</c> already follows for the
    /// leak-rate columns, and the reason the index can outlive the build that wrote it.
    /// </summary>
    [Fact]
    public void Columns_are_located_by_name_and_an_unknown_one_is_ignored()
    {
        var rows = Parse(Header + ",Operator",
                         Row(20, "R_C", "N2 337 / CO 330 (C)", "C", 0.0195) + ",wang");

        var p = Assert.Single(rows);
        Assert.Equal("R_C", p.RatioKey);
        Assert.Equal("N2 337 / CO 330 (C)", p.RatioLabel);
        Assert.Equal("C", p.ProcessClass);
        Assert.Equal(0.0195, p.Value, 6);
        Assert.Equal(new DateTime(2026, 8, 20, 9, 0, 0), p.Start);
        Assert.Equal(17, p.Steps);
    }

    [Fact]
    public void A_row_whose_value_or_timestamp_is_unreadable_is_skipped_not_fatal()
    {
        var rows = Parse(Header,
                         Row(20, "R_C", "c", "C", 0.02),
                         "not-a-date,,,,R_C,c,C,0.03",
                         "2026-08-21 09:00:00,,17,,R_C,c,C,",
                         Row(22, "R_C", "c", "C", 0.021));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Value > 0));
    }

    /// <summary>
    /// One series per ratio, oldest batch first — the file is appended to in batch order today,
    /// but a rebuilt index (it is rebuildable from the day files) need not be.
    /// </summary>
    [Fact]
    public void Rows_group_into_one_series_per_ratio_oldest_first()
    {
        var rows = Parse(Header,
                         Row(22, "R_C", "c", "C", 0.021),
                         Row(20, "R_C", "c", "C", 0.019),
                         Row(21, "R_C", "c", "C", 0.020),
                         Row(23, "R_C", "c", "C", 0.020),
                         Row(20, "R_A", "a", "A", 1.40),
                         Row(21, "R_A", "a", "A", 1.42),
                         Row(22, "R_A", "a", "A", 1.41),
                         Row(23, "R_A", "a", "A", 1.43));

        var series = BatchIndexReader.Series(rows);
        Assert.Equal(2, series.Count);

        var c = series.Single(s => s.RatioKey == "R_C");
        Assert.Equal(4, c.Points.Count);
        Assert.Equal(new[] { 20, 21, 22, 23 }, c.Points.Select(p => p.Start.Day));
        Assert.Equal(0.0200, c.Median, 4);
    }

    /// <summary>
    /// A ratio with too few batches is left out entirely. A band drawn from two batches is not a
    /// band, and the page's whole claim is that a new batch can be read against the ones before
    /// it.
    /// </summary>
    [Fact]
    public void A_ratio_with_too_few_batches_gets_no_series()
    {
        var rows = Parse(Header,
                         Row(20, "R_C", "c", "C", 0.020),
                         Row(21, "R_C", "c", "C", 0.021),
                         Row(22, "R_C", "c", "C", 0.020),
                         Row(23, "R_C", "c", "C", 0.020),
                         Row(23, "R_new", "just added", "C", 0.5));

        var series = BatchIndexReader.Series(rows);
        Assert.Equal("R_C", Assert.Single(series).RatioKey);
    }

    /// <summary>
    /// <b>The one that matters.</b> Three of twelve batches sit 30 % high — the shape a developing
    /// leak has, since it does not go away on the next batch. The robust sigma is unmoved by them,
    /// so they read tens of sigma out; a standard deviation is dragged up by the very batches it
    /// is supposed to flag and puts them under two sigma, i.e. invisible. Same trap as the live
    /// sigma inside a step (docs/leak-monitor-plan-zh-TW.md 9.3), one level up.
    /// </summary>
    [Fact]
    public void A_run_of_excursion_batches_cannot_widen_the_band_meant_to_catch_them()
    {
        double[] normal = { 0.0198, 0.0199, 0.0200, 0.0201, 0.0202, 0.0200, 0.0199, 0.0201, 0.0200 };
        double[] leaking = { 0.0260, 0.0261, 0.0259 };

        var lines = new List<string> { Header };
        int day = 10;
        foreach (var v in normal) lines.Add(Row(day++, "R_C", "c", "C", v));
        foreach (var v in leaking) lines.Add(Row(day++, "R_C", "c", "C", v));

        var s = Assert.Single(BatchIndexReader.Series(Parse(lines.ToArray())));
        Assert.Equal(0.0200, s.Median, 4);       // the leaking batches did not move the centre
        Assert.True(s.Sigma > 0, "a band of zero would make every batch infinitely out");

        double robustSigmas = (0.0260 - s.Median) / s.Sigma;
        Assert.True(robustSigmas > 10,
            $"the excursion reads only {robustSigmas:F1} sigma out — the band absorbed it");

        // What a standard deviation would have said about the same twelve batches.
        var all = normal.Concat(leaking).ToArray();
        double mean = all.Average();
        double sd = Math.Sqrt(all.Sum(v => (v - mean) * (v - mean)) / all.Length);
        Assert.True((0.0260 - mean) / sd < 2,
            "the contrast this test exists for is gone: fix the test, not the reader");
    }

    /// <summary>
    /// Normalising is what lets ratios an order of magnitude apart share one axis. A series whose
    /// median is not positive has nothing to normalise against and says so, rather than returning
    /// an infinity that would take the plot's axis with it.
    /// </summary>
    [Fact]
    public void Normalising_against_a_non_positive_median_is_NaN_not_infinity()
    {
        var rows = Parse(Header,
                         Row(20, "R_z", "z", "", 0),
                         Row(21, "R_z", "z", "", 0),
                         Row(22, "R_z", "z", "", 0.001),
                         Row(23, "R_z", "z", "", -0.001));

        var s = Assert.Single(BatchIndexReader.Series(rows));
        Assert.True(s.Median <= 0);
        Assert.True(double.IsNaN(s.Normalised(0)));
    }

    /// <summary>
    /// Round-trips what the logger actually writes, so the reader cannot drift from the writer:
    /// same header, same date format, same numeric format, and read while the file is still open
    /// for appending.
    /// </summary>
    [Fact]
    public void It_reads_the_file_the_logger_writes_while_the_logger_still_holds_it()
    {
        var path = BatchIndexReader.PathFor(_root);
        Assert.Equal(Path.Combine(_root, BatchCsvLogger.IndexFileName), path);

        using (var w = new StreamWriter(path, append: true))
        {
            w.WriteLine(Header);
            for (int d = 10; d < 16; d++) w.WriteLine(Row(d, "R_C", "N2 337 / CO 330 (C)", "C", 0.02));
            w.Flush();

            var series = Assert.Single(BatchIndexReader.Series(BatchIndexReader.Read(path)));
            Assert.Equal(6, series.Points.Count);
            Assert.Equal("N2 337 / CO 330 (C)", series.RatioLabel);
        }
    }
}
