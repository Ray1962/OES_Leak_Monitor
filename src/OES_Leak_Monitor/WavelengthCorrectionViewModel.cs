using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace OES_Leak_Monitor;

/// <summary>
/// One editable row of the wavelength-correction table: a catalog species, one of that
/// species' catalog wavelengths, and the additive drift offset (nm). The wavelength options
/// track the selected species. Maps to / from a <see cref="WavelengthCorrection"/>.
/// </summary>
public sealed class CorrectionRowViewModel : INotifyPropertyChanged
{
    public CorrectionRowViewModel(WavelengthCorrection src)
    {
        _species = !string.IsNullOrWhiteSpace(src.Species) &&
                   SpectralLineCatalog.Species.Contains(src.Species)
            ? src.Species
            : SpectralLineCatalog.Species.FirstOrDefault() ?? "";
        RebuildWavelengths();
        _wavelength = NearestOption(src.WavelengthNm);
        _offsetNm = src.OffsetNm;
    }

    /// <summary>Every catalog species, in catalog order — the species picker's source.</summary>
    public IReadOnlyList<string> SpeciesOptions => SpectralLineCatalog.Species;

    private string _species;
    public string Species
    {
        get => _species;
        set
        {
            if (!Set(ref _species, value)) return;
            RebuildWavelengths();
            Wavelength = WavelengthOptions.FirstOrDefault();
        }
    }

    /// <summary>Catalog wavelengths of the selected species — the wavelength picker's source.</summary>
    public ObservableCollection<double> WavelengthOptions { get; } = new();

    private double _wavelength;
    public double Wavelength { get => _wavelength; set => Set(ref _wavelength, value); }

    private double _offsetNm;
    public double OffsetNm { get => _offsetNm; set => Set(ref _offsetNm, value); }

    public WavelengthCorrection ToModel() => new()
    {
        Species = _species,
        WavelengthNm = _wavelength,
        OffsetNm = _offsetNm,
    };

    private void RebuildWavelengths()
    {
        WavelengthOptions.Clear();
        foreach (var line in SpectralLineCatalog.ForSpecies(_species))
            WavelengthOptions.Add(line.WavelengthNm);
    }

    /// <summary>Exact catalog wavelength if present, else the nearest, else 0.</summary>
    private double NearestOption(double wl)
    {
        if (WavelengthOptions.Count == 0) return 0;
        double best = WavelengthOptions[0];
        double bestErr = Math.Abs(best - wl);
        foreach (var w in WavelengthOptions)
        {
            double e = Math.Abs(w - wl);
            if (e < bestErr) { best = w; bestErr = e; }
        }
        return best;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
}

/// <summary>
/// Backs the Wavelength Calibration tab — a staged editor for the catalog-level wavelength-drift
/// correction overlay (<see cref="LeakMonitorSettings.WavelengthCorrections"/>). Each row corrects
/// one catalog <c>(species, wavelength)</c> line; the correction then applies to <em>every</em>
/// ratio (signal or reference) that uses that line. Save persists to <c>settings.json</c> but the
/// change only takes effect when OES acquisition is stopped and started again (it is applied by
/// <see cref="LeakMonitorEngine.ReloadRatios"/>, exactly like a Ratio Setup edit).
/// </summary>
public sealed class WavelengthCorrectionViewModel : INotifyPropertyChanged
{
    /// <summary>Hard cap on the number of stored corrections.</summary>
    public const int MaxCorrections = 50;

    /// <summary>Largest allowed absolute offset, nm — beyond this a "correction" is almost
    /// certainly a wrong catalog pick, and it would push the peak search off the real line.</summary>
    public const double MaxOffsetNm = 5.0;

    private readonly LeakMonitorEngine _engine;
    private readonly Action _persistSettings;
    private readonly SystemLogger? _log;

    private bool _engineerPlus;
    private bool _loading;

    public WavelengthCorrectionViewModel(LeakMonitorEngine engine, Action persistSettings,
        SystemLogger? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _persistSettings = persistSettings ?? throw new ArgumentNullException(nameof(persistSettings));
        _log = log;

        Corrections = new ObservableCollection<CorrectionRowViewModel>();
        Corrections.CollectionChanged += OnCorrectionsCollectionChanged;

        AddCommand    = new RelayCommand(Add,            () => _engineerPlus && Corrections.Count < MaxCorrections);
        RemoveCommand = new RelayCommand(Remove,         () => _engineerPlus && _selected is not null);
        SaveCommand   = new RelayCommand(Save,           () => _engineerPlus && _isDirty);
        RevertCommand = new RelayCommand(LoadFromEngine, () => _isDirty);

        LoadFromEngine();
    }

    public ObservableCollection<CorrectionRowViewModel> Corrections { get; }

    /// <summary>Raised after the overlay has been persisted, so other editors that display
    /// per-line offsets (the Ratio Setup line pickers) can pick up the new values.</summary>
    public event EventHandler? CorrectionsSaved;

    public RelayCommand AddCommand    { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand SaveCommand   { get; }
    public RelayCommand RevertCommand { get; }

    private CorrectionRowViewModel? _selected;
    public CorrectionRowViewModel? SelectedCorrection
    {
        get => _selected;
        set { if (Set(ref _selected, value)) RemoveCommand.RaiseCanExecuteChanged(); }
    }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!Set(ref _isDirty, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
            RevertCommand.RaiseCanExecuteChanged();
        }
    }

    private string _statusMessage = "Add a per-line wavelength correction, then Save.";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public string CountText => $"{Corrections.Count} correction(s)";

    /// <summary>Propagates the signed-in role: only Engineer+ may add / remove / save.</summary>
    public void SetRole(bool engineerOrHigher)
    {
        _engineerPlus = engineerOrHigher;
        AddCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    // --- load / edit ---------------------------------------------------------

    /// <summary>(Re)loads the editor from the engine's current correction overlay.</summary>
    public void LoadFromEngine()
    {
        _loading = true;
        foreach (var row in Corrections) row.PropertyChanged -= OnRowEdited;
        Corrections.Clear();
        foreach (var c in _engine.Settings.WavelengthCorrections)
        {
            var row = new CorrectionRowViewModel(c.Clone());
            row.PropertyChanged += OnRowEdited;
            Corrections.Add(row);
        }
        _loading = false;

        SelectedCorrection = Corrections.Count > 0 ? Corrections[0] : null;
        IsDirty = false;
        OnPropertyChanged(nameof(CountText));
        AddCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        StatusMessage = "Add a per-line wavelength correction, then Save.";
    }

    private void Add()
    {
        if (Corrections.Count >= MaxCorrections) return;
        // Friendly starting point: atomic-oxygen 777.2 nm if present, else the first catalog line.
        var seed = new WavelengthCorrection
        {
            Species = SpectralLineCatalog.Species.Contains("O")
                ? "O"
                : SpectralLineCatalog.Species.FirstOrDefault() ?? "",
            WavelengthNm = 777.2,
            OffsetNm = 0,
        };
        var row = new CorrectionRowViewModel(seed);
        row.PropertyChanged += OnRowEdited;
        Corrections.Add(row);
        SelectedCorrection = row;
        IsDirty = true;
    }

    private void Remove()
    {
        if (_selected is null) return;
        int at = Corrections.IndexOf(_selected);
        _selected.PropertyChanged -= OnRowEdited;
        Corrections.Remove(_selected);
        SelectedCorrection = Corrections.Count > 0 ? Corrections[Math.Min(at, Corrections.Count - 1)] : null;
        IsDirty = true;
    }

    private void OnCorrectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CountText));
        AddCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    private void OnRowEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        IsDirty = true;
    }

    private void Save()
    {
        // Range check — an offset larger than the peak-search window would defeat the whole point.
        foreach (var row in Corrections)
        {
            if (double.IsNaN(row.OffsetNm) || Math.Abs(row.OffsetNm) > MaxOffsetNm)
            {
                SelectedCorrection = row;
                StatusMessage = $"“{row.Species} {row.Wavelength:0.#}”: offset must be between " +
                                $"−{MaxOffsetNm:0.#} and +{MaxOffsetNm:0.#} nm.";
                return;
            }
        }

        // Reject duplicate (species, wavelength) rows — the last one would silently win the lookup.
        var dup = Corrections
            .GroupBy(r => (r.Species, Math.Round(r.Wavelength, 3)))
            .FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            SelectedCorrection = dup.Last();
            StatusMessage = $"“{dup.Key.Species} {dup.Key.Item2:0.#}” is listed more than once — " +
                            "keep one correction per line.";
            return;
        }

        var models = Corrections.Select(r => r.ToModel()).ToList();
        _engine.Settings.WavelengthCorrections = models;

        try
        {
            _persistSettings();
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
            return;
        }

        IsDirty = false;
        StatusMessage =
            "Saved. Stop OES acquisition, then press Start OES again to apply the wavelength corrections.";
        _log?.LogSystemEvent(LogSeverity.Information, "WavelengthCorrectionsSaved",
            $"Wavelength-correction overlay saved ({models.Count} line(s)) — applies on the next acquisition start.");
        CorrectionsSaved?.Invoke(this, EventArgs.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
}
