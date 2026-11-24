using FlowGuard.Core.Processes;
using FlowGuard.Core.Qos;
using FlowGuard.Core.Settings;
using FlowGuard.Core.Shaping;
using FlowGuard.Core.Stats;

namespace FlowGuard.Core.Tests;

public class QosControllerTests : IDisposable
{
    private const long Hz = 1000; // 1 tick = 1 ms
    private const double KB = 1024;
    private const long LineDown = 7000 * 1024;
    private const long LineUp = 1000 * 1024;

    private readonly TrafficShaper _shaper = new(new ProcessTable(pid => new ProcessIdentity(pid, "x.exe", null, 0)));
    private readonly QosController _qos;

    public QosControllerTests()
    {
        _qos = new QosController(_shaper, new QosOptions
        {
            HoldTime = TimeSpan.FromSeconds(5),
            MinBytesPerSecond = 50 * 1024,
            LineUtilization = 0.92,
            Headroom = 0.12,
        }, ticksPerSecond: Hz);

        _qos.SetRank("chrome", 2);
        _qos.SetRank("steam.exe", 4);
    }

    public void Dispose() => _shaper.Dispose();

    private static Dictionary<string, KeyRates> Rates(params (string key, double downKBps)[] r)
        => r.ToDictionary(x => x.key, x => new KeyRates(x.downKBps * KB, 0), StringComparer.OrdinalIgnoreCase);

    private QosStatus Eval(long t, params (string key, double downKBps)[] r)
        => _qos.Evaluate(Rates(r), t, LineDown, LineUp);

    private double CapKB(string key) => (_shaper.FindGroup(key)?.EffectiveLimit.DownBytesPerSecond ?? 0) / KB;

    private bool Uncapped(string key) => _shaper.FindGroup(key)?.EffectiveLimit.IsUnlimited ?? true;

    // ------------------------------------------------------------ priority numbers

    [Fact]
    public void A_lone_download_gets_the_whole_line()
    {
        QosStatus s = Eval(0, ("steam.exe", 7000), ("chrome.exe", 5));
        Assert.False(s.IsThrottling);
        Assert.True(Uncapped("steam.exe"));
    }

    [Fact]
    public void Lower_priority_gets_what_is_left_minus_headroom()
    {
        // 7000 × 0.92 − 3000 (Chrome) − 7000 × 0.12 (room for Chrome to grow) = 2600 KB/s
        QosStatus s = Eval(0, ("chrome.exe", 3000), ("steam.exe", 3800));
        Assert.True(s.IsThrottling);
        Assert.Equal(["chrome.exe"], s.TriggeringKeys);
        Assert.Equal(["steam.exe"], s.ThrottledKeys);
        Assert.Equal(2600, CapKB("steam.exe"), 1);
        Assert.True(Uncapped("chrome.exe"));
    }

    [Fact]
    public void A_starved_important_app_still_frees_room_for_itself()
    {
        // Steam fills the line, Chrome only manages 30 KB/s: Steam drops to leave headroom.
        Eval(0, ("chrome.exe", 30), ("steam.exe", 7000));
        Assert.Equal(7000 * 0.92 - 30 - 7000 * 0.12, CapKB("steam.exe"), 1);
    }

    [Fact]
    public void Same_number_shares_fairly()
    {
        _qos.SetRank("qbittorrent.exe", 4);
        Eval(0, ("chrome.exe", 1000), ("steam.exe", 4000), ("qbittorrent.exe", 2000));
        // (6440 − 1000 − 840) / 2 each
        Assert.Equal(2300, CapKB("steam.exe"), 1);
        Assert.Equal(2300, CapKB("qbittorrent.exe"), 1);
    }

    [Fact]
    public void Priorities_cascade_down_the_numbers()
    {
        _qos.SetRank("idm.exe", 3);
        Eval(0, ("chrome.exe", 1000), ("idm.exe", 2000), ("steam.exe", 3000));
        Assert.Equal(6440 - 1000 - 840, CapKB("idm.exe"), 1);             // below Chrome
        Assert.Equal(6440 - 1000 - 2000 - 840, CapKB("steam.exe"), 1);    // below Chrome and IDM
    }

    [Fact]
    public void Apps_without_a_number_are_left_alone_but_their_use_counts()
    {
        Eval(0, ("chrome.exe", 1000), ("discord.exe", 1000), ("steam.exe", 4000));
        Assert.True(Uncapped("discord.exe"));
        Assert.True(Uncapped("chrome.exe"));
        Assert.Equal(6440 - 1000 - 1000 - 840, CapKB("steam.exe"), 1);
    }

    [Fact]
    public void Nothing_is_slowed_below_the_minimum()
    {
        Eval(0, ("chrome.exe", 7000), ("steam.exe", 500));
        Assert.Equal(50, CapKB("steam.exe"), 1);
    }

    [Fact]
    public void Priority_holds_after_the_important_app_goes_quiet_then_releases()
    {
        Eval(0, ("chrome.exe", 1000), ("steam.exe", 4000));
        Assert.False(Uncapped("steam.exe"));
        Eval(4_900, ("chrome.exe", 0), ("steam.exe", 4000));
        Assert.False(Uncapped("steam.exe"));                              // within the 5 s hold
        QosStatus s = Eval(5_100, ("chrome.exe", 0), ("steam.exe", 4000));
        Assert.False(s.IsThrottling);
        Assert.True(Uncapped("steam.exe"));
    }

    [Fact]
    public void Unknown_line_speed_caps_nothing()
    {
        QosStatus s = _qos.Evaluate(Rates(("chrome.exe", 1000), ("steam.exe", 4000)), 0, 0, 0);
        Assert.False(s.IsThrottling);
        Assert.True(Uncapped("steam.exe"));
    }

    [Fact]
    public void Turning_sharing_off_lifts_every_cap()
    {
        Eval(0, ("chrome.exe", 1000), ("steam.exe", 4000));
        _qos.Options = _qos.Options with { Enabled = false };
        QosStatus s = Eval(250, ("chrome.exe", 1000), ("steam.exe", 4000));
        Assert.False(s.IsThrottling);
        Assert.True(Uncapped("steam.exe"));
    }

    [Fact]
    public void Removing_a_number_releases_the_app()
    {
        Eval(0, ("chrome.exe", 1000), ("steam.exe", 4000));
        _qos.SetRank("steam.exe", Rank.None);
        Eval(250, ("chrome.exe", 1000), ("steam.exe", 4000));
        Assert.True(Uncapped("steam.exe"));
        Assert.Equal(Rank.None, _qos.GetRank("steam.exe"));
    }

    [Fact]
    public void Small_changes_do_not_churn_the_shaper()
    {
        Eval(0, ("chrome.exe", 3000), ("steam.exe", 3800));
        double first = CapKB("steam.exe");
        Eval(250, ("chrome.exe", 3100), ("steam.exe", 3800));             // wants 2500: within 8 %
        Assert.Equal(first, CapKB("steam.exe"));
        Eval(500, ("chrome.exe", 4000), ("steam.exe", 3800));             // wants 1600: re-applied
        Assert.Equal(1600, CapKB("steam.exe"), 1);
    }

    [Fact]
    public void A_stricter_hard_limit_wins()
    {
        _shaper.SetHardLimit("steam.exe", RateLimit.FromKBps(500, 0));
        Eval(0, ("chrome.exe", 1000), ("steam.exe", 500));
        Assert.Equal(500, CapKB("steam.exe"));
        Eval(10_000, ("chrome.exe", 0), ("steam.exe", 500));
        Assert.Equal(RateLimit.FromKBps(500, 0), _shaper.FindGroup("steam.exe")!.EffectiveLimit);
    }

    [Fact]
    public void Upload_is_shared_separately()
    {
        var rates = new Dictionary<string, KeyRates>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = new(0, 300 * KB),
            ["steam.exe"] = new(0, 900 * KB),
        };
        _qos.Evaluate(rates, 0, LineDown, LineUp);
        RateLimit cap = _shaper.FindGroup("steam.exe")!.EffectiveLimit;
        Assert.Equal(1000 * 0.92 - 300 - 1000 * 0.12, cap.UpBytesPerSecond / KB, 1);
        Assert.Equal(7000 * 0.92 - 0 - 7000 * 0.12, cap.DownBytesPerSecond / KB, 1);
    }

    // ------------------------------------------------------------ Game mode

    [Fact]
    public void Game_mode_keeps_a_fixed_reservation_and_margin_while_the_game_runs()
    {
        _qos.SetGames(["valorant"]);
        _qos.GameMode = new GameModeOptions { Enabled = true, ReserveBytesPerSecond = 100 * 1024, SafetyMargin = 0.07 };

        // Game idle in a menu (0 KB/s): its 100 KB/s stays reserved; Discord (no number) is capped too.
        QosStatus s = Eval(0, ("valorant.exe", 0), ("discord.exe", 20), ("steam.exe", 7000));
        Assert.True(s.IsGameActive);
        Assert.Equal(["valorant.exe"], s.RunningGames);
        Assert.True(Uncapped("valorant.exe"));
        Assert.False(Uncapped("discord.exe"));
        // Everything but the game shares 7000 × 0.93 − 100 = 6410 KB/s: Discord's 20 first, then Steam.
        Assert.Equal(6410, CapKB("discord.exe"), 1);
        Assert.Equal(6410 - 20, CapKB("steam.exe"), 1);
    }

    [Fact]
    public void Game_mode_uses_the_ping_guard_factor()
    {
        _qos.SetGames(["valorant.exe"]);
        _qos.GameMode = new GameModeOptions { Enabled = true, ReserveBytesPerSecond = 100 * 1024, SafetyMargin = 0.07 };
        _qos.Evaluate(Rates(("valorant.exe", 50), ("steam.exe", 7000)), 0, LineDown, LineUp, pingFactor: 0.5);
        Assert.Equal(3500 * 0.93 - 100, CapKB("steam.exe"), 1);
    }

    [Fact]
    public void Game_mode_does_nothing_when_off_or_when_no_game_runs()
    {
        _qos.SetGames(["valorant.exe"]);
        Assert.False(Eval(0, ("valorant.exe", 50), ("steam.exe", 7000)).IsThrottling);   // mode off
        _qos.GameMode = _qos.GameMode with { Enabled = true };
        Assert.False(Eval(250, ("steam.exe", 7000)).IsThrottling);                      // game not running
    }

    [Fact]
    public void Games_come_before_numbered_apps()
    {
        _qos.SetGames(["valorant.exe"]);
        _qos.GameMode = new GameModeOptions { Enabled = true, ReserveBytesPerSecond = 100 * 1024, SafetyMargin = 0.07 };
        Eval(0, ("valorant.exe", 80), ("chrome.exe", 2000), ("steam.exe", 5000));
        // Chrome (2) shares 6510 − 100 with Steam below it.
        Assert.Equal(6510 - 100, CapKB("chrome.exe"), 1);
        Assert.Equal(6510 - 100 - 2000 - 840, CapKB("steam.exe"), 1);
    }

    // ------------------------------------------------------------ Movie mode

    [Fact]
    public void Movie_mode_keeps_room_waiting_between_video_chunks()
    {
        _qos.SetVideoTargets(["site:youtube"]);
        _qos.MovieMode = new MovieModeOptions { Enabled = true, ReserveBytesPerSecond = 3000 * 1024, HoldTime = TimeSpan.FromSeconds(20) };

        QosStatus s = Eval(0, ("site:youtube", 2500), ("steam.exe", 4000));          // a chunk downloads
        Assert.True(s.IsVideoActive);
        Assert.Equal(6440 - 3000, CapKB("steam.exe"), 1);

        s = Eval(10_000, ("site:youtube", 0), ("steam.exe", 4000));                  // between chunks
        Assert.True(s.IsVideoActive);
        Assert.Equal(6440 - 3000, CapKB("steam.exe"), 1);                           // room still waiting

        s = Eval(20_500, ("site:youtube", 0), ("steam.exe", 4000));                  // video stopped
        Assert.False(s.IsVideoActive);
        Assert.True(Uncapped("steam.exe"));
    }

    [Fact]
    public void Movie_mode_learns_the_reservation_from_the_video()
    {
        _qos.SetVideoTargets(["site:youtube"]);
        _qos.MovieMode = new MovieModeOptions { Enabled = true };
        QosStatus s = default!;
        for (long t = 0; t <= 60_000; t += 250)
            s = Eval(t, ("site:youtube", 2000), ("steam.exe", 3000));
        Assert.InRange(s.VideoReserveBytesPerSecond, 2940 * 1024, 3060 * 1024);      // 1.5 × 2000
        Assert.InRange(CapKB("steam.exe"), 6440 - 3070, 6440 - 2930);
    }

    [Fact]
    public void A_video_target_that_never_played_reserves_nothing()
    {
        _qos.SetVideoTargets(["site:youtube"]);
        _qos.MovieMode = new MovieModeOptions { Enabled = true };
        Assert.False(Eval(0, ("site:youtube", 0), ("steam.exe", 7000)).IsThrottling);
    }

    // ------------------------------------------------------------ Focus Mode

    [Fact]
    public void Focus_mode_makes_the_foreground_app_number_one()
    {
        _qos.FocusModeEnabled = true;
        _qos.ReportForeground("vlc.exe");

        Assert.Equal(Rank.None, _qos.GetRank("vlc.exe"));
        Assert.Equal(Rank.Highest, _qos.GetEffectiveRank("vlc.exe"));

        QosStatus s = Eval(0, ("vlc.exe", 300), ("steam.exe", 4000));
        Assert.True(s.IsThrottling);
        Assert.Contains("vlc.exe", s.TriggeringKeys);
    }

    [Fact]
    public void Focus_mode_off_ignores_the_foreground_app()
    {
        _qos.ReportForeground("vlc.exe");
        Assert.False(Eval(0, ("vlc.exe", 300), ("steam.exe", 4000)).IsThrottling);
    }

    [Fact]
    public void Shell_surfaces_do_not_steal_focus()
    {
        _qos.FocusModeEnabled = true;
        Assert.True(_qos.ReportForeground("game.exe"));
        Assert.False(_qos.ReportForeground("explorer.exe"));
        Assert.False(_qos.ReportForeground("StartMenuExperienceHost.exe"));
        Assert.False(_qos.ReportForeground(null));
        Assert.Equal("game.exe", _qos.ForegroundExe);
    }
}

public class LineMonitorTests
{
    [Fact]
    public void Learns_the_fastest_sustained_second()
    {
        var line = new LineCapacity();
        Assert.False(line.Observe(2_000_000, 0, 0.25));            // under a second: not trusted yet
        Assert.Equal(0, line.LearnedDown);
        line.Observe(2_000_000, 0, 0.25);
        line.Observe(2_000_000, 0, 0.25);
        Assert.True(line.Observe(2_000_000, 100_000, 0.25));
        Assert.Equal(8_000_000, line.LearnedDown);
        Assert.Equal(100_000, line.LearnedUp);

        line.Observe(0, 0, 0.25);                                   // quieter seconds don't lower it
        Assert.Equal(8_000_000, line.Down);

        line.ManualDown = 5_000_000;
        Assert.Equal(5_000_000, line.Down);
    }

    [Fact]
    public void Ping_guard_backs_off_fast_and_recovers_slowly()
    {
        var guard = new PingGuard();
        var calm = new PingStatus(null, 25, 20, false);
        var laggy = new PingStatus(null, 90, 20, false);

        Assert.Equal(1, guard.Update(calm));
        Assert.Equal(0.85, guard.Update(laggy), 3);
        Assert.Equal(0.7225, guard.Update(laggy), 3);
        Assert.Equal(0.7225 * 0.85, guard.Update(new PingStatus(null, null, 20, true)), 3);
        double low = guard.Factor;
        Assert.Equal(low + 0.02, guard.Update(calm), 3);
        for (int i = 0; i < 100; i++) guard.Update(calm);
        Assert.Equal(1, guard.Factor);
        for (int i = 0; i < 100; i++) guard.Update(laggy);
        Assert.Equal(guard.MinFactor, guard.Factor, 3);
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var s = new FlowGuardSettings { FocusMode = true };
        s.Limits["steam.exe"] = new LimitSetting { DownKBps = 500, UpKBps = 0 };
        s.Priorities["chrome.exe"] = 2;
        s.Priorities["site:youtube"] = 1;
        s.Sites.Add("site:youtube");
        s.GameMode.Enabled = true;
        s.GameMode.Apps.Add("valorant.exe");
        s.MovieMode.ReserveKBps = 3000;
        s.Line.DownKBps = 7000;
        s.Ui.ShowIdleProcesses = true;

        string json = SettingsStore.Serialize(s);
        Assert.Contains("\"chrome.exe\": 2", json);    // numbers, hand-editable
        Assert.Contains("\"gameMode\"", json);         // camelCase

        FlowGuardSettings back = SettingsStore.Deserialize(json);
        Assert.True(back.FocusMode);
        Assert.Equal(500, back.Limits["STEAM.EXE"].DownKBps); // case-insensitive after load
        Assert.Equal(2, back.Priorities["chrome.exe"]);
        Assert.Equal(1, back.Priorities["site:youtube"]);
        Assert.Equal(["site:youtube"], back.Sites);
        Assert.True(back.GameMode.Enabled);
        Assert.Equal(["valorant.exe"], back.GameMode.Apps);
        Assert.Equal(3000, back.MovieMode.ReserveKBps);
        Assert.Equal(7000, back.Line.DownKBps);
        Assert.True(back.Ui.ShowIdleProcesses);
    }

    [Fact]
    public void Version_1_files_still_load()
    {
        FlowGuardSettings s = SettingsStore.Deserialize("""
            {
              // comments and trailing commas are tolerated
              "priorities": { "Steam": "Low", "chrome.exe": "High", "vlc": "Normal", },
              "limits": { "qbittorrent": { "downKBps": 100 } },
              "autoThrottle": { "enabled": true, "thresholdKBps": 5000, "fallbackKBps": 50, "holdSeconds": 5 },
            }
            """);
        Assert.Equal(Rank.LegacyLow, s.Priorities["steam.exe"]);
        Assert.Equal(Rank.LegacyHigh, s.Priorities["chrome.exe"]);
        Assert.False(s.Priorities.ContainsKey("vlc.exe"));        // Normal = no number
        Assert.Equal(100, s.Limits["qbittorrent.exe"].DownKBps);
        Assert.True(s.AutoThrottle.Enabled);
        Assert.Equal(2, s.Version);
        Assert.Contains("site:youtube", s.MovieMode.Targets);     // new sections get defaults
    }

    [Fact]
    public void Missing_file_gives_defaults_and_corrupt_file_is_moved_aside()
    {
        string dir = Path.Combine(Path.GetTempPath(), "flowguard-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        try
        {
            Assert.Empty(SettingsStore.Load(path).Limits);

            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ not json");
            FlowGuardSettings s = SettingsStore.Load(path);
            Assert.Empty(s.Limits);
            Assert.True(File.Exists(path + ".bad"));
            Assert.False(File.Exists(path));

            SettingsStore.Save(new FlowGuardSettings { FocusMode = true }, path);
            Assert.True(SettingsStore.Load(path).FocusMode);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Service_persists_and_restores_configuration()
    {
        string dir = Path.Combine(Path.GetTempPath(), "flowguard-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        try
        {
            using (var svc = new FlowGuardService(new FlowGuardOptions { SettingsPath = path, EnablePing = false }))
            {
                svc.SetLimitKBps("steam", 500, 100);
                svc.SetRank("chrome", 2);
                svc.SetRank("steam", 4);
                Assert.Equal("site:youtube", svc.AddSite("https://www.youtube.com/watch?v=x"));
                Assert.Equal("site:example.com", svc.AddSite("example.com"));
                svc.SetRank("site:youtube", 1);
                svc.SetGames(["valorant"]);
                svc.GameMode = svc.GameMode with { Enabled = true, ReserveBytesPerSecond = 200 * 1024 };
                svc.SetVideoTargets(["site:netflix", "vlc"]);
                svc.MovieMode = svc.MovieMode with { Enabled = true };
                svc.SetManualLineSpeed(7000 * 1024, 0);
                svc.QosOptions = svc.QosOptions with { MinBytesPerSecond = 30 * 1024 };
            } // Dispose flushes the debounced save

            using var restored = new FlowGuardService(new FlowGuardOptions { SettingsPath = path, EnablePing = false });
            Assert.Equal(RateLimit.FromKBps(500, 100), restored.GetLimits()["steam.exe"]);
            Assert.Equal(2, restored.GetRank("chrome.exe"));
            Assert.Equal(4, restored.GetRank("steam.exe"));
            Assert.Equal(1, restored.GetRank("site:youtube"));
            Assert.Equal(["site:youtube", "site:example.com"], restored.GetTrackedSites());
            Assert.Equal(["valorant.exe"], restored.GetGames());
            Assert.True(restored.GameMode.Enabled);
            Assert.Equal(200 * 1024, restored.GameMode.ReserveBytesPerSecond);
            Assert.Equal(["site:netflix", "vlc.exe"], restored.GetVideoTargets());
            Assert.True(restored.MovieMode.Enabled);
            Assert.Equal(7000 * 1024, restored.Line.Down);
            Assert.Equal(30 * 1024, restored.QosOptions.MinBytesPerSecond);

            // Tracked = added sites + Movie mode sites, the latter only while Movie mode is on.
            Assert.Equal(["site:example.com", "site:netflix", "site:youtube"],
                restored.Shaper.SiteRoutes.Select(r => r.Site.Key).Order().ToArray());
            restored.MovieMode = restored.MovieMode with { Enabled = false };
            Assert.Equal(["site:example.com", "site:youtube"],
                restored.Shaper.SiteRoutes.Select(r => r.Site.Key).Order().ToArray());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
