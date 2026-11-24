using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowGuard.UI.Services;
using FlowGuard.UI.ViewModels;
using Wpf.Ui.Controls;
using Wpf.Ui.Tray.Controls;

namespace FlowGuard.UI;

public partial class MainWindow : FluentWindow
{
    /// <summary>How long after the last pointer move over the list its order stays frozen.</summary>
    private const long PointerFreezeMs = 2500;

    /// <summary>How long after the last key press in a limit box the order stays frozen.</summary>
    private const long KeyFreezeMs = 5000;

    private readonly MainViewModel _vm;
    private bool _exiting;
    private bool _recenter;
    private int _openDropDowns;
    private long _lastPointerActivity;
    private long _lastKeyActivity;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        if (TrayIcon.Menu is { } menu)
            menu.DataContext = viewModel;

        viewModel.IsUserInteracting = IsUserInteracting;
        IsVisibleChanged += (_, _) =>
        {
            viewModel.IsWindowVisible = IsVisible;
            if (IsVisible)
                viewModel.Refresh();
        };
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        ContentRendered += (_, _) => Log.Info($"Window rendered (tray icon registered: {TrayIcon.IsRegistered})");
    }

    // ------------------------------------------------------------------ tray / window lifecycle

    /// <summary>
    /// Starts hidden. The tray icon only registers once it has been rendered, so the window is
    /// shown once off-screen and hidden straight after its first frame.
    /// </summary>
    public void StartInTray()
    {
        _recenter = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        ShowActivated = false;
        ShowInTaskbar = false;

        void HideAfterFirstFrame(object? sender, EventArgs e)
        {
            ContentRendered -= HideAfterFirstFrame;
            Hide();
            ShowInTaskbar = true;
            ShowActivated = true;
            Log.Info($"Started in the tray (tray icon registered: {TrayIcon.IsRegistered})");
        }

        ContentRendered += HideAfterFirstFrame;
        Show();
    }

    public void ShowFromTray()
    {
        if (_exiting)
            return;

        if (_recenter)
        {
            _recenter = false;
            Rect area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Activate();
        Topmost = true;  // reliably come to the front when restored from the tray
        Topmost = false;
        Focus();
    }

    /// <summary>Called by <see cref="App.ExitApplication"/>: lets the window close and removes the tray icon.</summary>
    public void PrepareForExit()
    {
        _exiting = true;
        TrayIcon.Unregister();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            _restoreState = WindowState;
        else if (_vm.CloseToTray)
            Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;

        e.Cancel = true;
        if (_vm.CloseToTray)
            Hide();
        else
            Dispatcher.BeginInvoke(App.Current.ExitApplication); // can't close from inside Closing
    }

    private void OnTrayLeftClick(NotifyIcon sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayPause(object sender, RoutedEventArgs e) => _vm.EngineEnabled = !_vm.EngineEnabled;

    private void OnTrayExit(object sender, RoutedEventArgs e) => App.Current.ExitApplication();

    // ------------------------------------------------------------------ list interaction

    private bool IsUserInteracting()
    {
        if (_openDropDowns > 0)
            return true;
        // Only while actually typing: a caret left in a limit box must not freeze the list forever.
        if (AppList.IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.Controls.TextBox &&
            Environment.TickCount64 - _lastKeyActivity < KeyFreezeMs)
            return true;
        return AppList.IsMouseOver && Environment.TickCount64 - _lastPointerActivity < PointerFreezeMs;
    }

    private void OnListPointerActivity(object sender, InputEventArgs e) => _lastPointerActivity = Environment.TickCount64;

    private void OnDropDownOpened(object? sender, EventArgs e) => _openDropDowns++;

    private void OnDropDownClosed(object? sender, EventArgs e) => _openDropDowns = Math.Max(0, _openDropDowns - 1);

    /// <summary>Enter commits a limit box, Escape reverts it.</summary>
    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TextBox box)
            return;
        _lastKeyActivity = Environment.TickCount64;

        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
            Keyboard.Focus(AppList);
            e.Handled = true;
        }
    }

    private static AppRowViewModel? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as AppRowViewModel;

    private void OnOpenFileLocation(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Path is { } path && File.Exists(path))
            OpenShell("explorer.exe", $"/select,\"{path}\"");
    }

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            Clipboard.SetText(row.ExeName);
    }

    private void OnResetRules(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { IsEditable: true } row)
            return;
        row.RankText = "";
        row.DownLimitText = "";
        row.UpLimitText = "";
    }

    private void OnToggleGame(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsApp: true } row)
            _vm.ToggleGame(row.ExeName);
    }

    private void OnToggleVideo(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsEditable: true } row)
            _vm.ToggleVideo(row.ExeName);
    }

    private void OnRemoveSite(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsSite: true } row)
            _vm.RemoveSiteKey(row.ExeName);
    }

    // ------------------------------------------------------------------ priority order (drag and drop)

    private Point _dragStart;
    private RankItem? _dragItem;

    private void OnRankMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        if (e.OriginalSource is DependencyObject source && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is null)
        {
            _dragStart = e.GetPosition(RankList);
            _dragItem = (FindAncestor<ListBoxItem>(source)?.DataContext) as RankItem;
        }
    }

    private void OnRankMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        Vector moved = e.GetPosition(RankList) - _dragStart;
        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;
        RankItem item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(RankList, item, DragDropEffects.Move);
    }

    private void OnRankDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(RankItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRankDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RankItem)) is not RankItem item)
            return;
        int from = _vm.RankItems.IndexOf(item);
        int to = e.OriginalSource is DependencyObject target && FindAncestor<ListBoxItem>(target)?.DataContext is RankItem over
            ? _vm.RankItems.IndexOf(over)
            : _vm.RankItems.Count - 1;
        _vm.MoveRank(from, to);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        string dir = Path.GetDirectoryName(_vm.SettingsFilePath)!;
        Directory.CreateDirectory(dir);
        OpenShell("explorer.exe", $"\"{dir}\"");
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_vm.LogFilePath))
            OpenShell("notepad.exe", $"\"{_vm.LogFilePath}\"");
    }

    private void OpenShell(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _vm.ShowError($"Couldn't open {exe}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>Renders the window content (over a dark backdrop, since Mica isn't captured) to a PNG.</summary>
    internal void ExpandOptions(int tab = 0)
    {
        OptionsExpander.IsExpanded = true;
        OptionsTabs.SelectedIndex = tab;
    }

    internal void RenderTo(string pngPath)
    {
        UpdateLayout();

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)Math.Ceiling(Root.ActualWidth * scale);
        int height = (int)Math.Ceiling(Root.ActualHeight * scale);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null,
                new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream fs = File.Create(pngPath);
        encoder.Save(fs);
        Log.Info($"Rendered window to {pngPath} (tray icon registered: {TrayIcon.IsRegistered}, rows: {_vm.Apps.Count})");
    }
}
