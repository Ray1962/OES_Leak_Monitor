using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace OES_Leak_Monitor;

/// <summary>
/// Parsed contents of one ratio-trend CSV — the <c>{prefix}_Ratio_*.csv</c> sibling that
/// <see cref="RatioCsvLogger"/> writes alongside an intensity-logger save session. Holds the
/// per-frame timestamps, each ratio's raw value and % of baseline, and the composite leak
/// state. Arrays under <see cref="Raw"/> / <see cref="Pct"/> are aligned with
/// <see cref="Timestamps"/>; a blank CSV cell parses to <see cref="double.NaN"/>.
/// </summary>
public sealed class RatioTrendData
{
    public IReadOnlyList<string> RatioKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DateTime> Timestamps { get; init; } = Array.Empty<DateTime>();
    public IReadOnlyDictionary<string, double[]> Raw { get; init; } = new Dictionary<string, double[]>();
    public IReadOnlyDictionary<string, double[]> Pct { get; init; } = new Dictionary<string, double[]>();

    /// <summary>Composite leak state per row ("Idle" / "Normal" / "Warning" / "Alarm").</summary>
    public IReadOnlyList<string> States { get; init; } = Array.Empty<string>();

    public int RowCount => Timestamps.Count;
}

/// <summary>
/// Reads a ratio-trend CSV produced by <see cref="RatioCsvLogger"/>. The header is
/// <c>Timestamp, (key, key_pctBaseline) × N, OverallState</c>; ratio keys are recovered
/// from the header so the reader tracks however many ratios the run logged.
/// </summary>
public static class RatioCsvReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static RatioTrendData Read(string path, CancellationToken token = default)
    {
        using var reader = new StreamReader(path);

        var header = reader.ReadLine();
        if (header is null) return new RatioTrendData();

        var cols = header.Split(',');
        // Layout: Timestamp, (key, key_pctBaseline) × N, OverallState.
        if (cols.Length < 4) return new RatioTrendData();
        int ratioCount = (cols.Length - 2) / 2;
        int stateIdx = 1 + 2 * ratioCount;

        var keys = new string[ratioCount];
        for (int i = 0; i < ratioCount; i++) keys[i] = cols[1 + 2 * i].Trim();

        var timestamps = new List<DateTime>();
        var states = new List<string>();
        var raw = new List<double>[ratioCount];
        var pct = new List<double>[ratioCount];
        for (int i = 0; i < ratioCount; i++) { raw[i] = new List<double>(); pct[i] = new List<double>(); }

        string? line;
        int seen = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if ((++seen & 0x3FF) == 0) token.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;

            var f = line.Split(',');
            if (f.Length < 2 || !DateTime.TryParse(f[0], Inv, DateTimeStyles.None, out var ts))
                continue;

            timestamps.Add(ts);
            for (int i = 0; i < ratioCount; i++)
            {
                raw[i].Add(ParseNum(Field(f, 1 + 2 * i)));
                pct[i].Add(ParseNum(Field(f, 2 + 2 * i)));
            }
            states.Add(Field(f, stateIdx) ?? "");
        }

        var rawDict = new Dictionary<string, double[]>(ratioCount);
        var pctDict = new Dictionary<string, double[]>(ratioCount);
        for (int i = 0; i < ratioCount; i++)
        {
            rawDict[keys[i]] = raw[i].ToArray();
            pctDict[keys[i]] = pct[i].ToArray();
        }

        return new RatioTrendData
        {
            RatioKeys = keys,
            Timestamps = timestamps,
            Raw = rawDict,
            Pct = pctDict,
            States = states,
        };
    }

    private static string? Field(string[] f, int i) => i >= 0 && i < f.Length ? f[i] : null;

    private static double ParseNum(string? s) =>
        !string.IsNullOrWhiteSpace(s) && double.TryParse(s, NumberStyles.Float, Inv, out var v)
            ? v
            : double.NaN;
}
