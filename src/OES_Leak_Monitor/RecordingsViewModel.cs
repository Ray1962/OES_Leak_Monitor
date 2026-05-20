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
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using OxyPlot.Wpf;

namespace OES_Leak_Monitor;

public enum RecordingsViewMode { Line, Heatmap }

/// <summary>
/// Backs the Recordings tab. Scans the logger's base directory for completed CSV files,
/// turns each into a single-device session, and surfaces line / heatmap / frame-spectrum
/// views of the selected session(s). Supports compare-mode (2 sessions overlaid), notes,
/// PNG export, clipboard copy, and search.
/// </summary>
public sealed class RecordingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LoggerViewModel _logger;
    private readonly DualIntensityLogger _intensityLogger;
    private readonly string _defaultDataDirectory;

    // Line plot — primary session uses solid; compare session uses dashed.
    private readonly PlotModel _linePlotModel;
    private readonly LineSeries _series1A;
    private readonly LineSeries _series1B;

    // Heatmap plot — built lazily when ViewMode flips.
    private readonly PlotModel _heatmapPlotModel;
    private readonly LinearColorAxis _heatmapColorAxis;
    private readonly LinearAxis _heatmapXAxis;  // wavelength
    private readonly LinearAxis _heatmapYAxis;  // elapsed seconds

    // Frame spectrum sub-plot — wavelength vs intensity at the clicked time.
    private readonly LineSeries _frameSeries1;

    private CancellationTokenSource? _loadCts;

    // Cached parsed data for the currently displayed session(s).
    private FullRecording? _primaryOes1;
    private FullRecording? _compareOes1;
    private RecordingGroup? _primary, _compare;

    // Full, unfiltered group list. `Groups` is the bound filtered subset.
    private readonly List<RecordingGroup> _allGroups = new();

    private const int HeatmapMaxAxis = 1500;

    public RecordingsViewModel(LoggerViewModel logger, DualIntensityLogger intensityLogger, string defaultDataDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _intensityLogger = intensityLogger ?? throw new ArgumentNullException(nameof(intensityLogger));
        if (string.IsNullOrWhiteSpace(defaultDataDirectory))
            throw new ArgumentException("Default data directory is required.", nameof(defaultDataDirectory));
        _defaultDataDirectory = defaultDataDirectory;

        _wavelengthNm = _logger.TriggerWavelength > 0 ? _logger.TriggerWavelength : 337f;

        // --- line plot ---
        _linePlotModel = NewBaseModel(
            "Intensity @ Trigger Wavelength vs Time",
            xTitle: "Elapsed (s)",
            yTitle: "Intensity (a.u.)");
        _series1A = new LineSeries { Title = "Primary",       Color = OxyColors.SteelBlue, StrokeThickness = 1.2 };
        _series1B = new LineSeries { Title = "Compare",       Color = OxyColors.OrangeRed, StrokeThickness = 1.2,
                                     LineStyle = LineStyle.Dash, IsVisible = false };
        _linePlotModel.Series.Add(_series1A);
        _linePlotModel.Series.Add(_series1B);
        _linePlotModel.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.RightTop,
            LegendBackground = OxyColor.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
            LegendBorder = OxyColors.LightGray,
        });

        // --- heatmap plot ---
        _heatmapPlotModel = new PlotModel
        {
            Title = "Heatmap",
            TitleFontSize = 14,
            Background = OxyColors.White,
            PlotAreaBorderColor = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };
        _heatmapXAxis = new LinearAxis { Position = AxisPosition.Bottom, Title = "Wavelength (nm)" };
        _heatmapYAxis = new LinearAxis { Position = AxisPosition.Left,   Title = "Elapsed (s)" };
        _heatmapColorAxis = new LinearColorAxis
        {
            Position = AxisPosition.Right,
            Palette = OxyPalettes.Hot64,
            Title = "Intensity",
        };
        _heatmapPlotModel.Axes.Add(_heatmapXAxis);
        _heatmapPlotModel.Axes.Add(_heatmapYAxis);
        _heatmapPlotModel.Axes.Add(_heatmapColorAxis);

        // --- frame spectrum sub-plot ---
        FrameSpectrumModel = NewBaseModel(
            "Spectrum @ click — pick a point on the time-series",
            xTitle: "Wavelength (nm)",
            yTitle: "Intensity (a.u.)");
        _frameSeries1 = new LineSeries { Title = "Spectrum", Color = OxyColors.SteelBlue, StrokeThickness = 1.0 };
        FrameSpectrumModel.Series.Add(_frameSeries1);
        FrameSpectrumModel.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.RightTop,
            LegendBackground = OxyColor.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
            LegendBorder = OxyColors.LightGray,
        });

        // Mirror DeviceViewModel's controller: keep OxyPlot defaults (wheel, middle-drag,
        // Ctrl+Left, 'A') and only free up right-click so the WPF ContextMenu opens.
        PlotController = BuildController();
        FrameController = BuildController();

        ZoomInCommand              = new RelayCommand(() => ZoomBy(ActivePlotModel, 1.25));
        ZoomOutCommand             = new RelayCommand(() => ZoomBy(ActivePlotModel, 0.8));
        ZoomAllCommand             = new RelayCommand(() => ZoomAll(ActivePlotModel));
        RefreshCommand             = new RelayCommand(Refresh);
        OpenBaseFolderCommand      = new RelayCommand(OpenBaseFolder);
        OpenSelectedFolderCommand  = new RelayCommand(OpenSelectedFolder, () => Primary is not null);
        OpenOesFileCommand         = new RelayCommand(() => OpenFile(Primary?.Oes1?.FilePath), () => Primary?.Oes1 is not null);
        SetLineViewCommand         = new RelayCommand(() => ViewMode = RecordingsViewMode.Line);
        SetHeatmapViewCommand      = new RelayCommand(() => ViewMode = RecordingsViewMode.Heatmap);
        SavePngCommand             = new RelayCommand(SavePng);
        CopyImageCommand           = new RelayCommand(CopyImage);
        ClearCompareCommand        = new RelayCommand(() => SetSelection(Primary, null), () => Compare is not null);
        SaveNotesCommand           = new RelayCommand(SaveNotes, () => Primary is not null);
        ClearFrameCommand          = new RelayCommand(ClearFrameSpectrum, () => _frameSeries1.Points.Count > 0);

        _intensityLogger.FilesChanged += OnFilesChanged;

        _startDate = DateTime.Today.AddDays(-7);
        _endDate   = DateTime.Today;
        ActivePlotModel = _linePlotModel;
        Refresh();
    }

    public ObservableCollection<RecordingGroup> Groups { get; } = new();

    public PlotModel FrameSpectrumModel { get; }
    public IPlotController PlotController { get; }
    public IPlotController FrameController { get; }

    private PlotModel _activePlotModel = null!;
    public PlotModel ActivePlotModel
    {
        get => _activePlotModel;
        private set => Set(ref _activePlotModel, value);
    }

    /// <summary>Primary session. The DataGrid binds this through <see cref="SetSelection"/>.</summary>
    public RecordingGroup? Primary
    {
        get => _primary;
        private set
        {
            if (Set(ref _primary, value))
            {
                OnPropertyChanged(nameof(IsPrimarySelected));
                OpenSelectedFolderCommand.RaiseCanExecuteChanged();
                OpenOesFileCommand.RaiseCanExecuteChanged();
                SaveNotesCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsPrimarySelected => _primary is not null;

    public RecordingGroup? Compare
    {
        get => _compare;
        private set
        {
            if (Set(ref _compare, value))
            {
                OnPropertyChanged(nameof(IsCompareSelected));
                ClearCompareCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsCompareSelected => _compare is not null;

    private DateTime _startDate;
    public DateTime StartDate
    {
        get => _startDate;
        set { if (Set(ref _startDate, value)) Refresh(); }
    }

    private DateTime _endDate;
    public DateTime EndDate
    {
        get => _endDate;
        set { if (Set(ref _endDate, value)) Refresh(); }
    }

    private string _searchText = "";
    /// <summary>Substring filter applied to group date / time / filenames.</summary>
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) ApplyFilter(); }
    }

    private float _wavelengthNm;
    /// <summary>Wavelength projected onto the line view; falls through to the closest header column.</summary>
    public float WavelengthNm
    {
        get => _wavelengthNm;
        set { if (Set(ref _wavelengthNm, value)) RebuildPlots(); }
    }

    private RecordingsViewMode _viewMode = RecordingsViewMode.Line;
    public RecordingsViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (Set(ref _viewMode, value))
            {
                OnPropertyChanged(nameof(IsLineMode));
                OnPropertyChanged(nameof(IsHeatmapMode));
                ActivePlotModel = value == RecordingsViewMode.Line ? _linePlotModel : _heatmapPlotModel;
                RebuildPlots();
            }
        }
    }
    public bool IsLineMode    => _viewMode == RecordingsViewMode.Line;
    public bool IsHeatmapMode => _viewMode == RecordingsViewMode.Heatmap;

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _wavelengthInfoText = "";
    public string WavelengthInfoText { get => _wavelengthInfoText; private set => Set(ref _wavelengthInfoText, value); }

    private string _metaText = "";
    public string MetaText { get => _metaText; private set => Set(ref _metaText, value); }

    private string _frameInfoText = "";
    public string FrameInfoText { get => _frameInfoText; private set => Set(ref _frameInfoText, value); }

    private string _notes = "";
    public string Notes { get => _notes; set => Set(ref _notes, value ?? ""); }

    public string EffectiveBaseDirectory =>
        LoggerSettings.ResolveBaseDirectory(_logger.ToSettings().BaseDirectory, _defaultDataDirectory);

    public RelayCommand ZoomInCommand              { get; }
    public RelayCommand ZoomOutCommand             { get; }
    public RelayCommand ZoomAllCommand             { get; }
    public RelayCommand RefreshCommand             { get; }
    public RelayCommand OpenBaseFolderCommand      { get; }
    public RelayCommand OpenSelectedFolderCommand  { get; }
    public RelayCommand OpenOesFileCommand         { get; }
    public RelayCommand SetLineViewCommand         { get; }
    public RelayCommand SetHeatmapViewCommand      { get; }
    public RelayCommand SavePngCommand             { get; }
    public RelayCommand CopyImageCommand           { get; }
    public RelayCommand ClearCompareCommand        { get; }
    public RelayCommand SaveNotesCommand           { get; }
    public RelayCommand ClearFrameCommand          { get; }

    /// <summary>Called by the DataGrid's selection-changed handler in code-behind.</summary>
    public void SetSelection(RecordingGroup? primary, RecordingGroup? compare)
    {
        Primary = primary;
        Compare = ReferenceEquals(primary, compare) ? null : compare;
        LoadNotes();
        _ = LoadAndRebuildAsync();
    }

    public void Refresh()
    {
        _allGroups.Clear();
        OnPropertyChanged(nameof(EffectiveBaseDirectory));

        var baseDir = EffectiveBaseDirectory;
        if (!Directory.Exists(baseDir))
        {
            ApplyFilter();
            StatusText = $"Base directory not found: {baseDir}";
            return;
        }

        var groups = new Dictionary<string, RecordingGroup>();
        int fileCount = 0;

        try
        {
            foreach (var monthDir in Directory.EnumerateDirectories(baseDir))
            {
                var monthName = Path.GetFileName(monthDir);
                if (monthName.Length != 6) continue;
                if (!int.TryParse(monthName.Substring(0, 4), out var year)) continue;
                if (!int.TryParse(monthName.Substring(4, 2), out var month)) continue;

                DateTime monthStart;
                try { monthStart = new DateTime(year, month, 1); } catch { continue; }
                var monthEnd = monthStart.AddMonths(1).AddTicks(-1);
                if (monthEnd < StartDate.Date) continue;
                if (monthStart > EndDate.Date.AddDays(1).AddTicks(-1)) continue;

                foreach (var dayDir in Directory.EnumerateDirectories(monthDir))
                {
                    var dayName = Path.GetFileName(dayDir);
                    if (dayName.Length != 2 || !int.TryParse(dayName, out var day)) continue;

                    DateTime date;
                    try { date = new DateTime(year, month, day); } catch { continue; }
                    if (date < StartDate.Date || date > EndDate.Date) continue;

                    foreach (var path in Directory.EnumerateFiles(dayDir, "*.csv"))
                    {
                        var rec = Recording.TryParse(path);
                        if (rec is null) continue;
                        fileCount++;
                        if (!groups.TryGetValue(rec.GroupKey, out var grp))
                        {
                            grp = new RecordingGroup
                            {
                                Prefix        = rec.Prefix,
                                SessionStart  = rec.SessionStart,
                                RotationIndex = rec.RotationIndex,
                            };
                            groups[rec.GroupKey] = grp;
                        }
                        // Single-OES app: only files tagged "OES1" populate the group.
                        // Any historical "OES2" siblings on disk are silently ignored.
                        if (rec.DeviceTag.Equals("OES1", StringComparison.OrdinalIgnoreCase)) grp.Oes1 = rec;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = "Scan error: " + ex.Message;
            return;
        }

        foreach (var g in groups.Values.OrderByDescending(g => g.SessionStart).ThenBy(g => g.RotationIndex))
            _allGroups.Add(g);

        ApplyFilter();
        StatusText = $"{Groups.Count} of {_allGroups.Count} session(s) · {fileCount} file(s) under {baseDir}";
    }

    private void ApplyFilter()
    {
        Groups.Clear();
        var q = _searchText?.Trim() ?? "";
        IEnumerable<RecordingGroup> filtered = _allGroups;
        if (q.Length > 0)
        {
            filtered = filtered.Where(g =>
                g.DateText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                g.TimeText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (g.Oes1?.FileName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var g in filtered) Groups.Add(g);
        if (_allGroups.Count > 0)
            StatusText = $"{Groups.Count} of {_allGroups.Count} session(s)";
    }

    private async Task LoadAndRebuildAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        ClearAllPlots();

        if (Primary is null)
        {
            _primaryOes1 = null;
            _compareOes1 = null;
            ApplyTitles();
            ActivePlotModel.InvalidatePlot(true);
            FrameSpectrumModel.InvalidatePlot(true);
            return;
        }

        StatusText = "Loading…";

        try
        {
            var p1 = ParseAsync(Primary.Oes1?.FilePath, token);
            var c1 = ParseAsync(Compare?.Oes1?.FilePath, token);

            _primaryOes1 = await p1.ConfigureAwait(true);
            _compareOes1 = await c1.ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            RebuildPlots();
            StatusText = "Loaded.";
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            StatusText = "Load error: " + ex.Message;
        }
    }

    private static Task<FullRecording?> ParseAsync(string? path, CancellationToken token) =>
        path is null
            ? Task.FromResult<FullRecording?>(null)
            : Task.Run(() => RecordingCsvParser.ReadFull(path, token), token);

    /// <summary>
    /// Re-project the in-memory recordings onto the active plot. Called whenever
    /// view mode, wavelength, or heatmap-device changes — no re-parse needed.
    /// </summary>
    private void RebuildPlots()
    {
        ClearAllPlots();
        if (Primary is null) return;

        if (_viewMode == RecordingsViewMode.Line)
            BuildLinePlot();
        else
            BuildHeatmap();

        BuildMetaText();
        ApplyTitles();
        ActivePlotModel.ResetAllAxes();
        ActivePlotModel.InvalidatePlot(true);
        FrameSpectrumModel.InvalidatePlot(true);
    }

    private void BuildLinePlot()
    {
        var infos = new List<string>();
        Project(_primaryOes1, _series1A, "primary", infos);

        bool hasCompare = _compareOes1 is not null;
        _series1B.IsVisible = hasCompare;
        if (hasCompare)
            Project(_compareOes1, _series1B, "compare", infos);
        else
            _series1B.Points.Clear();
        WavelengthInfoText = string.Join("   |   ", infos);
    }

    private void Project(FullRecording? rec, LineSeries series, string label, List<string> infos)
    {
        series.Points.Clear();
        if (rec is null || rec.FrameCount == 0) return;
        int col = rec.FindClosestWavelength(_wavelengthNm);
        if (col < 0) return;

        series.Points.Capacity = Math.Max(series.Points.Capacity, rec.FrameCount);
        for (int i = 0; i < rec.FrameCount; i++)
            series.Points.Add(new DataPoint(rec.ElapsedSec[i], rec.Intensities[i][col]));
        infos.Add($"{label} @ {rec.Wavelengths[col]:F2} nm · {rec.FrameCount} frames");
    }

    private void BuildHeatmap()
    {
        // Drop previously built HeatMapSeries, leave axes in place.
        for (int i = _heatmapPlotModel.Series.Count - 1; i >= 0; i--)
            _heatmapPlotModel.Series.RemoveAt(i);

        FullRecording? rec = _primaryOes1;
        if (rec is null || rec.FrameCount == 0 || rec.Wavelengths.Length == 0)
        {
            WavelengthInfoText = "(no data for heatmap mode)";
            return;
        }

        // Stride-downsample both axes to keep the heatmap matrix bounded.
        int wlStride = Math.Max(1, (int)Math.Ceiling(rec.Wavelengths.Length / (double)HeatmapMaxAxis));
        int frStride = Math.Max(1, (int)Math.Ceiling(rec.FrameCount         / (double)HeatmapMaxAxis));
        int outW = (rec.Wavelengths.Length + wlStride - 1) / wlStride;
        int outF = (rec.FrameCount         + frStride - 1) / frStride;

        var data = new double[outW, outF];
        for (int fi = 0, fo = 0; fi < rec.FrameCount && fo < outF; fi += frStride, fo++)
        {
            var row = rec.Intensities[fi];
            for (int wi = 0, wo = 0; wi < row.Length && wo < outW; wi += wlStride, wo++)
            {
                var v = row[wi];
                data[wo, fo] = float.IsNaN(v) ? 0.0 : v;
            }
        }

        float wl0 = rec.Wavelengths[0];
        float wl1 = rec.Wavelengths[rec.Wavelengths.Length - 1];
        double t0 = rec.ElapsedSec[0];
        double t1 = rec.ElapsedSec[rec.FrameCount - 1];

        _heatmapPlotModel.Series.Add(new HeatMapSeries
        {
            X0 = wl0, X1 = wl1,
            Y0 = t0,  Y1 = t1,
            Data = data,
            Interpolate = false,
        });

        WavelengthInfoText = $"Heatmap · {rec.FrameCount} frames × {rec.Wavelengths.Length} wavelengths"
                           + (wlStride > 1 || frStride > 1 ? $" (downsampled stride {wlStride}×{frStride})" : "");
    }

    private void BuildMetaText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Primary    : {SummariseGroup(Primary, _primaryOes1)}");
        if (Compare is not null)
            sb.AppendLine($"Compare    : {SummariseGroup(Compare, _compareOes1)}");
        MetaText = sb.ToString().TrimEnd();
    }

    private static string SummariseGroup(RecordingGroup? g, FullRecording? r)
    {
        if (g is null) return "(none)";
        var parts = new List<string> { g.DateText + " " + g.TimeText };
        if (g.RotationIndex > 0) parts.Add($"rot #{g.RotationIndex}");
        if (r is not null) parts.Add($"{r.FrameCount}f/{r.Wavelengths.Length}wl");
        return string.Join("  ·  ", parts);
    }

    private void ApplyTitles()
    {
        if (Primary is null)
        {
            _linePlotModel.Title = "Intensity @ Trigger Wavelength vs Time";
            _heatmapPlotModel.Title = "Heatmap";
            return;
        }
        var rotSuffix = string.IsNullOrEmpty(Primary.RotationText) ? "" : " " + Primary.RotationText;
        var compareSuffix = Compare is not null ? $"  vs  {Compare.DateText} {Compare.TimeText}" : "";
        _linePlotModel.Title    = $"{Primary.DateText} {Primary.TimeText}{rotSuffix}{compareSuffix}";
        _heatmapPlotModel.Title = $"{Primary.DateText} {Primary.TimeText}{rotSuffix}";
    }

    private void ClearAllPlots()
    {
        _series1A.Points.Clear();
        _series1B.Points.Clear();
        _series1B.IsVisible = false;
        for (int i = _heatmapPlotModel.Series.Count - 1; i >= 0; i--)
            _heatmapPlotModel.Series.RemoveAt(i);
        ClearFrameSpectrum();
    }

    private void ClearFrameSpectrum()
    {
        _frameSeries1.Points.Clear();
        FrameSpectrumModel.Title = "Spectrum @ click — pick a point on the time-series";
        FrameInfoText = "";
        FrameSpectrumModel.InvalidatePlot(true);
        ClearFrameCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Called from the panel's code-behind when the user clicks on the line plot.
    /// Loads the full spectrum at the closest-matching elapsed-seconds frame into
    /// the frame sub-plot.
    /// </summary>
    public void ShowFrameAt(double elapsedSec)
    {
        if (_viewMode != RecordingsViewMode.Line || Primary is null) return;

        _frameSeries1.Points.Clear();

        var labels = new List<string>();
        FillFrame(_primaryOes1, elapsedSec, _frameSeries1, labels);

        FrameSpectrumModel.Title = labels.Count > 0
            ? $"Spectrum @ ~{elapsedSec:F3} s"
            : "Spectrum @ click — no data for this time";
        FrameInfoText = string.Join("   |   ", labels);
        FrameSpectrumModel.ResetAllAxes();
        FrameSpectrumModel.InvalidatePlot(true);
        ClearFrameCommand.RaiseCanExecuteChanged();
    }

    private static void FillFrame(FullRecording? rec, double elapsedSec, LineSeries series, List<string> labels)
    {
        if (rec is null || rec.FrameCount == 0) return;
        int idx = rec.FindClosestFrame(elapsedSec);
        if (idx < 0) return;
        var row = rec.Intensities[idx];
        series.Points.Capacity = Math.Max(series.Points.Capacity, row.Length);
        for (int i = 0; i < row.Length && i < rec.Wavelengths.Length; i++)
            series.Points.Add(new DataPoint(rec.Wavelengths[i], row[i]));
        labels.Add($"frame #{idx} @ {rec.ElapsedSec[idx]:F3}s ({rec.WallTimes[idx]:hh\\:mm\\:ss\\.fff})");
    }

    private void OnFilesChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(Refresh);

    // ---- folder / file open ----

    private void OpenBaseFolder() => OpenFolder(EffectiveBaseDirectory);

    private void OpenSelectedFolder()
    {
        var folder = Path.GetDirectoryName(Primary?.Oes1?.FilePath);
        if (!string.IsNullOrEmpty(folder)) OpenFolder(folder);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open folder.\n\nPath: {path}\nError: {ex.Message}",
                "Recordings", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    "Recordings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open file.\n\nPath: {path}\nError: {ex.Message}",
                "Recordings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- PNG / clipboard ----

    private void SavePng()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save current plot as PNG",
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
            exporter.Export(ActivePlotModel, fs);
            StatusText = "Saved: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save PNG.\n\n{ex.Message}",
                "Recordings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyImage()
    {
        try
        {
            var exporter = new PngExporter { Width = 1600, Height = 900 };
            var bitmap = exporter.ExportToBitmap(ActivePlotModel);
            Clipboard.SetImage(bitmap);
            StatusText = "Copied plot image to clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy image.\n\n{ex.Message}",
                "Recordings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string SuggestImageName()
    {
        var stem = "recordings";
        if (Primary is not null)
            stem = $"{Primary.Prefix}_{Primary.SessionStart:yyyyMMdd_HHmmss}"
                 + (Primary.RotationIndex > 0 ? $"_r{Primary.RotationIndex}" : "");
        return stem + (_viewMode == RecordingsViewMode.Heatmap ? "_heatmap.png" : "_line.png");
    }

    // ---- notes (sidecar .notes.txt per session group) ----

    private void LoadNotes()
    {
        var path = NotesPathFor(Primary);
        if (path is not null && File.Exists(path))
        {
            try { Notes = File.ReadAllText(path); return; }
            catch { /* fall through to clear */ }
        }
        Notes = "";
    }

    private void SaveNotes()
    {
        var path = NotesPathFor(Primary);
        if (path is null)
        {
            StatusText = "Notes: no session selected.";
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Notes ?? "");
            StatusText = "Notes saved: " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save notes.\n\n{ex.Message}",
                "Recordings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? NotesPathFor(RecordingGroup? g)
    {
        var anyPath = g?.Oes1?.FilePath;
        if (anyPath is null) return null;
        var dir = Path.GetDirectoryName(anyPath);
        if (dir is null) return null;
        var stem = Path.GetFileNameWithoutExtension(anyPath);
        var parts = stem.Split('_');
        if (parts.Length < 3) return null;
        var sessionStem = parts.Length >= 4
            ? $"{parts[0]}_{parts[2]}_{parts[3]}"
            : $"{parts[0]}_{parts[2]}";
        return Path.Combine(dir, sessionStem + ".notes.txt");
    }

    // ---- zoom helpers ----

    private static PlotController BuildController()
    {
        var c = new PlotController();
        c.UnbindMouseDown(OxyMouseButton.Right);
        c.UnbindMouseDown(OxyMouseButton.Right, OxyModifierKeys.Control);
        return c;
    }

    private static PlotModel NewBaseModel(string title, string xTitle, string yTitle)
    {
        var m = new PlotModel
        {
            Title = title,
            TitleFontSize = 14,
            Background = OxyColors.White,
            PlotAreaBorderColor = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };
        m.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = xTitle,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE5, 0xE5, 0xE5),
            MinorGridlineStyle = LineStyle.Dot,
            MinorGridlineColor = OxyColor.FromRgb(0xF0, 0xF0, 0xF0),
        });
        m.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = yTitle,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE5, 0xE5, 0xE5),
            MinorGridlineStyle = LineStyle.Dot,
            MinorGridlineColor = OxyColor.FromRgb(0xF0, 0xF0, 0xF0),
        });
        return m;
    }

    private static void ZoomBy(PlotModel m, double factor)
    {
        foreach (var axis in m.Axes) axis.ZoomAtCenter(factor);
        m.InvalidatePlot(false);
    }

    private static void ZoomAll(PlotModel m)
    {
        m.ResetAllAxes();
        m.InvalidatePlot(false);
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
