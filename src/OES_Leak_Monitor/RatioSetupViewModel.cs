using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace OES_Leak_Monitor;

/// <summary>
/// Backs the Ratio Setup tab — a staged editor for the species-ratio configuration.
/// Edits a working copy of <see cref="LeakMonitorEngine.Settings"/>'s ratios (up to
/// <see cref="MaxRatios"/>); Save persists them to <c>settings.json</c> but they only
/// take effect when OES acquisition is stopped and started again, so a mid-run edit
/// never disturbs a live leak evaluation.
/// </summary>
public sealed class RatioSetupViewModel : INotifyPropertyChanged
{
    /// <summary>Hard cap on the number of monitored ratios.</summary>
    public const int MaxRatios = 5;

    private readonly LeakMonitorEngine _engine;
    private readonly Action _persistSettings;
    private readonly SystemLogger? _log;

    private bool _engineerPlus;
    private bool _loading;

    public RatioSetupViewModel(LeakMonitorEngine engine, Action persistSettings,
        SystemLogger? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _persistSettings = persistSettings ?? throw new ArgumentNullException(nameof(persistSettings));
        _log = log;

        // One shared, species-grouped view of the line catalog feeds every line picker;
        // IsSynchronizedWithCurrentItem="False" on the combos keeps their selections
        // independent despite the shared source.
        var cvs = new CollectionViewSource { Source = SpectralLineCatalog.All };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SpectralLine.Species)));
        cvs.SortDescriptions.Add(new SortDescription(nameof(SpectralLine.Species), ListSortDirection.Ascending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(SpectralLine.WavelengthNm), ListSortDirection.Ascending));
        LineCatalog = cvs.View;

        Ratios = new ObservableCollection<RatioEditViewModel>();
        Ratios.CollectionChanged += OnRatiosCollectionChanged;

        AddRatioCommand    = new RelayCommand(AddRatio,    () => _engineerPlus && Ratios.Count < MaxRatios);
        RemoveRatioCommand = new RelayCommand(RemoveRatio, () => _engineerPlus && _selectedRatio is not null && Ratios.Count > 1);
        SaveCommand        = new RelayCommand(Save,        () => _engineerPlus && _isDirty);
        RevertCommand      = new RelayCommand(LoadFromEngine, () => _isDirty);

        LoadFromEngine();
    }

    public ObservableCollection<RatioEditViewModel> Ratios { get; }

    /// <summary>Species-grouped emission-line catalog shared by every line picker.</summary>
    public ICollectionView LineCatalog { get; }

    public RelayCommand AddRatioCommand    { get; }
    public RelayCommand RemoveRatioCommand { get; }
    public RelayCommand SaveCommand        { get; }
    public RelayCommand RevertCommand      { get; }

    private RatioEditViewModel? _selectedRatio;
    public RatioEditViewModel? SelectedRatio
    {
        get => _selectedRatio;
        set { if (Set(ref _selectedRatio, value)) RemoveRatioCommand.RaiseCanExecuteChanged(); }
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

    private string _statusMessage = "Edit the species ratios, then Save.";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public string RatioCountText => $"{Ratios.Count} of {MaxRatios} ratios";

    /// <summary>Propagates the signed-in role: only Engineer+ may add / remove / save.</summary>
    public void SetRole(bool engineerOrHigher)
    {
        _engineerPlus = engineerOrHigher;
        AddRatioCommand.RaiseCanExecuteChanged();
        RemoveRatioCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    // --- load / edit ---------------------------------------------------------

    /// <summary>(Re)loads the editor from the engine's current ratio definitions.</summary>
    public void LoadFromEngine()
    {
        _loading = true;
        foreach (var row in Ratios) row.PropertyChanged -= OnRatioEdited;
        Ratios.Clear();
        foreach (var def in _engine.Settings.Ratios)
        {
            var row = new RatioEditViewModel(def.Clone());
            row.PropertyChanged += OnRatioEdited;
            Ratios.Add(row);
        }
        _loading = false;

        SelectedRatio = Ratios.Count > 0 ? Ratios[0] : null;
        IsDirty = false;
        OnPropertyChanged(nameof(RatioCountText));
        AddRatioCommand.RaiseCanExecuteChanged();
        RemoveRatioCommand.RaiseCanExecuteChanged();
        StatusMessage = "Edit the species ratios, then Save.";
    }

    private void AddRatio()
    {
        if (Ratios.Count >= MaxRatios) return;
        // Sensible starting point: atomic-oxygen signal over the N2 reference.
        var template = new RatioDefinition
        {
            Numerator   = new LineRegion { CenterNm = 777.2, Mode = LineExtractMode.PeakHeight },
            Denominator = new LineRegion { CenterNm = 337.1, Mode = LineExtractMode.Integral },
        };
        var row = new RatioEditViewModel(template);
        row.PropertyChanged += OnRatioEdited;
        Ratios.Add(row);
        SelectedRatio = row;
        IsDirty = true;
    }

    private void RemoveRatio()
    {
        if (_selectedRatio is null || Ratios.Count <= 1) return;
        int at = Ratios.IndexOf(_selectedRatio);
        _selectedRatio.PropertyChanged -= OnRatioEdited;
        Ratios.Remove(_selectedRatio);
        SelectedRatio = Ratios.Count > 0 ? Ratios[Math.Min(at, Ratios.Count - 1)] : null;
        IsDirty = true;
    }

    private void OnRatiosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RatioCountText));
        AddRatioCommand.RaiseCanExecuteChanged();
        RemoveRatioCommand.RaiseCanExecuteChanged();
    }

    private void OnRatioEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        IsDirty = true;
    }

    private void Save()
    {
        if (Ratios.Count == 0)
        {
            StatusMessage = "Add at least one ratio before saving.";
            return;
        }
        foreach (var r in Ratios)
        {
            if (r.SignalEqualsReference)
            {
                SelectedRatio = r;
                StatusMessage = $"“{r.DisplayName}”: the signal and reference must be different lines.";
                return;
            }
        }

        var defs = Ratios.Select(r => r.ToDefinition()).ToList();
        _engine.Settings.Ratios = defs;

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
            "Saved. Stop OES acquisition, then press Start OES again to apply the new ratio configuration.";
        _log?.LogSystemEvent(LogSeverity.Information, "RatioSetupSaved",
            $"Species-ratio configuration saved ({defs.Count} ratio(s)) — applies on the next acquisition start.");
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
