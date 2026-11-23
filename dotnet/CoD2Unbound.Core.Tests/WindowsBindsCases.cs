namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void WindowsBindsCases()
    {
        // Windows VK, set-1 scan position, and unshifted layout output transcribed
        // from the US, UK, German and Russian Windows keyboard tables.
        foreach (var (layout, scan, vk, printed, expected) in new[]
        {
            ("US", 0x15, 0x59, 'y', "y"),
            ("UK", 0x2b, 0xde, '#', "#"),
            ("German", 0x15, 0x5a, 'z', "z"),
            ("German", 0x27, 0xc0, 'ö', "0x8f"),
            ("Russian", 0x11, 0x57, 'ц', "w"),
            ("Russian", 0x29, 0xc0, 'ё', "~"),
        })
            Check($"{layout} layout at scan {scan:X2}", () =>
                Equal(EngineKey.Parse(expected), WindowsBindInput.Key(vk, scan, false, printed)));

        Check("Windows keypad and extended navigation do not collide", () =>
        {
            Equal(EngineKey.Parse("HOME"), WindowsBindInput.Key(0x24, 0x47, true, '\0'));
            Equal(EngineKey.Parse("KP_HOME"), WindowsBindInput.Key(0x24, 0x47, false, '\0'));
            Equal(EngineKey.Parse("KP_ENTER"), WindowsBindInput.Key(0x0d, 0x1c, true, '\0'));
        });
        Check("Windows capture rejects system keys and non-game buttons", () =>
        {
            foreach (int key in new[] { 0x2c, 0x5b, 0x5c, 0x5d })
                Equal<EngineKey?>(null, WindowsBindInput.Key(key, 0, false, '\0'));
            Equal(EngineKey.Parse("MOUSE5"), WindowsBindInput.Button(5));
            Equal<EngineKey?>(null, WindowsBindInput.Button(6));
            Equal(EngineKey.Parse("MWHEELDOWN"), WindowsBindInput.Wheel(-120));
        });
        Check("Windows verification uses physical positions and presents ambiguous sizes", () =>
        {
            var verification = new PcKeyboardVerification();
            verification.Press(0x30, false);
            Equal(0, verification.Step);
            Equal(true, verification.Note is not null);
            verification.Press(0x56, false);
            verification.Press(0x38, false);
            verification.Skip();
            verification.Press(0x3b, false);
            verification.Press(0x47, true);
            Equal(KeyboardShape.Iso, verification.Shape);
            Sequence(new[] { KeyboardSize.Tkl, KeyboardSize.Percent75 }, verification.Sizes);
        });
        Check("Imported shared binds keep command order and can keep only one action", () =>
        {
            var catalog = ConfigCatalog.LoadBundled();
            var studio = new ConfigStudioSession(new FixtureProfileFiles(), ConfigDialect.Stock, () => false, new FixedClock());
            var profile = studio.Create("Main", "Main", new("bind f \"+smoke; +activate\"\n"u8, ConfigDialect.Stock));
            studio.Select("Main");
            var binds = new ConfigBindings(profile.Document, catalog, ConfigCatalogMode.Stock);
            var shared = ConfigBindingDetails.Shared(binds, ConfigDialect.Stock).Single();
            var actions = ConfigBindingDetails.Actions(binds, shared, ConfigDialect.Stock);
            Sequence(new[] { "+smoke", "+activate" }, actions.Select(action => action.Name));
            var edit = binds.PrepareKey(profile, shared.Key, ConfigBindingDetails.Value(actions[1]));
            Equal(true, edit.Conflict is not null);
            _ = studio.Mutate("Main", store => ConfigBindings.Apply(edit, store, true));
            Equal("+activate", studio.State.Current!.Document.EffectiveBind("f"));
        });
        Check("Advanced bind drafts preview exact lines, report conflicts and save", () =>
        {
            var catalog = ConfigCatalog.LoadBundled();
            var studio = new ConfigStudioSession(new FixtureProfileFiles(), ConfigDialect.Stock, () => false, new FixedClock());
            var profile = studio.Create("Main", "Main", new("bind n \"+talk\"\n"u8, ConfigDialect.Stock));
            studio.Select("Main");
            var draft = ConfigChainDraft.New([]);
            draft.Key = EngineKey.Parse("n");
            draft.Name = "Jump";
            var preview = ConfigChainPreview.Create(draft, profile, catalog, ConfigCatalogMode.Stock);
            var binds = ConfigBindingDetails.ForWindows(profile.Document, catalog, ConfigCatalogMode.Stock);
            Equal("Voice chat", ConfigBindingDetails.Title(binds, binds.Entries.Single(), ConfigDialect.Stock));
            Equal("Forward", binds.Row("+forward")!.Action.Title);
            Equal(true, preview.NeedsApproval);
            Equal(false, preview.Blocked);
            Equal(true, preview.Lines.Contains("bind n \"+gostand\"", StringComparison.Ordinal));
            _ = studio.Mutate("Main", store => ConfigChains.Apply(preview.Edit, store, true));
            Equal("Jump", ConfigChains.OwnedChains(studio.State.Current!).Single().Name);
            draft.Kind = ChainEditorKind.Toggle;
            draft.Setting = "sensitivity";
            Equal(true, draft.Build().Behavior is ConfigChainBehavior.SettingCycle);
            draft.Kind = ChainEditorKind.Cycle;
            draft.Cycle[0] = new(ChainStepKind.Console, "weapnext");
            draft.Cycle[1] = new(ChainStepKind.Console, "weapprev");
            preview = ConfigChainPreview.Create(draft, studio.State.Current!, catalog, ConfigCatalogMode.Stock);
            Equal(false, preview.Edit.Script.IsEmpty);
        });
        Check("Expert notes, locks, reset, DPI and text share one serialized loadout history", () =>
        {
            var catalog = ConfigCatalog.LoadBundled();
            var mode = ConfigCatalogMode.Stock;
            var model = new ConfigStudioModel(catalog, mode, System.Globalization.CultureInfo.InvariantCulture);
            var studio = new ConfigStudioSession(new FixtureProfileFiles(), ConfigDialect.Stock, () => false, new FixedClock());
            var profile = studio.Create("Main", "Main", new("seta sensitivity \"2\"\nseta m_yaw \"0.03\"\n"u8, ConfigDialect.Stock));
            studio.Select("Main");
            var spec = model.Specs.Single(setting => setting.Name == "sensitivity");
            var checkup = new ConfigCheckup(catalog, ConfigRules.LoadBundled(), ConfigOptimizer.LoadBundled());
            Equal(true, double.IsNaN(ConfigMouseDpi.For(profile)));
            ConfigMouseDpi.Save(studio, profile, 800);
            profile = studio.State.Current!;
            Equal(800.0, ConfigMouseDpi.For(profile));
            Equal(ConfigDiscover.LoadoutTurnDistance(profile.Document, 800), ConfigMouseDpi.DistancePerSensitivity(profile) / 2);
            var dial = checkup.Evaluate(profile, [], ConfigTuneFor.Balanced).Dials.Single(d => d.Area == ConfigCheckupArea.Aim);
            Equal(true, dial.Source?.Contains("cm per full turn", StringComparison.Ordinal));
            ConfigExpertEdits.EditMetadata(studio, profile, spec, null, "  My note  ");
            ConfigExpertEdits.EditMetadata(studio, studio.State.Current!, spec, true, null);
            profile = studio.State.Current!;
            var state = ConfigStudioModel.State(spec, profile.Document, profile.Metadata);
            Equal("My note", state.Note);
            Equal(true, state.Locked);
            Equal(true, model.TakeTheirsBlock(spec, studio.State, null, [], "") is not null);
            ConfigExpertEdits.EditMetadata(studio, profile, spec, false, null);
            ConfigExpertEdits.Reset(model, studio, studio.State.Current!, spec);
            profile = studio.State.Current!;
            Equal<string?>(null, profile.Document.EffectiveSetting("sensitivity"));
            Equal(false, ConfigTextCheck.Inspect("seta sensitivity \"3", studio.Dialect, catalog, mode).CanApply);
            ConfigExpertEdits.ApplyText(studio, profile, "seta sensitivity \"3\"\n", catalog, mode);
            Equal("3", studio.State.Current!.Document.EffectiveSetting("sensitivity"));
            try
            {
                ConfigExpertEdits.ApplyText(studio, profile, "seta sensitivity \"4\"\n", catalog, mode);
                throw new InvalidDataException("Saved over a stale version.");
            }
            catch (ConfigProfileException error) { Equal(ConfigProfileFailure.Conflict, error.Failure); }
            Equal("3", studio.State.Current!.Document.EffectiveSetting("sensitivity"));
        });
    }
}
