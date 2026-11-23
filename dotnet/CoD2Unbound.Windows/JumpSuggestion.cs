using System.ComponentModel;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows;

public sealed class JumpSuggestion : INotifyPropertyChanged
{
    private readonly string message = "";
    private readonly Brush? selection;
    private bool selected;

    internal JumpSuggestion(JumpTarget target, string heading, bool footer, Brush? selection)
    {
        Target = target;
        Heading = heading;
        FooterVisible = IsVisible(footer);
        this.selection = selection;
    }

    internal JumpSuggestion(string message, bool footer)
    {
        this.message = message;
        FooterVisible = IsVisible(footer);
    }

    internal JumpTarget? Target { get; }
    public string Heading { get; } = "";
    public string Title => Target?.Title ?? message;
    public string Detail => Target?.Detail ?? "";
    public string Hint => Target is { Group: JumpGroup.Servers } && selected ? "Show in Servers ↵" : Target?.Keys ?? "";
    public double RowOpacity => Target?.Enabled == false ? 0.55 : 1;
    public string AccessibleName => string.Join(", ", new[] { Heading, Title, Detail, Hint, Target?.Enabled == false ? "Unavailable" : "" }
        .Where(text => text.Length != 0));
    public Brush? SelectionBrush => selected ? selection : null;
    public Visibility HeadingVisible => IsVisible(Heading.Length != 0);
    public Visibility DetailVisible => IsVisible(Detail.Length != 0);
    public Visibility IconVisible => IsVisible(Target is not null);
    public Visibility FooterVisible { get; }
    public TextWrapping TitleWrapping => Target is null ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public TextTrimming TitleTrimming => Target is null ? TextTrimming.None : TextTrimming.CharacterEllipsis;
    public string Icon => (string)Application.Current.Resources["Fluent" + (Target?.Group switch
    {
        JumpGroup.Servers => "Live",
        JumpGroup.Loadouts => "Stack",
        JumpGroup.Settings or JumpGroup.ConfigSettings => "Settings",
        JumpGroup.Bindings => "Tag",
        JumpGroup.Actions => ActionIcon(),
        _ => "ChevronRight"
    })];

    private string ActionIcon() => Enum.TryParse<ShellCommand>(Target?.Id, out var command) ? command switch
    {
        ShellCommand.Connect => "Add",
        ShellCommand.RefreshServers => "Refresh",
        ShellCommand.Launch or ShellCommand.OpenGameMenu => "PlayOutline",
        ShellCommand.Import or ShellCommand.CheckUpdates => "ArrowDownload",
        ShellCommand.Paste => "Copy",
        ShellCommand.Home => "Home",
        ShellCommand.Servers => "Live",
        ShellCommand.Library => "VideoClip",
        ShellCommand.Configs => "Stack",
        ShellCommand.Settings => "Settings",
        ShellCommand.Checkup => "HeartPulse",
        ShellCommand.History or ShellCommand.ClearRecent => "History",
        ShellCommand.GameDataFolder or ShellCommand.MyFilesFolder or ShellCommand.LogsFolder => "Folder",
        ShellCommand.ReleaseNotes => "Document",
        _ => "ChevronRight"
    } : "ChevronRight";

    internal bool IsSelected
    {
        set
        {
            if (selected == value)
                return;
            selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Hint)));
            PropertyChanged?.Invoke(this, new(nameof(SelectionBrush)));
            PropertyChanged?.Invoke(this, new(nameof(AccessibleName)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => AccessibleName;
}
