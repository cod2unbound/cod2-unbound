using System.Collections.ObjectModel;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private ConfigChainDraft? chainDraft;
    private bool listenOnNewChain;

    private static readonly string[] ChainKinds = ["Several", "Toggle", "Cycle"];
    private static readonly string[] ChainKindsHelp =
    [
        "One press runs every action, top to bottom.",
        "Each press turns a setting to its next value, then back to the start.",
        "Each press moves to the next step, then back to the start."
    ];
    private static readonly string[] ChainStepKinds =
        ["Set my name to", "Say to everyone", "Say to team", "Do an action", "Run a console command"];

    private sealed record AdvancedBindRow(ConfigChain Chain, string Key, string Title, string Summary);

    private readonly ObservableCollection<AdvancedBindRow> advancedBindRows = [];

    private void BuildChainRail(StackPanel panel, ConfigStudioState state, ConfigProfileRead profile)
    {
        var title = new StackPanel { Spacing = 2, Children =
        {
            new TextBlock { Text = "Advanced binds", Style = StyleFor("SectionTitle") },
            Caption("One key that does more than one thing.")
        }};
        var add = SmallButton("New Bind", () =>
        {
            chainDraft = ConfigChainDraft.New(ConfigChains.OwnedChains(profile));
            listenOnNewChain = true;
            RenderStudioBody();
        });
        add.Content = IconLabel("AddSmall", "New Bind", 16);
        AutomationProperties.SetName(add, "New Bind");
        add.Style = StyleFor("QuietButton");
        add.IsEnabled = !state.ReadOnly;
        var row = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(row, 0, title);
        Put(row, 1, add);
        panel.Children.Add(row);
        try
        {
            var chains = ConfigChains.OwnedChains(profile);
            advancedBindRows.Reconcile(chains.Select(chain =>
            {
                int kind = (int)ConfigChainDraft.From(chain).Kind;
                string key = EngineKey.Parse(chain.Key) is { } parsed ? KeyLabel(parsed) : chain.Key;
                return new AdvancedBindRow(chain, key, chain.Name ?? ChainKinds[kind], ChainKindsHelp[kind]);
            }).ToArray(), item => item.Chain.Id);
            if (advancedBindRows.Count > 0)
            {
                var list = new ListView
                {
                    ItemsSource = advancedBindRows, ItemTemplate = (DataTemplate)Resources["AdvancedBindRow"],
                    ItemContainerStyle = StyleFor("StudioColumnItem"),
                    SelectedItem = advancedBindRows.FirstOrDefault(item => item.Chain.Id == chainDraft?.Id)
                };
                AutomationProperties.SetName(list, "Advanced binds");
                list.SelectionChanged += (_, _) =>
                {
                    if (list.SelectedItem is AdvancedBindRow item && item.Chain.Id != chainDraft?.Id)
                    {
                        chainDraft = ConfigChainDraft.From(item.Chain);
                        RenderStudioBody();
                    }
                };
                panel.Children.Add(BindCard(list));
            }
            else
                panel.Children.Add(Caption("No advanced binds yet."));
        }
        catch (ConfigChainException error)
        {
            advancedBindRows.Clear();
            add.IsEnabled = false;
            panel.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
                Title = "Advanced binds changed outside Config Studio", Message = error.Message
            });
        }
    }

    private FrameworkElement ChainEditor(ConfigStudioState state, ConfigProfileRead profile)
    {
        var draft = chainDraft!;
        string id = profile.Profile.Id;
        var panel = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = ChainTitle(), Style = StyleFor("SectionTitle") };
        var name = new TextBox { Header = "Name", Text = draft.Name, PlaceholderText = "Name this bind", IsReadOnly = state.ReadOnly };
        var key = new Button { Content = draft.Key is { } saved ? KeyLabel(saved) : "Choose…", IsEnabled = !state.ReadOnly, MinWidth = 90 };
        key.Style = StyleFor("Keycap");
        AutomationProperties.SetName(key, $"Key for this bind, {key.Content}");
        var kind = new ComboBox { Header = "Type", Height = double.NaN, ItemsSource = ChainKinds,
            SelectedIndex = (int)draft.Kind, IsEnabled = !state.ReadOnly };
        var help = Caption(ChainKindsHelp[kind.SelectedIndex]);
        var steps = new StackPanel { Spacing = 8 };
        var preview = Body("");
        var problem = new InfoBar { IsClosable = false };
        var lines = new TextBlock { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var showLines = new Expander { Header = "Show the lines", Content = lines };
        var delete = Action("Delete bind", async () =>
        {
            var current = ForBindingEdit(id);
            var existing = ConfigChains.OwnedChains(current);
            if (existing.Any(chain => chain.Id == draft.Id))
            {
                if (await Dialog("Delete this advanced bind?", Body("The key will stop running this bind."), "Delete bind").ShowSheetAsync()
                    != ContentDialogResult.Primary)
                    return;
                var edit = ConfigChains.Prepare([.. existing.Where(chain => chain.Id != draft.Id)], current);
                model.Studio.Mutate(id, store => ConfigChains.Apply(edit, store));
            }
            chainDraft = null;
            RenderStudioBody();
        });
        delete.IsEnabled = !state.ReadOnly;
        var status = Caption("Saved as you go");
        panel.Children.Add(heading);
        name.Header = null;
        name.Width = 190;
        kind.Header = null;
        kind.MaxWidth = 190;
        AutomationProperties.SetName(name, "Name");
        AutomationProperties.SetName(kind, "Type");
        var identity = new StackPanel();
        AddBindRow(identity, SettingRow("Name", null, name, grouped: true));
        AddBindRow(identity, SettingRow("Key", null, key, grouped: true));
        AddBindRow(identity, SettingRow("Type", null, kind, grouped: true));
        panel.Children.Add(BindCard(identity));
        panel.Children.Add(help);
        panel.Children.Add(steps);
        panel.Children.Add(preview);
        panel.Children.Add(problem);
        panel.Children.Add(showLines);
        panel.Children.Add(Buttons(delete, status));

        ConfigChainPreview? pending = null;
        ConfigProfileVersion expected = profile.Version;
        key.Click += (_, _) => Listen();
        if (listenOnNewChain)
        {
            listenOnNewChain = false;
            key.Loaded += (_, _) => _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (key.IsLoaded && ReferenceEquals(chainDraft, draft) && listening is null)
                    Listen();
            });
        }

        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedIndex < 0)
                return;
            draft.Kind = (ChainEditorKind)kind.SelectedIndex;
            help.Text = ChainKindsHelp[kind.SelectedIndex];
            BuildSteps();
            Validate(save: true);
        };
        name.TextChanged += (_, _) =>
        {
            draft.Name = name.Text;
            heading.Text = ChainTitle();
            Validate(save: false);
        };
        name.LostFocus += (_, _) => { if (pending is null) Validate(save: true); };
        BuildSteps();
        Validate(save: false);
        return new Border { Style = StyleFor("FieldCard"), Child = panel };

        string ChainTitle() => string.IsNullOrWhiteSpace(draft.Name) ? draft.Key.HasValue ? "Advanced bind" : "New bind" : draft.Name;

        void Listen() => StartListening(key, pressed =>
        {
            draft.Key = pressed;
            key.Content = KeyLabel(pressed);
            AutomationProperties.SetName(key, $"Key for this bind, {key.Content}");
            heading.Text = ChainTitle();
            Validate(save: true);
            return Task.CompletedTask;
        });

        void Validate(bool save)
        {
            if (!ReferenceEquals(chainDraft, draft) || studioTab != 1 || bindView != 0)
                return;
            string? detail = null;
            problem.IsOpen = false;
            pending = null;
            preview.Text = lines.Text = "";
            showLines.Visibility = Visibility.Collapsed;
            try
            {
                if (model.State.Current is not { } shown || shown.Profile.Id != id)
                    return;
                var current = model.Studio.ReadProfile(id);
                if (!current.Version.Matches(expected))
                    throw new ConfigProfileException(ConfigProfileFailure.Conflict);
                var result = ConfigChainPreview.Create(draft, current, model.Catalog, model.Model.Mode);
                preview.Text = result.Description;
                lines.Text = result.Lines;
                showLines.Visibility = Visibility.Visible;
                if (result.Blocked)
                {
                    detail = result.Risks.IsEmpty ? "This bind contains a command Config Studio won’t save." : string.Join(" ", result.Risks);
                }
                else if (result.NeedsApproval && result.Edit.HasChanges)
                {
                    pending = result;
                    var binds = ConfigBindingDetails.ForWindows(current.Document, model.Catalog, model.Model.Mode);
                    var notices = result.Edit.ConflictingKeys.Select(spelling =>
                    {
                        var existing = binds.Entries.First(entry => entry.Key == EngineKey.Parse(spelling));
                        return $"{KeyLabel(existing.Key)} already does “{ConfigBindingDetails.Title(binds, existing, current.Document.Dialect)}”. Saving replaces it.";
                    }).Concat(result.Risks);
                    SetProblem(InfoBarSeverity.Warning, "Needs your OK before saving:", string.Join(" ", notices),
                        state.ReadOnly ? null : Action("Save bind", () =>
                        {
                            if (ReferenceEquals(pending, result))
                                Save(result.Edit, true);
                            return Task.CompletedTask;
                        }));
                }
                else if (save && result.Edit.HasChanges && !model.Studio.IsReadOnly)
                    Save(result.Edit, false);
            }
            catch (ConfigChainException error) when (error.Failure == ConfigChainFailure.OpaqueOwnedFile)
            {
                SetProblem(InfoBarSeverity.Informational, "Advanced binds changed outside Config Studio", error.Message);
            }
            catch (ConfigProfileException error) when (error.Failure == ConfigProfileFailure.Conflict)
            {
                SetProblem(InfoBarSeverity.Informational, "Advanced binds changed outside Config Studio",
                    "Choose Reload to open the current version.", SmallButton("Reload", () =>
                    {
                        chainDraft = null;
                        model.Studio.Refresh();
                        RenderStudioBody();
                    }));
            }
            catch (Exception error) when (error is InvalidDataException or ConfigChainException or ConfigEditException or IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                if (draft.Key.HasValue || save)
                    detail = error.Message;
            }
            if (detail is not null)
                SetProblem(InfoBarSeverity.Error, "Config Studio won’t save this:", detail);
        }

        void Save(ConfigChainEdit edit, bool replace)
        {
            try
            {
                _ = ForBindingEdit(id);
                model.Studio.Mutate(id, store => ConfigChains.Apply(edit, store, replace));
                expected = model.State.Current!.Version;
                status.Text = "Saved as you go";
                problem.IsOpen = false;
                if (replace)
                    Validate(save: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigChainException or ConfigEditException)
            {
                SetProblem(InfoBarSeverity.Error, "Config Studio won’t save this:", error.Message);
            }
        }

        void SetProblem(InfoBarSeverity severity, string title, string message, Button? action = null)
        {
            problem.Severity = severity;
            problem.Title = title;
            problem.Message = message;
            problem.ActionButton = action;
            problem.IsOpen = true;
        }

        void BuildSteps()
        {
            steps.Children.Clear();
            steps.Children.Add(new TextBlock { Text = draft.Kind switch
            {
                ChainEditorKind.Several => "Actions, in order", ChainEditorKind.Toggle => "What a press does", _ => "Steps, in order"
            }, Style = StyleFor("BodyStrongTextBlockStyle") });
            if (draft.Kind == ChainEditorKind.Toggle)
            {
                var choices = model.Model.Specs.Where(spec => !spec.LauncherOwned && ((spec.Type is "bool" or "enum") ||
                    string.Equals(spec.Name, draft.Setting, StringComparison.OrdinalIgnoreCase))).ToArray();
                var settings = new ComboBox { Header = "Setting", Height = double.NaN, ItemsSource = choices, DisplayMemberPath = "Title",
                    SelectedItem = choices.FirstOrDefault(spec => spec.Name.Equals(draft.Setting, StringComparison.OrdinalIgnoreCase)),
                    IsEnabled = !state.ReadOnly };
                settings.SelectionChanged += (_, _) =>
                {
                    if (settings.SelectedItem is ConfigSettingSpec spec)
                    {
                        draft.Setting = spec.Name;
                        Validate(save: true);
                    }
                };
                steps.Children.Add(settings);
                AddRows(draft.Values, (value, index) => TextField($"Value {index + 1}", value, text => draft.Values[index] = text), "");
            }
            else
            {
                var values = draft.Kind == ChainEditorKind.Several ? draft.Several : draft.Cycle;
                AddRows(values, (step, index) =>
                {
                    var choose = new ComboBox { Header = $"Step {index + 1}", Height = double.NaN,
                        ItemsSource = ChainStepKinds, SelectedIndex = (int)step.Kind,
                        IsEnabled = !state.ReadOnly, HorizontalAlignment = HorizontalAlignment.Stretch };
                    choose.SelectionChanged += (_, _) =>
                    {
                        if (choose.SelectedIndex >= 0 && choose.IsLoaded)
                        {
                            values[index] = values[index] with { Kind = (ChainStepKind)choose.SelectedIndex };
                            Validate(save: true);
                        }
                    };
                    return new StackPanel { Spacing = 4, Children =
                    {
                        choose, TextField($"Step {index + 1} text", step.Text, text => values[index] = values[index] with { Text = text })
                    }};
                }, new ChainEditorStep(ChainStepKind.Action, ""));
            }
        }

        FrameworkElement TextField(string title, string value, Action<string> edit)
        {
            var field = new TextBox { Text = value, IsReadOnly = state.ReadOnly, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(field, title);
            field.TextChanged += (_, _) => { if (field.IsLoaded) { edit(field.Text); Validate(save: false); } };
            field.LostFocus += (_, _) => { if (pending is null) Validate(save: true); };
            return field;
        }

        void AddRows<T>(List<T> values, Func<T, int, FrameworkElement> field, T blank)
        {
            for (int i = 0; i < values.Count; i++)
            {
                int index = i;
                var up = SmallButton("Move up", () => Move(index - 1, "Move up"));
                var down = SmallButton("Move down", () => Move(index + 1, "Move down"));
                var remove = SmallButton("Remove", () =>
                {
                    values.RemoveAt(index);
                    BuildSteps();
                    steps.Children[^1].Focus(FocusState.Programmatic);
                    Validate(save: true);
                });
                AutomationProperties.SetName(up, $"Move step {i + 1} up");
                AutomationProperties.SetName(down, $"Move step {i + 1} down");
                AutomationProperties.SetName(remove, $"Remove step {i + 1}");
                up.IsEnabled = !state.ReadOnly && i > 0;
                down.IsEnabled = !state.ReadOnly && i + 1 < values.Count;
                remove.IsEnabled = !state.ReadOnly;
                steps.Children.Add(new StackPanel { Spacing = 4, Children = { field(values[i], i), Buttons(up, down, remove) } });

                void Move(int target, string direction)
                {
                    (values[index], values[target]) = (values[target], values[index]);
                    BuildSteps();
                    int first = steps.Children.Count - values.Count - 1;
                    if (steps.Children[first + target] is StackPanel moved && moved.Children[1] is StackPanel buttons)
                    {
                        var move = (Button)buttons.Children[direction == "Move up" ? 0 : 1];
                        if (!move.Focus(FocusState.Programmatic))
                            buttons.Children[2].Focus(FocusState.Programmatic);
                    }
                    Validate(save: true);
                }
            }
            var add = SmallButton("Add step", () =>
            {
                values.Add(blank);
                BuildSteps();
                if (steps.Children[^2] is StackPanel added)
                {
                    FrameworkElement field = (FrameworkElement)added.Children[0];
                    if (field is StackPanel pair)
                        field = (FrameworkElement)pair.Children[0];
                    field.Focus(FocusState.Programmatic);
                }
                Validate(save: false);
            });
            add.IsEnabled = !state.ReadOnly;
            steps.Children.Add(add);
        }
    }
}
