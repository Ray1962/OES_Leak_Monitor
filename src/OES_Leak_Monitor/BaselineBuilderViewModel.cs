using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace OES_Leak_Monitor;

/// <summary>One candidate recording in the builder's list.</summary>
public sealed class BuilderFileViewModel : INotifyPropertyChanged
{
    public BuilderFileViewModel(Recording recording) => Recording = recording;

    public Recording Recording { get; }

    public string FileName => Recording.FileName;
    public string DateText => Recording.DateText;
    public string TimeText => Recording.TimeText;
    public string SizeText => Recording.FileSizeText;
    public string ArchivedText => Recording.ArchivedText;

    private bool _selected;
    /// <summary>Ticked into the build.</summary>
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    /// <summary>The re-extracted traces, once scanned. Null until then.</summary>
    public RecordingScan? Scan { get; set; }

    private double _fromSec, _toSec;
    public double FromSec { get => _fromSec; set { if (Set(ref _fromSec, value)) OnPropertyChanged(nameof(WindowText)); } }
    public double ToSec { get => _toSec; set { if (Set(ref _toSec, value)) OnPropertyChanged(nameof(WindowText)); } }

    public string WindowText => Scan is null ? "" : $"{FromSec:0.#}–{ToSec:0.#} s";

    private string _status = "not scanned";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _isOutlier;
    /// <summary>Set aside by the consistency check.</summary>
    public bool IsOutlier { get => _isOutlier; set => Set(ref _isOutlier, value); }

    private bool _keepAnyway;
    /// <summary>Operator overruled the consistency check for this recording.</summary>
    public bool KeepAnyway { get => _keepAnyway; set => Set(ref _keepAnyway, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Backs the Baseline Builder tab: pick recordings already on disk, pick the steady segment out of
/// each, and pool them into a Golden Run.
///
/// <para>It is the answer to the processes the live capture cannot serve — a recipe that ramps for
/// its first seconds, and one whose whole run is shorter than a capture window. Both need the
/// segment chosen <em>afterwards</em>, with the trace visible, which is not something you can do
/// standing in front of the tool with a stopwatch.</para>
///
/// <para>The arithmetic is <see cref="BaselineBuilder"/>'s, which is the engine's
/// (<see cref="RatioFrameSampling"/>). This class does the picking and the saying-what-happened,
/// and nothing else — a number that appears here and nowhere else would be a number nobody can
/// check against a live capture.</para>
/// </summary>
public sealed class BaselineBuilderViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LeakMonitorEngine _engine;
    private readonly LoggerViewModel _logger;
    private readonly string _defaultDataDirectory;
    private readonly SystemLogger? _log;

    private CancellationTokenSource? _scanCts;
    private bool _engineerPlus;
    private bool _busy;

    private const string WindowAnnotationTag = "window";

    public BaselineBuilderViewModel(LeakMonitorEngine engine, LoggerViewModel logger,
        string defaultDataDirectory, SystemLogger? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultDataDirectory = defaultDataDirectory ?? "";
        _log = log;

        Files = new ObservableCollection<BuilderFileViewModel>();
        // Straight to the fields: the properties rescan on set, and a rescan before the commands
        // exist walks into RaiseCanExec with every one of them still null. The single Refresh()
        // at the end of this constructor is the one that is meant to happen.
        _endDate = DateTime.Today;
        _startDate = DateTime.Today.AddDays(-14);

        TrendModel = NewTrendModel();

        RefreshCommand = new RelayCommand(Refresh, () => !_busy);
        ScanCommand = new RelayCommand(() => _ = ScanSelectedAsync(), () => !_busy && Files.Any(f => f.Selected));
        SuggestCommand = new RelayCommand(SuggestWindow, () => !_busy && SelectedFile?.Scan is not null);
        WholeFileCommand = new RelayCommand(UseWholeFile, () => !_busy && SelectedFile?.Scan is not null);
        BuildCommand = new RelayCommand(Build,
            () => !_busy && Files.Any(f => f.Selected && f.Scan is not null));
        SaveCommand = new RelayCommand(Save,
            () => _engineerPlus && !_busy && _result is { Accepted: true });

        Refresh();
    }

    // --- list ---------------------------------------------------------------

    public ObservableCollection<BuilderFileViewModel> Files { get; }

    private BuilderFileViewModel? _selectedFile;
    /// <summary>The recording whose traces are on the chart. Independent of the tick boxes:
    /// looking at one is not the same as building from it.</summary>
    public BuilderFileViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!Set(ref _selectedFile, value)) return;
            RedrawTrend();
            RaiseCanExec();
        }
    }

    private DateTime _startDate;
    public DateTime StartDate { get => _startDate; set { if (Set(ref _startDate, value)) Refresh(); } }

    private DateTime _endDate;
    public DateTime EndDate { get => _endDate; set { if (Set(ref _endDate, value)) Refresh(); } }

    public string EffectiveBaseDirectory =>
        LoggerSettings.ResolveBaseDirectory(_logger.ToSettings().BaseDirectory, _defaultDataDirectory);

    // --- output -------------------------------------------------------------

    public PlotModel TrendModel { get; }

    private string _runName = "";
    public string RunName { get => _runName; set { Set(ref _runName, value ?? ""); RaiseCanExec(); } }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _resultText = "";
    /// <summary>Everything the build has to say: what it accepted, what it refused and why, which
    /// recordings it set aside, and how far each one goes outside its own window.</summary>
    public string ResultText { get => _resultText; set => Set(ref _resultText, value); }

    private BaselineBuildResult? _result;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand SuggestCommand { get; }
    public RelayCommand WholeFileCommand { get; }
    public RelayCommand BuildCommand { get; }
    public RelayCommand SaveCommand { get; }

    /// <summary>
    /// Engineer+ may save. The tab itself is open to anyone, like Ratio Setup and Leak
    /// Calibration: reading a recording changes nothing, and an Operator who can see why a
    /// baseline was refused is an Operator who can say something useful about it.
    /// </summary>
    public void SetRole(bool engineerOrHigher)
    {
        _engineerPlus = engineerOrHigher;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NeedsSignIn));
        RaiseCanExec();
    }

    public bool CanSave => _engineerPlus;

    /// <summary>Shown beside the Save button when the signed-in role cannot use it — a disabled
    /// button with no reason beside it is the commonest way an operator concludes the app is broken.</summary>
    public bool NeedsSignIn => !_engineerPlus;

    // --- commands -----------------------------------------------------------

    public void Refresh()
    {
        var baseDir = EffectiveBaseDirectory;
        OnPropertyChanged(nameof(EffectiveBaseDirectory));
        Files.Clear();
        _result = null;
        ResultText = "";

        try
        {
            foreach (var rec in Recording.EnumerateSpectra(baseDir, StartDate, EndDate)
                                         .OrderByDescending(r => r.SessionStart))
            {
                var vm = new BuilderFileViewModel(rec);
                // Ticking a box changes what Scan and Build can do, and a button that stays grey
                // after you ticked the thing it needs reads as a broken button.
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(BuilderFileViewModel.Selected)
                                       or nameof(BuilderFileViewModel.KeepAnyway)) RaiseCanExec();
                };
                Files.Add(vm);
            }
            Status = Files.Count == 0
                ? $"No full-spectrum recordings in {baseDir} for the selected dates."
                : $"{Files.Count} recording(s) in {baseDir}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not list {baseDir}: {ex.Message}";
        }
        RaiseCanExec();
    }

    /// <summary>
    /// Re-extracts every ticked recording. This is the expensive step — every frame, every
    /// enabled ratio — so it is explicit rather than implicit in ticking a box: the operator
    /// decides when to spend it, and can see what it cost.
    /// </summary>
    private async Task ScanSelectedAsync()
    {
        var chosen = Files.Where(f => f.Selected).ToList();
        if (chosen.Count == 0) return;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        Busy = true;
        var settings = _engine.Settings;
        var lookup = WavelengthCalibration.Build(settings.WavelengthCorrections);
        var defs = settings.Ratios.Select(d => WavelengthCalibration.Correct(d, lookup)).ToList();
        var gate = new PlasmaGate(_logger.ToSettings());

        try
        {
            foreach (var file in chosen)
            {
                ct.ThrowIfCancellationRequested();
                Status = $"Scanning {file.FileName}…";
                file.Status = "scanning…";
                try
                {
                    var scan = await Task.Run(() =>
                    {
                        using var reader = file.Recording.OpenText();
                        var parsed = RecordingCsvParser.ReadFull(reader, ct)
                            ?? throw new InvalidOperationException("not a full-spectrum recording");
                        // Conditions from the sidecar written when the recording was made; null
                        // for anything older, in which case the fingerprint carries only the axis
                        // the CSV itself proves.
                        var acq = AcquisitionSidecar.TryRead(file.Recording);
                        return BaselineBuilder.Scan(file.Recording.FilePath, file.FileName,
                            file.Recording.SessionStart, parsed, defs, gate, acq, null, ct);
                    }, ct);

                    file.Scan = scan;
                    // Default to the whole recording rather than a guess: a window nobody chose
                    // should look like one nobody chose.
                    file.FromSec = scan.ElapsedSec.Length > 0 ? scan.ElapsedSec[0] : 0;
                    file.ToSec = scan.ElapsedSec.Length > 0 ? scan.ElapsedSec[^1] : 0;
                    file.Status = $"{scan.FrameCount} frames, {scan.DurationSeconds:0.#} s";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    file.Scan = null;
                    file.Status = $"failed: {ex.Message}";
                }
            }
            Status = $"Scanned {chosen.Count(f => f.Scan is not null)} of {chosen.Count} recording(s).";
            SelectedFile ??= chosen.FirstOrDefault(f => f.Scan is not null);
            RedrawTrend();
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        finally
        {
            Busy = false;
        }
    }

    private bool Busy
    {
        get => _busy;
        set { _busy = value; OnPropertyChanged(nameof(IsBusy)); RaiseCanExec(); }
    }

    public bool IsBusy => _busy;

    /// <summary>Offers the flattest windows this recording has. It can see that a stretch is
    /// steady, never that it is leak-free — which is why it fills the boxes rather than deciding.</summary>
    private void SuggestWindow()
    {
        var file = SelectedFile;
        if (file?.Scan is null) return;

        double want = Math.Min(_engine.Settings.GoldenRunCaptureSeconds, file.Scan.DurationSeconds);
        var picks = BaselineBuilder.Suggest(file.Scan, want);
        if (picks.Count == 0)
        {
            Status = "No steady stretch found — pick a window by dragging on the chart.";
            return;
        }
        var best = picks[0];
        file.FromSec = best.FromSec;
        file.ToSec = best.ToSec;
        Status = $"Flattest {want:0.#} s window: {best.FromSec:0.#}–{best.ToSec:0.#} s " +
                 $"({best.Frames} frames). Check it against the trace before building.";
        RedrawTrend();
    }

    private void UseWholeFile()
    {
        var file = SelectedFile;
        if (file?.Scan is null || file.Scan.FrameCount == 0) return;
        file.FromSec = file.Scan.ElapsedSec[0];
        file.ToSec = file.Scan.ElapsedSec[^1];
        RedrawTrend();
    }

    /// <summary>Called by the panel when the operator drags a window on the chart.</summary>
    public void SetWindow(double fromSec, double toSec)
    {
        var file = SelectedFile;
        if (file?.Scan is null) return;
        file.FromSec = Math.Min(fromSec, toSec);
        file.ToSec = Math.Max(fromSec, toSec);
        RedrawTrend();
        RaiseCanExec();
    }

    private void Build()
    {
        var picks = Files.Where(f => f.Selected && f.Scan is not null)
                         .Select(f => (f.Scan!, new SteadyWindow(f.FromSec, f.ToSec)))
                         .ToList();
        if (picks.Count == 0) return;

        var keep = Files.Where(f => f.KeepAnyway).Select(f => f.Recording.FilePath).ToList();
        var result = BaselineBuilder.Build(picks, new BaselineBuildOptions
        {
            RunName = string.IsNullOrWhiteSpace(RunName) ? "Recipe 1" : RunName.Trim(),
            ForceInclude = keep,
        });
        _result = result;

        var outlierPaths = result.Outliers.Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files) f.IsOutlier = outlierPaths.Contains(f.Recording.FilePath);

        ResultText = Describe(result);
        Status = result.Accepted
            ? $"Built {result.Run.Baselines.Count} baseline(s) from {result.Run.Source?.Files.Count ?? 0} recording(s)."
            : "Nothing to save — see below.";
        RaiseCanExec();
    }

    private string Describe(BaselineBuildResult r)
    {
        var sb = new System.Text.StringBuilder();
        if (r.Error.Length > 0) sb.AppendLine("Refused: " + r.Error).AppendLine();

        if (r.Run.Baselines.Count > 0)
        {
            sb.AppendLine($"Baselines ({r.Run.Baselines.Count}):");
            bool anyMarginal = false;
            foreach (var b in r.Run.Baselines)
            {
                string name = _engine.Settings.Ratios.FirstOrDefault(x => x.Key == b.Key)?.DisplayName ?? b.Key;
                double snr = b.Sigma > 0 ? b.Mean / b.Sigma : double.PositiveInfinity;
                bool marginal = snr < BaselineBuilder.MarginalMeanToSigma;
                anyMarginal |= marginal;
                sb.AppendLine($"  {name}: {Fmt(b.Mean)} ± {Fmt(b.Sigma)}  " +
                              $"(mean/σ {snr:0.#}, {b.SampleCount} frames)" +
                              (marginal ? "   << only just clears the floor" : ""));
            }
            if (anyMarginal)
                sb.AppendLine()
                  .AppendLine($"  A baseline barely over mean/σ {BaselineBuilder.MinBaselineMeanToSigma:0} " +
                              "has thresholds several times wider than a clean one, and looks exactly " +
                              "the same on the Leak Monitor tab. Usually it means the window took in " +
                              "something that was not steady — check the σ/mean figures below.");
            sb.AppendLine();
        }

        if (r.Rejected.Count > 0)
        {
            sb.AppendLine("No baseline for:");
            foreach (var x in r.Rejected) sb.AppendLine($"  {x.DisplayName} — {x.Reason}.");
            sb.AppendLine();
        }

        if (r.Outliers.Count > 0)
        {
            sb.AppendLine("Set aside by the consistency check (tick \"keep anyway\" to overrule):");
            foreach (var o in r.Outliers)
                sb.AppendLine($"  {System.IO.Path.GetFileName(o.Path)} — {o.Reason}.");
            sb.AppendLine();
        }

        if (r.Spread.Count > 0)
        {
            sb.AppendLine("How far the recordings disagree with each other (worst first):");
            foreach (var sp in r.Spread)
                sb.AppendLine($"  {sp.RatioDisplayName} — levels differ by {sp.RelativeSpread * 100:0.##} %, " +
                              $"{sp.SpreadOverSigma:0.#}× the scatter within a single recording " +
                              $"({sp.TypicalRelativeSigma * 100:0.##} %)");
            sb.AppendLine();
            sb.AppendLine("This is what inflates a pooled σ while every window looks steady on its own. " +
                          "A ratio divides the difference out — that is what its reference line is for — " +
                          "so a large figure here on an absolute-intensity entry usually means the runs " +
                          "were simply at different plasma brightness, and that entry wants a baseline " +
                          "from one recording rather than several.");
            sb.AppendLine();
        }

        if (r.Steadiness.Count > 0)
        {
            sb.AppendLine("How steady each chosen window is (worst ratio in it):");
            foreach (var st in r.Steadiness)
            {
                string file = System.IO.Path.GetFileName(st.Path);
                if (double.IsNaN(st.RelativeSigma))
                {
                    sb.AppendLine($"  {file} — no ratio produced a usable value in this window.");
                }
                else
                {
                    sb.AppendLine($"  {file} — σ/mean {st.RelativeSigma * 100:0.##} %, " +
                                  $"drift {st.RelativeDrift * 100:0.##} % on {st.RatioDisplayName}" +
                                  (st.WindowCoversWholeRecording ? "   << window is the whole recording" : ""));
                }
            }
            sb.AppendLine();
            sb.AppendLine("A steady plateau reads around 1 %. Ten times that means the window took in " +
                          "something the process was doing — drift says it was a ramp, σ alone says " +
                          "it was noise.");
            sb.AppendLine();
        }

        bool nothingOutside = r.Steadiness.Count > 0 && r.Steadiness.All(s => s.WindowCoversWholeRecording);
        if (r.BackChecks.Count > 0)
        {
            sb.AppendLine("Furthest each recording goes outside its own window, against this baseline:");
            foreach (var c in r.BackChecks)
                sb.AppendLine($"  {System.IO.Path.GetFileName(c.Path)} — {c.MaxSigmas:0.#} σ on " +
                              $"{c.RatioDisplayName} at {c.AtSeconds:0.#} s.");
            sb.AppendLine();
            sb.AppendLine("A large figure is not a fault by itself: outside the window is where the " +
                          "process changes, and on a recording that contained a leak it is the leak.");
        }
        else if (nothingOutside)
        {
            sb.AppendLine("No back-check: every window covers its whole recording, so there is nothing " +
                          "outside it to compare. That check is what would otherwise notice a window " +
                          "containing an excursion — with it unavailable, the σ/mean figures above are " +
                          "the only thing saying whether these windows are steady.");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Fmt(double v) =>
        Math.Abs(v) >= 1000 || (Math.Abs(v) < 0.001 && v != 0)
            ? v.ToString("G4", CultureInfo.InvariantCulture)
            : v.ToString("0.#####", CultureInfo.InvariantCulture);

    private void Save()
    {
        if (_result is not { Accepted: true }) return;
        var run = _result.Run;

        var outcome = _engine.ImportGoldenRun(run);
        if (outcome.NeedsConfirmation)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"Golden Run “{run.Name}” was built with {run.Baselines.Count} ratio " +
                      $"baseline(s). Saving it replaces the stored “{run.Name}”");
            if (outcome.Replaced is not null)
                sb.Append($" (captured {outcome.Replaced.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}, " +
                          $"{outcome.Replaced.Baselines.Count} baseline(s))");
            sb.AppendLine(", which cannot be undone.").AppendLine()
              .AppendLine("These ratio(s) have a baseline there and none here:");
            foreach (var l in outcome.Lost) sb.Append("• ").AppendLine(l.DisplayName);
            sb.AppendLine().Append("Replace the stored Golden Run?");

            var owner = Application.Current?.MainWindow;
            var answer = owner is not null
                ? MessageBox.Show(owner, sb.ToString(), "Replace Golden Run?",
                                  MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(sb.ToString(), "Replace Golden Run?",
                                  MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                Status = $"Golden Run “{run.Name}” discarded — the stored one is unchanged.";
                return;
            }
            _engine.ConfirmCapturedRun(run, keep: true);
        }

        _log?.LogSystemEvent(LogSeverity.Information, "GoldenRunBuiltOffline",
            $"Golden Run “{run.Name}” built from {run.Source?.Files.Count ?? 0} recording(s): " +
            $"{run.Baselines.Count} ratio baseline(s), {run.Source?.Excluded.Count ?? 0} recording(s) " +
            "set aside by the consistency check. It is now the active baseline.",
            related: $"GoldenRun={run.Name}",
            value: string.Join("; ", run.Source?.Files.Select(f =>
                $"{System.IO.Path.GetFileName(f.Path)} {f.FromUtc.ToLocalTime():HH:mm:ss}–{f.ToUtc.ToLocalTime():HH:mm:ss}")
                ?? Array.Empty<string>()));

        Status = $"Golden Run “{run.Name}” saved and made active.";
    }

    // --- chart --------------------------------------------------------------

    private static PlotModel NewTrendModel()
    {
        var model = new PlotModel
        {
            PlotAreaBorderColor = OxyColor.FromRgb(210, 210, 210),
            TextColor = OxyColors.Black,
        };
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Seconds from the start of the recording",
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(230, 230, 230),
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            // Each ratio divided by its own median over the whole recording, so lines of very
            // different magnitudes share one axis. It is not the Leak Monitor's % of baseline —
            // there is no baseline yet, which is the point of this tab.
            Title = "× own median over the recording",
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(230, 230, 230),
        });
        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.TopRight,
            LegendPlacement = LegendPlacement.Inside,
            LegendBackground = OxyColor.FromAColor(200, OxyColors.White),
        });
        return model;
    }

    private void RedrawTrend()
    {
        TrendModel.Series.Clear();
        TrendModel.Annotations.Clear();

        var file = SelectedFile;
        if (file?.Scan is null) { TrendModel.InvalidatePlot(true); return; }

        var scan = file.Scan;
        int colour = 0;
        foreach (var trace in scan.Traces.Values)
        {
            double median = MedianOf(trace);
            if (!(Math.Abs(median) > 0)) continue;
            var series = new LineSeries
            {
                Title = trace.DisplayName,
                Color = Palette[colour++ % Palette.Length],
                StrokeThickness = 1.2,
                LineStyle = LineStyle.Solid,
            };
            for (int i = 0; i < trace.Value.Length; i++)
            {
                if (!trace.Accepted[i]) continue;
                series.Points.Add(new DataPoint(scan.ElapsedSec[i], trace.Value[i] / median));
            }
            if (series.Points.Count > 0) TrendModel.Series.Add(series);
        }

        if (file.ToSec > file.FromSec)
            TrendModel.Annotations.Add(new RectangleAnnotation
            {
                Tag = WindowAnnotationTag,
                MinimumX = file.FromSec,
                MaximumX = file.ToSec,
                Fill = OxyColor.FromAColor(40, OxyColors.SteelBlue),
                Stroke = OxyColors.SteelBlue,
                StrokeThickness = 1,
                Text = "baseline window",
                Layer = AnnotationLayer.BelowSeries,
            });

        TrendModel.ResetAllAxes();
        TrendModel.InvalidatePlot(true);
    }

    private static double MedianOf(RatioTrace trace)
    {
        var v = new List<double>();
        for (int i = 0; i < trace.Value.Length; i++)
            if (trace.Accepted[i]) v.Add(trace.Value[i]);
        if (v.Count == 0) return 0;
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
    }

    private static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x1F, 0x77, 0xB4), OxyColor.FromRgb(0xD6, 0x27, 0x28),
        OxyColor.FromRgb(0x2C, 0xA0, 0x2C), OxyColor.FromRgb(0xFF, 0x7F, 0x0E),
        OxyColor.FromRgb(0x94, 0x67, 0xBD), OxyColor.FromRgb(0x8C, 0x56, 0x4B),
        OxyColor.FromRgb(0xE3, 0x77, 0xC2), OxyColor.FromRgb(0x17, 0xBE, 0xCF),
    };

    // --- plumbing -----------------------------------------------------------

    private void RaiseCanExec()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        SuggestCommand.RaiseCanExecuteChanged();
        WholeFileCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
