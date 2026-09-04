using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>One batch's reading of one ratio, as the index records it.</summary>
public sealed record BatchPoint(DateTime Start, DateTime End, int Steps, string Classes,
                                string RatioKey, string RatioLabel, string ProcessClass,
                                double Value);

/// <summary>
/// Every batch of one ratio, in the order they were measured, with the band a new batch is judged
/// against.
/// </summary>
public sealed class BatchSeries
{
    public required string RatioKey { get; init; }
    public required string RatioLabel { get; init; }
    public required string ProcessClass { get; init; }
    public required IReadOnlyList<BatchPoint> Points { get; init; }

    /// <summary>Median of every batch, the series' own centre.</summary>
    public double Median { get; init; }

    /// <summary>
    /// Robust σ: 1.4826 × the median absolute deviation, not the standard deviation.
    ///
    /// <para>The band exists to make an excursion visible. A standard deviation is moved by the
    /// excursion itself, so a run of leaking batches widens the very band meant to catch them —
    /// the same trap the live σ falls into inside a step
    /// (<c>docs/leak-monitor-plan-zh-TW.md</c> §9.3), one level up. The MAD is not: half the
    /// batches would have to be affected before it moved.</para>
    /// </summary>
    public double Sigma { get; init; }

    /// <summary>Value as a multiple of <see cref="Median"/>, or NaN when the median is not
    /// positive (nothing to normalise against).</summary>
    public double Normalised(int i) =>
        Median > 0 ? Points[i].Value / Median : double.NaN;
}

/// <summary>
/// Reads the long-format batch index that <see cref="BatchCsvLogger"/> appends to.
///
/// <para>Long format is why this is a few lines rather than a schema negotiation: one row per
/// batch per ratio, eight fixed columns, so a ratio added, renamed or re-scoped half way through
/// a year of history changes nothing about how the file parses. Columns are located by header
/// name, so a future column appended on the end is ignored rather than fatal — the same rule
/// <c>RatioCsvReader</c> follows for the leak-rate columns.</para>
///
/// <para>Forward-only and share-tolerant: the index is appended to while the tool runs, so
/// anything reading a trend has to open it alongside the writer.</para>
/// </summary>
public static class BatchIndexReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Path of the index under <paramref name="baseDirectory"/>.</summary>
    public static string PathFor(string baseDirectory) =>
        Path.Combine(baseDirectory ?? "", BatchCsvLogger.IndexFileName);

    /// <summary>Every row, in file order. Empty when the file is absent or has no data rows.</summary>
    public static IReadOnlyList<BatchPoint> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Array.Empty<BatchPoint>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        return Read(sr);
    }

    /// <summary>Reads from an open reader, so a caller can supply an archive entry or a test
    /// string without this knowing where the bytes came from.</summary>
    public static IReadOnlyList<BatchPoint> Read(TextReader reader)
    {
        if (reader is null) return Array.Empty<BatchPoint>();
        var header = reader.ReadLine();
        if (header is null) return Array.Empty<BatchPoint>();

        var cols = header.Split(',');
        int iStart = IndexOf(cols, "BatchStart"), iEnd = IndexOf(cols, "BatchEnd");
        int iSteps = IndexOf(cols, "Steps"), iClasses = IndexOf(cols, "Classes");
        int iKey = IndexOf(cols, "RatioKey"), iLabel = IndexOf(cols, "RatioLabel");
        int iClass = IndexOf(cols, "ProcessClass"), iValue = IndexOf(cols, "Value");
        if (iStart < 0 || iKey < 0 || iValue < 0) return Array.Empty<BatchPoint>();

        var rows = new List<BatchPoint>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var c = line.Split(',');
            if (c.Length <= iValue) continue;
            if (!DateTime.TryParse(Cell(c, iStart), Inv, DateTimeStyles.None, out var start)) continue;
            if (!double.TryParse(Cell(c, iValue), NumberStyles.Float, Inv, out var value)) continue;
            DateTime.TryParse(Cell(c, iEnd), Inv, DateTimeStyles.None, out var end);
            int.TryParse(Cell(c, iSteps), NumberStyles.Integer, Inv, out int steps);
            rows.Add(new BatchPoint(start, end, steps, Cell(c, iClasses),
                                    Cell(c, iKey), Cell(c, iLabel), Cell(c, iClass), value));
        }
        return rows;
    }

    /// <summary>
    /// Groups rows into one series per ratio, oldest batch first, each with its own centre and
    /// band. A ratio the index has fewer than <paramref name="minBatches"/> rows for is left out:
    /// a band drawn from two batches is not a band, and the page's whole claim is that a new
    /// batch can be read against the ones before it.
    /// </summary>
    public static IReadOnlyList<BatchSeries> Series(IReadOnlyList<BatchPoint> rows, int minBatches = 4)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<BatchSeries>();
        var result = new List<BatchSeries>();
        foreach (var g in rows.GroupBy(r => r.RatioKey, StringComparer.Ordinal))
        {
            var pts = g.OrderBy(r => r.Start).ToList();
            if (pts.Count < minBatches) continue;
            var values = pts.Select(p => p.Value).ToList();
            double median = Median(values);
            double mad = Median(values.Select(v => Math.Abs(v - median)).ToList());
            var last = pts[^1];
            result.Add(new BatchSeries
            {
                RatioKey = g.Key,
                RatioLabel = string.IsNullOrWhiteSpace(last.RatioLabel) ? g.Key : last.RatioLabel,
                ProcessClass = last.ProcessClass,
                Points = pts,
                Median = median,
                Sigma = 1.4826 * mad,
            });
        }
        return result.OrderBy(s => s.ProcessClass, StringComparer.Ordinal)
                     .ThenBy(s => s.RatioLabel, StringComparer.Ordinal)
                     .ToList();
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var v = values.OrderBy(x => x).ToList();
        return v.Count % 2 == 1 ? v[v.Count / 2] : 0.5 * (v[v.Count / 2 - 1] + v[v.Count / 2]);
    }

    private static int IndexOf(string[] cols, string name)
    {
        for (int i = 0; i < cols.Length; i++)
            if (cols[i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Cell(string[] c, int i) => i >= 0 && i < c.Length ? c[i].Trim() : "";
}
