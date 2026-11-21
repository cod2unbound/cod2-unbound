# Launcher port map

The table covers the 81 Swift files and the Objective-C setup bridge at
`c7ee91656e679b86e6e2f17f58f6b641d1b7ba2c`. Files with more than one destination
have separate rows for their logical parts.

Coverage is 117 rows, with 49 ported, 0 planned, 4 partly ported, 48 UI and 16 Apple-only.

| Swift source or part | Status | .NET type or Windows destination |
| --- | --- | --- |
| AppSheets.swift | UI | WinUI dialogs and owner-window placement |
| Artwork.swift | UI | Approved launcher assets and WinUI image handling |
| BrandShapes.swift | UI | Approved vector assets |
| Components.swift | UI | WinUI controls, shared styles, and accessibility states |
| ConfigBindings.swift | Ported | `ConfigBindings`, `ConfigBindingEdit` and `EngineKey` resolve groups, choices, effective binds, duplicate actions, conflicts and locks. |
| ConfigBindsView.swift | UI | Binding editor, capture, and native accessible labels |
| ConfigCatalog.swift | Ported | `ConfigCatalog`, `ConfigCatalogSetting`, `ConfigCatalogCommand`, registration records |
| ConfigChains.swift | Ported | `ConfigChainMachine` and `ConfigChains` inspect press/release, publish matching definitions and companions, and verify portable exports. |
| ConfigChecker.swift | Ported | `ConfigChecker` returns byte-addressed findings and in-memory fixes, retaining safe line previews. |
| ConfigCheckerView.swift | UI | Check a Config workflow, selection and findings |
| ConfigCheckup.swift | Ported | `ConfigOptimizer`, `ConfigCheckup` and `ConfigScriptReview` produce cited recommendations, measured facts and version-checked Apply and Undo. |
| ConfigCheckupView.swift | UI | Check-up results and tune-for controls |
| ConfigClean.swift | Ported | `ConfigClean` reviews both engine dialects and preserves approved bytes and omission reasons |
| ConfigDirectory.swift · trust and metadata | Ported | `ConfigDirectory` verifies exact Ed25519-signed bytes, closed metadata, revisions and downloaded profile digests. Review of the signed profile is tracked with the import rows below. |
| ConfigDirectory.swift · transport and cache | Ported | `IConfigTextFetcher`, `IConfigDirectoryStorage`, core cache re-verification and saved entries. The platform supplies bounded HTTP and private transactional storage. |
| ConfigDiscoverModel.swift · state rules | Ported in part | `ConfigDiscover` loads a verified catalogue, retries from cache, filters, groups and saves entries, and returns a verified import session. The composed comparison cards and application navigation still need the Windows presenter. |
| ConfigDiscoverModel.swift · dialogs | UI | File and link handling, navigation and feedback |
| ConfigDiscoverView.swift | UI | Player Setups directory and result views |
| ConfigDocument.swift | Ported | `ConfigDocument`, `ConfigDialect`, `ConfigTokenization`, `ConfigCommand`, `ConfigDiagnostic`, `ByteSpan`, `ConfigEditException` |
| ConfigHistory.swift | Ported | `ConfigHistoryEntry` and `ConfigProfileStore` preserve both revisions, timestamps, restoration and retention |
| ConfigHistoryView.swift | UI | Revision history and restore actions |
| ConfigImport.swift | Ported | `ConfigImportLoader`, `ConfigImportScript` and `IConfigImportFiles` implement source rules and bounded dependency discovery |
| ConfigImportCatalogAdapter.swift | Ported | `ConfigImportCatalogAdapter` and `IConfigImportCatalog` use conservative edition-specific registration flags |
| ConfigImportSafety.swift | Ported | `ConfigImportSafety` reviews saved command shapes, secret redaction, dependency graphs and engine limits |
| ConfigImportSession.swift | Ported | `ConfigImportSession` owns eligibility, selection, locks, composition and one-shot commit. `IConfigImportStore` supplies transactional profile ownership. Dialog layout and interaction belong to WinUI. |
| ConfigImportView.swift | UI | Import source, review and apply dialogs |
| ConfigLauncherModel.swift · workflows | Ported | `ConfigLauncherCommands` projects loadout and import command eligibility over the Studio state. Mutations use `ConfigStudioSession`, and import completion uses `ConfigImportSession`. |
| ConfigLauncherModel.swift · dialogs | UI | Open, drop, paste and confirmation flows |
| ConfigProfiles.swift · profiles and journals | Ported | `ConfigProfileStore` owns format, creation, copying, selection, atomic updates and journal replay behind `IConfigProfileFiles` |
| ConfigProfiles.swift · migration, Trash and connection cleanup | Ported | `ConfigProfileStore` replays migration receipts, preserves companions in Trash and disposes verified temporary profiles |
| ConfigProfiles.swift · Darwin persistence | Apple-only | Windows private atomic writes, locks, and reparse-point containment |
| ConfigReadyCheck.swift | Ported | `ConfigReadyCheck` compares saved values against cited rules, and `ConfigServerConnection` confines freshly reviewed overrides to one endpoint and profile revision. |
| ConfigRules.swift | Ported | `ConfigRules` and `ConfigOptimizer` load the bundled resources. `ConfigReadyCheck` infers cited rules from advertised and observed facts. |
| ConfigSettingsView.swift | UI | Guided and expert editors using catalogue controls |
| ConfigShare.swift | Ported | `ConfigShare`, `ConfigShareClient`, `ConfigShareRecords` and `ConfigShareService` implement the service contract and private receipt lifecycle |
| ConfigShareModel.swift | UI | Share flow, progress and feedback over the service client |
| ConfigShareView.swift | UI | Share sheets and preview |
| ConfigSheets.swift · naming | Ported | `LoadoutName` validates blank, duplicate and read-only names |
| ConfigSheets.swift · sheets | UI | Loadout create, rename and select |
| ConfigStudioBinds.swift | UI | Binding editor display models and keyboard diagram |
| ConfigStudioBlocks.swift | UI | Studio controls, banners and feedback |
| ConfigStudioCheckup.swift | UI | Findings copy, severity and accessibility |
| ConfigStudioModel.swift · model rules | Ported in part | `ConfigStudioModel`, `ConfigSettingSpec`, `ConfigValues` and `ConfigTextCheck` project typed settings, search, locks, comparison eligibility and diagnostics. `ConfigDiff` supplies History differences and `ConfigStudioSession` guards publication. The Windows editor still needs to bind undo and retry presentation and supply live resolution choices. |
| ConfigStudioModel.swift · view state | UI | Tabs, detail levels and focus state |
| ConfigStudioSession.swift | Ported | `ConfigStudioSession`, `ConfigProfileStore` and `ConfigEngineLimits` preserve drafts, revisions, launch reservations, per-connection isolation, engine handoff and durable exit reconciliation. The native host holds the launch lease. |
| ConfigStudioStyle.swift | UI | Studio appearance and spacing |
| ConfigsHomeView.swift | UI | Loadout and Player Setups entry actions |
| ConfigsView.swift | UI | Studio container and navigation |
| Core.swift · names and server packets | Ported | `QuakeColors`, `WireParser`, `GameServer`, `ServerPlayer` |
| Core.swift · connect link and engine arguments | Ported | `LaunchLink`, `EngineCommandLine`, `GameLaunchPlan`, `ConfigStudioLaunch`, `ConfigConnectionLaunch`, `WindowsLaunchPlan` |
| Core.swift · demo aliasing | Ported | `MediaName` and `MediaLibrary.PrepareDemoAsync` normalize playback names and use compare-or-publish for collisions. Windows also refuses reserved device components and alternate streams. |
| Core.swift · edition, discovery and renderer | Ported | `GameEdition`, `GameCapabilities`, `IGameDiscovery`, `GameBinary`, `WindowsRenderMode` |
| Core.swift · Mac bundles and Metal | Apple-only | Installer inventory supplies Windows edition/role paths. Native Windows Direct3D 9 or DirectX 7 selection |
| Core.swift · file and key persistence | Apple-only | Private settings writes via `ISettingsFolder`. Credential Manager and the engine's CD-key ingress via `ISecretStore` |
| Core.swift · key normalization and checksum | Ported | `CdKey` implements normalization, CRC-16 and key-field status |
| Core.swift · data-folder preference and crash selection | Ported | `SetupSession.InitialDataPath`, `GameExit` and `CrashFiles` preserve the remembered folder and select reports from the current session |
| Core.swift · release offer and checksum | Ported | `ReleasePackage`, `ReleaseOffer`, `UpdateBlock` |
| Core.swift · bundle update | Apple-only | Signed Windows installer, publisher verification and staged update |
| Core.swift · crash reports and CoD2x files | Ported | `CrashReporting`, `SentryDsn` and `Cod2xFiles` implement consent, redaction, bounded envelope/ledger, pinned release extraction and private placement. File handles and service dispatch belong to adapters. |
| Core.swift · settings and display resolution | Ported | `GameSettings`, `GameSettingsJson`, `GameSettingsStorage`, `DisplayResolutions` |
| Core.swift · launcher library and session logs | Ported | `LauncherLibrary` preserves the saved schema and recents; `GameSessionLog` redacts streamed output and bounds line, log and retention lengths. |
| HomeLibraryMenus.swift | Apple-only | WinUI context menus and focus restoration |
| HomeView.swift | UI | Home cards and commands |
| Hosting.swift · dedicated start | Ported | `GameLaunchPlan.Dedicated`, `WindowsLaunchPlan.Dedicated` |
| Hosting.swift · form, config, export and status | Ported | `HostedServerJson`, `HostingConfig`, `HostedStatus` and `ZpamRelease` cover saved configs, export, replies and release selection |
| HostingModel.swift · lifecycle and publication | Ported | `HostingSession` owns start, private config, polling, admin ownership, exit, stop and export. `ZpamDownload` owns lookup, progress, cancellation and verified publication. |
| HostingModel.swift · admin queue and zPAM package policy | Ported | `HostedAdminQueue` and `ZpamPackage` provide paced local commands and verified bounded staging |
| HostingModel.swift · native services | Apple-only | Win32 process supervision and shell actions behind core seams |
| HostingView.swift | UI | Hosted server form, list and state feedback |
| InputDevices.swift · key layout model | Ported in part | `EngineInputMapping`, `InputKeyMapping`, `InputDevicePresentation` and `PointerButtonLearning` expose labels, rejection, guesses and pointer learning. Windows key-position verification requires the adapter’s SDL/layout observations, so the Darwin virtual-key wizard is deferred. |
| InputDevices.swift · IOKit and Carbon | Apple-only | Windows HID/device APIs and keyboard layout mapping |
| JumpPalette.swift | UI | Searchable command surface and keyboard navigation |
| LibraryView.swift | UI | Library controls and Windows file preview |
| Main.swift | Apple-only | WinUI entry, app lifecycle, activation, menus, notifications and window routing |
| MapArtwork.swift · archive rules | Ported | `IwdArtworkArchive`, `MapArtwork` and `IwiImageDecoder` validate bounded stock archive lookup and decode bounded BGRA/DXT images. The adapter opens contained stock handles and owns the image cache. |
| MapArtwork.swift · AppKit objects | Apple-only | Windows image decoding and WinUI image surfaces |
| MatchSummaries.swift | Ported | `MatchSummaries` reads a bounded JSONL tail, validates measured data and formats legacy or current freeze details |
| Model.swift · lifecycle | Ported | `GameLifecycle` reserves and reconciles games, verifies identity for link and quit delivery, and classifies exit. `ConfigStudioSession` supplies transaction and restart recovery. `LauncherQuit` sequences game and hosted-server prompts. |
| Model.swift · navigation and windows | UI | WinUI shell and model binding |
| ModelLibrary.swift · media | Ported in part | `MediaLibrary`, `MediaFacts`, `MatchFacts` and `MatchSummaries` supply media, playback, dates and match facts. Home’s combined recent-server, selected loadout/display and match artwork projections still need the Windows Home model. |
| ModelLibrary.swift · OS actions and UI states | UI | Native open/reveal actions through the appropriate seam |
| ModelServers.swift · browse/refresh logic | Ported | `ServerBrowser`, `ServerFilters`, `ServerBrowserPreferences`, `ServerAddress`, `ServerAdmission` and `ServerPasswordStore` cover batching, offline cache, scope, filters, sorting, validated joins, edition choice and saved launch times. |
| ModelServers.swift · Keychain and native actions | Apple-only | Windows Credential Manager and shell/clipboard adapters |
| ModelSettings.swift · settings lifecycle | Ported | `LauncherSettingsSession`, `AdvancedSettings` and `LauncherUpdates` provide reset and save timing, advanced text validation, release/news checks and live-process interlocks before installer handoff. |
| ModelSettings.swift · Mac integration | Apple-only | Installer activation, monitor modes, application folders and system information |
| ModelSetup.swift · game-data preflight | Ported | `GameArchives` checks all 16 required archives and recovers a valid remembered or discovered root |
| ModelSetup.swift · setup coordination and rollback | Ported | `SetupSession` validates confirmed data, stages key replacement, saves consent and completion, and discards unconfirmed edits on cancel |
| ModelSetup.swift · Mac setup effects | Apple-only | Retail/Steam discovery and path validation. Windows uses the system shader compiler |
| ModelShell.swift | UI | Status, windows, menu actions and notifications |
| ModelSteam.swift · recovery and progress | Ported | `SteamDownload`, `SteamAttempt` and `SteamDownloadProgress` run validation, stall, stop and resume policy over a typed downloader process and folder seam |
| ModelSteam.swift · helper and UI | Apple-only | Windows supplies the approved Steam downloader and emits typed progress, sign-in and file events. |
| Network.swift · query orchestration | Ported | `ServerQueryClient`, `IUdpTransport`, `UdpExchange`, `UdpReplies` |
| Network.swift · Network.framework connection | Apple-only | Connected Windows UDP sockets and cancellation behind `IUdpTransport` |
| PlayerCard.swift | UI | Native player card and CD-key status |
| Presentation.swift · release rules, mouse math, eligibility | Ported | `ReleaseNotes`, `ReleaseFeed`, `ReleaseNoteItem`, `MouseDistance`, `GameCapabilities` |
| Presentation.swift · other lookups | Ported | `MapCatalog`, `GameModes`, `ServerFacts`, `NewsFeed`, `MediaFacts`, `MatchFacts`, `LauncherText` and `ScrimMath` provide portable presentation rules. |
| Presentation.swift · display copy and composition | UI | Localized date/size/copy for native views and Windows report metadata |
| RootView.swift | UI | WinUI main navigation and workspace |
| ServerDetailsPanel.swift | UI | Server details and actions |
| ServerReadyCheck.swift · state | Ported | `ServerReadySession` evaluates cached observations, refreshes one endpoint without stale responses winning, stages per-game choices and hands a checked request to launch. |
| ServerReadyCheck.swift · presentation | UI | Readiness and rules feedback |
| ServerReadyCheckView.swift | UI | Ready Check view |
| ServerSheets.swift | UI | Direct connect, password and edition-switch dialogs |
| ServersList.swift | UI | Server rows, selection and context menus |
| ServersToolbar.swift | UI | Server search/filter and refresh controls |
| ServersView.swift | UI | Server browser and status banners |
| SettingsAcknowledgements.swift | UI | Windows distribution acknowledgements sourced from components that ship |
| SettingsPanes.swift | UI | Native settings panes |
| SettingsSheets.swift | UI | CD-key, update notes and settings dialogs |
| SettingsWindow.swift | UI | Windows settings window and navigation |
| SetupSteamView.swift | UI | Approved Steam setup screens |
| SetupView.swift | UI | Setup wizard, native file picker and secure entry |
| ShellSheets.swift | UI | Shortcut, privacy and error dialogs |
| SteamDownload.swift | Apple-only | Windows selects a trusted downloader adapter. Portable depot and progress rules are in the ModelSteam row. |
| Theme.swift | UI | Approved tokens, WinUI Fluent resources, contrast and typography |
| WindowsProfiles.swift | Ported | `WindowsProfiles` searches only bounded named layouts and explicitly selected folders through `IWindowsProfileFiles` |
| NativeSetup.h / NativeSetup.m · import bridge | Apple-only | Steam/retail discovery, settings root, and credentials via core seams. `LaunchLink` implements the portable URL rules |

## View model contracts

| Swift file | Inputs | Outputs | Events |
| --- | --- | --- | --- |
| ConfigBindings.swift and InputDevices.swift | Loadout bytes, key identity or reported SDL symbol, catalogue, metadata locks, verified device facts | Binding groups, choices, current keys, reservation/conflict reason, device guesses and learned buttons | Capture, review replace, apply, clear, provide override, learn or skip a button |
| ConfigChains.swift | Profile revision, chain steps, held key or external file bytes, wait policy | Press/release traces, affected binds, conflicts, companion bytes, portable export or refusal | Preview, confirm replace, publish or export |
| ConfigChecker.swift | Selected file or pasted bytes, engine dialect, optional policy | Byte-addressed findings, redacted source lines, suggested corrections | Review and apply a fix, apply all eligible fixes, send the reviewed copy to Import |
| ConfigCheckup.swift | Profile revision, target edition, policy, provenance-bearing measurements | Dials, cited policy differences, suggestions and exact changes | Dismiss, apply selected advice, undo a version-checked receipt |
| ConfigDiscoverModel.swift | Signed catalogue transport/cache, search, filter and optional comparison loadout | Load and retry state, signed entries, teams, saved entries and import rows | Open, refresh, save for later, remove from Saved, review setup |
| ConfigLauncherModel.swift | Setup completion, selection, game ownership, catalogue and active import state | Enabled loadout and import commands | Focus, create, duplicate, rename, export, delete or start import |
| ConfigReadyCheck.swift | Observed server facts, target dialect, exact profile and companion, clock | Cited rule IDs, report status and limitations, temporary fixes | Check, review, stage a fix, clear or revalidate before launch |
| ConfigStudioModel.swift | Catalogue, selected profile or draft, search/filter, compared setting, rules and editor text | Typed values, editable state, comparison ownership and text diagnostics | Search, select, compare, stage an edit and apply |
| ConfigStudioSession.swift | Private profile storage, verified process identities, chosen and active loadouts, launch lease | Draft, exact versions, active selection, recovery receipt, read-only state and launch plan inputs | Focus, edit/rebase/save/discard, reserve/commit/cancel, reconcile, restore or recover |
| Core.swift | Launch inventory, approved media path, bounded crash bytes and service consent, optional CoD2x files | Plan, playable basename, redacted report or refusal, game-exit classification and resource status | Prepare launch, open or send report with consent, import/remove auxiliary assets |
| HostingModel.swift | Saved server, dedicated inventory, eligible data root, release source, process results, UDP status and port availability | Per-server phase, occupied port and suggested retry, status/players, exit problem, export, package progress | Start, retry on port, watch, rotate, kick, stop, stop all, export, get/cancel zPAM |
| MapArtwork.swift | Validated game root handles, map key and requested variation | Image dimensions and straight RGBA, or no art | Decode, replace current request, cache if applicable |
| MatchSummaries.swift | Bounded JSONL stream and recorded match fields | Validated current and legacy matches and measured detail | Reload, choose a match or hand its available measurements to Check-up |
| Model.swift | Verified client identities, setup state, launch request, Studio reservation and recent crash files | Running/attached result, quit state, last exit outcome and report path | Launch, attach, return to game, connect to running client, quit or reconcile |
| ModelLibrary.swift | Private media listings, recent joins, match stream and update dispatches | Sortable media rows, dates, map hint, replay alias, match and news facts | Reload, select, play recording, ask native adapter to rename/recycle/reveal and restore |
| ModelServers.swift | Master/status query adapter, cache, favorites and recents, filters, selected endpoint and password | Display rows, refresh counts, admission or edition prompt, saved join choices | Refresh, stop, favorite, forget, enter password, confirm or cancel edition and join |
| ModelSettings.swift | Stored and working settings, configured release source, clock, live engine and hosting state | Save/reset result, available release, notes, failure stage or install refusal | Edit, reset, flush, check, install, cancel, retry |
| ModelSetup.swift | Discovered or selected normalized game root, stored key presence, secure key entry, privacy service availability, personalization commit | Current step, archive survey and data problem, key state and stored suffix, cancel eligibility | Choose root, continue, replace/keep/skip key, save or skip personalization, answer consent, go back, cancel, finish |
| ModelShell.swift | Running game and hosted-server flags, sequential confirmation answers | Game or server prompt, pending shutdown, or exit | Quit Launcher Only, Quit Both, stop hosted servers, cancel quit |
| ModelSteam.swift | Selected directory, locale, optional free space, process events, folder verification and scheduler | Offer, sign-in, code, progress, current file, estimated time, stopped/resume, missing entitlement, completed data root | Start or resume, request stop, leave setup, accept validated data |
| Presentation.swift | Edition, map/mode, media metadata, date/locale, release and news bytes | English labels, stamps, release dispatches, safe news links and artwork contrast inputs | Recompute if source data or date changes |
| ServerReadyCheck.swift | Current endpoint, target edition, active or selected loadout, latest status reply | Checking state, report, unapplied temporary choice and exact launch receipt | Check Again, use loadout, fix for this server only, undo, hand off to launch |
| WindowsProfiles.swift | Explicit volume and folder roots supplied by the adapter | Ordered profile candidates with active and backup flags, truncation, unreadable-directory count | Rescan and select a config to review through the existing import workflow |

## Copy and resources

The core keeps the Swift English for shared behavior and fixture comparisons.
Windows sentence case, Windows terminology, accessibility labels, and platform
specific references are mapped by the UI from the owner-approved design
canvas. File picker, shell, accessibility and localization behavior belongs to WinUI.

`ConfigCatalog.json`, `ConfigRulesets.json` and `ConfigOptimizer.json` are shared
embedded resources. Usage-data events are absent from the launcher sources at
the branch base and remain a later addition from `feature/usage-data`.
