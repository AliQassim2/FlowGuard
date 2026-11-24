#if DEBUG
using System.Windows.Threading;
using FlowGuard.UI.Services;
using FlowGuard.UI.ViewModels;

namespace FlowGuard.UI;

/// <summary>
/// Debug-build-only command line hooks for automated smoke tests:
/// <list type="bullet">
/// <item><c>--render &lt;file.png&gt;</c> renders the window after <c>--delay &lt;ms&gt;</c> (default 4000), then quits.</item>
/// <item><c>--expand-options</c> opens the settings card; <c>--show-idle</c> lists idle apps.</item>
/// <item><c>--expand-options [tab]</c> opens the settings card on that tab (0-5).</item>
/// <item><c>--set-limit &lt;exe&gt; &lt;text&gt;</c> / <c>--set-priority &lt;exe&gt; &lt;1-128 or empty&gt;</c>
///   edit that app's row (through the same view-model path as typing) after 2 s.</item>
/// <item><c>--exit-after &lt;ms&gt;</c> quits (gracefully) after that long.</item>
/// </list>
/// </summary>
internal static class DebugHooks
{
    public static void Install(string[] args, MainWindow window)
    {
        var vm = (MainViewModel)window.DataContext;
        string? render = Value(args, "--render");
        int delay = int.TryParse(Value(args, "--delay"), out int d) ? d : 4000;
        int exitAfter = int.TryParse(Value(args, "--exit-after"), out int e) ? e : -1;

        if (args.Contains("--show-idle"))
            vm.ShowIdle = true;
        if (args.Contains("--expand-options"))
            window.ExpandOptions(int.TryParse(Value(args, "--expand-options"), out int tab) ? tab : 0);

        (string Exe, string Text)? limit = Pair(args, "--set-limit");
        (string Exe, string Text)? priority = Pair(args, "--set-priority");
        if (limit is not null || priority is not null)
        {
            After(2000, () =>
            {
                vm.Refresh();
                if (limit is { } l)
                {
                    AppRowViewModel? row = Find(vm, l.Exe);
                    if (row is null) Log.Info($"Debug: no row for {l.Exe}");
                    else row.DownLimitText = l.Text;
                }
                if (priority is { } p)
                {
                    AppRowViewModel? row = Find(vm, p.Exe);
                    if (row is null) Log.Info($"Debug: no row for {p.Exe}");
                    else row.RankText = p.Text;
                }
            });
        }

        if (render is not null)
        {
            After(delay, () =>
            {
                if (!window.IsVisible)
                    window.ShowFromTray();
                vm.Refresh();
                window.RenderTo(render);
                if (exitAfter < 0)
                    App.Current.ExitApplication();
            });
        }

        if (exitAfter >= 0)
            After(exitAfter, App.Current.ExitApplication);
    }

    private static AppRowViewModel? Find(MainViewModel vm, string exe)
        => vm.Apps.FirstOrDefault(r => string.Equals(r.ExeName, exe, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static (string, string)? Pair(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 2 < args.Length ? (args[i + 1], args[i + 2]) : null;
    }

    private static void After(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action(); }
            catch (Exception ex) { Log.Error("Debug hook", ex); }
        };
        timer.Start();
    }
}
#endif
