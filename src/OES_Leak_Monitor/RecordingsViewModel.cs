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
/// One wavelength plotted on the Recordings line view, with the colour it was dealt. The colour
/// is positional, so the list and the plot legend cannot disagree about which trace is which.
/// </summary>
public sealed class TrendWavelengthViewModel : INotifyPropertyChanged
{
    public TrendWavelengthViewModel(double nm, OxyColor color)
    {
        Nm = nm;
        SetColor(color);
    }

    public double Nm { get; }
    public string Text => $"{Nm:0.###} nm";

    public OxyColor Color { get; private set; }

    /// <summary>Swatch for the list, so the colour is legible away from the plot legend.</summary>
    public System.Windows.Media.Brush Swatch { get; private set; } =
        System.Windows.Media.Brushes.Transparent;

    public void SetColor(OxyColor color)
    {
        Color = color;
        Swatch = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        Swatch.Freeze();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Swatch)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

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

    // Line plot. Colour encodes the wavelength and line style encodes the session (primary
    // solid, compare dashed) — the two questions being asked, "which line moved" and "how does
    // this run differ from that one", are separate, so they get separate visual channels rather
    // than being made mutually exclusive.
    private readonly PlotModel _linePlotModel;
    private readonly LinearAxis _lineValueAxis;

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

    /// <summary>How many wavelengths the line view can hold. Six is one palette's worth of
    /// distinguishable colours — and twice that many series once a compare session is on.</summary>
    public const int MaxTrendWavelengths = 6;

    /// <summary>Half-width of the optional peak window, nm.</summary>
    private const double PeakWindowNm = 0.5;

    private static readonly OxyColor[] TrendPalette =
    {
        OxyColors.SteelBlue, OxyColors.Firebrick, OxyColors.ForestGreen,
        OxyColors.DarkOrange, OxyColors.MediumPurple, OxyColors.Teal,
    };

    public RecordingsViewModel(LoggerViewModel logger, DualIntensityLogger intensityLogger, string defaultDataDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _intensityLogger = intensityLogger ?? throw new ArgumentNullException(nameof(intensityLogger));
        if (string.IsNullOrWhiteSpace(defaultDataDirectory))
            throw new ArgumentException("Default data directory is required.", nameof(defaultDataDirectory));
        _defaultDataDirectory = defaultDataDirectory;

        // One wavelength to start with — the same one this tab has always opened on, so the
        // change is purely additive: what you saw before, plus the ability to add to it.
        // Fallback to the N2 337.1 nm band head (one decimal place) when no trigger is set.
        TrendWavelengths = new ObservableCollection<TrendWavelengthViewModel>();
        AddWavelength(_logger.TriggerWavelength > 0 ? _logger.TriggerWavelength : 337.1);

        // Species-grouped catalog for the line picker — built-in and user-defined lines
        // together, the same view the Ratio Setup and Configuration pickers offer.
        _catalogOptions = new ObservableCollection<SpectralLineOption>(
            SpectralLineCatalog.All.Select(l => new SpectralLineOption(l)));
        var catalogView = new System.Windows.Data.CollectionViewSource { Source = _catalogOptions };
        catalogView.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(SpectralLineOption.Species)));
        catalogView.SortDescriptions.Add(
            new SortDescription(nameof(SpectralLineOption.Species), ListSortDirection.Ascending));
        catalogView.SortDescriptions.Add(
            new SortDescription(nameof(SpectralLineOption.WavelengthNm), ListSortDirection.Ascending));
        LineCatalog = catalogView.View;

        // --- line plot ---
        _linePlotModel = NewBaseModel(
            "Intensity vs Time",
            xTitle: "Elapsed (s)",
            yTitle: "Intensity (a.u.)");
        _lineValueAxis = _linePlotModel.Axes.OfType<LinearAxis>()
            .First(a => a.Position == AxisPosition.Left);
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
        AddCatalogLineCommand      = new RelayCommand(AddCatalogLine,
            () => _selectedCatalogLine is not null && TrendWavelengths.Count < MaxTrendWavelengths);
        AddTypedWavelengthCommand  = new RelayCommand(AddTypedWavelength,
            () => TrendWavelengths.Count < MaxTrendWavelengths);
        RemoveWavelengthCommand    = new RelayCommand(RemoveWavelength,
            () => _selectedTrendWavelength is not null && TrendWavelengths.Count > 1);

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

    /// <summary>Wavelengths projected onto the line view, up to <see cref="MaxTrendWavelengths"/>.</summary>
    public ObservableCollection<TrendWavelengthViewModel> TrendWavelengths { get; }

    /// <summary>Species-grouped emission-line catalog feeding the picker.</summary>
    public System.ComponentModel.ICollectionView LineCatalog { get; }

    private readonly ObservableCollection<SpectralLineOption> _catalogOptions;

    private SpectralLineOption? _selectedCatalogLine;
    public SpectralLineOption? SelectedCatalogLine
    {
        get => _selectedCatalogLine;
        set { if (Set(ref _selectedCatalogLine, value)) AddCatalogLineCommand.RaiseCanExecuteChanged(); }
    }

    private TrendWavelengthViewModel? _selectedTrendWavelength;
    public TrendWavelengthViewModel? SelectedTrendWavelength
    {
        get => _selectedTrendWavelength;
        set { if (Set(ref _selectedTrendWavelength, value)) RemoveWavelengthCommand.RaiseCanExecuteChanged(); }
    }

    private string _newWavelengthText = "";
    /// <summary>Free-typed wavelength — a position with no line on it (a stretch of continuum
    /// used as a control) is exactly what the catalog cannot offer.</summary>
    public string NewWavelengthText
    {
        get => _newWavelengthText;
        set => Set(ref _newWavelengthText, value ?? "");
    }

    private bool _normalizeTrend;
    /// <summary>
    /// Divides each trace by its own mean over the whole recording, so lines that differ by
    /// orders of magnitude can be compared by shape. The mean, not the first frame: a recording
    /// usually starts before the plasma is lit, and dividing by ~0 would blow the trace up.
    /// Distinct from the Leak Monitor's "% of baseline", whose divisor is a Golden Run.
    /// </summary>
    public bool NormalizeTrend
    {
        get => _normalizeTrend;
        set { if (Set(ref _normalizeTrend, value)) RebuildPlots(); }
    }

    private bool _usePeakWindow;
    /// <summary>
    /// Off by default: the trace is the value at the nearest pixel, which is what was actually
    /// measured there. On, it takes the maximum within ±0.5 nm, matching the Monitor tab's live
    /// trend — useful against axis drift, but it biases a weak line upward (it picks the largest
    /// sample in a window) and inflates its scatter, which is the very judgement this tab exists
    /// to support. Hence a switch, not a default.
    /// </summary>
    public bool UsePeakWindow
    {
        get => _usePeakWindow;
        set { if (Set(ref _usePeakWindow, value)) RebuildPlots(); }
    }

    /// <summary>Raised when the wavelength list changes, so the host can persist it.</summary>
    public event EventHandler? TrendWavelengthsChanged;

    /// <summary>The current list, for persistence.</summary>
    public IReadOnlyList<double> TrendWavelengthValues =>
        TrendWavelengths.Select(w => w.Nm).ToList();

    /// <summary>Replaces the list — used once at start-up to restore the persisted selection.
    /// Silent: restoring what was already chosen is not a change worth persisting again.</summary>
    public void RestoreTrendWavelengths(IEnumerable<double>? wavelengths)
    {
        var list = (wavelengths ?? Enumerable.Empty<double>())
            .Where(w => w > 0).Distinct().Take(MaxTrendWavelengths).ToList();
        if (list.Count == 0) return;
        TrendWavelengths.Clear();
        foreach (var w in list) AddWavelength(w);
        RebuildPlots();
    }

    /// <summary>Re-reads the catalog so lines added on the Wavelength Calibration tab appear
    /// in the picker without a restart.</summary>
    public void RefreshLineCatalog()
    {
        var have = new HashSet<(string, double)>(
            _catalogOptions.Select(o => (o.Species, Math.Round(o.WavelengthNm, 3))));
        foreach (var line in SpectralLineCatalog.All)
        {
            if (have.Add((line.Species, Math.Round(line.WavelengthNm, 3))))
                _catalogOptions.Add(new SpectralLineOption(line));
        }
        var keep = new HashSet<(string, double)>(
            SpectralLineCatalog.All.Select(l => (l.Species, Math.Round(l.WavelengthNm, 3))));
        for (int i = _catalogOptions.Count - 1; i >= 0; i--)
        {
            var o = _catalogOptions[i];
            if (!keep.Contains((o.Species, Math.Round(o.WavelengthNm, 3))))
                _catalogOptions.RemoveAt(i);
        }
        LineCatalog.Refresh();
    }

    private void AddWavelength(double nm)
    {
        int index = TrendWavelengths.Count;
        TrendWavelengths.Add(new TrendWavelengthViewModel(nm, TrendPalette[index % TrendPalette.Length]));
    }

    private void AddCatalogLine()
    {
        if (_selectedCatalogLine is not { } opt) return;
        AddWavelengthChecked(opt.WavelengthNm, $"{opt.Species} {opt.WavelengthNm:0.###} nm");
    }

    private void AddTypedWavelength()
    {
        if (!double.TryParse(NewWavelengthText.Trim(),
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double nm) || nm <= 0)
        {
            StatusText = $"“{NewWavelengthText}” is not a wavelength.";
            return;
        }
        if (AddWavelengthChecked(nm, $"{nm:0.###} nm")) NewWavelengthText = "";
    }

    private bool AddWavelengthChecked(double nm, string label)
    {
        if (TrendWavelengths.Any(w => Math.Abs(w.Nm - nm) < 0.05))
        {
            StatusText = $"{label} is already plotted.";
            return false;
        }
        if (TrendWavelengths.Count >= MaxTrendWavelengths)
        {
            StatusText = $"The line view holds {MaxTrendWavelengths} wavelengths; remove one first.";
            return false;
        }
        AddWavelength(nm);
        AfterWavelengthsChanged($"Added {label}.");
        return true;
    }

    private void RemoveWavelength()
    {
        if (_selectedTrendWavelength is not { } row || TrendWavelengths.Count <= 1) return;
        TrendWavelengths.Remove(row);
        SelectedTrendWavelength = null;
        // Colours are positional, so they have to be re-dealt after a removal or the legend
        // and the swatches in the list would disagree with the plot.
        for (int i = 0; i < TrendWavelengths.Count; i++)
            TrendWavelengths[i].SetColor(TrendPalette[i % TrendPalette.Length]);
        AfterWavelengthsChanged($"Removed {row.Text}.");
    }

    private void AfterWavelengthsChanged(string status)
    {
        StatusText = status;
        RebuildPlots();
        AddCatalogLineCommand.RaiseCanExecuteChanged();
        AddTypedWavelengthCommand.RaiseCanExecuteChanged();
        RemoveWavelengthCommand.RaiseCanExecuteChanged();
        TrendWavelengthsChanged?.Invoke(this, EventArgs.Empty);
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
    public RelayCommand AddCatalogLineCommand      { get; }
    public RelayCommand AddTypedWavelengthCommand  { get; }
    public RelayCommand RemoveWavelengthCommand    { get; }

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
            // One walk of the tree, shared with the Baseline Builder — see
            // Recording.EnumerateSpectra for why this is not two loops.
            foreach (var rec in Recording.EnumerateSpectra(baseDir, StartDate, EndDate))
            {
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
                grp.Oes1 = rec;
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
            var p1 = ParseAsync(Primary.Oes1, token);
            var c1 = ParseAsync(Compare?.Oes1, token);

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

    /// <summary>
    /// Parse one recording off the UI thread. <see cref="Recording.OpenText"/> hides whether
    /// the CSV is a loose file or an entry inside an archived DD.zip — an archived one is
    /// decompressed as it is read rather than unpacked to temporary storage first, which
    /// matters when a single full-spectrum file can be several hundred megabytes.
    /// </summary>
    private static Task<FullRecording?> ParseAsync(Recording? rec, CancellationToken token) =>
        rec is null
            ? Task.FromResult<FullRecording?>(null)
            : Task.Run(() =>
            {
                using var reader = rec.OpenText();
                return RecordingCsvParser.ReadFull(reader, token);
            }, token);

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
        _linePlotModel.Series.Clear();
        var infos = new List<string>();

        foreach (var w in TrendWavelengths)
        {
            Project(_primaryOes1, w, compare: false, infos);
            if (_compareOes1 is not null) Project(_compareOes1, w, compare: true, infos);
        }

        _lineValueAxis.Title = _normalizeTrend ? "Intensity (× own mean)" : "Intensity (a.u.)";
        WavelengthInfoText = string.Join("   |   ", infos);
    }

    /// <summary>
    /// Adds one wavelength's trace for one session. Colour comes from the wavelength, dashing
    /// from the session, so both can be read off the same plot.
    /// </summary>
    private void Project(FullRecording? rec, TrendWavelengthViewModel w, bool compare, List<string> infos)
    {
        if (rec is null || rec.FrameCount == 0 || rec.Wavelengths.Length == 0) return;

        int col = rec.FindClosestWavelength((float)w.Nm);
        if (col < 0) return;

        // The peak window, when enabled, is resolved once per recording — the axis does not
        // move between frames, so re-scanning it per frame would buy nothing.
        int lo = col, hi = col;
        if (_usePeakWindow)
        {
            while (lo > 0 && rec.Wavelengths[lo - 1] >= w.Nm - PeakWindowNm) lo--;
            while (hi < rec.Wavelengths.Length - 1 && rec.Wavelengths[hi + 1] <= w.Nm + PeakWindowNm) hi++;
        }

        var values = new double[rec.FrameCount];
        double sum = 0;
        int counted = 0;
        for (int i = 0; i < rec.FrameCount; i++)
        {
            var row = rec.Intensities[i];
            double v = row[col];
            if (_usePeakWindow)
            {
                for (int c = lo; c <= hi && c < row.Length; c++)
                    if (row[c] > v) v = row[c];
            }
            values[i] = v;
            if (!double.IsNaN(v)) { sum += v; counted++; }
        }

        double mean = counted > 0 ? sum / counted : 0.0;
        // A trace whose mean is at or below zero cannot be normalized by it — that would flip
        // or explode it. Left in raw counts and said so, rather than drawn as nonsense.
        bool normalize = _normalizeTrend && mean > 0;

        var series = new LineSeries
        {
            Title = $"{w.Text} · {(compare ? "compare" : "primary")}",
            Color = w.Color,
            StrokeThickness = 1.2,
            LineStyle = compare ? LineStyle.Dash : LineStyle.Solid,
        };
        series.Points.Capacity = rec.FrameCount;
        for (int i = 0; i < rec.FrameCount; i++)
            series.Points.Add(new DataPoint(rec.ElapsedSec[i], normalize ? values[i] / mean : values[i]));
        _linePlotModel.Series.Add(series);

        string where = _usePeakWindow
            ? $"peak {rec.Wavelengths[lo]:F2}–{rec.Wavelengths[hi]:F2} nm"
            : $"{rec.Wavelengths[col]:F2} nm";
        string note = _normalizeTrend && !normalize ? " · not normalized (mean ≤ 0)" : "";
        infos.Add($"{(compare ? "compare" : "primary")} {w.Text} @ {where} · mean {mean:G4}{note}");
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
            _linePlotModel.Title = "Intensity vs Time";
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
        _linePlotModel.Series.Clear();
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
