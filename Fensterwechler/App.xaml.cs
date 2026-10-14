using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Fensterwechler;

public partial class App : Application
{
    const string MutexName = @"Local\WindowRotator";
    const string ShowEventName = @"Local\WindowRotator.Show";

    Mutex? mutex;
    EventWaitHandle? showEvent;
    NotifyIcon? tray;
    ToolStripMenuItem? startPauseItem, nextItem, stopItem;
    MainViewModel? vm;
    MainWindow? window;
    bool exiting, trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool trayOnly = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        // Opened before the mutex by every instance, so even a near-simultaneous second start can signal;
        // the auto-reset event keeps the signal until the first instance has finished initialising.
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Second start: bring the running instance to the front (it may take the foreground) and quit.
            if (!trayOnly)
            {
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                showEvent.Set();
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) => { Log.Error("Unerwarteter Fehler", args.Exception); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Schwerwiegender Fehler", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unbeobachteter Fehler", args.Exception); args.SetObserved(); };
        SessionEnding += (_, _) => ExitApplication(); // Windows logoff/shutdown: save before the process is killed

        try
        {
            var settings = Store.LoadSettings();
            Log.Level = settings.LogLevel;
            Log.PruneOldFiles();
            Log.Info($"Window Rotator {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} gestartet{(trayOnly ? " (Infobereich)" : "")}");

            vm = new MainViewModel(settings);
            window = new MainWindow { DataContext = vm };
            window.Closing += OnWindowClosing;
            CreateTray();
            vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.StatusText) && tray != null) tray.Text = vm.TrayText; };
            vm.Initialize();
        }
        catch (Exception ex)
        {
            // Without UI and tray the process would linger invisibly and block every further start via the mutex.
            Log.Error("Start fehlgeschlagen", ex);
            MessageBox.Show($"Der Window Rotator konnte nicht gestartet werden:\n\n{ex.Message}", "Window Rotator", MessageBoxButton.OK, MessageBoxImage.Error);
            if (tray != null) tray.Visible = false;
            Shutdown(1);
            return;
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        new Thread(() =>
        {
            while (showEvent.WaitOne() && !exiting) Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "SingleInstanceListener" }.Start();

        if (!trayOnly) ShowMainWindow();
    }

    void CreateTray()
    {
        var openItem = new ToolStripMenuItem("Steuerung öffnen", null, (_, _) => ShowMainWindow());
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        startPauseItem = new ToolStripMenuItem("Rotation starten", null, (_, _) => { if (vm!.IsStopped) vm.Start(); else vm.TogglePause(); });
        nextItem = new ToolStripMenuItem("Nächstes Fenster", null, (_, _) => vm!.Next());
        stopItem = new ToolStripMenuItem("Rotation beenden", null, (_, _) => vm!.Stop());
        var exitItem = new ToolStripMenuItem("Programm schließen", null, (_, _) => ExitFromTray());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([openItem, new ToolStripSeparator(), startPauseItem, nextItem, stopItem, new ToolStripSeparator(), exitItem]);
        menu.Opening += (_, _) => UpdateTrayMenu();

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/WindowRotator;component/Assets/app.ico")).Stream;
        tray = new NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, SystemInformation.SmallIconSize),
            Text = "Window Rotator",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseDoubleClick += (_, args) => { if (args.Button == MouseButtons.Left) ShowMainWindow(); };
    }

    void UpdateTrayMenu()
    {
        if (vm == null) return;
        startPauseItem!.Text = vm.IsStopped ? "Rotation starten" : vm.IsPaused ? "Rotation fortsetzen" : "Rotation pausieren";
        startPauseItem.Enabled = !vm.IsStopped || vm.StartCommand.CanExecute(null);
        nextItem!.Enabled = !vm.IsStopped;
        stopItem!.Enabled = !vm.IsStopped;
    }

    void ShowMainWindow()
    {
        if (window == null || exiting) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (!vm!.CloseToTray)
        {
            Dispatcher.BeginInvoke(ExitApplication); // Close() must not be called again from inside Closing
            return;
        }
        window!.Hide();
        if (!trayHintShown)
        {
            trayHintShown = true;
            tray?.ShowBalloonTip(4000, "Window Rotator läuft weiter",
                "Die Steuerung ist im Infobereich verfügbar: Doppelklick öffnet sie, Rechtsklick zeigt alle Aktionen.", ToolTipIcon.Info);
        }
    }

    void ExitFromTray()
    {
        if (vm != null && !vm.IsStopped
            && MessageBox.Show("Die Rotation läuft. Programm wirklich vollständig schließen?\n\nZum Anhalten ohne Beenden „Rotation beenden“ verwenden.",
                "Window Rotator", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        ExitApplication();
    }

    void ExitApplication()
    {
        if (exiting) return;
        exiting = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        vm?.Shutdown();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        window?.Close();
        Log.Info("Programm beendet");
        showEvent?.Set(); // release the listener thread
        Shutdown();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
            Dispatcher.BeginInvoke(() => vm?.OnSessionLockChanged(e.Reason == SessionSwitchReason.SessionLock));
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Log.Info("Bildschirmkonfiguration geändert");
            vm?.RefreshMonitors();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (tray != null) tray.Visible = false; // never leave a ghost icon behind
        base.OnExit(e);
    }
}
