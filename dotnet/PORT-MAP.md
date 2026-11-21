# Launcher port map

The table covers the 81 Swift files and the Objective-C setup bridge at
`c7ee91656e679b86e6e2f17f58f6b641d1b7ba2c`. Files with more than one destination
have separate rows for their logical parts.

Coverage is 117 rows, with 29 ported, 23 planned, 1 partly ported, 48 UI and 16 Apple-only.

| Swift source or part | Status | .NET type or Windows destination |
| --- | --- | --- |
| AppSheets.swift | UI | WinUI dialogs and owner-window placement |
| Artwork.swift | UI | Approved launcher assets and WinUI image handling |
| BrandShapes.swift | UI | Approved vector assets |
| Components.swift | UI | WinUI controls, shared styles, and accessibility states |
| ConfigBindings.swift | Planned | Binding groups, choices, duplicate and conflict rules |
| ConfigBindsView.swift | UI | Binding editor, capture, and native accessible labels |
| ConfigCatalog.swift | Ported | `ConfigCatalog`, `ConfigCatalogSetting`, `ConfigCatalogCommand`, registration records |
| ConfigChains.swift | Planned | Chain compilation, storage, and validation against the engine |
| ConfigChecker.swift | Planned | Findings and fix eligibility |
| ConfigCheckerView.swift | UI | Check a Config workflow, selection and findings |
| ConfigCheckup.swift | Planned | Recommendations from the configuration and match measurements |
| ConfigCheckupView.swift | UI | Check-up results and tune-for controls |
| ConfigClean.swift | Ported | `ConfigClean` reviews both engine dialects and preserves approved bytes and omission reasons |
| ConfigDirectory.swift · trust and metadata | Ported | `ConfigDirectory` verifies exact Ed25519-signed bytes, closed metadata, revisions and downloaded profile digests. Review of the signed profile is tracked with the import rows below. |
| ConfigDirectory.swift · transport and cache | Ported | `IConfigTextFetcher`, `IConfigDirectoryStorage`, core cache re-verification and saved entries. The platform supplies bounded HTTP and private transactional storage. |
| ConfigDiscoverModel.swift · state rules | Planned | Directory states, retry, and saving selected entries |
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
| ConfigLauncherModel.swift · workflows | Planned | Config Studio import and launch coordination |
| ConfigLauncherModel.swift · dialogs | UI | Open, drop, paste and confirmation flows |
| ConfigProfiles.swift · profiles and journals | Ported | `ConfigProfileStore` owns format, creation, copying, selection, atomic updates and journal replay behind `IConfigProfileFiles` |
| ConfigProfiles.swift · migration, Trash and connection cleanup | Ported | `ConfigProfileStore` replays migration receipts, preserves companions in Trash and disposes verified temporary profiles |
| ConfigProfiles.swift · Darwin persistence | Apple-only | Windows private atomic writes, locks, and reparse-point containment |
| ConfigReadyCheck.swift | Planned | Pre-join preset comparison and temporary override eligibility |
| ConfigRules.swift | Planned | Ruleset and optimizer resources and known-server inference |
| ConfigSettingsView.swift | UI | Guided and expert editors using catalogue controls |
| ConfigShare.swift | Ported | `ConfigShare`, `ConfigShareClient`, `ConfigShareRecords` and `ConfigShareService` implement the service contract and private receipt lifecycle |
| ConfigShareModel.swift | UI | Share flow, progress and feedback over the service client |
| ConfigShareView.swift | UI | Share sheets and preview |
| ConfigSheets.swift · naming | Ported | `LoadoutName` validates blank, duplicate and read-only names |
| ConfigSheets.swift · sheets | UI | Loadout create, rename and select |
| ConfigStudioBinds.swift | UI | Binding editor display models and keyboard diagram |
| ConfigStudioBlocks.swift | UI | Studio controls, banners and feedback |
| ConfigStudioCheckup.swift | UI | Findings copy, severity and accessibility |
| ConfigStudioModel.swift · model rules | Planned | Edit projection, search/filter and ownership checks |
| ConfigStudioModel.swift · view state | UI | Tabs, detail levels and focus state |
| ConfigStudioSession.swift | Planned | Transaction, draft, launch reservation and session reconciliation |
| ConfigStudioStyle.swift | UI | Studio appearance and spacing |
| ConfigsHomeView.swift | UI | Loadout and Player Setups entry actions |
| ConfigsView.swift | UI | Studio container and navigation |
| Core.swift · names and server packets | Ported | `QuakeColors`, `WireParser`, `GameServer`, `ServerPlayer` |
| Core.swift · connect link and engine arguments | Ported | `LaunchLink`, `EngineCommandLine`, `GameLaunchPlan`, `ConfigStudioLaunch`, `ConfigConnectionLaunch`, `WindowsLaunchPlan` |
| Core.swift · demo aliasing | Planned | Safe playback copy name and collision handling |
| Core.swift · edition, discovery and renderer | Ported | `GameEdition`, `GameCapabilities`, `IGameDiscovery`, `GameBinary`, `WindowsRenderMode` |
| Core.swift · Mac bundles and Metal | Apple-only | Installer inventory supplies Windows edition/role paths. Native Windows Direct3D 9 or DirectX 7 selection |
| Core.swift · file and key persistence | Apple-only | Private settings writes via `ISettingsFolder`. Credential Manager and the engine's CD-key ingress via `ISecretStore` |
| Core.swift · key normalization and checksum | Ported | `CdKey` implements normalization, CRC-16 and key-field status |
| Core.swift · data-folder preference and crash selection | Ported in part | `SetupSession.InitialDataPath` preserves an unavailable remembered folder after setup. Exit outcome and crash selection are tracked below with game lifecycle. |
| Core.swift · release offer and checksum | Ported | `ReleasePackage`, `ReleaseOffer`, `UpdateBlock` |
| Core.swift · bundle update | Apple-only | Signed Windows installer, publisher verification and staged update |
| Core.swift · crash reports and CoD2x files | Planned | Consent policy, redaction, report transport, archive checksum and placement |
| Core.swift · settings and display resolution | Ported | `GameSettings`, `GameSettingsJson`, `GameSettingsStorage`, `DisplayResolutions` |
| Core.swift · launcher library and session logs | Planned | Saved library format, output filtering, redaction, retention and recovery |
| HomeLibraryMenus.swift | Apple-only | WinUI context menus and focus restoration |
| HomeView.swift | UI | Home cards and commands |
| Hosting.swift · dedicated start | Ported | `GameLaunchPlan.Dedicated`, `WindowsLaunchPlan.Dedicated` |
| Hosting.swift · form, config, export and status | Ported | `HostedServerJson`, `HostingConfig`, `HostedStatus` and `ZpamRelease` cover saved configs, export, replies and release selection |
| HostingModel.swift · lifecycle and publication | Planned | Starter and shutdown coordination, health polling, private server-home files and release download wiring |
| HostingModel.swift · admin queue and zPAM package policy | Ported | `HostedAdminQueue` and `ZpamPackage` provide paced local commands and verified bounded staging |
| HostingModel.swift · native services | Apple-only | Win32 process supervision and shell actions behind core seams |
| HostingView.swift | UI | Hosted server form, list and state feedback |
| InputDevices.swift · key layout model | Planned | Preserve data model for binding suggestions |
| InputDevices.swift · IOKit and Carbon | Apple-only | Windows HID/device APIs and keyboard layout mapping |
| JumpPalette.swift | UI | Searchable command surface and keyboard navigation |
| LibraryView.swift | UI | Library controls and Windows file preview |
| Main.swift | Apple-only | WinUI entry, app lifecycle, activation, menus, notifications and window routing |
| MapArtwork.swift · archive rules | Planned | IWD lookup, image safety checks and portable decoding |
| MapArtwork.swift · AppKit objects | Apple-only | Windows image decoding and WinUI image surfaces |
| MatchSummaries.swift | Planned | Decode current and legacy summaries and index local history |
| Model.swift · lifecycle | Planned | Coordinate data discovery, private home, owned or attached process and recovery |
| Model.swift · navigation and windows | UI | WinUI shell and model binding |
| ModelLibrary.swift · media | Planned | Library discovery, filtering, replay handoff and containment |
| ModelLibrary.swift · OS actions and UI states | UI | Native open/reveal actions through the appropriate seam |
| ModelServers.swift · browse/refresh logic | Planned | Query batching, favorites, filtering, history and remembered passwords |
| ModelServers.swift · Keychain and native actions | Apple-only | Windows Credential Manager and shell/clipboard adapters |
| ModelSettings.swift · settings lifecycle | Planned | Reset orchestration, eligible update checks and retained state |
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
| Presentation.swift · other lookups | Planned | Map and mode names, safe project news, media and scrim data rules |
| Presentation.swift · display copy and composition | UI | Localized date/size/copy for native views and Windows report metadata |
| RootView.swift | UI | WinUI main navigation and workspace |
| ServerDetailsPanel.swift | UI | Server details and actions |
| ServerReadyCheck.swift · state | Planned | Pre-join readiness state and isolated connection handoff |
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
| ModelSetup.swift | Discovered or selected normalized game root, stored key presence, secure key entry, privacy service availability, personalization commit | Current step, archive survey and data problem, key state and stored suffix, cancel eligibility | Choose root, continue, replace/keep/skip key, save or skip personalization, answer consent, go back, cancel, finish |
| ModelSteam.swift | Selected directory, locale, optional free space, process events, folder verification and scheduler | Offer, sign-in, code, progress, current file, estimated time, stopped/resume, missing entitlement, completed data root | Start or resume, request stop, leave setup, accept validated data |
| WindowsProfiles.swift | Explicit volume and folder roots supplied by the adapter | Ordered profile candidates with active and backup flags, truncation, unreadable-directory count | Rescan and select a config to review through the existing import workflow |

## Copy and resources

The port keeps the existing English from `ConfigEditError`, wire and link
errors, launch validation, and release notes and stamps. The bundled catalogue
carries existing labels, summaries and detail text. Windows-specific launch and
hosting failures expose message keys. File pickers and clipboard interaction
belong to the native UI.

`ConfigRulesets.json` and `ConfigOptimizer.json` belong to the planned rules and
check-up rows. The catalogue embedded in this unit is the shared
`ConfigCatalog.json`, which defines registered settings and commands.
