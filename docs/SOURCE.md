# Source code reference

A guided tour of `src/`: the ten projects, every folder, what each class is for, how the parts connect, and the main
flows end to end. Section 1 was rewritten for the 0.2.0 split (2026-10-20); the later sections describe each area by
its folder, which the split kept (a folder now lives in the project named in section 1). Where the code and this file
disagree, the code wins: fix this file. Each project also has a short `README.md` (what lives there, what it may
reference).

Companion chapters:
- [ARCHITECTURE.md](ARCHITECTURE.md): the layering rules and the decision records (ADR-001 …). This file says *where*
  and *how*; the ADRs say *why*.
- [BUILD.md](BUILD.md): building, testing and releasing.
- [ADDONS.md](ADDONS.md): the add-on format, permissions, the encrypted bridge and the SDK.
- [SECURITY.md](SECURITY.md): the threat model and controls; [TESTING.md](TESTING.md): what the self-test covers.
- [SHARED-LIBRARY-DESIGN.md](SHARED-LIBRARY-DESIGN.md) and [PROVIDER-API-COVERAGE.md](PROVIDER-API-COVERAGE.md).

---

## 1. Shape of the solution

- **Ten projects, one process** (0.2.0, ADR-037). `DarkCipher.ServerManagement.sln` holds nine libraries and the app;
  `src/DcxManager/DcxManager.csproj` builds `DcxServerManager.exe`, a WPF app on .NET 10 (`net10.0-windows10.0.19041.0`,
  x64, nullable and implicit usings on), published as one self-contained single-file exe. Namespaces stayed
  `DcxManager.*` across the split, so a type's namespace still names its folder, not its project. The tree is a local
  git repository (`main`).
- **About 60,000 lines of C#**, plus `Themes/Controls.xaml` (all control styles, in the app), `App.xaml`,
  `MainWindow.xaml`, `Views/HostsView.xaml` and `Views/HostEditWindow.xaml`. Most screens are built in code, not XAML.

### Projects (references point down only; the self-test checks it on the built assemblies)

| Project | Owns | May reference |
|---|---|---|
| `Dcx.Core` | the encrypted store and keyring, settings and every stored record, audit, offline mode and the network policy, the capability registry, masking, the AI switch and lock, the assistant contract, snapshots, schema, updater, help search (+ `Assets/help/help.md`), notes, incidents, documents, and the **seams** (`Services/Ui.cs`) lower code uses instead of the UI. 0.3.0: `EncryptionAudit`, `Profiles`, `SignIn/` (directory bindings, Windows / LDAPS / Entra sign-in, JWT), `DashboardTile`, `VpnProfile`, `ToolboxRun`, `LanLastScan`, `TeamLibraryRef`. No WPF. | nothing of ours (+ `System.DirectoryServices.Protocols` for LDAPS) |
| `Dcx.Connectivity` | `Ssh/`, `Sessions/` (transports, profiles, session logs, `ITerminal`, the AI terminal bridge and command risk), `Files/` (+ `McTools`, `ArchiveReader`), `Reports/`, `Net/` (scanners, `NetTests`, `DnsClient`, `DnsRecords`, `Whois`, `HostPerf`), `Mail/` (Postfix, probes, SPF, DMARC / DKIM, blacklists, policies, header analyser), `Ansible/` (inventory, commands, runners), `Vpn/` (configs, routes, engines); (0.5.0) `Import/` (one file per connection manager, `GcmAnyIv`), `Puppet/`, `Wsl/`, `Ide/`, `BeyondTrust/` (PRA, Password Safe), `Ssh/KerberosSsh` | Core |
| `Dcx.Ui` | the WPF kit: Dialogs, Menus, Hint, NetUi, EnvUi, DetachedWindow, ThemeManager, Typography, NavLayout, UiErrorGuard, ShareService, and the module contracts (`AppModules.cs`) | Core |
| `Dcx.Infra` | Docker, Proxmox, Unraid, vCenter, Hyper-V engines | Core, Connectivity |
| `Dcx.Cloud` | the cloud layer and its providers: Vultr, Contabo, Hetzner, Azure, Google Cloud | Core, Connectivity, Ui |
| `Dcx.Addons` | add-on packages, sandbox, encrypted bridge, host, theme add-ons | Core, Connectivity, Ui |
| `Dcx.Library` | the shared library engine | Core, Ui, Addons |
| `Dcx.Browser` | the portal browser engine | Core, Connectivity, Ui, Infra, Addons |
| `Dcx.Notify` | what leaves the app to tell someone: syslog over TLS, (0.4.0) log sinks — Elasticsearch / OpenSearch, Loki, Azure Monitor / Sentinel, Splunk, Graylog, webhooks — and the Grafana dashboard list, SMTP and notifications; (0.5.0) Microsoft Teams sharing (`Teams/`) and Pingdom checks (`Pingdom/`) | Core |
| `Dcx.Ai` | everything AI as one module (engines, agent loop, AI views, `AiModule`) | everything below the app |
| `DcxManager` (the exe) | `App` (start-up, `WireLayers()` — the composition root that connects every hook the lower projects offer), the main window and `Shell/` (+ `StartupFlow`: profile choice, directory gate, first-run setup), every screen (`Views/`: + `Toolbox/`, `Lan/`, `Editors/`, `AnsibleView`, (0.5.0) `PuppetView`, `WslView`, `BeyondTrustView`, `ConnectionImportDialog`, `TeamsUi`, `IdeUi`, `SetupWindow`, `ProfilePickerWindow`, `DirectoryProfileDialog`, `PinCheckDialog`), app-level services (dashboard overview, `Checks`, `VpnManager`, `LanMonitors`, privacy report, recovery, screenshots, app lock), `Api/`, `Rdp/`, `Themes/`, `Assets/web`, `SelfTest/` (one file per area) | every project |

Only `App.LoadAiModule()` (never inlined) and the self-test name a `Dcx.Ai` type, so with AI features off that
assembly is never loaded (the AI-off self-test asserts it). `InternalsVisibleTo` goes from each project to the ones above
it.
- **Out-of-process pieces:** only add-on programs (sandboxed, per command), the WebView2 runtime (terminal, viewers,
  portal browser), ConPTY console hosts for local shells, and external programs the user asks for (mstsc, Windows
  Terminal, winget, the Foundry Local / Ollama CLIs).
- **Support projects outside `src`:** `tools/guide/Guide.csproj` (the PDF guide's HTML), `tools/msix/MsixTools.csproj`
  (restore-only: Windows SDK build tools) and the add-on SDK in `samples/addons/sdk` (compiled into the exe as
  resources).

### Folder map (by area; the projects table above says which project holds each folder)

| Folder | Namespace | Owns | Lines (approx., 0.1.18) |
|---|---|---|---|
| (root) | `DcxManager` | `App` (start-up, error handling, uninstall clean-up), `MainWindow` (shell, nav, tabs), `Commands`, the self-test (`SelfTest*.cs`) | 1,450 + tests |
| `Services/` | `DcxManager.Services` | encrypted store, settings, models, lock/2FA, audit, snapshots, schema, recovery, backup, network policy, offline mode, environments, theme, typography, updater, packaging; `Ui.cs`: the UI-free seams (`Ui.Dialogs`, `Ui.Window`, `Ui.Thread`) and the error `Log` that everything below the screens uses (0.2.0 phase 1; `Views\WpfUi.cs` is the WPF side) | 5,500 |
| `Sessions/` | `DcxManager.Sessions` | terminal transports: SSH, ConPTY (local shells, WSL), serial, Telnet; terminal profiles; session logs | 1,300 |
| `Ssh/` | `DcxManager.Ssh` | SSH connect (keys, jump hosts, TOFU), remote exec + sudo, tunnels, key tools, LAN scan, mstsc launcher, external ssh | 1,100 |
| `Files/` | `DcxManager.Files` | file systems (local, SFTP, SCP), transfer queue, folder compare/sync | 1,150 |
| `Rdp/` | `DcxManager.Rdp` | the embedded Remote Desktop ActiveX | 140 |
| `Cloud/` | `DcxManager.Cloud` | the provider-neutral cloud layer: catalogs, HTTP runner, generated forms and resource screens | 1,350 |
| `Vultr/`, `Contabo/`, `Hetzner/`, `Azure/`, `Gcp/` | `DcxManager.Vultr` … | one cloud provider each: client (+ auth for Azure / Google), catalog, provider (+ Vultr billing documents) | 2,430 + 2,000 (0.2.0) |
| `Infra/` | `DcxManager.Infra` | infrastructure providers: Docker, Proxmox VE, Unraid, VMware vCenter, Hyper-V; TLS pinning; last-known status | 2,460 |
| `Ai/` | `DcxManager.Ai` | AI engines, agent loop, tool registry, AI workspace, operator, terminal bridge, command risk, Foundry Local, resource guard, reports, help KB | 5,600 |
| `Addons/` | `DcxManager.Addons` | add-on packages, sandbox process, host, encrypted bridge, theme add-ons, the add-on API | 1,550 |
| `Browser/` | `DcxManager.Browser` | the portal browser's model and policies | 380 |
| `Library/` | `DcxManager.Library` | the shared library engine (crypto, share format, client, app integration) | 1,750 |
| `Api/` | `DcxManager.Api` | the loopback local API, tokens, OBS broadcasts | 870 |
| `Notes/`, `Incidents/`, `Documents/` | … | notes store + index, incidents store, PDF text extraction | 800 |
| `Views/` | `DcxManager.Views` | every screen, dialog and window; the terminal; panes; menus | 18,000 |
| `Themes/` | — | `Controls.xaml`: every control style, colours and fonts through `DynamicResource` only | 970 |
| `Assets/` | — | `app.ico`, `logo.png`, `web/` (terminal page + xterm.js), `help/help.md` (built-in help) | — |

Dependency direction (since 0.2.0 enforced by the compiler — the projects — and by the self-test's reference and source
scans): screens are in the app; nothing below the screens calls the UI except through the `Ui` seams; Core, Connectivity
and Infra have no WPF. `Ai/AiClient` is the only code that talks to a model; `Services/OfflineMode` is the only network
gate.

---

## 2. Start-up, shutdown and command-line switches

### `App.OnStartup` (`App.xaml.cs`), in order
1. **`--cleanup data=<0|1> cache=<0|1>`** (run by the MSI uninstaller): `Cleanup` deletes only
   `%APPDATA%\DarkCipher Systems\ServerManagement` (and, if the store was moved, the store/snapshot/session-log *files* at the
   moved place) and/or `%LOCALAPPDATA%\DarkCipher Systems\ServerManagement`, then exits. It ignores `DCX_DATA_DIR` on purpose.
2. **Single instance per store** (`SingleInstance.TryAcquire`, ADR-021). A second launch signals the first
   (`SignalExisting`) and exits before any window, store or browser opens. `--selftest-second` is the self-test's probe:
   exit code 9 means the guard let it through. During `--selftest` a held store writes an early report and exits 3.
3. `SingleInstance.Listen` brings the existing window (or a visible `UnlockWindow`) to the front on a later launch —
   never during the self-test.
4. Error plumbing: WinForms exceptions are thrown (never its own box); `DispatcherUnhandledException → OnUiException`;
   unobserved task exceptions are logged; a terminating `AppDomain` exception and Windows session end call
   `Recovery.Emergency`. The AUMID `darkcipher0x.DarkCipherServerManagement` is set unless packaged.
5. `ThemeManager.Apply(System)`, `Typography.Install()`.
6. **Open the store:** `SecureStore.Initialize()`. If the key needs the master password, `UnlockWindow(needPassword)`;
   if 2FA is on, `UnlockWindow(needCode)`. The self-test never shows these: it writes an early report and exits.
7. **Store recovery and migration:** `StoreRecovery.RunAtStart(ask)` (finish an interrupted restore, offer snapshot
   copies of damaged files, run `StoreSchema.Migrate`). In the self-test every question answers "close" and is logged.
8. `ShareService.Cleanup()`, **`AppSettings.Load()`**, **`AddonPackages.SealExisting()`** (ADR-035: seal data-only add-ons
   installed before 0.1.17), **`AppData.Load()`**, `Workspace.BeginSession()` (crash marker), `Recovery.LoadPrevious()`
   (ADR-022 journal), `SharedLibraries.Load()` (cached libraries; the share is synced later).
9. `--selftest-crash <a> <b>` (only with `DCX_DATA_DIR`): `RecoveryTests.Probe` journals work and waits to be killed,
   or reports what it finds. No UI.
10. `Typography.Apply()`, `AppLock.ReconcileSandbox()`, `NavLayout.ApplyDensity()`, the saved theme,
    `ThemeManager.FollowSystemChanges()`, `ExternalSsh.CleanUp()`.
11. **`new MainWindow()`**. With `--selftest <report>` and `DCX_DATA_DIR` set it is placed at -10000,-10000, not
    activated, not in the taskbar, and `SelfTest.RunAsync(main, report)` drives it. Otherwise it is shown,
    `AppLock.Start()` runs, a background task applies the AI default and Foundry Local's hands-off set-up
    (`AiDefaults.ApplyAsync`, `FoundryManager.AutoStartAsync`), and `AiResourceGuard.Start()` begins watching memory.

### Shutdown (`OnExit`)
`AppLock.Stop`, stop all OBS broadcasts and the local API listener, clean the share folder and the RDP temp folder,
`SecureStore.Forget()` (drops the key from memory). If a snapshot restore requested a restart, the single-instance hold is
released and the app starts again (`Packaging.RelaunchInfo`, through the package when installed from the Store).

### Errors (`OnUiException`, `Services/UiErrorGuard.cs`, s37)
- Each failure is logged once, then counted. More than 8 repeats in 3 s is a loop: `CloseLoopingScreen` closes the tab in
  front (never a terminal, RDP or files tab, never the dashboard).
- `Dispatcher processing has been suspended…` (a window message during drawing) is logged once and never shown.
- The error box is queued at idle priority, at most one at a time, and offers "✦ Explain this error with AI".
- `App.Log` appends to `%LOCALAPPDATA%\DarkCipher Systems\ServerManagement\logs\errors-<date>.log`; once the store is unlocked
  every entry is sealed (`ENC <base64>`, context `errlog`). `App.ReadLogs` decrypts them for Settings → Data & storage.

### `MainWindow` (`MainWindow.xaml(.cs)`)
- `MainWindow.Instance` is the shell: header (ad-hoc `user@host:port` box, branding from a theme add-on via
  `ApplyBranding`), the sidebar, the document area (`Panes`, a `PaneHost`), the status bar (`Status(...)`), and the AI
  dock column (`AiDock`).
- **`BuildNav()`** builds the sidebar sections and items (nav keys in brackets): OVERVIEW (`dashboard`); SERVERS (`hosts`,
  `infra`, `cloud`, `portals` only while the portal browser is on, `incidents`, `notes`, `commander`, `snippets`,
  `sessionlogs`, `tunnels`, `lan`); LOCAL (`local:<shell id>` per detected shell, `serial`); VAULT (`credentials`,
  `knownhosts`, `library` only while a shared library add-on is enabled); AI (`aipanel`, `assistant`, `reports`, `help`);
  SYSTEM (`addons`, `privacy`, `audit`, `settings`). Section order, hidden sections and density come from `NavLayout`;
  collapsed sections are remembered (`RememberNavExpansion`). `PortalBrowser.Changed` and
  `SharedLibraries.FeatureChanged` rebuild it.
- `Navigate(key)` opens or focuses the screen's tab. Interfaces a tab's content may implement: `ICloseable` (may refuse
  to close), `IActivatable` (refresh on selection), `IHostView` (tied to a host, persisted by `Workspace`),
  `ICloudView` (follows a provider account).

---

## 3. Data store and encryption (`Services/`)

### `SecureStore` — the only way to disk
- **Key:** one random 256-bit data key per store, never stored in the clear. `keyring.json` holds it wrapped by DPAPI
  (current user, entropy `darkcipher0x.ServerManagement.v1`) and/or by a master password (PBKDF2-SHA256, 600,000
  iterations, then AES-GCM). "Require on start" deletes the DPAPI copy. The keyring also holds the TOTP secret, hashed
  recovery codes and the last TOTP step (replay guard).
- **File format** (`Seal`/`Open`): `"DCX1" | nonce(12) | tag(16) | ciphertext`, AES-256-GCM, **AAD = the context string**,
  which for store files is the file name. A file renamed to another name, or changed by one bit, fails to decrypt.
- **Writes** (`Write<T>`): JSON → encrypt → `<name>.tmp` → atomic rename, under one lock. `Frozen` drops writes after a
  restore rolled files back underneath the running app.
- **Location** (`DataDir`): `DCX_DATA_DIR` if set (self-test), else the folder named by `%APPDATA%\DarkCipher Systems\ServerManagement\location.json`
  (after a store move), else `%APPDATA%\DarkCipher Systems\ServerManagement`.
- `Encrypt/Decrypt(bytes, context)` and `EncryptString/DecryptString` are used for everything sealed outside whole files:
  audit lines, error-log entries, session-log chunks, add-on files, recovery entries.

### Store files
| File | Owner | Holds |
|---|---|---|
| `keyring.json` | `SecureStore` | the wrapped data key, 2FA (not encrypted itself: it only holds wraps and hashes) |
| `data.dcx` | `AppData` | hosts, credentials (the vault), snippets, tunnels, known host keys, serial profiles, infrastructure connections, portal bookmarks and certificate pins |
| `settings.dcx` | `AppSettings` | preferences, cloud accounts and their tokens, terminal profiles, installed add-ons and their grants, API tokens, AI settings |
| `store.dcx` | `StoreSchema` | the schema version of each kind of store file |
| `note-<id>.dcx` | `NoteStore` | one note each, with its revisions |
| `incident-<id>.dcx`, `incfile-<id>.dcx` | `IncidentStore` | one incident each; screenshots/files as separate write-once files |
| `reports.dcx` | `ReportEngine` | saved server/fleet reports |
| `transfers.dcx` | `TransferHistory` | the last 300 finished transfers |
| `workspace.dcx` | `Workspace` | open tabs (saved hosts only), pane layout, groups |
| `recovery.dcx` | `Recovery` | unsaved editor drafts and unfinished transfers (deleted when empty) |
| `library-<libraryId>.dcx` | `SharedLibraries` | this member's keys and cached copy of one shared library |
| `audit-YYYY-MM.dcxlog` | `AuditLog` | one separately encrypted line per action (actor: `you`, `ai`, `app`, `api`, add-on ids…) |
| `snapshots\*.dcxsnap` | `Snapshots` | rolling snapshots: the raw encrypted store files + SHA-256, sealed again (AAD `snapshot`); the folder can be changed in Settings (`SnapshotDir`) |
| `sessionlogs\*` | `SessionLogger` | terminal logs, one AES-GCM chunk per line, the file name as context |
| `syslog-spool.dcx`, `observe-spool.dcx` | `SyslogForwarder`, `LogShipper` | messages / events waiting for an unreachable collector or sink (at most 5,000 each) |

`DataMover.StoreFiles` defines what moves with the store: `keyring.json`, `*.dcx`, `*.dcxlog`, `workspace.json`, plus the
snapshot and session-log folders.

### The other `Services/` files
| File | What it does |
|---|---|
| `AppData.cs` | `AppData.Current`: the `data.dcx` model; `FindHost` / `FindCredential` fall back to shared-library items (ids `lib_<lib>_<item>`). |
| `AppSettings.cs` | `AppSettings.Current`: the `settings.dcx` model. Read it live — a restore or import can replace it. |
| `Models.cs` | `HostEntry`, `Credential`, `RdpSettings`, `Snippet`, `TunnelDef`, `KnownHost`, `VultrAccount`, `ContaboAccount`, `HetznerAccount` (cloud accounts implement `Cloud.ICloudAccount`); enums `AuthMethod`, `CredentialKind`, `TunnelKind`. |
| `ProtectedSettings.cs` (Core) | Admin-protected settings (ADR-039): the 15 protected items, the HKLM record per Windows user (ACL checked on read), requests (`dcx-protected-change` v1) built by the app and read / committed by the elevated copy, `ChangeAsync` → the app's gate. |
| `Pqc.cs` (Core, 0.6.12) | Post-quantum primitives (ADR-090): ML-KEM-768 and ML-DSA-65 from BouncyCastle 2.7.0 (seeds as private keys), the hybrid combiner `Combine`, the `pq1:<classical>.<post-quantum>` text form, `Works()` self-check. |
| `Docs/DocStore.cs` (Core, 0.6.12) | The Markdown documents (ADR-091): one encrypted `doc-<id>.dcx` per document, folders (`docs-folders.dcx` for empty ones), pins (`host:`, `hostfolder:`, `infra:`), revisions, trash, search, backup snapshot / import. |
| `Authenticode.cs` (Core, 0.6.14) | WinVerifyTrust (whole chain, revocation online or cache-only) and the signer of a file: used by the updater and the Bitwarden CLI check |
| `Infra/UniFi.cs`, `Infra/UniFiCloud.cs` (Infra, 0.6.16) | UniFi (ADR-094): the Network Integration API provider (local or Cloud Connector, path discovery, strict parsing, verified changes) and the Site Manager fleet provider (read-only, adds consoles through the Cloud Connector) |
| `AdminGate.cs` | The app's gate: Hello confirm, stricter values for the session, the request, the installed exe started through UAC (`--protected-change`), the elevated side (`RunElevatedSide`), what follows a change. |
| `SensitiveAction.cs` | "Confirm sensitive actions with Windows Hello" (master password when Hello isn't available). |
| `WindowsHello.cs` (Ui) | Hello consent in front of the window (`IUserConsentVerifierInterop` through its vtable), Hello keys (create / sign / delete), the self-test's fake. |
| `PolicySettings.cs` (Core) | Group Policy (ADR-040): the catalog derived from `AppSettings` minus `Excluded`, categories, value rules, reading HKLM / HKCU, `ApplyTo` (wired as `AppSettings.Enforce`). |
| `PolicyTemplates.cs` (Core) | ADMX, en-US ADML and POLICIES.md from the catalog (`--export-policy-templates`). |
| `Uninstall.cs` | The uninstaller's clean-up: `CleanUser` (`--cleanup`, as the user) and `CleanMachine` (`--cleanup-machine`, as SYSTEM). |
| `AppLock.cs` | Locks every window behind `UnlockWindow` after inactivity (checked every 20 s with `GetLastInputInfo`), on Windows lock, or on demand; sessions keep running. Sandbox mode reconciliation. |
| `Totp.cs` | RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s). |
| `AuditLog.cs` | `AuditLog.Write(actor, target, action, result)` / `Read(month)`; per-line encryption so appends never rewrite the file. |
| `Backup.cs` | `.dcxbackup` files: PBKDF2-SHA256 (600k) → AES-256-GCM (label `backup`); full or selective (a host folder plus exactly the credentials, jump hosts, tunnels and host keys it needs); import merges (skip / replace / keep both) or replaces. |
| `DataMover.cs` | Moves the store: pre-flight → copy → SHA-256 compare → decrypt every file at the new place → switch the pointer atomically → the old copy is kept until confirmed. |
| `Snapshots.cs` | Rolling encrypted snapshots (ADR-009); restore plans (`keyring.json` never replaced; audit logs only added back). |
| `StoreSchema.cs` | Schema versions and migrations per kind (`Meta`, `Migration`); refuses a store newer than the app. `StoreRecovery` runs at start-up before anything loads. |
| `Recovery.cs` | The crash-recovery journal (ADR-022): written 2 s after a change, at once on a fatal error / session end / exit. |
| `Workspace.cs` | Crash marker + open-tab persistence; terminals come back disconnected. |
| `SingleInstance.cs` | One instance per store (named per store identity); activation signal for the running instance. |
| `NetworkPolicy.cs` | `NetClass` and `NetworkPolicy.Classify`: the one classifier for "does this destination cross the external trust boundary?" (AI gate, Settings badges, Privacy view). |
| `OfflineMode.cs` | `Offline`: offline / air-gapped mode (ADR-016). `Offline.Handler()` wraps every `HttpClient` (URI check + a connect callback that connects only to the checked addresses: no DNS-rebinding gap); `EnforceAsync`/`Run` for SSH, RDP, web windows and launchers; `OfflineBlockedException`; blocked attempts are audited and listed. |
| `Environments.cs` | `Envs`: environment labels (PRODUCTION / TEST / LAB / LOCAL, ADR-017) and `Envs.Confirm` (type the host's name in PRODUCTION); `EnvTab` paints tabs. |
| `PrivacyReport.cs` | The facts behind Privacy & network: posture, every configured destination with its class, listening ports. |
| `PasteGuard.cs` | Inspects text before a terminal paste: multi-line, hidden control characters (e.g. an embedded `ESC[201~`), destructive one-liners. |
| `ThemeManager.cs` | The house theme (ADR-038): light/dark palette brushes, the theme packs (ADR-087: DarkCipher default, Graphite, Teal, High Contrast, and Neutral / Slate / Arctic with their own dark and light palettes and accents), accent swatches and the 3:1 `CheckAccent`, status intensity, Windows high contrast, dark title bars, theme add-on overlay (extra keys an add-on doesn't name are derived from its colours; `Env*` colours are never themable), `Normalize` for settings from elsewhere. |
| `Icons.cs` | `Icons.For(glyph)`: the app's glyphs mapped to Segoe Fluent Icons code points (the `IconFont` resource); `Pin`, chevrons. |
| `Typography.cs` | The one place for fonts and UI scale (ADR-001/015): `UiFont`, `ChromeFont`, `MonoFont`, `EditorFont`, the `FontStep` ramp, `Typography.Size(window, w, h)`. |
| `NavLayout.cs` | Sidebar order, hidden sections, density, sidebar styles and widths, pinned pages (at most 6), corners (`Radius` / `RadiusCard`) and motion (`Reduced`, `MotionChanged`); `Normalize` (pure logic, tested). |
| `TabGroups.cs` | Named, coloured tab groups for the session (ADR-019). |
| `Overview.cs` | `LiveSessions` events and the dashboard model (`Overview.Facts`, `Warning`, cards; ADR-018). |
| `Screenshot.cs` | Screenshots of terminals (WebView2 captures itself), RDP and panes (`PrintWindow` with a screen-copy fallback). |
| `ShareService.cs` | The Windows Share sheet via `IDataTransferManagerInterop`; a private temp folder emptied at start and exit. |
| `StorageGuard.cs` + `SyncDetector.cs` | Warn when the store or an export sits in OneDrive/Dropbox/a network share; detection via the Cloud Files sync-root registry first. |
| `UiErrorGuard.cs` | The repeating-UI-exception guard (above). |
| `Updater.cs` | User-initiated updates (ADR-008), see section 13. |
| `Packaging.cs` | `IsPackaged` (MSIX identity), `RelaunchInfo` (ADR-029). |

---

## 4. Terminals and sessions (`Sessions/`, `Views/TerminalView.cs`, `Views/WebHost.cs`)

**The model (ADR-004).** One terminal view, many transports.
- `SessionTarget` (abstract) says what a tab connects to and creates a **fresh transport for every (re)connect**:
  `SshTarget` (`Sessions.cs`), `LocalTarget` (`LocalShells.cs`), `SerialTarget` (`Serial.cs`), `TelnetTarget`
  (`Telnet.cs`). `SessionCaps` says what the type supports, so menus show only what works.
- `ITerminalTransport` is the byte pipe; `IByteWriter` marks transports that take encoded bytes (SSH, Telnet) so a
  profile's encoding applies to input too.
  - `SshTransport`: an SSH.NET shell channel.
  - `ConPtyTransport` (`ConPty.cs`): a local console program (PowerShell, pwsh, cmd, `wsl.exe -d …`) on a Windows pseudo
    console; headless, two anonymous pipes, VT both ways.
  - `SerialTransport`: System.IO.Ports, encoding conversion, line endings, local echo, BREAK/DTR/RTS (`ISerialControl`),
    "device removed" + re-resolve on replug.
  - `TelnetTransport` + `TelnetParser`: minimal negotiation (ECHO, SGA, NAWS, TTYPE `XTERM-256COLOR`), IAC escaping. Off
    unless Settings → Terminal → "Allow Telnet".
- `LocalShells` finds shells without running anything (PowerShell, cmd, PowerShell 7, WSL distributions from the Lxss
  registry, Docker Desktop's internal distributions skipped). `SerialPorts` lists ports with USB identities.
- `TerminalProfiles` + `TerminalProfile`, `KeyMap`, `TermScheme`: per-host/per-tab profiles (colours, cursor, encoding,
  scrollback, key mappings). `SessionLogger` / `SessionLog`: encrypted, readable session logs (ADR-019).

**The view.** `TerminalView` hosts xterm.js in WebView2 and owns everything the user sees: the bounded output pipeline
(a chunk queue capped at 6,000,000 pending characters, 200,000 per flush), `RequestQuickFlush` (echo at Normal priority,
floods stay on the timer; s39), an ordered background writer for input (`SendInput`), paste safety, links, search, menus,
auto-reconnect with back-off (never for an authentication failure: `SessionTarget.IsAuthFailure`, s37), screenshots,
broadcast input (`InputBroadcast`), renderer recovery (a killed WebView2 renderer is replaced and the session survives,
ADR-021). `SessionId` (8 hex chars) and `Epoch` (connect count) identify a live session for the AI bridge.

**The page.** `WebHost` keeps one WebView2 environment (user data folder under the cache, or `<DCX_DATA_DIR>-webview2`
in the self-test), unpacks `Assets/web/*` from the exe, and serves `https://dcx.term/*` through
`WebResourceRequested` (not a folder mapping, which aborted navigations on runtime 155). `WebHost.Paint` sets WebView2's
default background to the theme colour before the core starts (no white flash). `WebHost.GuardOffline` applies offline
mode to web windows. `WebWindow` is the window for the Vultr web console, bare-metal VNC and the Vultr portal.

**SSH plumbing (`Ssh/`).** `SshConnector` opens `SshSession<T>` (SSH or SFTP client plus jump-host hops and their
forwards): password, private key (OpenSSH, PEM, PuTTY `.ppk`), keyboard-interactive, chained jump hosts, trust-on-first-use
host keys (`HostKeyRejectedException` on change), and `CredentialFor` audits use of a shared-library credential.
`RemoteExec` runs commands (sudo password on stdin, never on the command line); `SshPool` keeps one management
connection per host for dashboards. `TunnelManager` runs -L/-R/-D forwards, one connection each. `KeyTools` reads,
generates and installs keys. `LanScanner` finds SSH banners on a subnet. `LinuxProbe` gathers a host's facts in one
round trip (`ServerFacts`, `DiskInfo`, `Fmt`). `ExternalSsh` opens Windows Terminal with OpenSSH (a vault key goes to a
current-user-only temp file deleted seconds later). `RdpLauncher` uses mstsc with a generated `.rdp` and a short-lived
`TERMSRV/<host>` credential.

**Remote Desktop (`Rdp/RdpControl.cs`, `Views/RdpView.cs`, ADR-013).** `RdpAxHost` (WinForms `AxHost`) creates the newest
registered `MsRdpClient`; properties through `IDispatch`, the password through `IMsTscNonScriptable`, events through the
`IMsTscAxEvents` connection point (`RdpEventSink`). `RdpView` puts it in a `WindowsFormsHost` tab.

---

## 5. Files (`Files/`, `Views/CommanderView.cs`, `SftpView.cs`, `SyncWindow.cs`)

- `IFileSystem` is the contract the Commander, transfer queue and AI file tools share; `FsItem`, `FsRef`
  (`local` / `host:<id>`).
- `LocalFs` (deletes go to the Recycle Bin via `RecycleBin`), `SftpFs` (lazy connect, reconnect, jump chain, vault key),
  `ScpFs` (SCP fallback via shell commands for listings and SSH.NET's `ScpClient` for data, ADR-014), `HostFs` (SFTP, or SCP
  when the host says so or the server refuses the SFTP subsystem).
- `TransferQueue` + `TransferJob`: app-wide, two jobs at a time, each with its own connections; every file goes to
  `<name>.dcxpart`, is verified by SHA-256 (default) and renamed over the target; part files resume; retries with back-off.
  `TransferHistory` keeps the last 300.
- `FolderSync`: compare (size + time with 2 s tolerance, or SHA-256) and plan (Update, Mirror, Two-way); `SyncWindow`
  reviews every action before it goes through the queue.

---

## 6. Cloud providers (`Dcx.Cloud`: `Cloud/`, `Vultr/`, `Contabo/`, `Hetzner/`, `Azure/`, `Gcp/`, ADR-020/028/031/037)

- **Contracts** (`CloudProvider.cs`): `ICloudApi` (one signed-in account; JSON in, JSON out, `CloudApiException` with a
  safe message and the provider's request id; `Uncertain` when a change timed out), `CloudProvider` (catalog, accounts,
  `CloudCaps`, neutral helpers such as `CloudInstance`), `CloudProviders.All` (the registry), `ICloudAccount`,
  `ICloudView`, `CloudExtra` (a provider-specific toolbar button).
- **Catalog model** (`CloudCatalog.cs`): `ResourceDef` (a screen), `Col`, `ActionDef` (method, path with `{field}`,
  `{parent.field}`, `{form.field}` templates, `When`, `Confirm`, `Special`, `Fixed`, `NoRow`), `Field` + `FieldKind`,
  `Lookup` (a pick list fed by another endpoint).
- **Engine:** `CloudHttp` (finite timeouts, retries only where repeating is safe: 429 honouring `Retry-After`; **orders are
  never retried**), `CloudProgress`, `CloudJson` (`J`, `JRow`, `Tpl` escapes every templated value in URLs),
  `CloudFormWindow` (forms generated from `Field`s, Preview JSON), `CloudResourceView` (list, details, create, edit,
  delete, actions, child lists; each action enabled only in its states and never twice at once), `CloudReferenceView`
  (regions, plans, images as tabs), `CloudDocuments` (any list as a printable document).
- **Providers:** each is a client + a catalog + a provider subclass.
  - Vultr: `VultrClient` (API v2, Personal Access Token, cursor pagination), `VultrCatalog`, `VultrProvider` (web console,
    bare-metal VNC, kubeconfig, startup scripts, invoices), `VultrDocuments` (printable invoices from line items).
  - Contabo: `ContaboClient` (OAuth2 password grant at auth.contabo.com, token in memory per account), `ContaboCatalog`
    (from `api-v1.yaml`), `ContaboProvider` (no delete/resize/console — the API doesn't offer them).
  - Hetzner: `HetznerClient` (project token as Bearer; a token marked read-only is never sent with a change),
    `HetznerCatalog` (from `cloud.spec.json`), `HetznerProvider` (no console claimed).
  - Azure (0.2.0): `AzureAuth` (Entra client-credentials grant with a certificate: a PS256 client assertion with the
    certificate's SHA-256 thumbprint, signed by `Cloud/CloudJwt`; the .pfx opened with an ephemeral key set per sign-in;
    the token in memory only), `AzureClient` (Resource Manager for one subscription: `_SUB_` in catalog paths, links
    followed only on management.azure.com, `nextLink` pages, long-running changes followed through
    `Azure-AsyncOperation` / `Location`, read-only refused locally), `AzureCatalog` (16 screens, item paths are resource
    ids filled with `{path:id}`), `AzureProvider` (stop = deallocate, resize and API explorer as Specials).
  - Google Cloud (0.2.0): `GcpAuth` (service-account key file; RS256 JWT-bearer grant with the key id; key files naming
    another token endpoint refused; the read-only scope while the project is read-only), `GcpClient` (full URLs on an
    allow-list of Google API hosts, `_PROJECT_` for the project id, other projects refused — the project number only
    after reading it from Resource Manager; aggregated lists flattened; `nextPageToken`; Compute / GKE / Cloud SQL
    operations and Cloud Run long-running operations followed; synthetic `_self`, `_zone`, `_type`, `_ip`, `_location`,
    `_short` fields), `GcpCatalog` (15 screens), `GcpProvider`.
  - Engine additions for them: `{path:field}` / `{path:parent.field}` (a resource path escaped segment by segment; `.`,
    `..` and empty segments refused), nested form bodies (`J.SetPath` / `J.GetPath`, `{form.a.b}`),
    `ResourceDef.CreateMethod` (PUT creates) and `ResourceDef.IsChild`.
- **UI:** `Views/CloudWorkspaceView.cs` is the single **Cloud providers** workspace (nav key `cloud`): a tree of every
  registered provider, its account and its catalog groups. `Views/ApiExplorerView.cs` calls any Vultr endpoint. The
  account cards are `Views/Settings/SettingsView.CloudAccounts.cs` (Vultr, Contabo, Hetzner), `SettingsView.Azure.cs`
  and `SettingsView.Gcp.cs`.
- The coverage of each provider's API is tracked in [PROVIDER-API-COVERAGE.md](PROVIDER-API-COVERAGE.md).

---

## 7. Infrastructure (`Infra/`, `Views/InfraView.cs`, ADR-007/028/031)

- `Infra.cs`: `ProviderConfig` (saved in `data.dcx`; secrets referenced by `CredentialId`), `ProviderCaps` (discovered,
  never assumed), `InfraObject`, `InfraAction` (+ `Consequence`, `InfraInput` values passed as data, never spliced into a
  command line), `ActionResult` (**Verified** only after the provider re-read the object), `OfferedAction` (disabled with
  the reason), `IInfraProvider`, and `Infra` (factory, `FillDefault`).
- Providers:
  - `DockerProvider` with two backends: `SshDocker` (docker CLI over SSH, `--format '{{json .}}'`) and `ApiDocker` (Docker
    Engine API over Docker Desktop's named pipe or any `HttpClient`).
  - `ProxmoxProvider`: REST API with an API token; what is offered follows the token's privileges (`/access/permissions`).
  - `UnraidProvider`: GraphQL with an API key; introspects the schema first and only uses what this server has; falls back
    to probing when introspection is off (ADR-007 addendum).
  - `VSphereProvider`: vSphere Automation REST (`/api`), session sign-in once with the vault credential.
  - `HyperVProvider`: the Hyper-V PowerShell module, locally or over WinRM/CIM.
- `TlsPin` + `ITlsPinned`: TOFU certificate pinning shared by Proxmox, Unraid and vCenter. `InfraStatus`: last-known state
  in memory only (the dashboard and the Infrastructure screen share it).
- `InfraView` shows each provider read-only until "Allow changes" is ticked; disabled actions show why.

---

## 8. AI (`Ai/`, `Views/AiPanel.cs`, `AiDock.cs`, `AssistantView.cs`, ADR-003/023/024/026/028/034)

### Engines and the front door
- `AiEngines.All`: `npu` (Foundry Local, NPU or CPU — the default), `ollama-gpu`, `ollama-cpu`, `openai` (any
  OpenAI-compatible server), `anthropic` (Messages API, or Ollama which speaks it), `off`.
- **`AiClient`** is the only code that talks to a model. `ChatAsync(messages, tools, schema, ct, onDelta)` streams on every
  engine (Ollama NDJSON, OpenAI/Foundry SSE, Anthropic SSE) through one pooled HTTP client; messages use Ollama's shape
  internally and are translated per engine. Before anything leaves the process `Redact` masks secrets; a non-local
  endpoint is refused unless allowed (`Blocked`, `IsLocal` via `NetworkPolicy`), and per-feature switches (`AiFeature`)
  apply. Also: `StructuredAsync` (JSON schema answers), `VisionAsync` (verified image support), `ModelsAsync`, `Warm`
  (on the first typed character), `UseModel` (per-chat model), `AiTiming`.
- `OllamaClient` (base URL: this PC, LAN, or through an SSH tunnel), `FoundryManager` (hands-off Foundry Local: winget
  install, service start, health probe, repair, model choice from this PC's catalogue, download and load), `AiHardware` +
  `AiDefaults` (RAM/GPU/NPU facts → the default model), `AiResourceGuard` (caps context and unloads idle local models under
  memory pressure; never kills anything), `ClaudeCode` (Claude Code CLI pointed at a local Ollama).

### The agent loop and tools
- **`AiAgent.RunAsync`** is the multi-step loop both AI surfaces use: plan → tool calls → observe → continue, up to
  `AiMaxSteps`, cancellable; independent read-only calls of one round run in parallel; a repeat guard; models without
  native tool calling get the same tools through a `<tool_call>{json}</tool_call>` text protocol (`AiMaxSteps` defaults to 12) (hidden from the user by
  `ToolTextFilter`). It reports `AgentStep`s (the STEPS log) and an `AgentRun` (steps, tool calls, first token, total).
- **`Services/Capabilities.cs`** (0.2.0 phase 2; was `Ai/AiTools.cs`) is the app's capability registry (`Capability`:
  name, description, `CapabilityRisk` Read / Private / Change, parameters, handler, `GatesItself`). It knows no feature:
  the composition root (`App.OnStartup`) sets `Capabilities.BuiltIns` to register, in this order, `OperatorCapabilities`
  (root; the operator's 17 former built-ins: `list_hosts`, `host_overview`, `read_logs`, `list_services`, `service_action`,
  `run_command`, `vultr_list_instances`, `vultr_instance_action`, `vultr_get`, `cloud_list_instances`,
  `cloud_instance_action`, `list_files`, `read_file`, `transfer_files`, `make_directory`, `rename_path`, `delete_path`;
  each asks with its own wording) and `FeatureCapabilities` (root; was `AiTools.RegisterBuiltIns`: `current_context`,
  `infra_*`, `cloud_list`, `cloud_action`, the `terminal_*` tools, notes, incidents, `read_pdf`, `transfer_status`,
  `portal_list`, `portal_open`, `library_list`). `Capabilities.Schema()` is the one tool list (cached while unchanged);
  `Capabilities.RunAsync` runs any of them with a `CapabilityContext` (approval callback + actor). The AI operator, the AI
  Engineer and add-ons (`app.call`) all go through it. `Ai/AiSelection.cs` tells the AI what the user is looking at
  (names and kinds only). `Services/Redaction.cs` is the secret masker (`AiClient.Redact` forwards to it).
- **`Assistant`** is the AI operator's chat (sidebar → AI operator, `AssistantView`): the system prompt, the agent loop
  with this chat's approvals as actor `ai`, and `AllTools` (= `Capabilities.Schema()`). Reads may auto-run (a setting);
  every change is shown and approved; everything is audited.
- **`AiWorkspace`** is the one AI Engineer workspace (conversations `AiConversation`, explicit context items
  `AiContextItem`, provider/model, running state). `AiPanel` presents it; `AiDock` docks, collapses, hides or detaches it
  into an `AiWindow` without starting a second conversation. `AiActions.AskAsync` is how "✦ … with AI" buttons post into
  it; `AiContext` builds safe per-object context (never secrets or notes); `AiSendPreview` shows "DATA BEING SENT TO AI".

### The terminal bridge (`TerminalBridge.cs`, `CommandRisk.cs`)
1. The user chooses one target session (`Choose`), captured as a `TerminalTarget` with its `SessionId` and `Epoch`; if
   the terminal reconnects or closes the target is stale and refused.
2. `CommandRisk.Assess` reads the command conservatively (POSIX shells and PowerShell; split on `;` `&&` `||` `|` and new
   lines; anything not recognised as read-only is a change): `RiskLevel` ReadOnly / Change / Sensitive / Destructive,
   plus `Interactive`, `MultiLine`, `Endless`. Since 0.1.18 `powershell|pwsh -Command|-c "…"` is judged by its inner command
   (`-EncodedCommand`, `-ExecutionPolicy`, `-File` are not unwrapped), more reading cmdlets are read-only, and a method
   call on `(…)` or a `$variable` counts as a change.
3. `Review` shows it in full with target, user, directory and environment; stricter in PRODUCTION. `ReviewOrAuto`
   auto-runs only when `AiAutoRunReadOnlyTarget` is on, the command is read-only, not interactive, not endless, and the
   target is not PRODUCTION (`WhyNotAuto`).
4. `RunAsync` types the command wrapped between unique markers and waits for the end marker (bounded; Stop sends Ctrl+C):
   - POSIX: `printf` begin marker; `{ <command>\n}`; `printf` end marker with `$?` and `$PWD`.
   - PowerShell (0.1.18): the helper `function global:__dcx` (`PsHelper`) is typed once per session and epoch
     (`HasHelper`); afterwards the line is `. __dcx <nonce> '<command>'` (dot-sourced, so variables persist; quotes,
     including typographic single quotes, doubled). Multi-line, control or non-ASCII commands go as `-__dcxB '<UTF-8
     base64>'`. If the shell answers "term '__dcx' is not recognized", the helper is typed again once.
5. `Parse` extracts output, exit status and directory; `ForAi` masks and bounds it (`MaxForAi` 8,000 characters); the audit
   log gets target, risk and the masked command, never the output.

### Other AI pieces
`Reports.cs` (`ReportEngine`: collect over SSH → `LogDigest` → rule findings → optional AI pass → Markdown/HTML;
saved in `reports.dcx`), `LogDigest.cs` (deterministic log clustering, the "AI off" analysis), `HelpAi.cs` (the help's
optional grounded answer; the offline BM25 search over `help.md` is `Services/HelpKb.cs`, which leaves out articles
tagged `module: ai` unless the AI module is registered), `AiOverview.cs` (the AI's dashboard tile and Privacy & network
rows, contributed by `Views/AiModule.cs`), `Views/ScreenshotAi.cs` (redact-then-send screenshots to a verified vision
model), `Views/FoundryPanel.cs` (Foundry Local set-up card).

---

## 9. Add-ons (`Addons/`, `Views/AddonsView.cs`, ADR-012/030/032/033/035)

The format, permissions, protocol and SDK are in [ADDONS.md](ADDONS.md). The code:

| File | What it does |
|---|---|
| `AddonPackages.cs` | `AddonManifest`, `AddonCommand`, `InstalledAddon` (stored in `settings.dcx`: manifest, granted permissions, folder, per-file SHA-256, package hash, signature text, `Sealed`, run log), `PackageInfo`, the permission `Catalog`, and `AddonPackages`: `Inspect` (zip limits, safe paths, `Validate`, per-kind data checks, downgrade refusal, Authenticode of an `.exe` entry), `Install` (re-hash, stage, seal data-only files, keep old grants on update, audit), `ReadInstalled` (decrypt + hash check), `SealExisting`, `VerifyFiles`, `SetEnabled`, `SetPermission`, `Remove`. Install root: `%LOCALAPPDATA%\DarkCipher Systems\ServerManagement\addons\<id>\<version>` (`<DCX_DATA_DIR>-addons` in the self-test); writable data: `%USERPROFILE%\AppData\LocalLow\DarkCipher Systems\ServerManagement\addons\<id>`. |
| `AddonHost.cs` | `RunAsync` runs one command: enabled? offline mode? API 2? files unchanged? → `SandboxProcess.Start` → the encrypted handshake → `invoke` → answer method calls (each checked against the grants, app calls on the UI thread) → `done`. Caps: 2,000 messages, 1 MB, 256 KB per line, the manifest timeout. Also the credential and file prompts and `ChildEnvironment`. |
| `AddonBridge.cs` | The app's end of the API 2 bridge: ephemeral ECDH P-256, HKDF-SHA256, AES-256-GCM per direction with a counter nonce. |
| `SandboxProcess.cs` | `CreateProcessAsUser` with a Low-integrity token, suspended, in a Job object (kill on close, 512 MB per process, 8 processes, die on unhandled exception), only the three pipe ends inherited (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`), protected process/thread DACLs (`ProcessSddl`, `ThreadSddl`). |
| `ThemeAddons.cs` | `ThemePack.Parse` (strict theme.json), `DecodeLogo` (PNG only, ≤ 1 MB, ≤ 2048 px, one frame), `ThemeAddons.Active/Use/Reapply/Peek` (one theme in use; verified on every apply). |
| `AddonApi.cs` | **New in 0.1.18 (ADR-036), the full-control API:** `Capabilities` (`app.capabilities`), `CallAsync` (`app.call`: any operator or `AiTools` capability through `Assistant.RunToolForAsync`, gated by `app.read` / `app.private` / `app.control` by the tool's risk, changes approved through `Approver`, audited as `addon:<id>`), and the `AddonMethod` registry (`Register`, `Find`) with the built-ins `hosts.add`, `hosts.update`, `hosts.remove` (`hosts.write`) and `ui.open`, `ui.openHost` (`ui.control`). `AddonHost.Call` falls through to registered methods for any name it doesn't handle itself. |
| `Views/AddonsView.cs` | `AddonUi` (install with the approval screen, run, menu entries for host/terminal commands) and the Add-ons screen (permissions, commands, run log, THEME, browser and library sections). |

Data-only add-on kinds hook in elsewhere: the theme via `ThemeManager`/`Typography`/`MainWindow.ApplyBranding`, the
portal browser via `Browser/PortalBrowser.cs`, the shared library via `Library/SharedLibraries.cs`. `AddonPackages`
calls `ThemeAddons.Reapply`, `PortalBrowser.Notify` and `SharedLibraries.Notify` after every install, enable/disable and
removal.

---

## 10. Portal browser (`Browser/PortalBrowser.cs`, `Views/BrowserView.cs`, `Views/PortalsView.cs`, ADR-032)

- `PortalBookmark` (global, or a host's web UI when `HostId` is set) and `WebCertPin` live in `data.dcx`
  (`AppData.Portals`, `AppData.WebPins`).
- `PortalBrowser.Enabled` = `AppSettings.PortalBrowserBuiltIn` (default on since 0.1.17; Settings → Integrations) **or**
  an enabled, verified portal browser add-on. `BrowserPack` parses the add-on's `browser.json` (name, notes, ≤ 50 starter
  bookmarks). `Portals` holds bookmarks, profiles, pins and policies.
- `BrowserView` is one tab: WebView2 in the profile of the bookmark's environment (separate cookie jars), an address bar
  with a security chip (`WebSecurity`), TOFU pinning of self-signed certificates per host:port, downloads that always
  ask, pop-ups as tabs, password saving/autofill off, vault sign-in only on the bookmark's own origin, offline mode on every
  request.
- `PortalsView` (nav key `portals`) manages bookmarks, starter bookmarks, download folder, open-in mode, pins and profiles;
  `PortalUi` adds "🌐 Open web UI" to the host menu.

---

## 11. Shared library (`Library/`, `Views/SharedLibraryView.cs`, `SharedLibraryUi.cs`, ADR-033)

Design: [SHARED-LIBRARY-DESIGN.md](SHARED-LIBRARY-DESIGN.md). The code:
- `LibraryCrypto.cs`: `MemberKeys` (P-256 ECDH + ECDSA per member), `Wrap` (ECDH-ES → HKDF-SHA256 → AES-256-GCM, like HPKE
  base mode), sealing helpers.
- `LibraryFormat.cs`: the share's files — `library.json` (`LibraryHeader`, owner keys, `OwnerTransfer` chain),
  `roster.json` (`Roster`, `RosterEntry`, `RemovedEntry`), `meta.json` (`Meta`, `MetaInfo`), `collections/<cid>/keys.json`
  (`KeysFile`), `collections/<cid>/items/*` (`ItemFile`, `ItemPayload`), `requests/<member>.json` (`JoinRequest`),
  `audit/` (`AuditGen`, `AuditLine`); `KeyBackup` (`.dcxkey`, PBKDF2 600k); `ShareIo` (temp-file-then-rename writes,
  bounded reads); `LibraryProfile` (the add-on's `library.json`, strict, no secret).
- `LibraryClient.cs`: one library for one member — `Create`, `Join`, `Sync` (everything read from the share is verified
  before it is believed), `SaveHost`/`SaveCredential`/`DeleteItem`, conflicts (kept, never merged silently), members
  (`Requests`, `Approve` after a fingerprint check, `Deny`, `SetAccess`, `Remove` → re-key + rotation tasks), ownership
  hand-over, `ExportKeys`/`ImportKeys`, the sealed hash-chained audit. Its state is `LibraryState`, saved as
  `library-<id>.dcx` in the member's own store, never on the share.
- `SharedLibraries.cs`: the app integration. One client per enabled, verified library add-on whose keys are on this PC;
  shared hosts and credentials are materialised as their own objects with ids `lib_<lib>_<item>` (never added to
  `AppData`, so a sync can't overwrite local items); a folder watcher and an optional interval sync; `Used` audits a
  shared credential's use; `WritePackage` writes a team add-on (`com.darkcipher.library.<first 12 of the id>`, **api 2**).
- Hooks: HostsView (a Shared node per library, ⇄ rows), CredentialsView (Source column), HostEditWindow (offers shared
  credentials), `SshConnector.CredentialFor` (audit), `AddonHost` (`library.read`), `HostEntry`/`Credential.SharedFrom`.

---

## 12. Local API, broadcasts, notes, incidents

- 0.6.12 Docs (ADR-091): `Views/Docs/DocsView.cs` (library tree, editor with split / edit / preview, toolbar, smart lists, outline, revisions, import / export, pins) and `Views/Docs/MarkdownRenderer.cs` (our own Markdown → FlowDocument: https / mailto links only, no images fetched, HTML as text); "📄 Docs" in the host and infrastructure menus (`MainWindow.OpenDocs`).
- 0.6.12 post-quantum SSH: `Ssh/SshPq.cs` orders the key exchanges (ML-KEM / NTRU Prime hybrids first, or only with `PqSshRequired`) and labels the negotiated one on the session strip.
- 0.6.14 Bitwarden (ADR-093): `Dcx.Connectivity/Bitwarden/` (`BitwardenCli.cs` finds, verifies and runs bw / bws with secrets only in their environment; `BitwardenVault.cs` the vault: API-key sign-in, unlock, sync, lists without secrets, one item read, lock and idle lock; `BitwardenSecrets.cs` Secrets Manager; `BitwardenModels.cs`), `Services/BitwardenSessions.cs` (host links, in-memory credentials for sessions), `Views/BitwardenView.cs` (VAULT → Bitwarden), `Views/SecretClipboard.cs` (copies kept out of clipboard history and cleared), `Views/Settings/SettingsView.Bitwarden.cs` (the six cards).

- `Api/LocalServer.cs`: a raw `TcpListener` on 127.0.0.1 only, a small HTTP/1.1 subset (GET/POST, bodies ≤ 64 KB,
  headers ≤ 8 KB, 5 s per request, one request per connection, ≤ 32 at once). `LocalApi.cs`: endpoints (inventory without
  secrets, incident events, notes) behind Bearer tokens with scopes; browser requests (Origin header) refused; failures
  rate-limited and audited. `ApiTokens.cs` (only the SHA-256 of a token is stored; `RateLimiter`).
  `Broadcasts.cs`: OBS broadcasts — explicit source (`TerminalSource`, `IncidentSource`), unguessable URL, expiry, frames
  every 400 ms redacted by `BroadcastRedactor` (also used for `terminal.screen` in add-ons).
- `Notes/NoteStore.cs` (one encrypted file per note, revisions, loaded and indexed in memory) + `NoteIndex.cs` (offline
  full-text: prefix matching, phrases, `tag:`, `host:`, `is:fav`, dates).
- `Incidents/IncidentStore.cs`: incidents with hosts, status, severity and a timeline (`ItemKind`: event, terminal, log,
  screenshot, file, note, report, AI analysis, connection, status); attachments in their own encrypted files.
  `Views/IncidentsView.cs` + `IncidentUi` ("Add to incident" from anywhere).
- `Documents/PdfText.cs`: PdfPig extraction off the UI thread with time, size, page and character caps; failures are a
  `PdfTextState`, never an exception.

---

## 13. Updater and packaging (`Services/Updater.cs`, `Services/Packaging.cs`, ADR-008/029)

- **Off today:** `Updater.Enabled` is `TrustedPublisherThumbprints.Length > 0 && !Packaging.IsPackaged`, and the thumbprint
  list is empty until a code-signing certificate exists.
- When on: the user starts a check against `https://darkciphersystems.com/downloads/servermanagement/update.json` (the
  file `build.ps1` writes). The manifest is untrusted: strict schema, https only, the package on the manifest's own host
  under the expected name, strictly newer versions only. The download goes to the app's private temp folder with a size
  cap and SHA-256 check, then `WinVerifyTrust` and the signer's thumbprint must match a pinned publisher; the MSI must
  carry the product's `UpgradeCode`. `UpdateVerdict` names each failure.
- `Packaging.IsPackaged` is true in an MSIX install (package family name present): Windows owns updates, the AUMID comes from
  the manifest, and `RelaunchInfo` restarts through the package.

---

## 14. Views (`Views/`) at a glance

| Area | Files |
|---|---|
| Shell pieces | `PaneHost.cs` (`DocPane`, `PaneFrame`, `SplitNode`, `PaneHost` split/tile/merge/pop-out; `DetachedWindow`, `FloatWindow`, `AiWindow`), `Menus.cs` (lazy right-click menus mirroring toolbars; `Menus.Group` named groups shown inline up to 12 rows, folded into submenus beyond, ADR-038), `Dialogs.cs` (every dialog; refused and recorded during the self-test, `ConfirmForTests`), `NetUi.cs` (the external-destination warning look), `UnlockWindow.cs`, `RecoveryWindow.cs`, `BackupDialogs.cs` |
| Home and servers | `DashboardView.cs`, `HostsView.xaml(.cs)` (+ `HostRow`, reconcile cloud imports), `HostEditWindow.xaml(.cs)`, `ServerView.cs` + `ServerPanels.cs` (`CommandTable`, `CommandText`, `StatTile`), `InfraView.cs`, `CloudWorkspaceView.cs`, `ApiExplorerView.cs`, `LanScanView.cs` (+ `KnownHostsView`), `TunnelsView.cs`, `SnippetsView.cs` (multi-run) |
| Sessions | `TerminalView.cs`, `RdpView.cs`, `SerialView.cs`, `SessionLogsView.cs`, `InputBroadcast.cs`, `WebHost.cs` (+ `WebWindow`) |
| Files | `CommanderView.cs` (+ `FilePane`), `SftpView.cs` (+ `TextEditorWindow`), `SyncWindow.cs` |
| Knowledge | `NotesView.cs`, `IncidentsView.cs`, `ReportView.cs` (0.4.0: This PC, Event Viewer exports), `ObservabilityView.cs` (0.4.0: log sinks' state and activity, Grafana dashboards, SIEM links), `DocumentViewer.cs` (`DocumentViewerWindow`, `DocumentHtml`), `HelpView.cs` |
| Vault | `CredentialsView.cs` (0.4.0: folders and types), `CredentialEditWindow.cs`, `SharedLibraryView.cs`, `SharedLibraryUi.cs` |
| AI | `AiPanel.cs`, `AiDock.cs`, `AiSendPreview.cs`, `AssistantView.cs`, `ScreenshotAi.cs`, `FoundryPanel.cs` |
| System | `AddonsView.cs`, `PrivacyView.cs`, `AuditView.cs`, `BroadcastUi.cs`, `PortalsView.cs`, `BrowserView.cs`, `SettingsView.cs` (+ `Settings/SettingsView.*.cs`; 0.4.0: `.Observe.cs` log sinks and dashboards cards, the Branding & colours card in `.Appearance.cs`) |

0.4.0 additions outside `Views/`: `Dcx.Core/Services/AppInfo.cs` (the one place that names folders, keys and the company,
with the one-time folder migration, ADR-054), `Dcx.Ui/Services/Branding.cs` (the branding draft → theme.json, add-on
export, ADR-055), `Dcx.Notify/Sinks/*` and `Dcx.Notify/Grafana/GrafanaClient.cs` (ADR-056),
`Dcx.Connectivity/Reports/LocalReport.cs` and `WindowsEvents.cs` (ADR-057), `Dcx.Connectivity/Net/NetTests.cs` (the
traceroute / mtr rewrite), `Dcx.Connectivity/Files/FileSystems.cs` (`LocalFs.SafeChild`), `Dcx.Ui/Views/Menus.cs`
(`CsvCell`).

`SettingsView` (the house theme's layout, ADR-038) shows its cards under a tab strip with the search above it: Security &
privacy, Cloud providers, AI & models, Terminals & connections, Appearance, Navigation, Data & backups, Integrations,
Import & export, About. One column of cards up to 900 px, each with a title, a scope caption and, where it can, its own
Reset. `Restyle` makes every plain CheckBox a switch, drop-downs 340 px and tooltips captions, including module and
add-on cards. `Services\AppearanceTransfer.cs` is the Import & export format (`dcx-appearance` v1, appearance and
navigation only, validated, previewed).

---

## 15. Where the tests live

All tests are part of the exe, in the app's `SelfTest/` folder: `DcxServerManager.exe --selftest <report>` with
`DCX_DATA_DIR=<scratch>` (BUILD.md section 4). With `DCX_AI_FEATURES=1` it runs the full suite; with `0` the AI-off
suite (`SelfTestAiOff.cs`), which never touches an AI type. `SelfTest.RunAsync` branches there; the full suite
(`RunFullAsync`, never inlined) runs its own blocks, split by area into `SelfTest.<Area>.cs` partial files sharing a
`SelfTestRun` (store and parsers, data and files, screens and sessions, UI, notes / incidents / documents,
infrastructure, API / add-ons / RDP, updates and services), and calls each unit:

| File | Class | Covers |
|---|---|---|
| `SelfTest.cs` + `SelfTest.*.cs`, `SelfTestRun.cs` | `SelfTest` (+ `FakeSerialStream`, `FakeHttp`, `FakeHttpStatus`) | the core suite by area; the final "no dialog was reached" and "no popup menu or tooltip was opened" checks |
| `SelfTestAiOff.cs` | `AiOffTests`, `AiSwitchTests` | the AI-off run (nothing AI registered, `Dcx.Ai` never loaded, start-up line), the switch, the lock (incl. snapshot / backup / recovered-settings guards) |
| `SelfTestArchitecture.cs` | `ArchitectureTests` | the seams, project references on the built assemblies, embedded resource names, the layering / assistant / AI-boundary / settings-writer source scans |
| `SelfTestAzureGcp.cs` | `AzureGcpTests` (+ `FakeAzure`, `FakeGcp`) | Azure and Google Cloud against fakes of their sign-in endpoints and APIs |
| `SelfTestHouseTheme.cs` | `HouseThemeTests` | the house theme: presets and contrast, theme add-on derivation, the Settings layout (tabs, switches, drop-downs, captions, resets), appearance import / export, sidebar styles and pins, every right-click menu's groups (none over 12 top-level rows) |
| `SelfTestStress.cs` | `StressTests` | fuzzing of every untrusted-input parser, endurance and leak loops |
| `SelfTestHandles.cs` | `HandleCensus` | handle counts by type (named culprits on endurance failures) |
| `SelfTestUi.cs` | `UiScaleTests` | type ramp and window sizing (ADR-015) |
| `SelfTestOffline.cs` | `OfflineTests` | offline mode with real loopback traffic and a fake resolver |
| `SelfTestEnv.cs` | `EnvTests` | environment labels and production safeguards |
| `SelfTestDashboard.cs` | `DashboardTests` | the dashboard model and tab |
| `SelfTestTerminal.cs` | `TerminalTests` | profiles, search, logging, broadcast input, tab groups, Telnet |
| `SelfTestContabo.cs` | `ContaboTests` (+ `FakeContabo`) | the cloud layer and Contabo |
| `SelfTestRecovery.cs` | `RecoveryTests` | crash recovery, including a real killed child process |
| `SelfTestStability.cs` | `StabilityTests` | single-instance guard, renderer recovery |
| `SelfTestFoundry.cs`, `SelfTestFoundrySetup.cs` | `FoundryTests`, `FoundrySetupTests` | Foundry Local CLI and hands-off set-up (fakes only) |
| `SelfTestRobustness.cs` | `RobustnessTests` | hanging background checks, the timer audit |
| `SelfTestRelease.cs` | `ReleaseTests` (+ `FakeHetzner`, `FakeVCenter`) | Hetzner, the cloud workspace, Settings categories, AI tools, **the terminal bridge and command risk** |
| `SelfTestThemes.cs` | `ThemeAddonTests` | theme add-ons with the repository's sample theme |
| `SelfTestFreeze.cs` | `FreezeTests` | the UI error guard and the cloud sweep |
| `SelfTestInfraCoverage.cs` | `InfraCoverageTests` | provider controls against fakes of each API |
| `SelfTestPortals.cs` | `PortalTests` (+ `TestWeb`) | the portal browser against loopback HTTP/HTTPS |
| `SelfTestLibrary.cs` | `LibraryTests` | the shared library with three members on a scratch share |
| `SelfTestAgent.cs` | `AgentTests` (+ `FakeAi`) | streaming, the agent loop, terminal control |
| `SelfTestNotify.cs` | `NotifyTests` (+ `Collector`, `Smtp`) | syslog over TLS and SMTP against fakes on 127.0.0.1 |
| `SelfTestObserve.cs` | `ObserveTests` (+ `FakeSinkServer`, `FakeGrafana`) | 0.4.0 log sinks (rules, payloads, a pinned TLS fake), Grafana, the Observability screen, the policies |
| `SelfTestPentest.cs` | `PentestTests` (+ `HostileGrafana`, `ScriptStream`) | 0.4.0 the penetration-test suite: adversarial inputs at every boundary (docs/SECURITY-REPORT.md); section J for 0.5.1 |
| `SelfTest051.cs` | `Release051Tests` (+ `Banner`, `TinyRfb`, `FakePra`) | 0.5.1: locks and restrictions, add-on policy, cloud locks, the AI tools with locked hosts, host import, Hyper-V editions, BeyondTrust pinning and page, the dashboard catalogue, Commander, LOGS, every dropdown, the VNC tab, help coverage |
| `SelfTestVnc.cs` | `VncTests` (+ `FakeRfb`) | 0.5.1 the VNC client against scripted loopback RFB servers (VNC password, Apple Remote Desktop, RFB 3.3, VeNCrypt Plain and X.509, every encoding, hostile sizes) |
| `SelfTestToolbox.cs`, `SelfTestAnsible.cs`, `SelfTestProfiles.cs`, `SelfTestChecks.cs`, `SelfTestVpn.cs`, `SelfTestLan.cs`, `SelfTestMail.cs`, `SelfTestEditors.cs`, `SelfTestCommanderMc.cs`, `SelfTestDcxCtl.cs`, `SelfTestSynology.cs`, `SelfTestSatellite.cs`, `SelfTestLibraryAccess.cs`, `SelfTestProtected.cs`, `SelfTestPolicies.cs`, `SelfTestExport.cs`, `SelfTestSshKeys.cs`, `SelfTestQuickConnect.cs` | one class each | the 0.3.0 units (TESTING.md lists what each covers) |

Rules every test follows: no visible window (everything off-screen, never activated), no dialog (refused and recorded),
no real network (loopback fakes and TEST-NET addresses only), never the real store. The list of checks and what is not
covered is in [TESTING.md](TESTING.md).
