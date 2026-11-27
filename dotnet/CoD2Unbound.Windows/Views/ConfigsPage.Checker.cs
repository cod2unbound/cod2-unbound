using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private sealed record CheckedSource(string Name, string Origin, ImmutableArray<byte> Bytes);
    private readonly StackPanel checkerBody = new() { Spacing = 12 };
    private ConfigDialect? checkerDialect;
    private string? checkerRule;
    private ConfigRules? checkerRules;
    private CheckedSource? checkedSource;
    private ConfigCheckCopy? checkedCopy;
    private string? checkerProblem;
    private bool checkerBusy;
    private bool markedOnly;
    private int? checkerSelectedLine;
    private ListView? checkerLines;
    private readonly ObservableCollection<CheckerLineRow> checkerLineRows = [];
    private Button? checkerSave;
    private TextBlock? checkerReadOnly;
    private Button? checkerAnother;
    private Button? chooseCheckerFile;
    private bool CheckingConfig => DiscoverPlaces.SelectedIndex == 2;

    private Task ShowCheckerAsync()
    {
        refreshSharing = null;
        model.Opened(UsagePage.Checker);
        Status.IsOpen = false;
        Loadouts.SelectedItem = null;
        Detail.Children.Clear();
        checkerDialect ??= model.Dialect;

        var editions = new ComboBox { ItemsSource = new[] { "Original 1.3", "CoD2x" },
            SelectedIndex = checkerDialect == ConfigDialect.Cod2x ? 1 : 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(editions, "Game version");
        ToolTipService.SetToolTip(editions, "The game version whose settings and limits the config is checked against");
        var rules = new ComboBox { MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(rules, "Server rules");
        ToolTipService.SetToolTip(rules, "Server rules to check against as well, such as zPAM's");
        try { checkerRules ??= ConfigRules.LoadBundled(); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        {
            checkerProblem = "The bundled server rules are unavailable. You can still check the game's own limits.";
        }
        var policies = checkerRules?.Rulesets ?? [];
        rules.ItemsSource = policies.Select(item => item.Title).Prepend("No server rules").ToArray();
        rules.SelectedIndex = checkerRule is null ? 0 : policies.IndexOf(policies.First(item => item.Id == checkerRule)) + 1;
        rules.SelectionChanged += async (_, _) =>
        {
            if (rules.SelectedIndex < 0)
                return;
            checkerRule = rules.SelectedIndex == 0 ? null : policies[rules.SelectedIndex - 1].Id;
            if (checkerRule is not null)
            {
                var policy = policies[rules.SelectedIndex - 1];
                var dialect = policy.Mode == ConfigCatalogMode.Cod2x ? ConfigDialect.Cod2x : ConfigDialect.Stock;
                if (checkerDialect != dialect)
                {
                    checkerDialect = dialect;
                    editions.SelectedIndex = dialect == ConfigDialect.Cod2x ? 1 : 0;
                }
            }
            await RecheckAsync();
        };
        editions.SelectionChanged += async (_, _) =>
        {
            var dialect = editions.SelectedIndex == 1 ? ConfigDialect.Cod2x : ConfigDialect.Stock;
            if (checkerDialect == dialect)
                return;
            checkerDialect = dialect;
            if (rules.SelectedIndex > 0 && policies[rules.SelectedIndex - 1].Mode != CheckerMode)
                rules.SelectedIndex = 0;
            else
                await RecheckAsync();
        };

        var label = CheckStrong("Check for");
        label.VerticalAlignment = VerticalAlignment.Center;
        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { label, editions, rules } };
        var another = Action("Check Another", () =>
        {
            checkedSource = null;
            checkedCopy = null;
            checkerProblem = null;
            checkerSelectedLine = null;
            markedOnly = false;
            RenderCheckerBody();
            chooseCheckerFile?.Focus(FocusState.Programmatic);
            return Task.CompletedTask;
        });
        another.Content = IconLabel("ChevronLeft", "Check Another", 16);
        AutomationProperties.SetName(another, "Check another");
        another.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTipService.SetToolTip(another, "Close this check.");
        checkerAnother = another;
        configToolbar.Children.Clear();
        Put(configToolbar, 0, pickers);
        Put(configToolbar, 1, another);
        configToolbar.SizeChanged += FitToolbar;
        pickers.Unloaded += (_, _) => configToolbar.SizeChanged -= FitToolbar;
        FitToolbar(configToolbar, null!);

        void FitToolbar(object sender, SizeChangedEventArgs args)
        {
            bool compact = configToolbar.ActualWidth < 665;
            label.Visibility = IsVisible(!compact);
            rules.MaxWidth = compact ? 190 : 260;
            ((TextBlock)((StackPanel)another.Content).Children[1]).Visibility = IsVisible(!compact);
        }
        var readOnly = Caption(ConfigProfileException.ReadOnlyWhilePlaying);
        checkerReadOnly = readOnly;
        Detail.Children.Add(readOnly);
        Detail.Children.Add(checkerBody);
        DetailScroll.ChangeView(null, 0, null);
        RenderCheckerBody();
        return Task.CompletedTask;
    }

    private ConfigCatalogMode CheckerMode => checkerDialect == ConfigDialect.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;

    private void UpdateCheckerAvailability()
    {
        if (!CheckingConfig)
            return;
        if (checkerSave is not null)
            checkerSave.IsEnabled = CanEdit;
        if (checkerReadOnly is not null)
            checkerReadOnly.Visibility = IsVisible(model.State.ReadOnly);
    }

    private async Task StartCheckAsync(Func<Task<CheckedSource?>> input)
    {
        if (checkerBusy)
            return;
        checkerBusy = true;
        try
        {
            var source = await input();
            if (source is null)
                return;
            checkedSource = source;
            if (CheckingConfig)
                RenderCheckerBody();
            await EvaluateCheckerAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ConfigEditException)
        {
            checkedSource = null;
            checkedCopy = null;
            checkerProblem = error.Message;
            model.Error(UsageProblem.Check);
        }
        finally
        {
            checkerBusy = false;
            if (CheckingConfig)
                RenderCheckerBody();
        }
    }

    private async Task RecheckAsync()
    {
        if (checkerBusy || checkedSource is null)
            return;
        await StartCheckAsync(() => Task.FromResult<CheckedSource?>(checkedSource));
    }

    private async Task EvaluateCheckerAsync()
    {
        var source = checkedSource!;
        checkerProblem = null;
        checkerSelectedLine = null;
        // A picker can change while a file is being read. Publish the report only for the final selection.
        ConfigDialect dialect;
        string? rule;
        ConfigCheckReport report;
        do
        {
            dialect = checkerDialect!.Value;
            rule = checkerRule;
            report = await Task.Run(async () => await ConfigChecker.CheckAsync(source.Bytes, dialect, rule, model.Catalog, checkerRules));
        }
        while (dialect != checkerDialect || rule != checkerRule);
        checkedCopy = new(report);
        model.Record(new(UsageKind.CheckRun));
        Announce(report.Findings.IsEmpty ? "This config is clean." : $"{report.Findings.Length} findings.");
    }

    private void RenderCheckerBody()
    {
        checkerBody.Children.Clear();
        checkerLines = null;
        checkerSave = null;
        chooseCheckerFile = null;
        if (checkerAnother is not null)
        {
            checkerAnother.Visibility = IsVisible(checkedCopy is not null);
            checkerAnother.IsEnabled = !checkerBusy;
        }
        if (checkerProblem is { } problem)
            checkerBody.Children.Add(CheckNotice(InfoBarSeverity.Error, "Couldn't check that", problem));
        if (checkerBusy && checkedCopy is null)
            checkerBody.Children.Add(Progress("Checking this config…"));
        else if (checkedCopy is { } copy && checkedSource is { } source)
            RenderCheckerResults(copy, source);
        else
            RenderCheckerStart();
        UpdateCheckerAvailability();
    }

    private void RenderCheckerStart()
    {
        var pick = Action("Choose a File…", () => StartCheckAsync(async () =>
        {
            var picker = new FileOpenPicker(shell.AppWindow.Id) { Title = "Choose a config file to check" };
            foreach (string extension in new[] { ".cfg", ".bak", ".txt", "*" })
                picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            return file is null ? null : await ReadCheckerFileAsync(file.Path);
        }));
        chooseCheckerFile = pick;
        pick.Style = StyleFor("AccentButtonStyle");
        pick.IsEnabled = !checkerBusy;
        var paste = Action("Paste from Clipboard", () => StartCheckAsync(async () =>
        {
            var data = Clipboard.GetContent();
            return data.Contains(StandardDataFormats.Text) ? CheckerText(await data.GetTextAsync(), false)
                : throw new IOException("The clipboard has no config text to check.");
        }));
        var loadouts = new DropDownButton
        {
            Content = "Check a Loadout", IsEnabled = !model.State.Profiles.IsEmpty,
        };
        AutomationProperties.SetName(loadouts, "Check a loadout");
        AutomationProperties.SetAutomationId(loadouts, "ConfigsCheckerLoadout");
        var menu = new MenuFlyout();
        foreach (var profile in model.State.Profiles)
        {
            menu.Items.Add(MenuItem(profile.DisplayName, () => StartCheckAsync(() =>
            {
                var input = model.Studio.ReadProfile(profile.Id);
                return Task.FromResult<CheckedSource?>(new(profile.DisplayName + " loadout", "your loadout", input.Version.Config));
            })));
        }
        loadouts.Flyout = menu;
        var actions = Buttons(pick, paste, loadouts);
        actions.HorizontalAlignment = HorizontalAlignment.Center;
        var target = new StackPanel { Spacing = 14, Children =
        {
            Fluent("Document", 36),
            new TextBlock { Text = "DROP A CONFIG HERE", Style = StyleFor("EmptyHeading"), FontSize = 30, TextAlignment = TextAlignment.Center },
            Body("Or paste one. It is read on this PC only: nothing is uploaded, and nothing is added to your loadouts unless you ask."),
            actions
        } };
        target.HorizontalAlignment = HorizontalAlignment.Center;
        target.VerticalAlignment = VerticalAlignment.Center;
        var intro = (TextBlock)target.Children[2];
        intro.MaxWidth = 440;
        intro.TextAlignment = TextAlignment.Center;
        var drop = CheckCard(target);
        drop.MinHeight = 420;
        drop.SizeChanged += (_, args) => actions.Orientation = args.NewSize.Width < 424 ? Orientation.Vertical : Orientation.Horizontal;
        drop.AllowDrop = true;
        drop.DragOver += (_, args) =>
        {
            if (!checkerBusy && (args.DataView.Contains(StandardDataFormats.StorageItems) || args.DataView.Contains(StandardDataFormats.Text)))
                args.AcceptedOperation = DataPackageOperation.Copy;
            args.Handled = true;
        };
        drop.Drop += OnCheckerDrop;
        var checks = new StackPanel { Spacing = 14, Children = { CheckStrong("What gets checked") } };
        foreach (var (glyph, title, detail) in new[]
        {
            ("Search", "Settings that don’t exist", "Typos and settings from other games, with the likely fix."),
            ("Gauge", "Values outside the limits", "Values the game refuses, and the nearest one it takes."),
            ("Lock", "Protected settings", "Read-only, or only changing with cheats on."),
            ("ShieldCheckmark", "What server rules change", "Settings that zPAM and other rule sets fix or limit."),
            ("Copy", "Repeats", "The same setting written twice, and which line wins."),
            ("Eye", "Passwords and logins", "Pointed out before you share the file, and hidden in the review.")
        })
        {
            var words = Caption(detail);
            words.Margin = new Thickness(26, 0, 0, 0);
            var bar = IconLabel(glyph, title, 18);
            ((TextBlock)bar.Children[1]).FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            checks.Children.Add(new StackPanel { Spacing = 2, Children = { bar, words } });
        }
        const double sideBySide = 700;
        var layout = CheckPair(drop, CheckCard(checks), sideBySide, rightWidth: 320);
        checkerBody.Children.Add(layout);
        layout.SizeChanged += FitDropZone;
        drop.Loaded += (_, _) =>
        {
            DetailScroll.SizeChanged += FitDropZone;
            FitDropZone(drop, null!);
        };
        drop.Unloaded += (_, _) => DetailScroll.SizeChanged -= FitDropZone;

        void FitDropZone(object sender, SizeChangedEventArgs args)
        {
            bool wide = layout.ActualWidth >= sideBySide;
            if (wide)
                layout.ColumnDefinitions[1].Width = new GridLength(Math.Max(300, (layout.ActualWidth - layout.ColumnSpacing) / 2.3));
            double noticeHeight = checkerReadOnly is { Visibility: Visibility.Visible } notice
                ? notice.ActualHeight + Detail.Spacing : 0;
            double insets = DetailColumn.Padding.Top + DetailColumn.Padding.Bottom;
            drop.MinHeight = wide ? Math.Max(420, DetailScroll.ActualHeight - insets - noticeHeight) : 420;
        }
    }

    private async void OnCheckerDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var deferral = args.GetDeferral();
        try
        {
            await StartCheckAsync(async () =>
            {
                if (args.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var files = await args.DataView.GetStorageItemsAsync();
                    if (files.Count == 1 && files[0] is StorageFile file)
                        return await ReadCheckerFileAsync(file.Path);
                    throw new IOException("Drop one config file.");
                }
                if (args.DataView.Contains(StandardDataFormats.Text))
                    return CheckerText(await args.DataView.GetTextAsync(), true);
                throw new IOException("Drop a config file or plain config text.");
            });
        }
        finally { deferral.Complete(); }
    }

    private static CheckedSource CheckerText(string text, bool dropped)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new IOException(dropped ? "There is no config text to check." : "The clipboard has no config text to check.");
        if (text.Length > new ConfigImportLimits().MaximumFileBytes)
            throw new ConfigImportException(ConfigImportFailure.TooLarge);
        return new(dropped ? "Dropped config" : "Pasted config", dropped ? "dropped text" : "pasted",
            [.. ConfigDocument.Encode(text)]);
    }

    private static async Task<CheckedSource> ReadCheckerFileAsync(string path)
    {
        string name = Path.GetFileName(path);
        if (ConfigImportLoader.IsCredentialFile(name))
            throw new IOException("Choose a multiplayer config file.");
        await using var stream = File.OpenRead(path);
        var bytes = await ConfigCheckCopy.ReadAsync(stream);
        return new(name, $"from {Path.GetFileName(Path.GetDirectoryName(path))}", bytes);
    }

    private void RenderCheckerResults(ConfigCheckCopy copy, CheckedSource source)
    {
        var lines = new ListView
        {
            ItemTemplate = (DataTemplate)Resources["CheckerLine"], ItemsSource = checkerLineRows, MaxHeight = 600,
            ItemContainerStyle = StyleFor("StudioCodeRow"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            SelectionMode = ListViewSelectionMode.Single, TabNavigation = KeyboardNavigationMode.Once
        };
        AutomationProperties.SetName(lines, "Config lines");
        checkerLines = lines;
        lines.SelectionChanged += (_, _) => checkerSelectedLine = (lines.SelectedItem as CheckerLineRow)?.Id;
        void SetLines()
        {
            int? previous = checkerSelectedLine;
            var rows = copy.Lines(markedOnly).Select(item => new CheckerLineRow(item)).ToArray();
            checkerLineRows.Reconcile(rows, item => item.Id);
            lines.SelectedItem = checkerLineRows.FirstOrDefault(item => item.Id == previous);
        }
        SetLines();
        var linePanel = new StackPanel { Spacing = 8, Children =
        {
            CheckStrong(source.Name), Caption($"{copy.LineCount} {(copy.LineCount == 1 ? "line" : "lines")} · {source.Origin} · nothing was changed on disk"),
            Tabs(["All lines", "Only marked"], markedOnly ? 1 : 0, tab => { markedOnly = tab == 1; SetLines(); }),
            lines
        } };

        var findings = copy.Findings;
        int remaining = findings.Select(item => item.Line).Distinct().Count();
        var fixable = copy.Fixable;
        bool clean = !copy.HasEdits && findings.IsEmpty;
        string title = clean ? "This config is clean" : copy.HasEdits
            ? remaining == 0 ? $"{copy.AppliedCount} fixed, nothing left" : $"{copy.AppliedCount} fixed, {remaining} left for you"
            : remaining == 1 ? "1 line needs a look" : $"{remaining} lines need a look";
        string detail = clean ? "The settings and keys are known to this game and their values are within its limits."
            : copy.HasEdits ? (remaining > 0 && findings.All(item => item.Kind == ConfigCheckSeverity.Note)
                ? "The remaining lines are notes. Nothing was changed on disk." : "Nothing was changed on disk.")
            : CheckerBreakdown(findings);

        var save = Action("Save as Loadout…", () => SaveCheckedLoadoutAsync(copy, source));
        checkerSave = save;
        var copyButton = Action(copy.HasEdits ? "Copy Cleaned Config" : "Copy Config", () =>
        {
            var clipboard = new DataPackage();
            clipboard.SetText(Encoding.Latin1.GetString(copy.Bytes.AsSpan()));
            Clipboard.SetContent(clipboard);
            Announce(copy.HasEdits ? "Copied the config with your fixes." : "Copied the config.");
            return Task.CompletedTask;
        });
        var count = CheckTone(remaining.ToString(), clean ? ConfigCheckupTone.Positive : ConfigCheckupTone.Information);
        count.Style = StyleFor("TitleTextBlockStyle");
        var words = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { CheckStrong(title), Caption(detail) } };
        var result = Columns(14, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        Put(result, 0, count);
        Put(result, 1, words);
        var summary = new StackPanel { Spacing = 10, Children = { result } };
        if (!fixable.IsEmpty)
        {
            var fix = Action(fixable.Length == 1 ? "Fix 1 Line" : $"Fix All {fixable.Length}", () =>
            {
                copy.FixAll();
                RenderCheckerBody();
                if (CanEdit)
                    checkerSave?.Focus(FocusState.Programmatic);
                else
                    checkerLines?.Focus(FocusState.Programmatic);
                Announce("Applied the fixes to the copy.");
                return Task.CompletedTask;
            });
            fix.Style = StyleFor("AccentButtonStyle");
            summary.Children.Add(fix);
        }
        if (fixable.IsEmpty)
            save.Style = StyleFor("AccentButtonStyle");
        var saveAndCopy = Buttons(save, copyButton);
        saveAndCopy.SizeChanged += (_, args) => saveAndCopy.Orientation = args.NewSize.Width < 305
            ? Orientation.Vertical : Orientation.Horizontal;
        summary.Children.Add(saveAndCopy);
        if (copy.HasEdits)
        {
            summary.Children.Add(Buttons(Action("Export…", () => ExportCheckedAsync(copy, source)), Action("Undo", () =>
            {
                copy.Undo();
                RenderCheckerBody();
                checkerLines?.Focus(FocusState.Programmatic);
                Announce("Put every line back.");
                return Task.CompletedTask;
            })));
        }
        var right = new StackPanel { Spacing = 12, Children = { CheckCard(summary) } };
        if (clean)
        {
            var checks = new StackPanel { Spacing = 6, Children =
            {
                CheckStrong("Nothing to fix"),
                Body($"✓  {copy.CommandCount} settings and keys, all known to {(CheckerMode == ConfigCatalogMode.Cod2x ? "CoD2x" : "Original 1.3")}"),
                Body("✓  Every value inside the game's limits"),
                Body("✓  Nothing that needs cheats"),
                Body("✓  No repeats"),
                Caption("A server can still apply rules of its own. The ready check on Servers shows those before you join.")
            } };
            right.Children.Add(CheckCard(checks));
        }
        foreach (var group in findings.GroupBy(item => item.Kind).OrderBy(group => group.Key))
        {
            string heading = group.Key switch
            {
                ConfigCheckSeverity.Broken => "Won't work",
                ConfigCheckSeverity.Different => "Won't do what it says",
                _ => "Worth knowing"
            };
            var marked = new ListView
            {
                ItemsSource = group.Select(item => new CheckerFindingRow(item)).ToArray(),
                ItemTemplate = (DataTemplate)Resources["CheckerFinding"], IsItemClickEnabled = true,
                SelectionMode = ListViewSelectionMode.Single, MaxHeight = 480,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, TabNavigation = KeyboardNavigationMode.Once
            };
            AutomationProperties.SetName(marked, heading);
            marked.SelectionChanged += (_, _) =>
            {
                if (marked.SelectedItem is CheckerFindingRow item)
                    SelectCheckerLine(item.Line);
            };
            marked.ItemClick += (_, args) =>
            {
                if (args.ClickedItem is CheckerFindingRow item)
                    SelectCheckerLine(item.Line);
            };
            int countLines = group.Select(item => item.Line).Distinct().Count();
            right.Children.Add(CheckCard(new StackPanel
            {
                Spacing = 4, Children = { CheckStrong(heading), Caption(countLines == 1 ? "1 line" : $"{countLines} lines"), marked }
            }));
        }
        checkerBody.Children.Add(CheckPair(CheckCard(linePanel), right, 730, rightWidth: 380));
    }

    private static string CheckerBreakdown(ImmutableArray<ConfigCheckFinding> findings)
    {
        int Count(ConfigCheckSeverity kind) => findings.Where(item => item.Kind == kind).Select(item => item.Line).Distinct().Count();
        int broken = Count(ConfigCheckSeverity.Broken), different = Count(ConfigCheckSeverity.Different), note = Count(ConfigCheckSeverity.Note);
        return string.Join(", ", new[]
        {
            broken > 0 ? $"{broken} won't work" : null,
            different > 0 ? $"{different} won't do what {(different == 1 ? "it says" : "they say")}" : null,
            note > 0 ? $"{note} {(note == 1 ? "is" : "are")} worth knowing" : null
        }.OfType<string>()) + ".";
    }

    private void SelectCheckerLine(int line)
    {
        if (checkerLines is not { } lines || lines.Items.OfType<CheckerLineRow>().FirstOrDefault(item => item.Id == line) is not { } row)
            return;
        lines.SelectedItem = row;
        lines.ScrollIntoView(row);
    }

    private void OnCheckerFix(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: CheckerFindingRow row } && checkedCopy is { } copy)
        {
            checkerSelectedLine = row.Line;
            copy.Fix(row.Finding.Id);
            RenderCheckerBody();
            SelectCheckerLine(row.Line);
            checkerLines?.Focus(FocusState.Programmatic);
            Announce($"Line {row.Line} fixed in the copy.");
        }
    }

    private Task SaveCheckedLoadoutAsync(ConfigCheckCopy copy, CheckedSource source) =>
        ShowImportReviewAsync(Path.GetFileNameWithoutExtension(source.Name), new("Checked config", source.Name, "Document", Scope: "Text only"),
            async token =>
            {
                var catalog = new ConfigImportCatalogAdapter(model.Catalog);
                var dialect = model.Dialect;
                string name = Path.GetFileNameWithoutExtension(source.Name);
                var bundle = await new ConfigImportLoader(catalog).LoadAsync(new ConfigImportInput.Paste(copy.Bytes), dialect, token);
                return new(bundle, dialect, catalog, name, ConfigImportPlatform.Windows, ConfigImportPlatform.Windows);
            });

    private async Task ExportCheckedAsync(ConfigCheckCopy copy, CheckedSource source)
    {
        var picker = new FileSavePicker(shell.AppWindow.Id)
        {
            Title = "Export the cleaned config", SuggestedFileName = Path.GetFileNameWithoutExtension(source.Name) + "-cleaned.cfg",
            DefaultFileExtension = ".cfg"
        };
        picker.FileTypeChoices.Add("Config file", new List<string> { ".cfg" });
        if (await picker.PickSaveFileAsync() is not { } file)
            return;
        await model.ExportCheckedCopyAsync(file.Path, copy.Bytes);
        Feedback("Cleaned config exported.");
    }

    private void OnCheckerLineLoaded(object sender, RoutedEventArgs args) => StyleCheckerLine((Border)sender);
    private void OnCheckerLineContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => StyleCheckerLine((Border)sender);
    private void OnCheckerLineThemeChanged(FrameworkElement sender, object args) => StyleCheckerLine((Border)sender);

    private void StyleCheckerLine(Border row)
    {
        if (row.DataContext is not CheckerLineRow line || row.FindName("LineCode") is not TextBlock code ||
            row.FindName("LineMark") is not TextBlock mark || row.FindName("LineNote") is not TextBlock note)
            return;
        code.TextDecorations = line.Mark == ConfigCheckMark.Removed ? global::Windows.UI.Text.TextDecorations.Strikethrough : global::Windows.UI.Text.TextDecorations.None;
        row.ClearValue(Border.BackgroundProperty);
        mark.ClearValue(TextBlock.ForegroundProperty);
        note.ClearValue(TextBlock.ForegroundProperty);
        if (line.Mark is null || shell.IsHighContrast)
            return;
        string key = line.Mark switch
        {
            ConfigCheckMark.Broken => "NegativeBrush",
            ConfigCheckMark.Different => "GameYellowBrush",
            ConfigCheckMark.Note => "AccentBrush",
            _ => "PositiveBrush"
        };
        if (ThemeBrush(key, row.ActualTheme, false) is SolidColorBrush ink)
        {
            mark.Foreground = note.Foreground = ink;
            var color = ink.Color;
            color.A = 28;
            row.Background = new SolidColorBrush(color);
        }
    }
}

public sealed record CheckerLineRow(ConfigCheckShownLine Line)
{
    public int Id => Line.Id;
    public string Text => Line.Text;
    public ConfigCheckMark? Mark => Line.Mark;
    public string Note => Line.Note;
    public string Icon => Mark switch
    {
        ConfigCheckMark.Broken => "×", ConfigCheckMark.Different => "!", ConfigCheckMark.Note => "ⓘ",
        ConfigCheckMark.Changed => "✓", ConfigCheckMark.Removed => "−", _ => ""
    };
    public string AccessibleName => $"Line {Id}, {Text}. {Note}";
}

public sealed record CheckerFindingRow(ConfigCheckFinding Finding)
{
    public int Line => Finding.Line;
    public string Title => ConfigsPage.CheckTimingCopy(Finding.Title);
    public string Detail => ConfigsPage.CheckTimingCopy(Finding.Detail);
    public string? FixLabel => Finding.Fix?.Label;
    public string FixName => $"{FixLabel}, line {Line}";
    public Visibility FixVisibility => PagePresentation.IsVisible(Finding.Fix is not null);
    public string AccessibleName => $"Line {Line}. {Title}. {Detail}";
}
