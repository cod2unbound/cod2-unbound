using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConfigsPage
{
    private async Task ShowImportReviewAsync(string initialName, ImportSource source,
        Func<CancellationToken, Task<ConfigImportSession>> load, bool newOnly = false,
        Func<double?>? setupDistance = null, string? catalogueId = null)
    {
        if (!CanEdit)
            return;
        using var cancellation = new CancellationTokenSource();
        var progress = new StackPanel { Spacing = 8, Children = { Progress("Reading and checking every line…") } };
        string title = catalogueId is null ? "Review this config" : $"Review {initialName}’s setup";
        var modal = Dialog(title, progress, "");
        SheetPresentation.SetSheetTitle(modal, "FluentArrowDownload", title);
        modal.CloseButtonText = "Close";
        modal.DefaultButton = ContentDialogButton.Close;
        modal.Resources["ContentDialogMaxWidth"] = 1000d;

        ConfigImportReceipt? receipt = null;
        bool created = false;
        bool goToHistory = false;
        string? historyProfileId = null;
        string targetName = initialName;
        Task? pending = null;
        var root = modal.XamlRoot;
        Action? resize = null;
        Action? validate = null;
        void Resize(XamlRoot sender, XamlRootChangedEventArgs args) => resize?.Invoke();
        TrackGameState(modal, () => validate?.Invoke());
        modal.Opened += (_, _) =>
        {
            root.Changed += Resize;
            pending = ReadAsync();
        };
        modal.Closed += (_, _) =>
        {
            root.Changed -= Resize;
            cancellation.Cancel();
        };

        await modal.ShowSheetAsync();
        if (pending is not null)
            await pending;
        if (goToHistory)
        {
            studioTab = 3;
            if (historyProfileId is not null)
                model.Studio.Focus(historyProfileId);
            ShowStudio();
            RenderLoadouts();
        }
        if (receipt is null)
            return;

        lastImport = (receipt, created);
        if (catalogueId is not null)
            model.CatalogueEvent(UsageKind.SetupImported, catalogueId);
        if (created)
            model.Record(new(UsageKind.LoadoutMade));
        model.Studio.Focus(receipt.ProfileId);
        studioTab = 0;
        await RefreshStudioAsync();
        string message = created ? $"Imported as a new loadout, {targetName}. Your other loadouts are unchanged."
            : $"Imported into {targetName}. History has the version from before.";
        var undo = Action("Undo", UndoImportAsync);
        undo.Tag = "UndoImport";
        undo.IsEnabled = CanEdit;
        Feedback(message, undo);
        Announce(message);

        async Task ReadAsync()
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var review = await Task.Run(() => load(deadline.Token), deadline.Token);
                if (cancellation.IsCancellationRequested)
                    return;
                if (review.IsEmpty)
                {
                    Phase("Nothing to import", "This config has no settings or key bindings to bring in.");
                    return;
                }
                BuildReview(review);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                model.Error(UsageProblem.Import);
                Phase("Couldn't read it", "The config took too long to read.");
            }
            catch (Exception error) when (ImportProblem(error))
            {
                if (!cancellation.IsCancellationRequested)
                {
                    model.Error(UsageProblem.Import);
                    Phase("Couldn't read it", error.Message);
                }
            }
        }

        void Phase(string title, string reason)
        {
            progress.Children.Clear();
            progress.Children.Add(ReviewHeading(title));
            progress.Children.Add(Body(reason));
        }

        void BuildReview(ConfigImportSession review)
        {
            var profiles = model.State.Profiles;
            string newId = model.NewId(initialName);
            review.RenameNewTarget(newId, initialName);

            var header = new StackPanel { Spacing = 10 };
            var provenance = new StackPanel { Spacing = 4 };
            provenance.Children.Add(ReviewHeading(source.Label));
            if (source.Credit is { } credit)
            {
                var signed = Caption("Signed catalogue entry");
                signed.Style = StyleFor("PositiveCaption");
                provenance.Children.Add(signed);
                provenance.Children.Add(Caption($"Credit: {credit} · Shared with permission"));
            }
            if (source.Detail.Length > 0)
                provenance.Children.Add(MonoCaption(source.Detail));
            if (source.Scope is { } scope)
                provenance.Children.Add(Caption(scope));
            var attribution = Columns(12, GridLength.Auto, new GridLength(1, GridUnitType.Star));
            Put(attribution, 0, new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Top, Child = Fluent(source.Icon, 24) });
            Put(attribution, 1, provenance);
            header.Children.Add(attribution);

            // A native ComboBox is taller when it has a header.
            var into = new ComboBox { Header = "Into", Height = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(into, "Into");
            into.Items.Add($"New loadout “{initialName}”");
            if (!newOnly)
                foreach (var profile in profiles)
                    into.Items.Add(profile.DisplayName);
            into.SelectedIndex = 0;
            var intoNote = Caption("");
            var mode = new ComboBox { Header = "How to import", Height = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[] { "Review", "Import Everything" }, SelectedIndex = 0 };
            var choices = Columns(12, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
            Put(choices, 0, into);
            Put(choices, 1, mode);
            header.Children.Add(choices);
            header.Children.Add(intoNote);

            var summary = Caption("");
            var detailsLink = new HyperlinkButton { Content = "Show Details", Padding = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetName(detailsLink, "Show details");
            var summaryRow = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            summary.VerticalAlignment = VerticalAlignment.Center;
            Put(summaryRow, 0, summary);
            Put(summaryRow, 1, detailsLink);
            header.Children.Add(summaryRow);

            var body = new StackPanel { Spacing = 12, Padding = new Thickness(0, 2, 8, 8) };
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var problem = Caption("");
            var content = new StackPanel { Spacing = 8, Children = { header, scroll, problem } };
            var question = new StackPanel { Spacing = 12 };
            var confirmScroll = new ScrollViewer { Content = question, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var confirmation = new StackPanel { Spacing = 8, Children = { confirmScroll } };
            var history = new Hyperlink();
            history.Inlines.Add(new Run { Text = "History" });
            history.Click += (_, _) =>
            {
                goToHistory = true;
                historyProfileId = (review.Target as ConfigImportTarget.Existing)?.Read.Profile.Id;
                modal.Hide();
            };
            var footer = Caption("");
            footer.Inlines.Add(new Run { Text = "Nothing applies until you confirm. You can undo it from " });
            footer.Inlines.Add(history);
            footer.Inlines.Add(new Run { Text = "." });
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
            var warning = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
            var riskCaption = Caption("");
            var riskHeader = Columns(12, new GridLength(1, GridUnitType.Star), new GridLength(0.9, GridUnitType.Star));
            var riskHeading = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false,
                IsOpen = true, Title = "Needs your OK", Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0) };
            Put(riskHeader, 0, riskHeading);
            riskCaption.HorizontalAlignment = HorizontalAlignment.Right;
            riskCaption.VerticalAlignment = VerticalAlignment.Center;
            Put(riskHeader, 1, riskCaption);
            var counters = new List<(TextBlock Text, ConfigImportRow[] Rows)>();
            bool details = false, narrow = false, confirming = false, changing = false, blockedTarget = false, applying = false;

            detailsLink.Click += (_, _) =>
            {
                details = !details;
                detailsLink.Content = details ? "Hide Details" : "Show Details";
                AutomationProperties.SetName(detailsLink, details ? "Hide details" : "Show details");
                Fill();
            };
            into.SelectionChanged += (_, _) => SetTarget();
            mode.SelectionChanged += (_, _) =>
            {
                if (changing || mode.SelectedIndex < 0)
                    return;
                review.SetMode((ConfigImportMode)mode.SelectedIndex);
                if (!blockedTarget)
                    error.IsOpen = false;
                Fill();
            };

            modal.PrimaryButtonClick += async (_, args) =>
            {
                if (!CanEdit || blockedTarget)
                {
                    args.Cancel = true;
                    Validate();
                    return;
                }
                if (!confirming && review.RequiresConfirmation)
                {
                    args.Cancel = true;
                    Confirm();
                    return;
                }

                var deferral = args.GetDeferral();
                applying = true;
                Validate();
                try
                {
                    created = review.Target is ConfigImportTarget.NewLoadout;
                    targetName = DestinationName();
                    receipt = await review.ApplyAsync(model);
                }
                catch (Exception failure) when (ImportProblem(failure))
                {
                    args.Cancel = true;
                    model.Error(UsageProblem.Import);
                    error.Title = "Couldn't import the config";
                    error.Message = failure.Message;
                    error.IsOpen = true;
                }
                finally
                {
                    applying = false;
                    Validate();
                    deferral.Complete();
                }
            };

            modal.CloseButtonClick += (_, args) =>
            {
                if (!confirming)
                    return;
                args.Cancel = true;
                confirming = false;
                ShowReview();
                into.Focus(FocusState.Programmatic);
            };
            modal.Closing += (_, args) => args.Cancel = applying;
            resize = Layout;
            validate = Validate;
            ShowReview();
            Layout();
            into.Focus(FocusState.Programmatic);

            void Layout()
            {
                bool compact = root.Size.Width <= 1120;
                double width = Math.Max(280, Math.Min(1000, root.Size.Width - 48) - 48);
                content.Width = confirmation.Width = width;
                scroll.MaxHeight = Math.Max(132, root.Size.Height - (compact ? 320 : 328));
                confirmScroll.MaxHeight = Math.Max(132, root.Size.Height - 200);
                if (narrow != compact)
                {
                    narrow = compact;
                    Fill();
                }
            }

            void ShowReview()
            {
                if (error.Parent is Panel old)
                    old.Children.Remove(error);
                if (!content.Children.Contains(error))
                    content.Children.Add(error);
                SheetPresentation.SetSheetTitle(modal, "FluentArrowDownload", title);
                modal.Content = content;
                modal.CloseButtonText = "Cancel";
                modal.DefaultButton = ContentDialogButton.Primary;
                Fill();
            }

            void Confirm()
            {
                confirming = true;
                int risk = review.SelectedRiskCount;
                modal.Title = $"Import {Changes(review.SelectedIds.Count)}{(risk == 0 ? "" :
                    $", including {risk} that need{(risk == 1 ? "s" : "")} your OK")}?";
                question.Children.Clear();
                string riskText = Consequences();
                if (riskText.Length > 0)
                    question.Children.Add(Body(riskText));
                question.Children.Add(Body(review.Target is ConfigImportTarget.NewLoadout
                    ? $"The changes go into a new loadout, {DestinationName()}."
                    : $"This changes {DestinationName()}. History keeps the version from before."));
                content.Children.Remove(error);
                confirmation.Children.Add(error);
                modal.Content = confirmation;
                modal.CloseButtonText = "Back";
                Validate();
            }

            void SetTarget()
            {
                try
                {
                    review.SetTarget(into.SelectedIndex > 0
                        ? new ConfigImportTarget.Existing(model.Studio.ReadImportTarget(profiles[into.SelectedIndex - 1].Id))
                        : new ConfigImportTarget.NewLoadout(newId, initialName));
                    blockedTarget = false;
                    error.IsOpen = false;
                }
                catch (Exception failure) when (ImportProblem(failure))
                {
                    blockedTarget = true;
                    error.Title = "Couldn't read that loadout";
                    error.Message = failure.Message;
                    error.IsOpen = true;
                }
                intoNote.Text = into.SelectedIndex > 0
                    ? $"This changes {profiles[into.SelectedIndex - 1].DisplayName} itself. History keeps the version from before, so you can undo it." : "";
                intoNote.Visibility = IsVisible(intoNote.Text.Length != 0);
                Fill();
            }

            string DestinationName() => review.Target is ConfigImportTarget.Existing existing
                ? existing.Read.Profile.DisplayName : initialName;

            void Validate()
            {
                int count = review.SelectedIds.Count;
                int risky = review.SelectedRiskCount;
                bool all = review.Mode == ConfigImportMode.Everything;
                modal.PrimaryButtonText = (all ? $"Import all {count}" : $"Apply {Changes(count)}") +
                    (risky == 0 ? "" : $", including {risky} risky");
                bool writable = CanEdit && !applying;
                modal.IsPrimaryButtonEnabled = writable && !blockedTarget && count > 0;
                into.IsEnabled = writable;
                var blocker = !CanEdit ? (model.Studio.IsReadOnly
                    ? ConfigProfileException.ReadOnlyWhilePlaying : "Finish the pending loadout change before importing.")
                    : count == 0 ? "Tick at least one change to import." : "";
                problem.Text = blocker;
                problem.Visibility = IsVisible(blocker.Length != 0);
                if (confirming && blocker.Length > 0 && !applying)
                {
                    error.Title = blocker;
                    error.Message = "";
                    error.IsOpen = true;
                }

                riskCaption.Text = all ? "Ticked, because you chose Import everything" : "Left out unless you tick them";
                warning.IsOpen = all && risky > 0;
                if (warning.IsOpen)
                {
                    warning.Title = $"This imports {risky} {(risky == 1 ? "line" : "lines")} that need{(risky == 1 ? "s" : "")} your OK";
                    warning.Message = Consequences() + " Switch to Review to leave them out.";
                }
                foreach (var counter in counters)
                    counter.Text.Text = $"{counter.Rows.Count(row => review.SelectedIds.Contains(row.Id))} of {counter.Rows.Length}";
            }

            void Fill()
            {
                body.Children.Clear();
                counters.Clear();
                intoNote.Visibility = IsVisible(intoNote.Text.Length != 0);
                var rows = review.Rows.Where(row => row.Candidate.Command.Kind is not (ConfigCommandKind.Empty or ConfigCommandKind.Comment)).ToArray();
                var risky = rows.Where(row => row.CanSelect && row.Candidate.Classification == ConfigImportClassification.NeedsOk).ToArray();
                var regular = rows.Where(row => row.CanSelect && row.Candidate.Classification != ConfigImportClassification.NeedsOk).ToArray();
                var locked = rows.Where(row => !row.LockedNames.IsEmpty).ToArray();
                var omitted = rows.Where(row => !row.CanSelect && row.LockedNames.IsEmpty).ToArray();
                summary.Text = $"{Changes(regular.Length + risky.Length)}, grouped by what they do." +
                    (locked.Length == 0 ? "" : locked.Length == 1 ? " 1 setting is locked, so it stays yours." :
                        $" {locked.Length} settings are locked, so they stay yours.");

                if (risky.Length > 0)
                {
                    body.Children.Add(riskHeader);
                    body.Children.Add(warning);
                    body.Children.Add(CardColumns(risky.Select(row => Card(Row(row, true))).ToArray(), narrow ? 1 : 3));
                }

                var groups = new List<FrameworkElement>();
                foreach (var group in regular.GroupBy(GroupName))
                {
                    var groupRows = group.ToArray();
                    var count = Caption("");
                    counters.Add((count, groupRows));
                    groups.Add(Section(group.Key, groupRows, count));
                }
                if (locked.Length > 0)
                    groups.Add(Section("Protected", locked, Caption("Locked by you · never imported"), "Lock"));
                body.Children.Add(CardColumns(groups.ToArray(), narrow ? 1 : 2));
                if (omitted.Length > 0)
                    body.Children.Add(Section("Left out", omitted, Caption("Excluded from this import")));
                body.Children.Add(footer);
                Validate();
            }

            string GroupName(ConfigImportRow row)
            {
                if (row.Group != "Settings" || !row.Title.StartsWith("Set ", StringComparison.Ordinal))
                    return row.Group == "Bindings" ? "Key bindings" : row.Group;
                string? group = model.Catalog.Setting(row.Title[4..])?.Group;
                return model.Catalog.Groups.FirstOrDefault(item => item.Id == group)?.Title ?? "Other settings";
            }

            FrameworkElement Section(string heading, ConfigImportRow[] rows, TextBlock counter, string? glyph = null)
            {
                var top = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
                Put(top, 0, glyph is null ? ReviewHeading(heading) : IconLabel(glyph, heading, 16));
                Put(top, 1, counter);
                var section = new StackPanel { Spacing = 4, Children = { top } };
                foreach (var row in rows)
                    section.Children.Add(Row(row, false));
                return Card(section);
            }

            FrameworkElement Row(ConfigImportRow row, bool risky)
            {
                var (heading, detail) = ReviewCopy(row, setupDistance);
                var line = new StackPanel { Spacing = 2 };
                if (row.CanSelect)
                {
                    var box = new CheckBox { Content = ReviewHeading(heading), MinWidth = 0,
                        IsChecked = review.SelectedIds.Contains(row.Id) };
                    AutomationProperties.SetName(box, heading);
                    AutomationProperties.SetHelpText(box, risky ? Consequence(row) : detail);
                    box.Checked += (_, _) => Toggle(true);
                    box.Unchecked += (_, _) => Toggle(false);
                    line.Children.Add(box);

                    void Toggle(bool selected)
                    {
                        review.Select(row.Id, selected);
                        changing = true;
                        mode.SelectedIndex = (int)review.Mode;
                        changing = false;
                        error.IsOpen = false;
                        Validate();
                    }
                }
                else if (!row.LockedNames.IsEmpty && row.OldValue is { } value)
                {
                    line.Children.Add(ReviewHeading($"{heading} stays {value}"));
                    detail = "Locked by you · never imported";
                }
                else
                    line.Children.Add(ReviewHeading(heading));

                if (detail.Length > 0 || risky)
                    line.Children.Add(Caption(risky ? Consequence(row) : detail));
                if (details && row.SourceLine is { } sourceLine)
                {
                    var exact = MonoCaption("");
                    exact.TextWrapping = TextWrapping.Wrap;
                    if (row.PreviousLine is { } previousLine)
                    {
                        exact.Inlines.Add(new Run { Text = previousLine, TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough });
                        exact.Inlines.Add(new Run { Text = " → " });
                    }
                    exact.Inlines.Add(new Run { Text = sourceLine });
                    line.Children.Add(exact);
                    line.Children.Add(Caption(row.Candidate.Location.File));
                }
                return line;
            }

            string Consequences() => string.Join(" ", review.Rows.Where(row => review.SelectedIds.Contains(row.Id) &&
                row.Candidate.Classification == ConfigImportClassification.NeedsOk).Select(Consequence).Distinct());
        }
    }

    private static bool ImportProblem(Exception error) => error is IOException or UnauthorizedAccessException or
        ConfigImportSessionException or ConfigDirectoryException or ConfigEditException or ArgumentException;

    private static string Consequence(ConfigImportRow row)
    {
        if (row.Candidate.Reasons.Contains(ConfigImportRisk.Identity) && row.NewValue is { } name)
            return $"Your name in game would change to {name}.";
        if (row.Candidate.Reasons.Contains(ConfigImportRisk.Connect) && row.Candidate.Command.Tokens is [var command, var address, ..] &&
            command.Text.Equals("connect", StringComparison.OrdinalIgnoreCase))
            return $"As soon as this loadout starts, the game would connect to {address.Text}.";
        return string.Join(" ", row.Candidate.Reasons.Select(risk =>
            (risk == ConfigImportRisk.FileWrite ? "Writes files on this PC" : risk.Summary()) + "."));
    }

    private static string Changes(int count) => $"{count} {(count == 1 ? "change" : "changes")}";
    private static TextBlock ReviewHeading(string label) => new() { Text = label,
        Style = StyleFor("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
    private static Border Card(FrameworkElement child) => new() { Style = StyleFor("ReviewRow"), Child = child };

    private static FrameworkElement CardColumns(FrameworkElement[] cards, int columns)
    {
        var layout = Columns(10, Enumerable.Repeat(new GridLength(1, GridUnitType.Star), columns).ToArray());
        var lanes = Enumerable.Range(0, columns).Select(_ => new StackPanel { Spacing = 8 }).ToArray();
        for (int index = 0; index < columns; index++)
            Put(layout, index, lanes[index]);
        for (int index = 0; index < cards.Length; index++)
            lanes[index % columns].Children.Add(cards[index]);
        return layout;
    }

    private static FrameworkElement Progress(string text)
    {
        var ring = new ProgressRing { IsActive = true, Height = 24, Width = 24 };
        AutomationProperties.SetName(ring, text);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { ring, Body(text) } };
    }
}
