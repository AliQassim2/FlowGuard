using System.Windows;
using System.Windows.Threading;
using FlowGuard.Core;
using FlowGuard.Core.Settings;
using FlowGuard.UI.Services;
using FlowGuard.UI.ViewModels;

namespace FlowGuard.UI;

public partial class App : Application
{
    private SingleInstance? _instance;
    private FlowGuardService? _service;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _exiting;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            Shutdown(); // the running instance has been asked to show itself
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled (fatal)", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };

        Log.Info($"FlowGuard starting (pid {Environment.ProcessId}, args: {string.Join(' ', e.Args)})");

        try
        {
            Initialize(e);
        }
        catch (Exception ex)
        {
            // Without a window there is nothing to recover into: report, clean up, quit.
            Log.Error("Startup", ex);
            MessageBox.Show($"FlowGuard couldn't start.\n\n{ex.Message}\n\nDetails: {Log.FilePath}",
                "FlowGuard", MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            try { _service?.Dispose(); } catch { /* best effort */ }
            _instance.Dispose();
            _instance = null;
            Shutdown(1);
        }
    }

    private void Initialize(StartupEventArgs e)
    {
        // Diagnostics: FLOWGUARD_FILTER narrows what is captured (a WinDivert filter), so a test
        // can run without touching any other traffic on the machine.
        string? filter = Environment.GetEnvironmentVariable("FLOWGUARD_FILTER");
        var engine = string.IsNullOrWhiteSpace(filter) ? new FlowGuard.Core.Engine.PacketEngineOptions()
            : new FlowGuard.Core.Engine.PacketEngineOptions { Filter = filter };
        if (!string.IsNullOrWhiteSpace(filter))
            Log.Info($"Capture filter override: {filter}");
        _service = new FlowGuardService(new FlowGuardOptions { SettingsPath = SettingsStore.DefaultPath, Engine = engine });
        Log.Info($"Core ready ({_service.GetLimits().Count} limits, {_service.GetRanks().Count} priorities, {_service.GetTrackedSites().Count} sites, {_service.GetGames().Count} games loaded; line {_service.Line.Down / 1024} KB/s)");
        _viewModel = new MainViewModel(_service, Dispatcher);
        _window = new MainWindow(_viewModel);
        MainWindow = _window;
        Log.Info("Window created");

        bool minimized = _service.UiSettings.StartMinimized || e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (minimized)
            _window.StartInTray();
        else
            _window.Show();

        _viewModel.Start();
        _instance!.Listen(() => Dispatcher.BeginInvoke(() => _window?.ShowFromTray()));

#if DEBUG
        DebugHooks.Install(e.Args, _window);
#endif
    }

    /// <summary>Stops capture (so traffic flows normally again), saves settings and quits.</summary>
    public void ExitApplication()
    {
        if (_exiting)
            return;
        _exiting = true;
        Log.Info("FlowGuard exiting");

        try { _window?.PrepareForExit(); } catch (Exception ex) { Log.Error("Tray cleanup", ex); }
        try { _viewModel?.Dispose(); } catch (Exception ex) { Log.Error("View model cleanup", ex); }
        try { _service?.Dispose(); } catch (Exception ex) { Log.Error("Service shutdown", ex); }
        try { _window?.Close(); } catch (Exception ex) { Log.Error("Window close", ex); }
        _instance?.Dispose();
        _instance = null;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        ExitApplication(); // sign-out / shutdown: flush settings and close the driver handle
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && _service is not null)
        {
            try { _service.Dispose(); } catch { /* best effort */ }
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        _viewModel?.ShowError("Unexpected error", e.Exception.Message);
        e.Handled = true; // keep running: the core is independent of the UI thread
    }
}
