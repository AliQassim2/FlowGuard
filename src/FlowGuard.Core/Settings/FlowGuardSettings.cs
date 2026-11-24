using System.Text.Json;
using System.Text.Json.Serialization;
using FlowGuard.Core.Qos;
using FlowGuard.Core.Sites;

namespace FlowGuard.Core.Settings;

/// <summary>Persisted user configuration (%AppData%\FlowGuard\settings.json). KB = 1024 bytes.</summary>
public sealed class FlowGuardSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Hard caps keyed by executable name or "site:…".</summary>
    public Dictionary<string, LimitSetting> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Priority numbers (1 = most important … 128) keyed by executable name or "site:…".
    /// Version 1 files said "High" / "Low"; those still load.
    /// </summary>
    [JsonConverter(typeof(RankMapConverter))]
    public Dictionary<string, int> Priorities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sites counted (and prioritised) separately from the browser, e.g. "site:youtube".</summary>
    public List<string> Sites { get; set; } = [];

    /// <summary>Smart sharing (kept under its old name so older files keep their values).</summary>
    public AutoThrottleSetting AutoThrottle { get; set; } = new();

    public LineSetting Line { get; set; } = new();

    public GameModeSetting GameMode { get; set; } = new();

    public MovieModeSetting MovieMode { get; set; } = new();

    /// <summary>Host pinged once a second to measure lag.</summary>
    public string PingTarget { get; set; } = "1.1.1.1";

    public bool FocusMode { get; set; }

    /// <summary>UI preferences round-tripped by the core but not interpreted by it.</summary>
    public UiSetting Ui { get; set; } = new();
}

public sealed class LimitSetting
{
    /// <summary>0 = unlimited.</summary>
    public double DownKBps { get; set; }

    /// <summary>0 = unlimited.</summary>
    public double UpKBps { get; set; }
}

public sealed class AutoThrottleSetting
{
    public bool Enabled { get; set; } = true;

    /// <summary>Never slow an app below this (KB/s, each way).</summary>
    public double FallbackKBps { get; set; } = 50;

    /// <summary>How long a numbered app keeps priority after it goes quiet.</summary>
    public double HoldSeconds { get; set; } = 5;
}

public sealed class LineSetting
{
    /// <summary>Manual line speed in KB/s; 0 = learn it automatically.</summary>
    public double DownKBps { get; set; }
    public double UpKBps { get; set; }

    /// <summary>What FlowGuard has learned so far (fastest sustained total seen).</summary>
    public double LearnedDownKBps { get; set; }
    public double LearnedUpKBps { get; set; }
}

public sealed class GameModeSetting
{
    public bool Enabled { get; set; }
    public List<string> Apps { get; set; } = [];
    public double ReserveKBps { get; set; } = 100;
    public double MarginPercent { get; set; } = 7;
    public bool PingGuard { get; set; } = true;
}

public sealed class MovieModeSetting
{
    public bool Enabled { get; set; }

    /// <summary>Sites ("site:youtube") and player apps ("vlc.exe").</summary>
    public List<string> Targets { get; set; } = ["site:youtube", "site:netflix", "site:twitch", "site:shahid"];

    /// <summary>Space kept for video in KB/s; 0 = automatic.</summary>
    public double ReserveKBps { get; set; }

    public double HoldSeconds { get; set; } = 20;
}

public sealed class UiSetting
{
    public bool ShowIdleProcesses { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
}

/// <summary>Reads priority numbers, and the High / Normal / Low words of version 1 files.</summary>
public sealed class RankMapConverter : JsonConverter<Dictionary<string, int>>
{
    public override Dictionary<string, int> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (reader.TokenType == JsonTokenType.Null)
            return map;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object of priorities.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString() ?? "";
            reader.Read();
            int rank = reader.TokenType switch
            {
                JsonTokenType.Number when reader.TryGetInt32(out int n) => n,
                JsonTokenType.String => reader.GetString()?.Trim().ToLowerInvariant() switch
                {
                    "high" => Rank.LegacyHigh,
                    "low" => Rank.LegacyLow,
                    { } s when int.TryParse(s, out int n) => n,
                    _ => 0,
                },
                _ => 0,
            };
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                reader.Skip();
            if (key.Length > 0 && Rank.IsValid(rank))
                map[key] = rank;
        }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, int> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach ((string key, int rank) in value.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            writer.WriteNumber(key, rank);
        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(FlowGuardSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Loads and atomically saves <see cref="FlowGuardSettings"/>.</summary>
public static class SettingsStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowGuard", "settings.json");

    /// <summary>
    /// Loads settings, or returns defaults when the file is missing. A corrupt file is moved
    /// aside to <c>settings.json.bad</c> rather than silently overwritten.
    /// </summary>
    public static FlowGuardSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new FlowGuardSettings();

        try
        {
            using FileStream fs = File.OpenRead(path);
            FlowGuardSettings? s = JsonSerializer.Deserialize(fs, SettingsJsonContext.Default.FlowGuardSettings);
            return Normalize(s ?? new FlowGuardSettings());
        }
        catch (JsonException)
        {
            try { File.Move(path, path + ".bad", overwrite: true); } catch { /* keep going with defaults */ }
            return new FlowGuardSettings();
        }
    }

    public static void Save(FlowGuardSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string temp = path + ".tmp";
        using (FileStream fs = File.Create(temp))
            JsonSerializer.Serialize(fs, settings, SettingsJsonContext.Default.FlowGuardSettings);
        File.Move(temp, path, overwrite: true); // atomic replace on NTFS
    }

    public static string Serialize(FlowGuardSettings settings)
        => JsonSerializer.Serialize(settings, SettingsJsonContext.Default.FlowGuardSettings);

    public static FlowGuardSettings Deserialize(string json)
        => Normalize(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.FlowGuardSettings) ?? new());

    // The deserializer creates case-sensitive dictionaries; hand-edited files may also say "Steam".
    // Re-key through TargetKey.Normalize (last entry wins on collisions).
    private static FlowGuardSettings Normalize(FlowGuardSettings s)
    {
        var limits = new Dictionary<string, LimitSetting>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, LimitSetting l) in s.Limits ?? [])
            if (!string.IsNullOrWhiteSpace(key) && l is not null)
                limits[TargetKey.Normalize(key)] = l;

        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, int rank) in s.Priorities ?? [])
            if (!string.IsNullOrWhiteSpace(key) && Rank.IsValid(rank))
                priorities[TargetKey.Normalize(key)] = rank;

        s.Limits = limits;
        s.Priorities = priorities;
        s.Sites = Keys(s.Sites).Where(TargetKey.IsSite).ToList();
        s.AutoThrottle ??= new AutoThrottleSetting();
        s.Line ??= new LineSetting();
        s.GameMode ??= new GameModeSetting();
        s.GameMode.Apps = Keys(s.GameMode.Apps).Where(k => !TargetKey.IsSite(k)).ToList();
        s.MovieMode ??= new MovieModeSetting();
        s.MovieMode.Targets = Keys(s.MovieMode.Targets).ToList();
        s.PingTarget = string.IsNullOrWhiteSpace(s.PingTarget) ? "1.1.1.1" : s.PingTarget.Trim();
        s.Ui ??= new UiSetting();
        s.Version = FlowGuardSettings.CurrentVersion;
        return s;
    }

    private static IEnumerable<string> Keys(IEnumerable<string>? keys)
        => (keys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(TargetKey.Normalize)
                       .Distinct(StringComparer.OrdinalIgnoreCase);
}
