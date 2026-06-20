using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using OxyPlot.Wpf;

namespace OES_Leak_Monitor;

/// <summary>Which value the Ratio Review chart plots.</summary>
public enum RatioReviewMode { PercentOfBaseline, RawRatio, LeakRate }

/// <summary>
/// Backs the Ratio Review tab. Scans the logger's base directory for the
/// <c>{prefix}_Ratio_*.csv</c> files written by <see cref="RatioCsvLogger"/>, lists them,
/// and plots the selected file's per-ratio trend — either % of baseline (with the
/// 100 / 120 / 150 % threshold lines) or the raw ratio — with the composite leak state
/// drawn as translucent Warning / Alarm bands behind the series.
/// </summary>
public sealed class RatioReviewViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly OxyColor[] Palette =
    {
        OxyColors.SteelBlue, OxyColors.ForestGreen, OxyColors.DarkOrange, OxyColors.MediumPurple,
        OxyColors.Teal, OxyColors.Crimson,
    };

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LoggerViewModel _logger;
    private readonly DualIntensityLogger _intensityLogger;
    private readonly string _defaultDataDirectory;

    private readonly List<Recording> _allFiles = new();
    private RatioTrendData? _data;
    private CancellationTokenSource? _loadCts;

    public RatioReviewViewModel(LoggerViewModel logger, DualIntensityLogger intensityLogger,
        string defaultDataDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _intensityLogger = intensityLogger ?? throw new ArgumentNullException(nameof(intensityLogger));
        if (string.IsNullOrWhiteSpace(defaultDataDirectory))
            throw new ArgumentException("Default data directory is required.", nameof(defaultDataDirectory));
        _defaultDataDirectory = defaultDataDirectory;

        // Keep OxyPlot's default wheel / drag zoom but free up right-click for the menu.
        var controller = new PlotController();
        controller.UnbindMouseDown(OxyMouseButton.Right);
        controller.UnbindMouseDown(OxyMouseButton.Right, OxyModifierKeys.Control);
        PlotController = controller;

        RefreshCommand        = new RelayCommand(Refresh);
        ShowPercentCommand    = new RelayCommand(() => Mode = RatioReviewMode.PercentOfBaseline);
        ShowRawCommand        = new RelayCommand(() => Mode = RatioReviewMode.RawRatio);
        ShowLeakRateCommand   = new RelayCommand(() => Mode = RatioReviewMode.LeakRate);
        ZoomAllCommand        = new RelayCommand(() => { PlotModel.ResetAllAxes(); PlotModel.InvalidatePlot(false); });
        SavePngCommand        = new RelayCommand(SavePng,  () => _data is { RowCount: > 0 });
        CopyImageCommand      = new RelayCommand(CopyImage, () => _data is { RowCount: > 0 });
        OpenFileCommand       = new RelayCommand(() => OpenFile(SelectedFile?.FilePath), () => SelectedFile is not null);
        OpenFolderCommand     = new RelayCommand(OpenSelectedFolder, () => SelectedFile is not null);
        OpenBaseFolderCommand = new RelayCommand(() => OpenFolder(EffectiveBaseDirectory));

        RebuildPlot();

        _intensityLogger.FilesChanged += OnFilesChanged;
        Refresh();
    }

    public ObservableCollection<Recording> Files { get; } = new();
    public IPlotController PlotController { get; }

    private PlotModel _plotModel = null!;
    public PlotModel PlotModel { get => _plotModel; private set => Set(ref _plotModel, value); }

    private Recording? _selectedFile;
    /// <summary>The ratio CSV being reviewed; the DataGrid binds this two-way.</summary>
    public Recording? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!Set(ref _selectedFile, value)) return;
            OpenFileCommand.RaiseCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
            _ = LoadAsync();
        }
    }

    private RatioReviewMode _mode = RatioReviewMode.PercentOfBaseline;
    public RatioReviewMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsPercentMode));
            OnPropertyChanged(nameof(IsRawMode));
            OnPropertyChanged(nameof(IsLeakRateMode));
            RebuildPlot();
        }
    }
    public bool IsPercentMode  => _mode == RatioReviewMode.PercentOfBaseline;
    public bool IsRawMode      => _mode == RatioReviewMode.RawRatio;
    public bool IsLeakRateMode => _mode == RatioReviewMode.LeakRate;

    private string _searchText = "";
    /// <summary>Substring filter applied to the file date / time / name.</summary>
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) ApplyFilter(); }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _metaText = "";
    public string MetaText { get => _metaText; private set => Set(ref _metaText, value); }

    public string EffectiveBaseDirectory =>
        LoggerSettings.ResolveBaseDirectory(_logger.ToSettings().BaseDirectory, _defaultDataDirectory);

    public RelayCommand RefreshCommand        { get; }
    public RelayCommand ShowPercentCommand    { get; }
    public RelayCommand ShowRawCommand        { get; }
    public RelayCommand ShowLeakRateCommand   { get; }
    public RelayCommand ZoomAllCommand        { get; }
    public RelayCommand SavePngCommand        { get; }
    public RelayCommand CopyImageCommand      { get; }
    public RelayCommand OpenFileCommand       { get; }
    public RelayCommand OpenFolderCommand     { get; }
    public RelayCommand OpenBaseFolderCommand { get; }

    // --- scan -----------------------------------------------------------------

    /// <summary>Re-scans the base directory for ratio CSV files, keeping the selection.</summary>
    public void Refresh()
    {
        var prevPath = _selectedFile?.FilePath;
        _allFiles.Clear();
        OnPropertyChanged(nameof(EffectiveBaseDirectory));

        var baseDir = EffectiveBaseDirectory;
        if (!Directory.Exists(baseDir))
        {
            ApplyFilter();
            StatusText = $"Base directory not found: {baseDir}";
            return;
        }

        try
        {
            foreach (var monthDir in Directory.EnumerateDirectories(baseDir))
            {
                if (Path.GetFileName(monthDir).Length != 6) continue;
                foreach (var dayDir in Directory.EnumerateDirectories(monthDir))
                {
                    if (Path.GetFileName(dayDir).Length != 2) continue;
                    foreach (var path in Directory.EnumerateFiles(dayDir, "*_Ratio_*.csv"))
                    {
                        // Recording.TryParse handles the shared {prefix}_{tag}_{ts}[_N]
                        // naming; the ratio files just carry the "Ratio" tag.
                        var rec = Recording.TryParse(path);
                        if (rec is not null &&
                            rec.DeviceTag.Equals("Ratio", StringComparison.OrdinalIgnoreCase))
                            _allFiles.Add(rec);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = "Scan error: " + ex.Message;
            return;
        }

        _allFiles.Sort((a, b) => b.SessionStart.CompareTo(a.SessionStart));
        ApplyFilter();

        // Restore the selection (a new Recording instance after the re-scan).
        var match = prevPath is null ? null : Files.FirstOrDefault(f => f.FilePath == prevPath);
        if (!ReferenceEquals(match, _selectedFile)) SelectedFile = match;

        StatusText = $"{Files.Count} of {_allFiles.Count} ratio file(s) under {baseDir}";
    }

    private void ApplyFilter()
    {
        Files.Clear();
        var q = _searchText.Trim();
        IEnumerable<Recording> filtered = _allFiles;
        if (q.Length > 0)
            filtered = filtered.Where(r =>
                r.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                $"{r.DateText} {r.TimeText}".Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var r in filtered) Files.Add(r);
    }

    private void OnFilesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Refresh);

    // --- load -----------------------------------------------------------------

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        if (_selectedFile is null)
        {
            _data = null;
            RebuildPlot();
            StatusText = "No ratio file selected.";
            RaiseExportCanExec();
            return;
        }

        var path = _selectedFile.FilePath;
        StatusText = "Loading…";
        try
        {
            var data = await Task.Run(() => RatioCsvReader.Read(path, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;
            _data = data;
            RebuildPlot();
            StatusText = data.RowCount > 0
                ? $"Loaded {data.RowCount} row(s), {data.RatioKeys.Count} ratio(s)."
                : "File has no data rows.";
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer selection
        }
        catch (Exception ex)
        {
            _data = null;
            RebuildPlot();
            StatusText = "Load error: " + ex.Message;
        }
        RaiseExportCanExec();
    }

    private void RaiseExportCanExec()
    {
        SavePngCommand.RaiseCanExecuteChanged();
        CopyImageCommand.RaiseCanExecuteChanged();
    }

    // --- plot -----------------------------------------------------------------

    private void RebuildPlot()
    {
        bool pct = _mode == RatioReviewMode.PercentOfBaseline;
        bool leak = _mode == RatioReviewMode.LeakRate;

        var model = new PlotModel
        {
            Title = _selectedFile is null
                ? "Ratio trend"
                : $"Ratio trend — {_selectedFile.FileName}",
            TitleFontSize = 13,
            Background = OxyColors.White,
            PlotAreaBorderColor = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Time",
            StringFormat = "HH:mm:ss",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE8, 0xE8, 0xE8),
        });
        var yAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = leak ? "leak rate (mbar·L/s)" : pct ? "% of baseline" : "raw ratio",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE8, 0xE8, 0xE8),
        };
        if (pct)
        {
            yAxis.AbsoluteMinimum = 0;
            yAxis.MinimumRange = 10;
        }
        else if (leak)
        {
            yAxis.AbsoluteMinimum = 0;   // a leak rate can't be negative
        }
        model.Axes.Add(yAxis);

        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.LeftTop,
            LegendPlacement = LegendPlacement.Inside,
            LegendFontSize = 11,
            LegendBackground = OxyColor.FromAColor(0xC0, OxyColors.White),
        });

        if (pct)
        {
            AddThreshold(model, 100, OxyColors.Gray,       "baseline");
            AddThreshold(model, 120, OxyColors.DarkOrange, "warn ~120%");
            AddThreshold(model, 150, OxyColors.Firebrick,  "alarm ~150%");
        }

        if (_data is { RowCount: > 0 } d)
        {
            AddStateBands(model, d);

            if (leak)
            {
                AddLeakRateSeries(model, d);
            }
            else
            {
                int idx = 0;
                foreach (var key in d.RatioKeys)
                {
                    var series = new LineSeries
                    {
                        Title = FriendlyName(key),
                        Color = Palette[idx % Palette.Length],
                        StrokeThickness = 1.5,
                        CanTrackerInterpolatePoints = false,
                    };
                    var values = pct ? d.Pct[key] : d.Raw[key];
                    for (int i = 0; i < d.RowCount; i++)
                    {
                        double v = values[i];
                        if (double.IsNaN(v)) continue;
                        series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(d.Timestamps[i]), v));
                    }
                    model.Series.Add(series);
                    idx++;
                }
            }
            BuildMetaText(d);
        }
        else
        {
            MetaText = "";
        }

        PlotModel = model;
    }

    private static void AddThreshold(PlotModel model, double y, OxyColor color, string text) =>
        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            Y = y,
            Color = color,
            LineStyle = LineStyle.Dash,
            Text = text,
            TextColor = color,
            FontSize = 10,
        });

    /// <summary>Draws a translucent band behind the series over each Warning / Alarm span.</summary>
    private static void AddStateBands(PlotModel model, RatioTrendData d)
    {
        int i = 0;
        while (i < d.RowCount)
        {
            int level = StateLevel(d.States[i]);
            if (level == 0) { i++; continue; }

            int j = i;
            while (j + 1 < d.RowCount && StateLevel(d.States[j + 1]) == level) j++;

            model.Annotations.Add(new RectangleAnnotation
            {
                MinimumX = DateTimeAxis.ToDouble(d.Timestamps[i]),
                MaximumX = DateTimeAxis.ToDouble(d.Timestamps[j]),
                Fill = level == 2
                    ? OxyColor.FromAColor(40, OxyColors.Firebrick)
                    : OxyColor.FromAColor(36, OxyColors.DarkOrange),
                Layer = AnnotationLayer.BelowSeries,
            });
            i = j + 1;
        }
    }

    /// <summary>Plots the fused leak-rate estimate Q̂ as a line with a translucent ±σ band.</summary>
    private static void AddLeakRateSeries(PlotModel model, RatioTrendData d)
    {
        if (!d.HasLeakRate) return;   // older file, or no calibration was active — meta explains

        var band = new AreaSeries
        {
            Title = "±1σ",
            Color = OxyColors.Transparent,
            Color2 = OxyColors.Transparent,
            Fill = OxyColor.FromAColor(46, OxyColors.MidnightBlue),
            RenderInLegend = false,
        };
        var line = new LineSeries
        {
            Title = "leak rate Q̂",
            Color = OxyColors.MidnightBlue,
            StrokeThickness = 1.8,
            CanTrackerInterpolatePoints = false,
        };
        for (int i = 0; i < d.RowCount; i++)
        {
            double q = d.LeakRate[i];
            if (double.IsNaN(q)) continue;
            double x = DateTimeAxis.ToDouble(d.Timestamps[i]);
            double s = i < d.LeakRateSigma.Count && !double.IsNaN(d.LeakRateSigma[i]) ? d.LeakRateSigma[i] : 0;
            line.Points.Add(new DataPoint(x, q));
            band.Points.Add(new DataPoint(x, q + s));
            band.Points2.Add(new DataPoint(x, Math.Max(0, q - s)));
        }
        model.Series.Add(band);
        model.Series.Add(line);
    }

    private static int StateLevel(string state) => state switch
    {
        "Alarm"   => 2,
        "Warning" => 1,
        _         => 0,
    };

    private void BuildMetaText(RatioTrendData d)
    {
        var sb = new StringBuilder();
        var span = d.Timestamps[d.RowCount - 1] - d.Timestamps[0];
        sb.Append($"{d.RowCount} rows · {d.Timestamps[0]:yyyy-MM-dd HH:mm:ss}–")
          .Append($"{d.Timestamps[d.RowCount - 1]:HH:mm:ss} ({span:hh\\:mm\\:ss})");

        if (_mode == RatioReviewMode.LeakRate)
        {
            if (!d.HasLeakRate)
            {
                sb.Append("   ·   no leak-rate data in this file " +
                          "(logged before calibration, or no calibration was active).");
            }
            else
            {
                var qs = d.LeakRate.Where(v => !double.IsNaN(v)).ToList();
                double lastQ = double.NaN, lastS = double.NaN;
                for (int i = d.RowCount - 1; i >= 0; i--)
                    if (!double.IsNaN(d.LeakRate[i])) { lastQ = d.LeakRate[i]; lastS = d.LeakRateSigma[i]; break; }
                sb.Append("   ·   leak rate (mbar·L/s): ");
                sb.Append(qs.Count == 0
                    ? "(no finite samples)"
                    : $"min {qs.Min():G4} / max {qs.Max():G4} / last {lastQ:G4} ± {(double.IsNaN(lastS) ? 0 : lastS):G2}");
            }
            MetaText = sb.ToString();
            return;
        }

        bool pct = _mode == RatioReviewMode.PercentOfBaseline;
        foreach (var key in d.RatioKeys)
        {
            var src = pct ? d.Pct[key] : d.Raw[key];
            var vals = src.Where(v => !double.IsNaN(v)).ToList();
            sb.Append("   ·   ").Append(FriendlyName(key)).Append(": ");
            sb.Append(vals.Count == 0
                ? "(no data)"
                : $"min {vals.Min():G4} / max {vals.Max():G4} / last {vals[vals.Count - 1]:G4}");
        }
        MetaText = sb.ToString();
    }

    /// <summary>Short legend label for a ratio key; falls back to the key itself.</summary>
    private static string FriendlyName(string key) => key switch
    {
        "R_O"  => "O 777",
        "R_OH" => "OH 309",
        "R_NO" => "NO 237",
        "R_Ar" => "Ar 750",
        _      => key,
    };

    // --- folder / file open ---------------------------------------------------

    private void OpenSelectedFolder()
    {
        var folder = Path.GetDirectoryName(SelectedFile?.FilePath);
        if (!string.IsNullOrEmpty(folder)) OpenFolder(folder);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open folder.\n\nPath: {path}\nError: {ex.Message}",
                "Ratio Review", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show($"File no longer exists.\n\n{path}",
                    "Ratio Review", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open file.\n\nPath: {path}\nError: {ex.Message}",
                "Ratio Review", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- PNG / clipboard ------------------------------------------------------

    private void SavePng()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save ratio trend as PNG",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            FileName = SuggestImageName(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var exporter = new PngExporter { Width = 1600, Height = 900 };
            using var fs = File.Create(dlg.FileName);
            exporter.Export(PlotModel, fs);
            StatusText = "Saved: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save PNG.\n\n{ex.Message}",
                "Ratio Review", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyImage()
    {
        try
        {
            var exporter = new PngExporter { Width = 1600, Height = 900 };
            Clipboard.SetImage(exporter.ExportToBitmap(PlotModel));
            StatusText = "Copied ratio trend to clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy image.\n\n{ex.Message}",
                "Ratio Review", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string SuggestImageName()
    {
        var stem = _selectedFile is null
            ? "ratio_trend"
            : Path.GetFileNameWithoutExtension(_selectedFile.FileName);
        return stem + _mode switch
        {
            RatioReviewMode.RawRatio  => "_raw.png",
            RatioReviewMode.LeakRate  => "_leakrate.png",
            _                         => "_pct.png",
        };
    }

    public void Dispose()
    {
        _intensityLogger.FilesChanged -= OnFilesChanged;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
