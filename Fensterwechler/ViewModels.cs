using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fensterwechler;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null) : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>One row of the rotation plan: persisted data plus the window it is bound to in this session.</summary>
public sealed class RotationEntry(RotationEntryData data) : ObservableObject
{
    string status = "";
    bool isCurrent;
    ImageSource? icon;

    public RotationEntryData Data { get; } = data;
    public IntPtr Handle { get; private set; }
    public uint ProcessId { get; private set; }
    public bool IsBound => WindowActivator.IsAlive(Handle, ProcessId);
    public string? BindError { get; set; }

    public string Title => string.IsNullOrEmpty(Data.Title) ? Data.ProcessName : Data.Title;
    public string ProcessName => Data.ProcessName;
    public string DisplayName => $"\"{Title}\" ({Data.ProcessName})";

    public int DurationSeconds
    {
        get => Data.DurationSeconds;
        set
        {
            int clamped = Math.Clamp(value, RotationProfile.MinDuration, RotationProfile.MaxDuration);
            if (Data.DurationSeconds == clamped) { OnPropertyChanged(); return; } // re-sync the TextBox after clamping
            Data.DurationSeconds = clamped;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => Data.Enabled;
        set { if (Data.Enabled == value) return; Data.Enabled = value; OnPropertyChanged(); }
    }

    public string? TitleFilter
    {
        get => Data.TitleFilter;
        set { value = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); if (Data.TitleFilter == value) return; Data.TitleFilter = value; OnPropertyChanged(); }
    }

    public string Status { get => status; set => Set(ref status, value); }
    public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value); }
    public ImageSource? Icon { get => icon; private set => Set(ref icon, value); }

    public static RotationEntry FromWindow(WindowInfo window)
    {
        var entry = new RotationEntry(new RotationEntryData
        {
            Title = window.Title,
            ProcessName = window.ProcessName,
            ProcessPath = window.ProcessPath,
            ClassName = window.ClassName,
        });
        entry.Bind(window);
        return entry;
    }

    public void Bind(WindowInfo window)
    {
        Handle = window.Handle;
        ProcessId = window.ProcessId;
        Data.ProcessPath = window.ProcessPath ?? Data.ProcessPath;
        Data.ClassName = window.ClassName;
        Icon = window.Icon;
        RefreshTitle();
    }

    public void Unbind() => Handle = IntPtr.Zero;

    /// <summary>Keeps the saved title current (browser tabs change it), which helps re-matching after a restart.</summary>
    public void RefreshTitle()
    {
        string title = WindowDiscovery.GetTitle(Handle);
        if (title.Length == 0 || title == Data.Title) return;
        Data.Title = title;
        OnPropertyChanged(nameof(Title));
    }
}

public sealed record MonitorOption(string? DeviceName, string Label);

public sealed class MainViewModel : ObservableObject
{
    const int MaxLogEntries = 500;
    static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(1);

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly AppSettings settings;
    RotationProfile profile = new();
    List<WindowInfo>? snapshot;
    TimeSpan snapshotTime;
    string searchText = "";
    string statusText = "";
    string countdownText = "";
    string newProfileName = "";
    string? selectedProfileName;
    WindowInfo? selectedWindow;
    RotationEntry? selectedEntry;
    bool autoPausedByLock;
    bool loadingProfile;

    public MainViewModel(AppSettings settings)
    {
        this.settings = settings;
        Engine = new RotationEngine(Entries, ActivateEntry, e => WindowActivator.IsForeground(e.Handle));
        Engine.Changed += OnEngineChanged;

        LibraryView = CollectionViewSource.GetDefaultView(Library);
        LibraryView.Filter = o => o is WindowInfo w && (searchText.Length == 0
            || w.Title.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || w.ProcessName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase));

        Entries.CollectionChanged += OnEntriesChanged;
        Log.Written += entry => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogEntries.Insert(0, entry);
            while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(LogEntries.Count - 1);
        });

        RefreshCommand = new RelayCommand(RefreshLibrary);
        AddCommand = new RelayCommand(p => AddWindow(p as WindowInfo ?? SelectedWindow), p => (p as WindowInfo ?? SelectedWindow) != null);
        RemoveCommand = new RelayCommand(p => { if (p is RotationEntry e) Entries.Remove(e); }, p => p is RotationEntry);
        MoveUpCommand = new RelayCommand(p => Move(p as RotationEntry, -1), p => p is RotationEntry e && Entries.IndexOf(e) > 0);
        MoveDownCommand = new RelayCommand(p => Move(p as RotationEntry, +1), p => p is RotationEntry e && Entries.IndexOf(e) < Entries.Count - 1);
        AssignCommand = new RelayCommand(AssignSelectedWindow, () => SelectedEntry != null && SelectedWindow != null);
        StartCommand = new RelayCommand(Start, () => Engine.State == RotationState.Stopped && Entries.Any(e => e.Enabled));
        PauseResumeCommand = new RelayCommand(TogglePause, () => Engine.State != RotationState.Stopped);
        StopCommand = new RelayCommand(Stop, () => Engine.State != RotationState.Stopped);
        NextCommand = new RelayCommand(Next, () => Engine.State != RotationState.Stopped);
        SaveProfileCommand = new RelayCommand(SaveProfileNow);
        SaveProfileAsCommand = new RelayCommand(SaveProfileAs, () => Store.ValidateProfileName(NewProfileName) == null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => ProfileNames.Count > 1);
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as string == "logs" ? Log.Directory : AppPaths.Roaming));
        ClearLogCommand = new RelayCommand(LogEntries.Clear);

        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveProfileQuietly(); };
        timer.Tick += (_, _) => { Engine.Tick(Now); UpdateStatus(); };
        timer.Start();
    }

    public static TimeSpan Now => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public RotationEngine Engine { get; }
    public ObservableCollection<WindowInfo> Library { get; } = [];
    public ICollectionView LibraryView { get; }
    public ObservableCollection<RotationEntry> Entries { get; } = [];
    public ObservableCollection<string> ProfileNames { get; } = [];
    public ObservableCollection<MonitorOption> Monitors { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public IReadOnlyList<KeyValuePair<FailureStrategy, string>> FailureStrategies { get; } =
    [
        new(FailureStrategy.RetryThenSkip, "Erneut versuchen, dann überspringen"),
        new(FailureStrategy.Skip, "Sofort überspringen"),
    ];
    public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand AssignCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand SaveProfileAsCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ClearLogCommand { get; }

    public string SearchText
    {
        get => searchText;
        set { if (Set(ref searchText, value?.Trim() ?? "")) LibraryView.Refresh(); }
    }

    public WindowInfo? SelectedWindow { get => selectedWindow; set => Set(ref selectedWindow, value); }
    public RotationEntry? SelectedEntry { get => selectedEntry; set => Set(ref selectedEntry, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string CountdownText { get => countdownText; private set => Set(ref countdownText, value); }
    public string NewProfileName { get => newProfileName; set => Set(ref newProfileName, value); }
    public string? NewProfileNameError => string.IsNullOrEmpty(NewProfileName) ? null : Store.ValidateProfileName(NewProfileName);

    public bool IsRunning => Engine.State is RotationState.Running or RotationState.Waiting;
    public bool IsPaused => Engine.State == RotationState.Paused;
    public bool IsStopped => Engine.State == RotationState.Stopped;
    public string PauseResumeLabel => IsPaused ? "Fortsetzen" : "Pause";

    public string CycleText
    {
        get
        {
            var active = Entries.Where(e => e.Enabled).ToList();
            return active.Count == 0 ? "Keine aktiven Einträge" : $"{active.Count} aktiv · Zyklus {FormatDuration(TimeSpan.FromSeconds(active.Sum(e => e.DurationSeconds)))}";
        }
    }

    public string? SelectedProfileName
    {
        get => selectedProfileName;
        set
        {
            if (value == null || value == selectedProfileName || loadingProfile) return;
            LoadProfile(value);
        }
    }

    // ---- Profile options (saved with the profile) ----

    public string ProfileName => profile.Name;

    public MonitorOption? SelectedMonitor
    {
        get => Monitors.FirstOrDefault(m => m.DeviceName == profile.TargetMonitor);
        set { if (value == null || value.DeviceName == profile.TargetMonitor) return; profile.TargetMonitor = value.DeviceName; ProfileOptionChanged(); }
    }

    public FailureStrategy FailureStrategy
    {
        get => profile.FailureStrategy;
        set { profile.FailureStrategy = value; Engine.Strategy = value; ProfileOptionChanged(); }
    }

    public int RetryCount
    {
        get => profile.RetryCount;
        set { profile.RetryCount = Math.Clamp(value, 0, 10); Engine.RetryCount = profile.RetryCount; ProfileOptionChanged(); }
    }

    public bool AltKeyFallback
    {
        get => profile.AltKeyFallback;
        set { profile.AltKeyFallback = value; ProfileOptionChanged(); }
    }

    // ---- Global settings ----

    public bool AutostartEnabled
    {
        get { try { return Autostart.IsEnabled; } catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; } }
        set
        {
            try { Autostart.IsEnabled = value; Log.Info(value ? "Autostart aktiviert" : "Autostart deaktiviert"); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { Log.Error("Autostart konnte nicht geändert werden", ex); }
            OnPropertyChanged();
        }
    }

    public bool StartRotationOnLaunch
    {
        get => settings.StartRotationOnLaunch;
        set { settings.StartRotationOnLaunch = value; SaveSettings(); OnPropertyChanged(); }
    }

    public bool CloseToTray
    {
        get => settings.CloseToTray;
        set { settings.CloseToTray = value; SaveSettings(); OnPropertyChanged(); }
    }

    public LogLevel LogLevel
    {
        get => settings.LogLevel;
        set { settings.LogLevel = value; Log.Level = value; SaveSettings(); OnPropertyChanged(); }
    }

    // ---- Startup ----

    public void Initialize()
    {
        Log.Level = settings.LogLevel;
        RefreshMonitors();
        RefreshLibrary();
        foreach (var name in Store.ProfileNames()) ProfileNames.Add(name);

        string? name0 = settings.LastProfile is { } last && ProfileNames.Contains(last) ? last : ProfileNames.FirstOrDefault();
        if (name0 == null || !TryLoadProfile(name0, interactive: false))
        {
            ApplyProfile(new RotationProfile { Name = UniqueProfileName("Standard") });
            SaveProfileQuietly();
        }

        if (settings.StartRotationOnLaunch && Entries.Any(e => e.Enabled))
        {
            Log.Info($"Rotation wird automatisch gestartet (Profil \"{profile.Name}\")");
            Start();
        }
        UpdateStatus();
    }

    public void RefreshMonitors()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        Monitors.Clear();
        Monitors.Add(new MonitorOption(null, "Fenster nicht verschieben"));
        foreach (var s in screens)
            Monitors.Add(new MonitorOption(s.DeviceName, $"{s.DeviceName.TrimStart('\\', '.')} – {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (Hauptmonitor)" : "")}"));
        if (profile.TargetMonitor != null && screens.All(s => s.DeviceName != profile.TargetMonitor))
            Monitors.Add(new MonitorOption(profile.TargetMonitor, $"{profile.TargetMonitor.TrimStart('\\', '.')} (nicht verbunden)"));
        OnPropertyChanged(nameof(SelectedMonitor));
    }

    // ---- Library & rotation editing ----

    public void RefreshLibrary()
    {
        snapshot = null;
        var windows = Snapshot();
        var selected = SelectedWindow?.Handle;
        Library.Clear();
        foreach (var w in windows) Library.Add(w);
        SelectedWindow = Library.FirstOrDefault(w => w.Handle == selected);
        RebindAll();
    }

    List<WindowInfo> Snapshot()
    {
        if (snapshot == null || Now - snapshotTime > SnapshotMaxAge)
        {
            snapshot = WindowDiscovery.Enumerate();
            snapshotTime = Now;
        }
        return snapshot;
    }

    public void AddWindow(WindowInfo? window, int index = -1)
    {
        if (window == null) return;
        if (!WindowActivator.IsAlive(window.Handle, window.ProcessId))
        {
            Log.Warn($"Fenster \"{window.Title}\" existiert nicht mehr");
            RefreshLibrary();
            return;
        }
        var entry = RotationEntry.FromWindow(window);
        entry.Status = "Bereit";
        Entries.Insert(index < 0 || index > Entries.Count ? Entries.Count : index, entry);
        SelectedEntry = entry;
        Log.Info($"{entry.DisplayName} zur Rotation hinzugefügt");
    }

    public void MoveTo(RotationEntry entry, int index)
    {
        int from = Entries.IndexOf(entry);
        if (from < 0) return;
        if (index > from) index--; // the item itself is removed first
        index = Math.Clamp(index, 0, Entries.Count - 1);
        if (index != from) Entries.Move(from, index);
    }

    void Move(RotationEntry? entry, int delta)
    {
        if (entry == null) return;
        int from = Entries.IndexOf(entry);
        int to = Math.Clamp(from + delta, 0, Entries.Count - 1);
        if (from >= 0 && to != from) Entries.Move(from, to);
    }

    void AssignSelectedWindow()
    {
        if (SelectedEntry is not { } entry || SelectedWindow is not { } window) return;
        // An explicit choice wins over a filter that would immediately unbind it again.
        if (entry.TitleFilter is { } filter && !window.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)) entry.TitleFilter = null;
        entry.Bind(window);
        entry.Data.ProcessName = window.ProcessName;
        entry.Status = "Bereit";
        Log.Info($"{entry.DisplayName} neu zugeordnet");
        MarkDirty();
    }

    /// <summary>Re-attaches entries whose window is gone (closed, process restarted, program restarted).</summary>
    void RebindAll()
    {
        var unbound = Entries.Where(e => !e.IsBound).ToList();
        if (unbound.Count == 0) return;
        var windows = Snapshot();
        var reserved = Entries.Where(e => e.IsBound).Select(e => e.Handle).ToHashSet();
        // Exact title matches first for all entries, then weaker ones, so the result does not depend on list order.
        foreach (bool exactOnly in new[] { true, false })
        {
            foreach (var entry in unbound.Where(e => !e.IsBound).ToList())
            {
                var (window, ambiguous) = WindowMatcher.Match(entry.Data, windows, reserved, exactOnly);
                if (window != null)
                {
                    entry.Bind(window);
                    reserved.Add(window.Handle);
                    if (!entry.IsCurrent) entry.Status = "Bereit";
                    Log.Info($"{entry.DisplayName} zugeordnet");
                }
                else if (!exactOnly)
                {
                    entry.Unbind();
                    string status = ambiguous ? "Mehrdeutig – Titelfilter setzen oder Fenster zuordnen" : "Fenster nicht gefunden";
                    entry.BindError = status;
                    if (!entry.Status.StartsWith(status)) entry.Status = status; // keep "… – übersprungen" so the log is not repeated
                }
            }
        }
    }

    ActivationResult ActivateEntry(RotationEntry entry)
    {
        // A bound browser window may have navigated away from what the filter asks for.
        bool FilterOk() => entry.TitleFilter is not { } filter || WindowDiscovery.GetTitle(entry.Handle).Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (entry.IsBound && !FilterOk())
        {
            entry.Unbind();
            snapshot = null;
        }
        if (!entry.IsBound)
        {
            RebindAll();
            if (!entry.IsBound) return new(ActivationOutcome.Missing, entry.BindError ?? "Fenster nicht gefunden");
        }
        if (!FilterOk()) // the snapshot used for re-matching may be up to a second old
        {
            entry.Unbind();
            snapshot = null;
            return new(ActivationOutcome.Missing, "Fenster passt nicht zum Titelfilter");
        }
        var result = WindowActivator.Activate(entry.Handle, profile.TargetMonitor, profile.AltKeyFallback);
        if (result.Outcome == ActivationOutcome.Activated) entry.RefreshTitle();
        return result;
    }

    // ---- Rotation control ----

    public void Start()
    {
        autoPausedByLock = false;
        RebindAll();
        Engine.Start(Now);
        UpdateStatus();
    }

    public void Stop()
    {
        autoPausedByLock = false;
        Engine.Stop();
        UpdateStatus();
    }

    public void TogglePause()
    {
        autoPausedByLock = false;
        if (IsPaused) { Engine.Resume(Now); Log.Info("Rotation fortgesetzt"); }
        else { Engine.Pause(Now); Log.Info("Rotation pausiert"); }
        UpdateStatus();
    }

    public void Next()
    {
        Engine.Next(Now);
        UpdateStatus();
    }

    /// <summary>A locked session cannot show windows. Only an automatic pause is undone on unlock, never a manual one.</summary>
    public void OnSessionLockChanged(bool locked)
    {
        if (locked && IsRunning)
        {
            Engine.Pause(Now);
            autoPausedByLock = true;
            Log.Info("Sitzung gesperrt – Rotation angehalten");
        }
        else if (!locked && autoPausedByLock)
        {
            autoPausedByLock = false;
            Engine.Resume(Now);
            Log.Info("Sitzung entsperrt – Rotation fortgesetzt");
        }
        UpdateStatus();
    }

    void OnEngineChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(PauseResumeLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    void UpdateStatus()
    {
        var now = Now;
        var current = Engine.Current;
        StatusText = Engine.State switch
        {
            RotationState.Stopped => "Gestoppt",
            RotationState.Paused when autoPausedByLock => "Pausiert (Sitzung gesperrt)",
            RotationState.Paused => current == null ? "Pausiert" : $"Pausiert · {current.Title}",
            RotationState.Waiting => $"Wartet · {Engine.WaitReason}",
            _ => current == null ? "Läuft" : $"Läuft · {current.Title}",
        };
        CountdownText = Engine.State switch
        {
            RotationState.Stopped => "",
            RotationState.Waiting => $"Nächste Prüfung in {FormatDuration(Engine.Remaining(now))}",
            _ => NextEnabledAfter(current) is { } next
                ? $"Nächster Wechsel in {FormatDuration(Engine.Remaining(now))} → {next.Title}"
                : $"Nächster Wechsel in {FormatDuration(Engine.Remaining(now))}",
        };
    }

    RotationEntry? NextEnabledAfter(RotationEntry? current)
    {
        int start = current == null ? 0 : Entries.IndexOf(current) + 1;
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[(start + i) % Entries.Count];
            if (e.Enabled) return e;
        }
        return null;
    }

    public string TrayText
    {
        get
        {
            string text = $"Window Rotator – {StatusText}";
            return text.Length > 120 ? text[..120] + "…" : text; // NotifyIcon limit: 127 chars
        }
    }

    static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Ceiling(t.TotalSeconds):0} s" : $"{(int)t.TotalMinutes}:{t.Seconds:00} min";

    // ---- Profiles ----

    bool TryLoadProfile(string name, bool interactive)
    {
        try
        {
            ApplyProfile(Store.LoadProfile(name));
            Log.Info($"Profil \"{name}\" geladen ({Entries.Count} Einträge)");
            return true;
        }
        catch (InvalidDataException ex)
        {
            Log.Error(ex.Message);
            if (interactive) MessageBox.Show(ex.Message, "Profil laden", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void LoadProfile(string name)
    {
        var previous = Engine.State;
        if (!SaveProfileQuietly())
        {
            MessageBox.Show($"Das Profil \"{profile.Name}\" konnte nicht gespeichert werden. Der Profilwechsel wurde abgebrochen, damit keine Änderungen verloren gehen.\n\nDetails im Protokoll.",
                "Profil wechseln", MessageBoxButton.OK, MessageBoxImage.Warning);
            RevertProfileSelection();
            return;
        }
        Engine.Stop();
        if (!TryLoadProfile(name, interactive: true)) RevertProfileSelection();
        if (previous == RotationState.Stopped) return;
        Start();
        if (previous == RotationState.Paused) TogglePause(); // a manual pause survives the switch
    }

    // Deferred: WPF ignores change notifications raised while it is still writing the bound value.
    void RevertProfileSelection() => Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedProfileName)));

    void ApplyProfile(RotationProfile loaded)
    {
        loadingProfile = true;
        try
        {
            profile = loaded;
            Entries.Clear();
            foreach (var data in loaded.Entries) Entries.Add(new RotationEntry(data));
            Engine.Strategy = loaded.FailureStrategy;
            Engine.RetryCount = loaded.RetryCount;
            if (!ProfileNames.Contains(loaded.Name)) InsertProfileName(loaded.Name);
            selectedProfileName = loaded.Name;
            settings.LastProfile = loaded.Name;
            SaveSettings();
            RefreshMonitors();
            RebindAll();
            saveTimer.Stop();
        }
        finally { loadingProfile = false; }
        foreach (var name in new[] { nameof(SelectedProfileName), nameof(ProfileName), nameof(SelectedMonitor), nameof(FailureStrategy), nameof(RetryCount), nameof(AltKeyFallback), nameof(CycleText) })
            OnPropertyChanged(name);
    }

    void InsertProfileName(string name)
    {
        int i = 0;
        while (i < ProfileNames.Count && StringComparer.CurrentCultureIgnoreCase.Compare(ProfileNames[i], name) < 0) i++;
        ProfileNames.Insert(i, name);
    }

    string UniqueProfileName(string baseName)
    {
        string name = baseName;
        for (int i = 2; ProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase); i++) name = $"{baseName} {i}";
        return name;
    }

    void SaveProfileNow()
    {
        if (SaveProfileQuietly()) Log.Info($"Profil \"{profile.Name}\" gespeichert");
    }

    /// <summary>Changes are saved automatically, so a reboot never loses an unsaved rotation plan.</summary>
    public bool SaveProfileQuietly()
    {
        saveTimer.Stop();
        profile.Entries = Entries.Select(e => e.Data).ToList();
        try
        {
            Store.SaveProfile(profile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error($"Profil \"{profile.Name}\" konnte nicht gespeichert werden", ex);
            return false;
        }
    }

    void SaveProfileAs()
    {
        string name = NewProfileName.Trim();
        if (Store.ValidateProfileName(name) is { } error) { MessageBox.Show(error, "Profil speichern"); return; }
        if (ProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            && MessageBox.Show($"Profil \"{name}\" überschreiben?", "Profil speichern", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        name = ProfileNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name; // one file per name on a case-insensitive file system
        SaveProfileQuietly();
        string oldName = profile.Name;
        profile.Name = name;
        if (!SaveProfileQuietly())
        {
            profile.Name = oldName;
            MessageBox.Show($"Profil \"{name}\" konnte nicht gespeichert werden. Details im Protokoll.", "Profil speichern", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ProfileNames.Contains(name)) InsertProfileName(name);
        selectedProfileName = name;
        settings.LastProfile = name;
        SaveSettings();
        NewProfileName = "";
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(ProfileName));
        Log.Info($"Profil \"{name}\" gespeichert");
    }

    void DeleteProfile()
    {
        string name = profile.Name;
        if (MessageBox.Show($"Profil \"{name}\" endgültig löschen?", "Profil löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        saveTimer.Stop();
        try { Store.DeleteProfile(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Profil konnte nicht gelöscht werden", ex); return; }
        ProfileNames.Remove(name);
        Log.Info($"Profil \"{name}\" gelöscht");
        Engine.Stop();
        if (!TryLoadProfile(ProfileNames[0], interactive: true)) ApplyProfile(new RotationProfile { Name = UniqueProfileName("Standard") });
    }

    void MarkDirty()
    {
        if (loadingProfile) return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    void ProfileOptionChanged([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        MarkDirty();
    }

    void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (RotationEntry item in e.OldItems) item.PropertyChanged -= OnEntryPropertyChanged;
        if (e.NewItems != null) foreach (RotationEntry item in e.NewItems) item.PropertyChanged += OnEntryPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var item in Entries) { item.PropertyChanged -= OnEntryPropertyChanged; item.PropertyChanged += OnEntryPropertyChanged; }
        OnPropertyChanged(nameof(CycleText));
        MarkDirty();
    }

    void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RotationEntry.DurationSeconds) or nameof(RotationEntry.Enabled) or nameof(RotationEntry.TitleFilter))
        {
            OnPropertyChanged(nameof(CycleText));
            MarkDirty();
        }
        if (e.PropertyName == nameof(RotationEntry.TitleFilter) && sender is RotationEntry entry && !string.IsNullOrEmpty(entry.TitleFilter)
            && !WindowDiscovery.GetTitle(entry.Handle).Contains(entry.TitleFilter, StringComparison.OrdinalIgnoreCase))
        {
            entry.Unbind(); // current window does not satisfy the new filter any more
            snapshot = null;
            RebindAll();
        }
    }

    void SaveSettings()
    {
        try { Store.SaveSettings(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Einstellungen konnten nicht gespeichert werden", ex); }
    }

    static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Log.Error("Ordner konnte nicht geöffnet werden", ex); }
    }

    public void Shutdown()
    {
        timer.Stop();
        Engine.Stop();
        SaveProfileQuietly();
        SaveSettings();
    }
}
