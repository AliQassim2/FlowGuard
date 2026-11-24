using CommunityToolkit.Mvvm.ComponentModel;
using FlowGuard.Core.Sites;

namespace FlowGuard.UI.ViewModels;

/// <summary>An app or site in a settings list (games, sites, video targets).</summary>
public sealed class NamedItem(string key)
{
    public string Key { get; } = key;
    public string DisplayName { get; } = SiteCatalog.DisplayName(key);
    public bool IsSite => TargetKey.IsSite(Key);
    public string Kind => IsSite ? "Website" : "App";
}

/// <summary>A built-in video platform with a checkbox (Movie mode).</summary>
public sealed partial class PlatformOption(SiteDefinition site, bool selected, Action changed) : ObservableObject
{
    public string Key { get; } = site.Key;
    public string DisplayName { get; } = site.DisplayName;
    public string Domains { get; } = string.Join(", ", site.Domains.Take(3));

    [ObservableProperty] private bool _isSelected = selected;

    partial void OnIsSelectedChanged(bool value) => changed();

    internal void SetQuietly(bool value)
    {
#pragma warning disable MVVMTK0034 // set the field to skip the change callback
        _isSelected = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>One numbered app or site in the priority order list.</summary>
public sealed partial class RankItem(string key, byte rank) : ObservableObject
{
    public string Key { get; } = key;
    public string DisplayName { get; } = SiteCatalog.DisplayName(key);
    public bool IsSite => TargetKey.IsSite(Key);
    public string Kind => IsSite ? "Website" : "App";

    [ObservableProperty] private byte _rank = rank;
}
