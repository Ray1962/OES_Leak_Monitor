using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using OxyPlot.Wpf;

namespace OES_Leak_Monitor;

/// <summary>One ratio's row in the picker, with the answer for its most recent batch.</summary>
public sealed class BatchSeriesRow : INotifyPropertyChanged
{
    public required BatchSeries Series { get; init; }

    /// <summary>
    /// The ratio's name, with its process appended — unless the name already says it. A ratio
    /// scoped to one process is normally named for it ("N2 337 / CO 330 (C)"), and a row reading
    /// "N2 337 / CO 330 (C)  (C)" looks like a fault in the configuration rather than a label.
    /// </summary>
    public string Label
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Series.RatioLabel) ? Series.RatioKey : Series.RatioLabel;
            var cls = Series.ProcessClass;
            if (string.IsNullOrEmpty(cls)) return name;
            return name.Contains($"({cls})", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}  ({cls})";
        }
    }

    public string Detail =>
        $"{Series.Points.Count} batches · median {Series.Median:G4} · σ {Series.Sigma:G3}";

    /// <summary>How far the newest batch sits from the series' own centre, in robust σ. This is
    /// the number the page exists to show: everything else is context for it.</summary>
    public double LatestSigmas => Series.Sigma > 0
        ? (Series.Points[^1].Value - Series.Median) / Series.Sigma
        : double.NaN;

    public string LatestText => double.IsNaN(LatestSigmas)
        ? "—"
        : $"{LatestSigmas:+0.0;-0.0;0.0} σ";

    private bool _shown = true;
    public bool Shown
    {
        get => _shown;
        set { if (_shown == value) return; _shown = value; Changed(); ShownChanged?.Invoke(); }
    }

    public event Action? ShownChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// The cross-batch trend: one point per batch per ratio, read from the batch index.
///
/// <para>This is the page the plan's primary detection mechanism is read from. The per-frame
/// panel answers "is it leaking now" against a baseline; this answers "is this batch different
/// from the ones before it", which is the only question the viewport's fouling leaves
/// askable — every batch is sampled at the same point in its own history
/// (<see cref="BatchTracker"/>), so batches are comparable to each other even though frames
/// within one are not.</para>
///
/// <para><b>Normalised by default.</b> Ratios of different processes sit an order of magnitude
/// apart, so a shared raw axis makes the small ones invisible; each series is divided by its own
/// median, and the axis says so. The same device the Recordings tab uses, for the same reason,
/// and raw is one click away.</para>
///
/// <para><b>The band is robust.</b> ±3 σ is drawn from 1.4826 × the median absolute deviation
/// rather than the standard deviation, because a run of leaking batches would otherwise widen the
/// band meant to catch it — the trap the live σ falls into inside a step, one level up.</para>
///
/// <para>Deliberately ungated. Reading whether the tool is drifting is an Operator concern, and
/// this page changes nothing — the same split as the Monitor tab's recorder strip and the SECS
/// tab.</para>
/// </summary>
public sealed class BatchTrendViewModel : INotifyPropertyChanged
{
    private readonly LoggerViewModel _logger;
    private readonly string _defaultDataDirectory;

    public BatchTrendViewModel(LoggerViewModel logger, string defaultDataDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultDataDirectory = defaultDataDirectory ?? "";

        var controller = new PlotController();
        controller.UnbindMouseDown(OxyMouseButton.Right);
        controller.UnbindMouseDown(OxyMouseButton.Right, OxyModifierKeys.Control);
        PlotController = controller;

        RefreshCommand = new RelayCommand(Refresh);
        ZoomAllCommand = new RelayCommand(() => { PlotModel.ResetAllAxes(); PlotModel.InvalidatePlot(false); });
        SavePngCommand = new RelayCommand(SavePng, () => Rows.Count > 0);
        CopyImageCommand = new RelayCommand(CopyImage, () => Rows.Count > 0);
        OpenIndexCommand = new RelayCommand(OpenIndex, () => File.Exists(IndexPath));
        OpenFolderCommand = new RelayCommand(() => OpenFolder(EffectiveBaseDirectory));

        RebuildPlot();
        Refresh();
    }

    public ObservableCollection<BatchSeriesRow> Rows { get; } = new();
    public IPlotController PlotController { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ZoomAllCommand { get; }
    public RelayCommand SavePngCommand { get; }
    public RelayCommand CopyImageCommand { get; }
    public RelayCommand OpenIndexCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    private PlotModel _plotModel = null!;
    public PlotModel PlotModel { get => _plotModel; private set => Set(ref _plotModel, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _normalise = true;
    /// <summary>Divide each series by its own median so ratios an order of magnitude apart share
    /// an axis. On by default: comparing batches across ratios is what the page is for.</summary>
    public bool Normalise
    {
        get => _normalise;
        set { if (Set(ref _normalise, value)) RebuildPlot(); }
    }

    private bool _showBand = true;
    /// <summary>Draw the ±3 σ band the detection rule uses.</summary>
    public bool ShowBand
    {
        get => _showBand;
        set { if (Set(ref _showBand, value)) RebuildPlot(); }
    }

    public string EffectiveBaseDirectory =>
        string.IsNullOrWhiteSpace(_logger.BaseDirectory) ? _defaultDataDirectory : _logger.BaseDirectory;

    public string IndexPath => BatchIndexReader.PathFor(EffectiveBaseDirectory);

    public void Refresh()
    {
        foreach (var row in Rows) row.ShownChanged -= OnRowShownChanged;
        Rows.Clear();

        var path = IndexPath;
        if (!File.Exists(path))
        {
            // Not an error. The index appears with the first completed batch, and saying so is
            // more use than an empty chart with no explanation.
            Status = $"No batch record yet — it appears at {path} once a batch completes.";
            RebuildPlot();
            return;
        }

        IReadOnlyList<BatchPoint> rows;
        try
        {
            rows = BatchIndexReader.Read(path);
        }
        catch (Exception ex)
        {
            Status = $"Could not read {path}: {ex.Message}";
            RebuildPlot();
            return;
        }

        foreach (var s in BatchIndexReader.Series(rows))
        {
            var row = new BatchSeriesRow { Series = s };
            row.ShownChanged += OnRowShownChanged;
            Rows.Add(row);
        }

        Status = Rows.Count == 0
            ? $"{rows.Count} rows in the batch record, but no ratio has enough batches to trend yet."
            : $"{Rows.Count} ratio(s) over {rows.Select(r => r.Start).Distinct().Count()} batches.";
        SavePngCommand.RaiseCanExecuteChanged();
        CopyImageCommand.RaiseCanExecuteChanged();
        OpenIndexCommand.RaiseCanExecuteChanged();
        RebuildPlot();
    }

    private void OnRowShownChanged() => RebuildPlot();

    private static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x2A, 0x78, 0xD6),   // blue
        OxyColor.FromRgb(0xEB, 0x68, 0x34),   // orange
        OxyColor.FromRgb(0x1B, 0xAF, 0x7A),   // aqua
        OxyColor.FromRgb(0xED, 0xA1, 0x00),   // yellow
        OxyColor.FromRgb(0x4A, 0x3A, 0xA7),   // violet
        OxyColor.FromRgb(0xE3, 0x49, 0x48),   // red
    };

    private void RebuildPlot()
    {
        var shown = Rows.Where(r => r.Shown).ToList();

        var model = new PlotModel
        {
            Title = "Batch trend",
            TitleFontSize = 13,
            // With nothing to draw, the axes would still show a range - a date span and a 0-100
            // scale that came from OxyPlot's defaults rather than from any batch. An empty chart
            // that looks like a populated one is worse than a blank panel, so say why instead.
            Subtitle = shown.Count == 0 ? Status : null,
            SubtitleFontSize = 11,
            Background = OxyColors.White,
            PlotAreaBorderColor = shown.Count == 0
                ? OxyColors.Transparent
                : OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Batch start",
            StringFormat = "MM-dd HH:mm",
            IsAxisVisible = shown.Count > 0,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE8, 0xE8, 0xE8),
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            IsAxisVisible = shown.Count > 0,
            // Named for what it holds, the way the ratio CSV's second column is: "× own median"
            // and a raw value are not the same number, and a chart that does not say which is
            // one nobody can re-read.
            Title = _normalise ? "× own median" : "value",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE8, 0xE8, 0xE8),
        });
        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.LeftTop,
            LegendPlacement = LegendPlacement.Inside,
            LegendFontSize = 11,
            LegendBackground = OxyColor.FromAColor(0xC0, OxyColors.White),
        });

        // The band only means anything when every series shares a scale. Drawn once, around 1.0,
        // rather than once per series — three overlapping bands would say less than none.
        if (_showBand && _normalise && shown.Count > 0)
        {
            double worst = shown.Max(r => r.Series.Median > 0 ? 3 * r.Series.Sigma / r.Series.Median : 0);
            if (worst > 0)
            {
                model.Annotations.Add(new RectangleAnnotation
                {
                    MinimumY = 1 - worst,
                    MaximumY = 1 + worst,
                    Fill = OxyColor.FromAColor(0x20, OxyColor.FromRgb(0x2A, 0x78, 0xD6)),
                    Layer = AnnotationLayer.BelowSeries,
                    Text = "±3 σ (widest series)",
                    TextVerticalAlignment = OxyPlot.VerticalAlignment.Top,
                    TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
                    FontSize = 10,
                    TextColor = OxyColor.FromRgb(0x66, 0x66, 0x66),
                });
            }
        }

        for (int i = 0; i < shown.Count; i++)
        {
            var s = shown[i].Series;
            var series = new LineSeries
            {
                Title = shown[i].Label,
                Color = Palette[i % Palette.Length],
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 4,
                MarkerFill = Palette[i % Palette.Length],
                // Every point is a whole batch, so every point is worth inspecting — unlike a
                // frame-rate trend, where a tooltip per point would be noise.
                TrackerFormatString = "{0}\n{2:yyyy-MM-dd HH:mm}\n{4:G5}",
            };
            for (int k = 0; k < s.Points.Count; k++)
            {
                double v = _normalise ? s.Normalised(k) : s.Points[k].Value;
                if (double.IsNaN(v)) continue;
                series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(s.Points[k].Start), v));
            }
            model.Series.Add(series);
        }

        if (_normalise && shown.Count > 0)
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = 1.0,
                Color = OxyColor.FromRgb(0x99, 0x99, 0x99),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1,
            });

        PlotModel = model;
    }

    // ---------------------------------------------------------------- export

    private void SavePng()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            FileName = $"batch-trend-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var exporter = new PngExporter { Width = 1400, Height = 700 };
            using var fs = File.Create(dlg.FileName);
            exporter.Export(PlotModel, fs);
            Status = $"Saved {dlg.FileName}";
        }
        catch (Exception ex)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    private void CopyImage()
    {
        try
        {
            var exporter = new PngExporter { Width = 1400, Height = 700 };
            Clipboard.SetImage(exporter.ExportToBitmap(PlotModel));
            Status = "Chart copied to the clipboard.";
        }
        catch (Exception ex)
        {
            Status = $"Could not copy: {ex.Message}";
        }
    }

    private void OpenIndex() => OpenPath(IndexPath);
    private static void OpenFolder(string folder) => OpenPath(folder);

    private static void OpenPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* the operator can find it themselves; a dialog here helps nobody */ }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
