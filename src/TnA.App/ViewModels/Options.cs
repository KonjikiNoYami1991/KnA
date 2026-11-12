using CommunityToolkit.Mvvm.ComponentModel;
using TnA.Core.Encoding;

namespace TnA.App.ViewModels;

public sealed record Option<T>(T Value, string Label, bool IsEnabled = true)
{
    public override string ToString() => Label;
}

public sealed record Chip(string Text, string Kind = "")
{
    public bool IsHw => Kind == "hw";
    public bool IsBit => Kind == "bit";
}

public sealed record BackendOption(EncoderBackend Value, string Label, bool IsEnabled)
{
    public override string ToString() => Label;
}

public static class Converters
{
    private static readonly Avalonia.Media.IBrush Ok = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#34D399"));
    private static readonly Avalonia.Media.IBrush Muted = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8D95A8"));

    public static Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.IBrush> OkBrush { get; } =
        new(ok => ok ? Ok : Muted);
}

public sealed partial class HardwareCell : ObservableObject
{
    public required string Text { get; init; }
    public required bool Ok { get; init; }
    public string? Tooltip { get; init; }
    public double CellOpacity => Ok ? 1 : 0.45;
}

public sealed class HardwareRow
{
    public required string Name { get; init; }
    public required HardwareCell H264 { get; init; }
    public required HardwareCell Hevc { get; init; }
    public required HardwareCell Av1 { get; init; }
}

public static class OptionLists
{
    public static IReadOnlyList<Option<QualityLevel>> Qualities { get; } =
        Enum.GetValues<QualityLevel>().Select(q => new Option<QualityLevel>(q, q.Display())).ToList();

    public static IReadOnlyList<Option<int>> Resolutions { get; } =
        Core.Encoding.Resolutions.All.Select(r => new Option<int>(r, Core.Encoding.Resolutions.Display(r))).ToList();

    public static IReadOnlyList<Option<SubtitleMode>> SubtitleModes { get; } =
        Enum.GetValues<SubtitleMode>().Select(m => new Option<SubtitleMode>(m, m.Display())).ToList();

    public static IReadOnlyList<Option<PostQueueAction>> PostActions { get; } =
        Enum.GetValues<PostQueueAction>().Select(a => new Option<PostQueueAction>(a, a.Display())).ToList();
}
