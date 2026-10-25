# Architecture

DarkCipher ServerManagement is a single-process WPF app on .NET 10 (Windows 10 19041+; .NET 8 until 0.6.12, ADR-092). Since 0.2.0 it is ten projects
with references pointing down only (ADR-037). From the bottom: `Dcx.Core`; `Dcx.Connectivity` and `Dcx.Ui` on it;
`Dcx.Infra`, `Dcx.Cloud` and `Dcx.Addons`; `Dcx.Library` and `Dcx.Browser`; `Dcx.Ai`; and the app on top
(`src/DcxManager`, the composition root). The
project map is in [SOURCE.md](SOURCE.md) section 1 and each project's `README.md`; building and releasing in
[BUILD.md](BUILD.md). The table below gives the layering rules by folder (the folders kept their names in the split):

| Folder | Owns | Must not |
|---|---|---|
| `Services/` | Encrypted store (`SecureStore`, `AppData`, `AppSettings`), lock/2FA (`AppLock`, `Totp`), audit, backup, store move, sync-folder guard, workspace/crash restore, **`Typography`**, `ThemeManager`, **`NetworkPolicy`**, `NavLayout`, screenshots | Reference views (exceptions: `AppLock` shows the unlock window, `StorageGuard` shows its dialog) |
| `Sessions/` | Terminal session layer (ADR-004): `ITerminalTransport`, `SessionTarget` + `SessionCaps`; `SshTransport`, `ConPtyTransport` (local shells, WSL), `SerialTransport` (System.IO.Ports; serial/USB-serial, `ISerialControl` BREAK), `LocalShells` / `SerialPorts` discovery | Touch WPF controls |
| `Infra/` | Infrastructure providers (ADR-007): `IInfraProvider`, caps/objects/actions/verified results, `DockerProvider` (SSH CLI + Engine API over named pipe), `ProxmoxProvider` (API token, permissions, tasks), `UnraidProvider` (GraphQL introspection → capabilities, API key), `TlsPin` (shared TOFU pinning) | Touch WPF controls |
| `Ssh/` | SSH connect (jump hosts, keys, agent), remote exec, tunnels, key tools, LAN scan, RDP launcher (mstsc) | Touch WPF controls |
| `Files/` | `IFileSystem` (LocalFs, SftpFs) and `TransferQueue` | UI |
| `Ai/` | `AiClient`: the **only** code that talks to a model (redaction, internet gate, timing). **`AiWorkspace`**: the one AI Engineer workspace (conversations, explicit context items, provider/model, running state). `AiActions`: consent, then posts into the workspace. `AiContext`: safe per-object context. Operator tools (`Assistant`), reports, log digest, help KB | Read secrets; call providers from views |
| `Vultr/` | API client, catalogue-driven views/forms, billing documents | |
| `Browser/` | Portal browser (ADR-032): bookmarks, profiles per environment, certificate pins, download / navigation / sign-in rules, the add-on's `browser.json` | Touch WPF controls (the tab is `Views/BrowserView.cs`) |
| `Views/` | Screens, dialogs, `Menus` (context-menu framework), `NetUi` (trust-boundary UI), document viewer, terminal (xterm.js in WebView2), `PaneHost` (+ `DetachedWindow` base for `FloatWindow` / `AiWindow`), **`AiPanel`** + **`AiDock`** (AI sidebar) | Hold protocol or crypto logic |
| `Themes/Controls.xaml` | All control styles; colours and fonts only through DynamicResource | Hard-code a colour or font family |
| `Assets/` | Help articles, xterm.js, terminal page | |

Verification: `DcxServerManager.exe --selftest <report>` with `DCX_DATA_DIR=<scratch>` opens every screen off-screen
and checks crypto, storage, typography, network classes, the AI guards and more, then writes PNG renders to
`<report dir>/shots`. It never opens a visible window and never touches the real store.

## Rules for new code
- **Where code goes (0.2.0, ADR-037):** an engine goes into the lowest project that owns its area (a cloud into
  `Dcx.Cloud`, a protocol into `Dcx.Connectivity`, infrastructure into `Dcx.Infra`, a stored record into `Dcx.Core`),
  one file per service; screens go into the app; anything the lower project needs from above is a hook it exposes and
  `App.WireLayers()` connects. A new reference must point down (the self-test checks the built assemblies). Nothing
  outside `Dcx.Ai` names an AI type except `App.LoadAiModule()` and the self-test. New features are read-only until
  allowed where they can change something, keep every secret in the encrypted store, list their destinations in Privacy
  & network, register their actions as capabilities, and come with fakes in the self-test and fuzzing for every parser.
- **Fonts:** never assign `FontFamily`. Use `{DynamicResource UiFont | ChromeFont | MonoFont | EditorFont}` in XAML, or
  `.Use("…")`, `.Mono()` or `.Editor()` in code. Windows get `UiFont` automatically. For UI text size on a new window's
  content, call `Typography.Track(window, root)` (`Dialogs.MakeWindow` already does).
- **Colours:** use `SetResourceReference` / `{DynamicResource}` with the `ThemeManager` palette keys.
- **Network:** anything that can send data off the PC goes through `NetworkPolicy.Classify`. In Settings, present it
  with `NetUi.External(...)`, and use `NetUi.ConfirmEnable` for the off→on change.
- **AI:** user-facing "✦" actions call `AiActions.AskAsync` (which posts into `AiWorkspace`); never open a per-action
  AI window. Background features may call `AiClient.ChatAsync` directly. Build context with `AiContext`. Never pass a
  credential object, `Secret`, `Passphrase` or `Notes`, and never stream a terminal, RDP session or notes into the workspace.
- **Storage:** write through `SecureStore` (atomic temp + rename, AES-256-GCM). There is no plaintext store.
- **UI from below the screens (0.2.0 phase 1):** code outside `Views\`, `MainWindow` and `App` never touches
  `MainWindow.Instance`, `Views.Dialogs`, `Application.Current` or a `Dispatcher`. It asks through the seams in
  `Services\Ui.cs`: `Ui.Dialogs` (prompt, confirm, choose, info, error, text, pick), `Ui.Window` (status, navigate,
  activate, save workspace / save all, open a web page, show a document, open a terminal, describe the screen, add to an
  incident) and `Ui.Thread` (check access, post, invoke, try-invoke with a timeout, async invoke, a repeating UI timer),
  and logs with `Log.Write` (`App.Log` forwards to it). `Views\WpfUi.cs` registers the WPF side at start-up; without it
  the defaults refuse every dialog and run inline. The self-test's layering scan (`SelfTestArchitecture.cs`) fails on
  any new direct call; since the split the compiler refuses most of them anyway.
- **Capabilities (0.2.0 phase 2):** anything the app can do on request is a `Capability` in `Services\Capabilities.cs`
  (name, risk, parameters, handler). The AI operator, the AI Engineer and add-ons' `app.call` all run through
  `Capabilities.RunAsync`; names never change once shipped (`docs/baseline/capabilities-0.1.18.txt` is checked by the
  self-test). The registry knows no feature: the composition root registers `OperatorCapabilities` then
  `FeatureCapabilities`; a feature registers its own with `Capabilities.Register`. Secret masking is `Redaction.Mask`.
- **App modules (0.2.0 phase 3):** a feature that adds to the screens does it through `IAppModule.Register(IAppHost)`
  (`Views\AppModules.cs`), registered in `App.OnStartup`; the screens render the contributions (`AppModules.NavItemsIn`,
  `FindNav`, …) and never name a module. The AI's surface lives in `Views\AiModule.cs`. A module that is not registered
  leaves nothing behind — that is how AI off will work (phase 5). The extension points (`IAppHost`): `NavItem` (sidebar),
  `SettingsCategory` / `SettingsCard`, `ContextMenu(surface, …)`, `ErrorAction` (error box button), `Shortcut`,
  `RightDock` (the window's right column, an `IDockController`), `WindowReady` / `Startup` / `Shutdown`,
  `DashboardSource` (a remote source + summary tile), `PrivacyRows` (posture rows + destinations) and `HelpArticles`
  (help articles tagged `module: <id>` in `help.md` exist only while their module is registered). Points read by code
  in `Services\` (the dashboard sources, privacy rows, help) are static lists there that `AppModules` feeds, so nothing
  below the screens references `Views`.
- **The assistant (0.2.0 phase 4):** a screen that offers "✦ … with AI" on its own data builds the material itself and
  hands it to `Assist.Current` (`Services\Assist.cs`, `IAssistant`: ask, preview what is sent, review a PDF), usually via
  `Menus.AssistItem`, which yields no item when no assistant is registered. Screens never call `AiActions`,
  `AiSendPreview` or the AI's context builders (the self-test scans for it); object descriptions without secrets are
  `Services\SafeMetadata.cs`. AI items that need nothing from the screen are menu contributions instead ("screenshot",
  "settings-card", "tab"). Reports are core (`Reports\`); their AI pass is the registered `IReportAnalyst`.
- **AI off means not loaded (0.2.0 phase 5):** `App.OnStartup` registers `AiModule` only when `AiFeatures.DecideAtStartup()`
  says so (machine policy → `DCX_AI_FEATURES` unless locked → the setting). Code outside the AI never checks "is AI on?":
  it asks for a contribution, the assistant or the analyst and gets none. A capability that needs a module names it
  (`Capability.Module`) and disappears with it. The AI-off self-test run proves the result on every screen.

## Architectural decision records

### ADR-001 Central typography (2026-10-18)
- **Problem:** the UI font setting saved but barely changed the app. Chrome styles were pinned to the mono font, popups
  are separate trees, several windows hard-coded Segoe UI, and views copied the mono font statically.
- **Decision:**
  - Five application resources, owned by `Services/Typography.cs`: `UiFont`, `ChromeFont`, `MonoFont`, `EditorFont`, `UiScaleTransform`.
  - Every style and view binds to them.
  - A `Window.Loaded` class handler applies `UiFont` to every window.
  - `ContextMenu.Opened` and `ToolTip.Opened` class handlers re-bind reused popups.
  - The terminal has its own event (`TerminalChanged`) and settings.
- **Reasoning:** WPF resource references propagate without per-window event subscriptions. That means no leaks, and
  new screens get it for free.
- **Security impact:** none.
- **Alternatives considered:**
  - A per-window event that re-applies fonts. This was the old approach and missed popups and untracked windows.
  - Bundling a third-party font. Not needed: Segoe UI Variable ships with Windows, so there is no redistribution and
    no licence question.
- **Migration:** new settings `EditorFont` and `MonoAccents` default to empty/false. Existing `UiFont` and `MonoFont`
  values are honoured, and now actually apply. The look changes from mono chrome to UI-font chrome; "Terminal-style
  accents" restores the old look.

### ADR-002 One network classifier (2026-10-18)
- **Problem:** "is this local?" lived only in the AI gate. It treated every non-public name loosely, and the UI had no
  way to show a trust boundary.
- **Decision:** `NetworkPolicy.Classify(url)` returns LOCAL, ON-PREMISES, USER-CONFIGURED or EXTERNAL.
  - Unknown DNS names are USER-CONFIGURED and are **not** treated as local.
  - The AI gate (`AiClient.IsLocal`) delegates to it.
  - The UI shows shape-coded badges (● ◆ ◇ ⚠) plus words, never colour alone.
- **Security impact:** stricter. A DNS-named endpoint now needs "external AI providers" enabled, which was already the
  behaviour for dotted names.
- **Alternatives considered:** DNS resolution to classify names. Rejected: it is slow, it leaks the lookup, and
  split-horizon DNS makes the answer unreliable anyway.
- **Migration:** none.

### ADR-003 One AI workspace behind a dockable sidebar (2026-10-18)
- **Problem:** every "✦ Explain" opened its own window with its own conversation, so there was no shared place to see
  what had been sent, follow up, or keep the AI next to the work.
- **Decision:**
  - `Ai/AiWorkspace` is a process-wide singleton: conversations (memory only), explicit context items (masked on entry;
    credentials always BLOCKED with no content), provider/model label, running state. It reaches models only through
    `AiClient.ChatAsync`.
  - `Views/AiPanel` only presents the workspace and subscribes to it once. `Views/AiDock` owns MainWindow's right-hand
    column (GridSplitter, docked / collapsed / hidden) and moves the *same* panel into an `AiWindow` (detach) and back.
  - `FloatWindow` and `AiWindow` share a `DetachedWindow` base (owner, theme, title bar, UI scale, shortcuts, quiet close).
  - `AiActions.AskAsync` keeps the internet consent prompt, then adds a context item, reveals the sidebar and asks.
  - State and width persist in the encrypted `AppSettings` (`AiPanelState`, `AiPanelWidth`).
- **Reasoning:** one workspace makes "what does the AI see?" answerable at a glance and makes detach/reattach a pure
  re-parenting (no state copy), so it can't fork the conversation or leak a second AI instance.
- **Security impact:** neutral to stricter. Same gate and masking; the context list is now visible and togglable, and
  masking of context items no longer depends on the "mask secrets" setting.
- **Alternatives considered:** a document tab for the AI (loses the always-beside-the-work layout); a separate workspace
  per window (would fork conversations on detach).
- **Migration:** new settings default to docked, 380 px. The old per-action answer window is gone.

### ADR-004 Session providers: one terminal view, pluggable transports (2026-10-18)
- **Problem:** `TerminalView` owned the SSH.NET shell directly, so adding local PowerShell/cmd, WSL (and later serial or
  telnet) meant copying the view. That would have duplicated the bounded output pipeline, paste safety, link
  handling, menus and reconnect logic.
- **Decision:** `Sessions/ITerminalTransport` is a byte pipe: Open(cols, rows), Write, Resize, Data and Closed events,
  IsOpen, Detail. A `SessionTarget` says what to connect to and what it supports (`SessionCaps`: HostTools,
  AutoReconnect, Sudo), and creates a fresh transport per (re)connect.
  - `TerminalView` keeps all presentation and safety and only offers the buttons and menus the capabilities allow.
  - SSH: `SshTarget` / `SshTransport` (the previous code, moved).
  - Local: `LocalTarget` / `ConPtyTransport` (Windows pseudo console; headless, VT both ways, same xterm front end).
  - WSL is a local target that runs `wsl.exe -d <distro> --cd ~`. Distributions come from the per-user Lxss registry,
    so nothing is started. Names must be plain identifiers because they go on a command line. Docker Desktop's
    internal distributions are skipped.
- **Reasoning:** the risky parts (parser front end, paste guard, output bounding) stay in one audited place.
- **Security impact:**
  - Local shells run as the current user with the app's own privileges, never elevated, with inherited environment.
    They can't do anything the user couldn't do in Windows Terminal.
  - ConPTY closes on tab close, and the process is terminated if it doesn't exit within 2 s.
  - Local sessions are audited ("opened local shell").
  - They aren't auto-restarted after exit (no hidden respawn).
- **Alternatives considered:**
  - A separate LocalTerminalView. Rejected: duplicated safety code.
  - winpty. Rejected: obsolete since ConPTY.
  - Running wsl.exe for discovery (`-l -q`). Rejected: it can start the WSL VM.
- **Migration:**
  - `IHostView.Host` is now nullable; local tabs have no host.
  - The sidebar has a new LOCAL section, merged into saved section orders after SERVERS.
  - Workspace restore records `local:<profile id>`.
- **Addendum, session 11: serial.** `SerialTarget` / `SerialTransport` on the same interface.
  - Dependency: Microsoft's System.IO.Ports 8.0.0, pinned (the maintained library, not hand-rolled Win32 comm code).
  - Profiles are stored encrypted in data.dcx (`AppData.SerialProfiles`) and included in backups and merges.
  - A USB adapter is tracked by VID/PID/serial from WMI, so it is followed across COM renumbering. If a *different*
    USB device now owns the old COM name, it is never opened.
  - Unplugging raises Closed("device removed"); reconnect uses the terminal's back-off when the profile allows it.
  - Wrong settings (ArgumentException) are not retried.
  - `CanDuplicate=false`, because a port opens once.
  - Transport-specific actions use a small optional interface (`ISerialControl.SendBreak`) rather than widening `ITerminalTransport`.

### ADR-005 Notes and knowledge-base storage (2026-10-18)
- **Problem:** notes are first-class: a notebook per host and session, autosave while typing, revision history, and
  fast offline search across everything. They must be as protected as credentials, survive crashes mid-save, and
  must not be lost by a store move, backup or restore.
- **Options considered:**
  1. SQLite + SQLCipher. Adds a native dependency and a second key-management path. SQLCipher's licence/packaging
     choice and an FTS index on disk would also need their own review.
  2. SQLite (Microsoft.Data.Sqlite) with per-field encryption. Then the full-text index is either plaintext on disk
     (a leak) or impossible.
  3. **Chosen:** one encrypted document per note through the existing `SecureStore`, plus an in-memory index built
     after unlock.
- **Decision:**
  - Each note is `note-<id>.dcx` in the store folder: AES-256-GCM under the same data key, with the file name as AAD.
    The files are top-level on purpose, so the existing store mover, its SHA-256 and decrypt verification, and the
    "remove old copy" step handle them unchanged.
  - Writes are atomic (temp file then rename). A crash can lose at most the keystrokes since the last autosave, never
    the note.
  - Revisions (up to 30 per note, at most 2 MB) live inside the note file. Deleting moves a note to the Trash (a flag);
    only "Empty trash" deletes files, after a confirmation.
  - A file that fails to decrypt is never overwritten. It is listed as damaged and left for restore.
  - Search (`NoteIndex`) is a deterministic inverted index (token → notes, prefix match through a sorted key array)
    that lives only in memory. It is never written to disk, so no plaintext index exists.
  - The query language is plain words (AND), "phrases", `tag:`, `host:`, `is:fav`, `before:` / `after:` / `on:`
    dates, and natural date words (today, yesterday, last tuesday, this week, last week).
  - Linked host names are indexed, so "everything I wrote about PROD-WEB-04" works without AI.
  - Encrypted backups include notes (`BackupFile.Notes`), and merge imports them by id.
- **Reasoning:**
  - No new crypto or native code.
  - Write amplification is one small file per autosave.
  - Corruption is isolated per note.
  - The index rebuilds in well under a second for thousands of notes (self-test measures 3,000 notes).
- **Revisit when:** a store passes tens of thousands of notes or about 50 MB. Then move to an encrypted SQLite (ADR
  needed), keeping `NoteStore` as the interface.
- **Security impact:**
  - Note contents never leave the encrypted store except when the user exports them or sends them to AI with a click
    (redaction and the internet gate still apply).
  - The in-memory index holds plaintext tokens while the app is unlocked, the same exposure as the open notes
    themselves.
- **Migration:** additive. New files appear only when the first note is created; older versions ignore them.

### ADR-006 Documents and images for AI (2026-10-18)
- **Problem:** the AI must get a PDF's actual content, and never pretend it read one. Screenshots may go only to
  models that can see. Both are sensitive.
- **Decision (PDF):**
  - `Documents/PdfText` uses PdfPig 0.1.9 (Apache-2.0, managed code, pinned). It runs off the UI thread with limits:
    100 MB, 500 pages, 2 M characters, 30 s.
  - The result is one of: Extracted, Partial, NoText ("OCR would be required"; no OCR is bundled), Encrypted (never
    bypassed) or Failed. It never throws to the UI.
  - The viewer's state line advances only as steps happen: PDF DISPLAYED → TEXT EXTRACTED / NO MACHINE-READABLE TEXT
    → CONTENT PROVIDED TO AI (scope).
  - `AiSendPreview` shows exactly what will be sent before Send: source, scope or page range, the number of masked
    secrets, credentials BLOCKED, the destination with its network class, and the final masked text truncated to the
    engine budget.
- **Decision (images):**
  - `AiClient.VisionAsync` verifies capability per engine:
    - Ollama: `/api/show` capabilities or projector.
    - Anthropic API: Claude models.
    - Ollama behind its Anthropic endpoint: same check as Ollama.
    - OpenAI-compatible: only if the user says the model accepts images (it can't be verified there).
    - Foundry/NPU: no.
  - `ChatAsync` refuses any message carrying images unless the check passes, before any request is made. The
    workspace does the same and says so in the conversation.
  - `ScreenshotAi`: preview, then black-out boxes burned into the pixels, scaled to at most 1920 px. It becomes an
    image context item (✓ / untick), attached only to the user question. It is held in memory only, audited, and goes
    through the internet confirmation for non-local engines.
- **Alternatives considered:**
  - Tesseract OCR: a native dependency plus language data. Deferred, and stated honestly in the UI instead.
  - Sending images "just in case": rejected as faking.
- **Migration:** a new setting `OpenAiAcceptsImages` (default off).

### ADR-007 Infrastructure providers (2026-10-18)
- **Problem:** Docker, Proxmox and later Unraid must not be one-off hacks. The app must never show controls a target
  doesn't allow, never report success it didn't see, and never act on production by accident.
- **Decision:**
  - `Infra/IInfraProvider`: `DetectAsync` → `ProviderCaps` (reachable, can read, can control, version, notes, all
    discovered from the target), `InventoryAsync` → `InfraObject`s, `ActionsFor`, `Forbidden`, and `RunAsync` →
    `ActionResult(Accepted, Verified)`.
  - `Infra.Offer` combines three gates for every action: readable; changes allowed for this provider (off by default,
    a per-provider trust decision); and the provider's own permission and state. Disabled actions carry their reason.
  - Every change asks first with target, action, environment and consequence. A typed name is required for
    PRODUCTION or data loss.
  - A change counts as Verified only after the object is re-read in the expected state (Docker: inspect; Proxmox:
    task exitstatus OK, then status/current). Otherwise it is reported as NOT verified and audited that way.
- **Docker:** one provider over two backends:
  - The CLI over SSH (`--format '{{json .}}'`, sudo fallback).
  - The Engine API over Docker Desktop's named pipe.
  - Only validated hex container ids reach a command line or URL.
  - Docker access is root-equivalent, and the app says so.
- **Proxmox:**
  - API token from the vault (`user@realm!id`), https only.
  - TOFU SHA-256 pinning for self-signed certificates. A changed certificate is refused and needs a typed TRUST.
  - Privileges come from `/access/permissions` (most specific path wins). VM.PowerMgmt is required to power guests.
  - Templates are never powered.
  - No delete or destroy operations are implemented.
- **AI:** "Explain" sends selected object summaries only. It has no tools here and never acts.
- **Polling:** none unless the user picks auto-refresh for a provider, and then only while the screen is open.
- **Alternatives considered:**
  - The Docker API over TCP 2375. Rejected: plaintext root access.
  - Proxmox username/password tickets. Rejected: tokens are revocable and scoped.
- **Migration:** additive (`AppData.InfraProviders`); backup merge remaps host and credential ids.
- **Addendum, session 16: Unraid.** `UnraidProvider` uses the official GraphQL API (Unraid 7.x or Unraid Connect)
  with an `x-api-key` from the vault.
  - **Capability discovery first:** the schema is introspected. The inventory query is built only from fields this
    server has, and container/VM actions appear only if the matching mutation exists (`docker{start,stop}`,
    `vm{start,stop,reboot,forceStop}`).
  - **Introspection off (0.1.10, s33):** a production Unraid API refuses `__schema` (HTTP 400,
    `INTROSPECTION_DISABLED`). Discovery then probes: the wanted selection is sent on one line, and each validation
    error's column names the field to drop until the query validates; the survivors form a synthetic schema, so the
    inventory, actions and verification code are unchanged. Mutations are probed without their required non-null
    `$id`, so validation or variable coercion always fails before any resolver runs; the errors reveal whether the
    field exists, the id's type, and whether the result needs a selection (`{ __typename }`).
  - **Ids:** sent as GraphQL variables, never interpolated.
  - **Scope:** array, disk and share operations are deliberately not offered.
  - **Transport:** https with TOFU pinning; http allowed only to a local/on-premises address, with a warning.
  - **Refusals:** a VIEWER key's refusal is reported, not hidden.
  - **Shared TLS:** TOFU pinning moved into `Infra/TlsPin.cs` with `ITlsPinned`, shared by Proxmox and Unraid.

### ADR-008 Update trust (2026-10-18)
- **Problem:** the updater is a code-execution path from the internet. A package must never be trusted because of
  where it came from, and must never downgrade, replay or clobber data.
- **Decision (`Services/Updater.cs`):**
  - **When:** checks are user-initiated. An opt-in weekly check (default off, ⚠ treatment, one-time confirmation)
    only shows a status message.
  - **Request:** a plain GET of the manifest with a generic User-Agent, no cookies, no query string and no redirects.
    It carries no identifiers.
  - **Manifest:** treated as untrusted. Strict schema; strictly newer x.y.z only (no downgrade); the MSI must be on
    the manifest's own https host and named `DarkCipherServerManagement-<ver>-x64.msi` (no traversal, no query); a
    size cap and a SHA-256 are required.
  - **Download:** into the app's private temp folder, which is wiped. Exceeding the announced size aborts. Every
    refusal deletes the file.
  - **Integrity:** SHA-256 must match.
  - **Authenticity:** WinVerifyTrust over the whole chain, with online revocation checking (an offline revocation
    failure is refused), AND the signer's thumbprint must be one of `TrustedPublisherThumbprints`, compiled into the
    app. That list is **empty** until a DarkCipher Systems code-signing certificate exists, so every package is
    refused for install today, by design.
  - **Product and version:** the MSI's own UpgradeCode and ProductVersion (read with msi.dll) must match, which stops
    a replayed older signed release.
  - **Install:** the file is re-verified right before running. The user is asked "Back up before upgrading?"; a
    backup counts only once it has been written and read back. Then the normal msiexec UI runs (UAC) and the app
    closes.
  - **After install:** on the next start the running version is compared with the expected one: "Update verified",
    or "did not complete; your data is unchanged". Both are audited.
- **Data:** the MSI upgrade doesn't touch %APPDATA%. Store formats are versioned per kind of file (ADR-009); a release
  with `schemaChange` makes the backup prompt "strongly recommended". A "pre-upgrade" snapshot is always taken.
- **Switched off (2026-10-18, DarkCipher0x: no signing certificate yet):** `Updater.Enabled` is false while
  `TrustedPublisherThumbprints` is empty. Settings → Updates then shows only an explanation; the weekly check never
  runs; the privacy view shows DISABLED. Pinning a thumbprint turns it all back on — no other code change.
- **Build (`build.ps1`):**
  - Optional signing via `DCX_SIGN_THUMBPRINT` (signtool, SHA-256, RFC 3161 timestamp; fails the build if the
    signature doesn't verify).
  - Writes `dist\update.json`.
- **Alternatives considered:**
  - Silent background updates. Rejected: against the spec.
  - Trusting the HTTPS download alone. Rejected: a compromised website would equal code execution.
  - A detached minisign/ed25519 signature with a pinned public key. A good option if Authenticode certificates prove
    impractical; it would need a key-management ADR.
- **To enable real updates:**
  1. Obtain a code-signing certificate and add its SHA-1 thumbprint to `TrustedPublisherThumbprints`.
  2. Build with `DCX_SIGN_THUMBPRINT`.
  3. Publish the MSI plus `update.json` under `/downloads/servermanagement/` on the website (the website's
     `sync-downloads.ps1` doesn't do this yet).

### ADR-009 Snapshots, store versions and recovery (2026-10-18)
- **Problem:** the store must survive crashes mid-write, damaged files, bad restores, imports and format changes
  without ever destroying the only usable copy, and without plaintext or network.
- **Decision (`Services/Snapshots.cs`, `Services/StoreSchema.cs`):**
  - **Snapshot = one file** (`snap-<yyyyMMdd-HHmmss-fff>-<reason>.dcxsnap`) holding the raw, already-encrypted bytes
    of every store file (keyring, `*.dcx`, audit logs) with their SHA-256, sealed again with the data key
    (AAD `snapshot`). Same privacy as the store; useless with another key. NOT a replacement for the
    password-protected `.dcxbackup` (another PC / lost Windows profile).
  - **Counts only when verified:** written to `.tmp`, renamed, read back, every hash checked. Pruning happens only
    after that, and never touches the snapshot just written. Automatic ones: newest N (default 10). Safety ones
    (`pre-restore`, `pre-migration`, `pre-import`, `pre-upgrade`): newest 5 per reason. Manual: never pruned.
  - **Low disk / failed write:** nothing written, nothing pruned, the user is told (status bar + Settings).
  - **Where:** default `<store>\snapshots` (moves with the store; the uninstaller's data removal includes it);
    optionally another folder (sync-folder warning).
  - **When:** automatically when the newest automatic one is older than the interval (default daily; checked 30 s
    after start, then hourly; local disk only); before every restore, import, data upgrade and app update; on demand.
  - **Restore drill** (`Verify`): decrypt, check every hash, decrypt each `.dcx` under its own name and parse it as its
    type, refuse a newer format. Restore runs it first.
  - **Restore:** a verified `pre-restore` snapshot of the current state first → journaled commit → the store is
    **frozen** (writes dropped and logged) → restart. `keyring.json` is never replaced (same data key; keeps the current
    password/2FA). Audit logs are only added back when missing, never rolled back. Files that didn't exist then are
    removed (they are in the pre-restore snapshot).
  - **Journaled commit** (restores, file recovery, migrations): new files are staged as `<name>.staged` and read back;
    then `pending-commit.json` is written atomically; then files are swapped under the store's write lock. At start,
    a journal is completed (roll forward); staged files without a journal are discarded; a journal naming anything
    unexpected is set aside, applying nothing.
  - **Schema versions:** `store.dcx` records a version per kind (data, settings, reports, workspace, notes). A store
    without it is version 1 everywhere (the baseline, which is today's format). Migrations are JSON transforms
    registered in `StoreSchema.Registry` (empty today).
  - **Start-up order (`StoreRecovery.RunAtStart`, after unlock, before anything loads):**
    1. Finish or discard pending commits.
    2. Damage check: every non-note `.dcx` must decrypt and parse. If one fails, offer the newest snapshot holding a
       good copy; the damaged file is renamed `.damaged-<time>`, never deleted. If no snapshot has one, offer
       "start without it" (renamed aside) or close.
    3. Migrate: a newer format than this app knows → refuse to open ("never downgrade data"). If migrations are due:
       verified `pre-migration` snapshot → transform in memory → parse-check → journaled commit → re-read and verify →
       any failure after the commit restores the snapshot. Any failure before it changes nothing.
- **Tested (self-test):** sealing, read-back, restore round trip with freeze, pruning, low disk, failed write, damaged
  and foreign snapshots, orphan staged files, crash after the journal → completion at start, hostile journal,
  damaged-file recovery and set-aside, migration success / idempotence / broken step / newer store, snapshots
  travelling with a store move.
- **Not covered:** a damaged `keyring.json` (the snapshot copy holds the same key, but restoring a keyring is left to
  a manual step); damaged notes are listed by NoteStore but not yet offered back from snapshots.

### ADR-010 Incidents / work sessions (2026-10-18)
- **Problem:** during an incident the material is scattered: terminal output, screenshots, logs, notes, diagnostics
  reports, AI answers, which hosts were touched and when. The spec asks for one place to gather it, plus an AI summary
  of selected material, without weakening the storage model.
- **Decision (`Incidents/IncidentStore.cs`, `Views/IncidentsView.cs`):**
  - **Storage like notes (ADR-005):**
    - One encrypted `incident-<id>.dcx` per incident (AAD = file name, atomic writes): title, status
      (Open / Monitoring / Resolved / Closed), severity SEV1–4, environment (PRODUCTION / TEST / LAB / not stated),
      summary, linked hosts and a timeline of items.
    - Screenshots and files are their own encrypted `incfile-<id>.dcx`, written once and bound to the incident by id.
      Editing the timeline never rewrites megabytes, and an attachment can't be read through another incident.
    - Attachments are written before the timeline points at them.
    - Trash is a flag; only "Empty trash" deletes (incident plus attachments).
    - Damaged files are listed and never overwritten; the start-up check (ADR-009) also offers snapshot copies.
  - **Timeline items:** Event, Terminal, Log, Screenshot, File, Note (a link: the incident shows the note's current
    text), Report (Markdown copied in), AI analysis, Connection, Status (status and severity changes are recorded
    automatically).
  - **The ACTIVE incident** (`AppSettings.ActiveIncidentId`) receives captures without a question. With none active,
    the user picks an open incident or starts one, which then becomes active. Captures come from:
    - terminal "⚑ Add selection to incident";
    - every 📷 menu;
    - AI answers ("⚑ Add to incident" and "✎ Save as note");
    - reports;
    - notes ("Link to incident");
    - host menus ("New incident for this host", "Add to active incident", incidents for this host).

    Opening a terminal to one of the active incident's hosts records a Connection entry, at most one per host per
    10 minutes.
  - **AI:** "✦ Summarise with AI" / "✦ Root-cause hypotheses" send the selected entries (or the whole timeline) as
    Markdown through the existing "Data being sent to AI" preview (masking, destination class, exact text), then
    into the AI sidebar. Screenshots and files are named, never included. Nothing is sent without that click.
  - **Export:** Markdown to a user-chosen file (sync-folder check, stated as not encrypted). Saving an attachment out
    is audited.
  - **Limits:** 1,000,000 characters per text entry (cut, with a marker), 20 MB per attachment, 2,000 entries per
    incident.
  - **Backups:** full backups carry incidents and their attachments; a selection carries the incidents involving the
    selected hosts. Merge adds unknown ids only, remaps host ids and validates every id (they become file names).
    Store moves and snapshots include both file kinds; `StoreSchema` knows them as `incidents` and `incident-files`.
- **Alternatives considered:**
  - Incidents as tagged notes. Rejected: no status, severity or structured timeline, and attachments would bloat the
    note files.
  - One file holding everything. Rejected: every edit would rewrite all the screenshots.
- **Not done:** OBS/broadcast of an incident, sharing, a post-incident (PIR) template, and the dashboard tile (§48).

### ADR-011 Local API and OBS broadcast (2026-10-18)
- **Problem:** scripts and monitoring want to read inventory and post to the incident timeline, and streams want to
  show a terminal live. Both open a door into an app holding credentials, so the door must be narrow, explicit,
  visible and revocable.
- **Decision (`Api/LocalServer.cs`, `Api/ApiTokens.cs`, `Api/Broadcasts.cs`):**
  - **Listener:**
    - A minimal HTTP/1.1 server on a raw `TcpListener` bound to **127.0.0.1 only**, not http.sys (no URL ACLs, and
      nothing else can widen the bind). Remote exposure is **not offered**: use an SSH tunnel if you need it.
    - It runs only while the API is switched on (Settings, default **off**) or a broadcast is active.
    - Port setting (default 47321). The privacy view lists it as a listening service.
  - **Every request:**
    - The Host header must be `127.0.0.1:<port>` or `localhost:<port>` (DNS-rebinding defence).
    - Request line + headers ≤ 8 KB, body ≤ 64 KB with a Content-Length (no chunked), 5 s read timeout.
    - Only the documented methods and paths are accepted.
    - Responses are `no-store`, `nosniff`, `no-referrer`, `Connection: close`.
    - While the app is locked, everything answers 423 and broadcasts are stopped.
  - **API auth:**
    - `Authorization: Bearer dcx_<43 chars>` capability tokens. They are shown once; only their SHA-256 is stored
      (in the encrypted settings).
    - Each token has a name, scopes, an expiry (1 / 7 / 30 / 90 days, or never with a warning), last-used time and use
      count, and can be revoked individually or all at once.
    - Scopes: `status.read`, `hosts.read`, `incidents.read`, `incidents.write`, `notes.write`.
    - **Never exposed:** credentials, keys, the vault, settings, terminals or files. Host records are returned without
      any credential reference.
  - **CSRF / browsers:**
    - API requests carrying an `Origin` header are refused, so a web page can't drive it (no CORS answers either).
    - Broadcast endpoints accept only their own origin.
  - **Limits:**
    - Per token: 10 requests/s, burst 30.
    - Authentication failures: more than 20 a minute locks the API for 60 s (audited once).
    - Per broadcast: 10 frames/s.
  - **Audit:** token created/revoked, every write, auth-failure lockouts, broadcast start/stop.
  - **Broadcast:**
    - The user starts it explicitly on a source: a **terminal tab** (its visible screen as text) or an **incident
      status card** (title, status, severity, duration, latest entry titles, never their text). The external
      Remote Desktop window is not a source.
    - Each broadcast gets its own unguessable URL (`/b/<token>/`), for an OBS Browser Source. It expires (default
      4 h) and is stopped by Stop, Stop all, closing its tab, locking the app or exiting.
    - A red **📡 BROADCASTING** indicator sits in the status bar (Stop all) and on the terminal itself.
    - **Text, not pixels:** the screen goes out as text lines so redaction is reliable. Colours are not kept.
    - **Redaction, always on:**
      - secret patterns (shared with the AI masking);
      - private-key blocks, including partial ones on screen;
      - long base64-like lines.
    - **Optional redaction:** IPv4/IPv6 addresses, e-mail addresses, and custom regexes (validated, 50 ms timeout).
    - **Password prompts:** while the cursor line looks like one (password / passphrase / `[sudo]` / verification
      code), the page freezes on the last safe frame with "input hidden".
    - **Display:** crop to the last N rows, font size, dark / light / transparent theme, title on or off.
    - The page is static HTML + JS from the app with a strict CSP (`default-src 'none'`, own script, `connect-src
      'self'`), polling `frame` JSON about 2.5 times a second.
- **Alternatives considered:**
  - http.sys `HttpListener`: needs URL ACLs for 127.0.0.1 and is easier to widen by mistake.
  - Streaming pixels (CapturePreview): redaction would need OCR.
  - WebSocket/SSE: more moving parts than polling, for no visible gain at 2–3 fps.
- **Not done:** remote access (by design), write access to terminals (never), RDP broadcast, per-token IP pinning.

### ADR-012 Add-ons (2026-10-18)
- **Problem:** people want to extend the app (their own checks, reports, integrations) without third-party code being
  able to read the vault, crash sessions, or quietly gain powers in an update.
- **Decision (`Addons/`, `Views/AddonsView.cs`, SDK in `docs/ADDONS.md`):**
  - **Out of process, always.** An add-on is a program (`.exe`, or a `.ps1` run by Windows PowerShell) started per
    command invocation. It never loads into the app, so its crash is an exit code, not ours. Every invocation has a
    timeout (default 60 s, max 600 s), output caps (1 MB of messages, 2,000 calls) and is killed on timeout or abuse.
  - **Sandbox:**
    - The child runs from a token duplicated from the user's, with **Low integrity**. It can't write to the user
      profile, the app's folders or other add-ons; its only writable place is its own folder under
      `%USERPROFILE%\AppData\LocalLow\DarkCipher Systems\ServerManagement\addons\<id>` (`DarkCipher\…` before 0.4.0).
    - It runs inside a **Job object**: killed with the job, 512 MB memory cap, at most 8 processes, no breakaway.
    - Its environment is a whitelist: no secrets inherited from ours.
    - If the restricted process can't be created, the add-on does not run. There is no unsandboxed fallback.
    - **Stated honestly in the UI:** Low integrity can still READ most files and use the network. The permissions
      govern what the APP gives it, not everything the program can do. "Install only add-ons you trust."
  - **Bridge:** JSON-RPC-style lines over stdin/stdout. The app sends `invoke`; the add-on calls methods and ends with
    `done`. Every method checks a permission:
    - `hosts.read`: hosts without credentials;
    - `incidents.read` / `incidents.write`;
    - `notes.read` / `notes.write`;
    - `terminal.read`: the invoking terminal's visible screen, redacted like a broadcast;
    - `filesystem.read`: only a file the USER picks in a dialog;
    - `credentials.request`: a per-request dialog naming the add-on and its reason, with deny as the default. It is
      never cached and always audited;
    - `app.info` and `ui.notify` need no permission.

    Requests for anything else are errors. Results are shown in the Add-ons run log and, if asked, a dialog.
  - **Packages:** `.dcxaddon` = zip with `addon.json`:
    - the manifest holds id, name, version, publisher, description, entry, timeout, permissions and commands with
      context `global` / `host` / `terminal`;
    - zip-slip, absolute paths, more than 500 files or more than 50 MB uncompressed are refused;
    - files are installed to `%LOCALAPPDATA%\…\addons\<id>\<version>` (Medium integrity: unwritable by the add-on);
    - every file's SHA-256 is recorded in the encrypted settings and **verified before every run**. A modified add-on
      is disabled and not run.
  - **Approval:** installing shows name, publisher, version, the package's SHA-256, the entry's Authenticode state
    (signed by X / NOT signed), each requested permission in plain words with a risk level, the sandbox, and the
    "it's a program" warning. Only checked permissions are granted.
  - **Updates can't silently gain permissions:**
    - a newer version keeps the previous grants;
    - any permission it adds is shown as NEW and stays ungranted until approved;
    - downgrades are refused.
  - **Management:** enable / disable, revoke or grant single permissions, remove (files + registry), and a per-add-on
    run log. Everything is audited.
- **Alternatives considered:**
  - In-process .NET plugins (AssemblyLoadContext). Rejected: no isolation; a plugin could read the key from memory.
  - AppContainer. Stronger, but would need its own capability and ACL management for every folder; Low IL plus a Job is
    a solid first step.
  - WASM. Small ecosystem for the scripts engineers actually write.
- **Not done:** a signed add-on catalogue / store, network denial per add-on, AppContainer.

### ADR-013 Embedded Remote Desktop (2026-10-18)
- **Problem:** Remote Desktop opens an external mstsc window, outside the tabs, panes, screenshots, incidents and
  audit. The spec wants it embedded, with pop-out and full screen that don't drop the session, fit modes, special keys,
  reconnect, and conservative device redirection.
- **Decision (`Rdp/RdpControl.cs`, `Views/RdpView.cs`):**
  - **The control:** Windows' own RDP client ActiveX (`mstscax.dll`, MsRdpClient*NotSafeForScripting), hosted by a
    WinForms `AxHost` inside a WPF `WindowsFormsHost`. The newest registered CLSID is used (MsTscAx 13 → 9). Same
    protocol stack, NLA/CredSSP, certificate prompts and codecs as mstsc; nothing third-party.
  - **Interop:**
    - Properties and methods are late-bound (`dynamic` over IDispatch), so there's no generated interop assembly.
    - The password goes in only through `IMsTscNonScriptable.ClearTextPassword` (vtable call). It is never in a file,
      on a command line or in the Windows credential store.
    - Events (connecting/connected/login/disconnected/full screen/fatal error/warning/size change/logon error/minimise
      request) come through an IDispatch sink on the `IMsTscAxEvents` connection point.
  - **Security defaults:**
    - CredSSP/NLA on; server-authentication level 2 (warn, using Windows' own prompt).
    - **Clipboard:** follows the host setting (on by default, as before).
    - **Off by default:** drives, printers, smart cards, serial ports, POS devices and the microphone.
    - **Audio:** plays on this PC unless the host says otherwise.
    - **Camera:** not redirected (not offered embedded).
    - **Gateway:** hosts with an RD Gateway open in mstsc, stated in the UI.
  - **Display modes:**
    - **Resize to window** (default): the session resolution follows the pane via `UpdateSessionDisplaySettings`,
      debounced.
    - **Scale to window:** SmartSizing.
    - **Fixed:** the host's width × height.
  - **Full screen** is the control's own (connection bar, restore = back into the tab). **Pop-out / dock back** reparent
    the host window; the session stays.
  - **Keys:**
    - Ctrl+Alt+Del is sent via `IMsRdpClientNonScriptable.SendKeys`, falling back to the hint Ctrl+Alt+End.
    - Windows-key combinations go to the remote only in full screen (default) or whenever focused (option).
  - **Reconnect:** auto-reconnect on (host setting, retries); ↻ Reconnect; the disconnect reason comes from
    `GetErrorDescription`.
  - **Screenshot:** captures the control's own window (with a screen-copy fallback for DirectX surfaces).
  - **Broadcast:** not offered for RDP (pixels can't be redacted reliably; ADR-011).
  - **Fallback:** Settings → "Open Remote Desktop inside the app" (default on). When off, or when the control can't be
    created, mstsc is used as before. Every host menu also offers "Open in mstsc".
- **Alternatives considered:**
  - Embedding the mstsc window with SetParent. Fragile and unsupported.
  - FreeRDP. A large native dependency with its own security update burden.
- **Not done:** RemoteApp, multi-monitor spanning while embedded (that's mstsc's job), camera/USB redirection,
  broadcasting RDP.

### ADR-014 File transfer: resume, verify, sync, SCP (2026-10-18)
- **Problem:** the Commander queue copied byte streams straight onto the target name. A drop left a half file that
  looked complete; nothing resumed, retried, verified or kept timestamps; there was no history and no folder compare.
  Servers without the SFTP subsystem couldn't be used at all.
- **Decision (`Files/TransferQueue.cs`, `Files/FolderSync.cs`, `Files/FileSystems.cs`):**
  - **Never a half file under the real name:**
    - every file is written to `<name>.dcxpart`, then renamed over the target only when complete (and verified);
    - a move deletes sources only after every file arrived.
  - **Resume:** an existing `.dcxpart` no larger than the source continues from its length. If the verified result
    then mismatches (the prefix was stale), the part is discarded and the file is copied once more from zero.
  - **Retries:** a failing file (I/O or connection) is retried up to 3 times with back-off (2 / 5 / 10 s), reconnecting
    both sides and resuming from its part file.
    - **Retry later:** a failed or cancelled job can be retried as a new job, which reuses the part files.
  - **Pause / resume** between 256 KB chunks.
  - **Verify (default on):** SHA-256 of source and target must match. Hashing is local for this PC; remote it runs
    `sha256sum` on the server over SSH, falling back to reading the file back through SFTP.
  - **Keep times (default on):** the modified time is set on the target; Unix permission bits too when both sides
    are remote.
  - **History:** the last 300 finished jobs (route, counts, bytes, verified, resumed, retries, result) in
    `transfers.dcx`, encrypted, shown from the Commander.
  - **Compare / sync:**
    - Two folders are compared recursively by size + modified time (2 s tolerance for FAT/SMB) or by SHA-256. The
      states are: same, differs (left/right newer), only left, only right, type conflict.
    - A sync is planned from that: **Update** (copy new and changed one way, never delete), **Mirror** (Update + delete
      extras on the target, only if ticked) or **Two-way** (newer side wins; both-changed = conflict, skipped).
    - The **preview** lists every action with a checkbox and totals. Deletions are opt-in, shown separately and
      confirmed with their count. They run only after all copies succeeded: Recycle Bin locally, permanent remotely
      (stated).
  - **SCP fallback:** a host whose server refuses the SFTP subsystem, or is set to "SCP", uses `ScpFs`:
    - listing and stat via `stat -c` in the shell (GNU and BusyBox);
    - mkdir / rm / mv / chmod / touch via the shell, with every path single-quoted;
    - data via SSH.NET's ScpClient through a temporary file in the app's private temp folder (deleted on close);
    - SCP has no append, so resume restarts that file (stated in the job detail);
    - the Commander shows "· SCP".
- **Alternatives considered:**
  - rsync. Not present on every server or on Windows, and it would need its own transport.
  - Resume by byte comparison. Hashing the result is what proves it.
- **Not done:** bandwidth limits, parallel chunked transfers, sync schedules.

### ADR-015 UI text size and window scaling (2026-10-18)
- **Problem:** two readability controls were missing or only partly there.
  - **No text-only size.** "UI text size" is a whole-UI zoom (a `ScaleTransform` on each window root). It enlarges padding, icons and
    borders along with text. The spec asks for a UI *font size* that is separate from UI scale and from the terminal.
    Font sizes were ~115 hard-coded numbers (11–26 px) across styles, XAML and code, so no setting could reach them.
  - **Four windows ignored the scale:** the Vultr form, the credential editor, the document viewer and web windows (AUDIT §6).
    The `Dialogs.MakeWindow` windows applied it to their content but kept their designed pixel width, so text at 150–175 % got cramped.
- **Decision (`Services/Typography.cs`):**
  - **A type ramp of font-size tokens.** These are application resources, each named after its role, with the designed size at 100 %:
    `FsMicro` 10.5, `FsCaption` 11, `FsSmall` 11.5, `FsMeta` 12, `FsBody` 12.5, `FsControl` 13, `FsBase` 13.5, `FsInput` 14,
    `FsTitle` 15, `FsHeading` 16, `FsH1` 17, `FsCode` 18, `FsValue` 20, `FsIcon` 26.
    - Every interface font size refers to one of them: `{DynamicResource Fs…}` in XAML and styles, or `.Fs(FontStep.…)` in code.
      A raw size is a bug; the self-test scans the source for them.
    - The terminal is the exception: it keeps its own size (`TerminalFontSize`) and never reads the ramp.
  - **`AppSettings.UiTextSize`** (85–150 %, default 100 %) multiplies the ramp, rounded to 0.5 px. It applies live to every open
    window, menu and tooltip. At 100 % every token equals its old literal, so the default look is unchanged pixel for pixel.
  - **The two settings combine.** UI scale zooms everything (layout included); text size changes only text. Effective text size = token × text size × scale.
  - **`Typography.Size(window, width, height?)` is the one sizing rule** for dialogs and fixed windows:
    - the designed size × UI scale × text size, capped to 96 % of the work area;
    - `MaxHeight` = the work area for size-to-content windows (each such window scrolls its body);
    - re-applied when typography changes while the window is open, unsubscribed on close;
    - a window the user has resized keeps its proportions: on a change, its size is multiplied by new factor ÷ old factor.
    - Note: at the default scale (110 %) these windows open 10 % wider than before; their content was already scaled by 110 %.
    - Used by `Dialogs.MakeWindow`, the host editor, unlock, the credential editor, the Vultr form, the document viewer and web windows.
  - **Web windows:** the toolbar (WPF) takes the UI scale and text size. The web content does not get a WPF transform:
    a `LayoutTransform` above an HwndHost (WebView2) mis-positions the native window. Instead:
    - **Document viewer:** the start zoom is `ZoomFactor` = UI scale × text size, adjustable with − / + / 100 % as before.
    - **Web windows (Vultr console / portal):** content stays at 100 %. The noVNC canvas would be resampled and blurred,
      and third-party pages keep their own Ctrl+wheel zoom.
- **Alternatives considered:**
  - **Only the zoom (status quo):** fails the spec's separate font-size control, and the zoom also inflates borders and icons.
  - **`FontSize` inheritance from the window alone:** most sizes are explicit, so inheritance can't move them.
  - **One base token with offsets:** a ratio keeps the hierarchy proportional (a 26 px icon doesn't grow by the same px as 11 px captions).
- **Not done:** per-window remembered sizes; a high-contrast type ramp.
### ADR-016 Offline / air-gapped mode (2026-10-18)
- **Problem:** spec §53 asks for one switch that blocks every external destination except approved ones.
  - The app can reach outside the network in many ways:
    - AI engines;
    - the Vultr API;
    - Proxmox / Unraid;
    - the updater (switched off);
    - SSH / SFTP / SCP and tunnels;
    - RDP (embedded and mstsc);
    - the external OpenSSH terminal;
    - web windows (Vultr portal / console);
    - links opened in the browser;
    - add-ons;
    - the Claude Code launcher.
  - `NetworkPolicy` only classifies destinations; nothing enforced a single rule across those paths.
- **Decision (`Services/OfflineMode.cs`, `Offline`):**
  - **Settings (encrypted):**
    - `OfflineMode` (off by default);
    - `OfflineAllowed`, the approved destinations, one per line: a host name, `*.suffix`, an IP, or an IPv4/IPv6 CIDR (validated);
    - `OfflineResolvePrivateNames` (on by default);
    - `OfflineAllowAddons` (off by default).
  - **The rule:** when the mode is on, a destination is allowed only if one of these holds:
    - it is Local (loopback) or OnPremises (private / LAN ranges, single-label and LAN-suffix names);
    - it matches the approved list, by name or by any resolved address;
    - it is a user-configured DNS name whose every resolved address is Local / OnPremises / approved, and name resolution is allowed.
    - Everything else is refused **before any connection is opened**. This covers public IPs, known internet services, and names that don't resolve or resolve publicly.
    - It fails closed: a resolution error counts as blocked.
  - **One enforcement point per transport:**
    - **HTTP:** every app HttpClient is built on `Offline.Handler()`, two layers:
      - an outer handler that checks the request URI, which also covers requests sent through a proxy;
      - a `SocketsHttpHandler` whose `ConnectCallback` resolves the name, checks every address, and connects to the address it checked (no DNS-rebinding gap).
      - Auto-redirects are off on these clients, so a 3xx can't lead outside unchecked. None of the APIs used needs redirects.
      - Proxmox / Unraid keep their certificate pin through `SslOptions` on the same handler.
      - Used by AiClient, OllamaClient, VultrClient, Proxmox, Unraid and the Updater.
      - Docker over the local named pipe is Local by construction.
    - **SSH / SFTP / SCP / tunnels:** `SshConnector` checks the host (and each jump host) before connecting.
      - A target reached *through* a jump host gets the check without DNS (it may only resolve on the jump host's network).
      - A name resolved by the check is connected by the address that was checked, so there is no second lookup to rebind. Host keys stay keyed by the saved address.
    - **RDP** (embedded, mstsc, the gateway host) and the **external OpenSSH terminal** are checked before launch.
    - **Web windows and browser links:**
      - checked before a window opens or a link is handed to the browser;
      - inside an open web window, each navigation and sub-resource request is checked against the rule. Without DNS, unknown names are blocked.
    - **AI:** `AiClient.Blocked()` reports offline-mode blocking, so buttons grey out with the reason. The HTTP layer still enforces it.
    - **Add-ons:** they are separate programs, and the Low-IL sandbox can't deny them the network (AppContainer is a #12 follow-up). So offline mode refuses to run them unless `OfflineAllowAddons` is ticked.
    - **Claude Code:** launched only when its Ollama endpoint is allowed. It already runs with `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` / `DISABLE_TELEMETRY=1`. The CLI is another program, so this is best effort, and stated as such.
  - **Visibility:**
    - an **OFFLINE MODE** badge in the status bar (click → Settings);
    - a Settings card with the switch, approved list, options and the last 50 blocked attempts;
    - a Privacy & network posture row, with each endpoint's state showing "BLOCKED by offline mode";
    - every block is written to the encrypted audit log and kept in memory for the session.
  - **Errors:** `OfflineBlockedException` names the destination, the purpose and how to approve it. Blocks are never silent and never retried.
- **Alternatives considered:**
  - Windows Firewall rules per process: needs admin, can't express "approved names", and changes system configuration.
  - A local proxy for all traffic: SSH and RDP aren't HTTP. One more listening service.
  - Checking only at the UI: misses background and indirect paths. The transport layer is where it can't be bypassed.
- **Not covered (stated):**
  - the WebView2 runtime's and Windows' own update / SmartScreen services (not this app's traffic);
  - DNS queries to the system resolver when name resolution is allowed (untick it to avoid them);
  - destinations a server reaches on its own (remote ends of tunnels, `curl` typed in a terminal);
  - what a separately launched program (mstsc, ssh.exe, Claude Code, an allowed add-on) does after launch.
### ADR-017 Environment labels and production safeguards (2026-10-18)
- **Problem:** spec §55 wants every host and session visibly marked PRODUCTION / TEST / LAB / LOCAL, with stronger
  confirmations for disruptive actions on production.
  - Only infrastructure providers (ADR-007) and incidents carried an environment, as free text with separate lists.
  - Hosts, tabs, terminals and Remote Desktop carried none. A reboot, a delete or a multi-host command on a production box
    asked exactly the same question as one on a lab VM.
- **Decision (`Services/Environments.cs`, `Envs`):**
  - **One vocabulary:** PRODUCTION, TEST, LAB, DEVELOPMENT, LOCAL, or "" (not set).
    - `Envs.Normalize` folds common spellings: PROD / LIVE → PRODUCTION, DEV → DEVELOPMENT, QA / UAT / STAGING → TEST.
    - Other text is kept as a custom label with a neutral colour. It is never treated as production.
    - Infrastructure providers and incidents use the same list.
  - **Where it is stored:** a new `HostEntry.Environment`, encrypted with the hosts.
    - Set in the host editor, or for many hosts at once (Hosts → right-click → Environment ▸).
    - Local shells (PowerShell / cmd / WSL) are LOCAL by definition.
    - A new incident created from a host takes the host's environment.
  - **Shown everywhere a host is:**
    - a colour plus a **text label**, so colour is never the only signal;
    - theme tokens `EnvProd` / `EnvTest` / `EnvLab` / `EnvDev` / `EnvLocal` / `EnvOther`, with dark and light values;
    - an Environment column in the host list;
    - on every tab that belongs to a host (terminal, Remote Desktop, server view, SFTP): a label in the tab header and a coloured top edge, including in pop-out windows;
    - a strip at the top of terminal, Remote Desktop and server views ("PRODUCTION · web-01");
    - the infrastructure provider list.
    - It updates live when a host's environment changes.
  - **Production safeguards:** `Envs.Confirm(host, …)` wraps `Dialogs.Confirm`.
    - The dialog shows a coloured environment banner for any labelled host.
    - For PRODUCTION it is always a danger confirmation that requires **typing the host's name**. It can't be skipped by turning confirmations off.
    - **Covered:**
      - reboot / shut down / Vultr power cycle and firewall changes (server view);
      - service and container actions (server panels);
      - "Run suggested command" (reports);
      - AI operator approvals;
      - deletes on a remote host (Commander, SFTP);
      - sync runs that delete on a host;
      - multi-host snippet runs. The list is grouped by environment; if it includes production, you type PRODUCTION.
    - **Multi-line paste into a PRODUCTION terminal** always shows the paste preview, even with paste confirmation off.
  - **Local API / add-ons:** the host summaries (hosts.read) include the environment, so scripts can respect it.
- **Alternatives considered:**
  - **Environment per folder only:** folders mix environments in practice, and a per-host value can still be set for a whole folder at once.
  - **Colour only:** not readable for colour-blind users or in screenshots. The label is always printed.
  - **Blocking production actions entirely:** this is a working tool. Friction proportional to the risk is the goal, not refusal.
- **Not done:** per-environment policies (e.g. read-only PRODUCTION, no AI on PRODUCTION), environment-based colour themes for the whole window, and inheritance from Vultr tags.

### ADR-018 Engineering dashboard (2026-10-18)
- **Problem:** spec §48 wants an operational overview: active sessions by type, infrastructure, recent notes and connections, transfers and local application warnings. It must not silently poll infrastructure, and any polling must be configurable and understandable.
  - The old dashboard showed Vultr tiles, favourite hosts and recent audit lines.
  - It contacted the Vultr API and the AI engine every time it loaded, and the screen never said so.
  - It knew nothing about sessions, infrastructure, incidents, transfers or warnings.
- **Decision:** rebuild `Views/DashboardView.cs` in place as the "Engineering overview". The nav key and the fixed first tab stay the same.
  - **Two kinds of data, kept apart:**
    - **Local facts** are read from what the app already holds, with no network. They are kept current by the app's own events, so the dashboard doesn't poll for them.
      - Local facts: open tabs and connection state, notes, incidents, transfers, the audit log, offline/environment state, warnings.
      - Events used: `LiveSessions.Changed` (new: tab added/closed, terminal or RDP connected/dropped), `NoteStore.Changed`, `IncidentStore.Changed`, `TransferQueue`, `AuditLog.Added`, `Offline.Changed`, `Envs.Changed`, `InfraStatus.Changed`.
      - Redraws are coalesced (one per 250 ms at most), and they are skipped while the dashboard isn't visible; it catches up when shown.
    - **Remote checks** contact something: the Vultr account, the AI engine and each infrastructure provider.
      - They run only by the user's **check rule** (the dashboard header's "Checks ▾" menu, saved in settings): only when I press "Check now", when the dashboard opens (the default, as before), or every 5 / 15 / 60 minutes *while the dashboard is on screen*.
      - Each source is included or excluded separately. The Vultr account and the AI engine are included by default (the old behaviour). **Infrastructure providers are excluded by default**: the dashboard shows their last-known state until you press "Check now" or include them.
      - Offline mode is respected before trying: an automatic check that offline mode would refuse is skipped and shown as "skipped: offline mode". It is not attempted, so it doesn't add blocked attempts.
      - Every remote card says where its data came from and when ("checked 14:02 · Infrastructure screen" / "not checked yet").
      - The Privacy view lists the check rule and its sources next to the other destinations.
  - **Last-known infrastructure state:** `Infra/InfraStatus.cs` records each provider's last detection and inventory, whoever ran it (Infrastructure screen or dashboard). It is kept in memory only, so nothing new is stored.
  - **Cards:**
    - summary tiles;
    - active sessions by type: SSH, Remote Desktop, serial, local shell, WSL, files, server views; connected vs open, PRODUCTION counted separately; click a row to go to that tab;
    - environment and safety: offline mode, blocked attempts, PRODUCTION sessions, local API, broadcasts, app lock;
    - infrastructure providers, with environment pills;
    - active incidents;
    - transfers: running plus recent;
    - recent connections (favourites first);
    - recent notes;
    - local application warnings;
    - Vultr;
    - recent activity.
  - **Local warnings (`Services/Overview.cs`, computed locally):**
    - store recovery notices, and damaged notes or incidents;
    - the automatic snapshot failed or is overdue;
    - data in a synchronised folder;
    - errors logged this session;
    - an internet AI provider or the local API is on;
    - add-ons blocked by offline mode;
    - transfers that failed in the last 24 h;
    - updates switched off (shown as info).
    - Each warning has a button that goes to the place that fixes it.
  - **Configurable:** right-click any card (or the header's "Customise ▾") to move it up or down, hide it, show hidden cards or reset. The order and hidden set are saved in settings (encrypted).
    - Cards are laid out in two columns in order, and in one column when the dashboard is narrower than 980 px.
  - **Testable core:** counting, warnings, layout order and the check rule live in `Overview` (no WPF), so the self-test can exercise them directly as well as through the real view.
- **Alternatives considered:**
  - **A separate new screen beside the old dashboard:** two overviews to keep in sync. The old content survives as cards (Vultr, recent activity, connections).
  - **Polling everything on a timer:** that's exactly what the spec forbids. Local facts don't need it, because events are cheaper and immediate.
  - **Persisting infrastructure status to disk:** stale state that looks current after a restart; not worth a new store.
  - **A drag-and-drop grid layout:** more code and harder to make reliable at every UI scale. Move up/down plus hide covers "configurable" with the same menus as the sidebar.
- **Not done:** per-card size choices, several saved dashboard layouts (§39), and charts or history (nothing is sampled over time by design).

### ADR-019 Terminal features: profiles, search, logging, broadcast input, tab groups, Telnet (2026-10-18)
- **Problem:** spec §65 lists what the terminal subsystem must support. Much of it already exists:
  - SSH, local shells, WSL and serial;
  - tabs, splits, duplicate, reconnect with back-off;
  - jump hosts, port forwarding, fonts, favourites, folders, tags, import/export.
- **Missing:**
  - colour schemes, cursor settings, encoding and key mappings, as per-host and per-session profiles;
  - searchable scrollback;
  - automatic, timestamped session logging;
  - simultaneous (broadcast) input;
  - tab groups;
  - Telnet for legacy systems, only where explicitly enabled.
- **Decision:**
  - **Terminal profiles (`Sessions/TerminalProfiles.cs`):** a named profile holds:
    - colour scheme: "App theme" (follows light/dark) or a built-in palette;
    - cursor style and blink;
    - encoding: UTF-8, ISO-8859-1, Windows-1252, IBM437; used both to decode output and encode input for SSH and Telnet. Local shells are always UTF-8; serial keeps its own setting;
    - scrollback, 1,000–200,000 lines (0 = the app setting);
    - Backspace sends ^H;
    - automatic logging (on/off, timestamps);
    - key mappings: `Ctrl+Shift+F5 = \e[15;6~` lines; escapes `\e \r \n \t \xHH \uHHHH ^X`.
  - **Where profiles apply:**
    - Profiles live in the encrypted settings. One is the default.
    - A host can name a profile (host editor).
    - A terminal tab can switch profile for this session only (right-click → Profile ▸). That is the per-session profile, not saved.
    - The page receives the profile as one message after it is ready, and again on a switch. Nothing is baked into the page URL beyond the font and theme.
  - **Searchable scrollback:** a find bar in the terminal (⌕ button or Ctrl+Shift+F) with next/previous, match case, regular expression and "n of m".
    - The search runs inside the page over the xterm buffer (logical lines, so wrapped lines match). It selects and scrolls to the match.
    - No extra library is needed: the buffer API is enough. Regexes are bounded: an invalid pattern is reported, and the search stops after 10,000 matches.
  - **Session logging (`Sessions/SessionLog.cs`):** when the profile says so, every connected session's output is logged.
    - Escape sequences are stripped, so the log is readable text. With timestamps on, each line is prefixed `[yyyy-MM-dd HH:mm:ss]`.
    - **Encrypted like the audit log:** one AES-GCM line per chunk, with the file name as context, in `<store>\sessionlogs\`. Nothing is written while the store is locked.
    - Logs are flushed every second and on disconnect. A file is capped at 64 MB, then continues in the next file.
    - **Session logs screen:** list, view (decrypted in memory), search, delete, and "Export as text…". The export is plaintext, so it says so and goes through the synced-folder check.
  - **Broadcast input (`Sessions/InputBroadcast.cs`):** while on, what you type or paste in any member terminal is also sent to the others.
    - **Starting it** from the terminal or tab menu covers all open terminals, the terminals in this tab group, or the terminals in this pane. It asks first, through `Envs.ConfirmMany`: a set including PRODUCTION means typing PRODUCTION.
    - **Every member shows a strip:** "⌨ INPUT GOES TO n TERMINALS · ■ Stop". Closing a member removes it, and one remaining member ends the broadcast.
    - **Paste:** a paste is checked once in the terminal where it happened (PasteGuard), then broadcast like typing.
  - **Tab groups (`Views/TabGroups.cs`):** name a group and put tabs in it (tab right-click → Tab group ▸).
    - The tab header shows the group's name in its colour; the name is always printed.
    - **Group actions:** select next in group, broadcast input to the group, reconnect all, disconnect all, close the group.
    - Groups are for this session (memory only), like the per-session profile.
  - **Telnet (`Sessions/Telnet.cs`):** off by default. Settings → Terminal → "Allow Telnet (legacy, unencrypted)" asks before turning on.
    - **How to open it:** a host's protocol can then be Telnet, and quick connect accepts `telnet host [port]` / `telnet://host:port`.
    - **The transport:** a plain TCP stream with minimal option negotiation: ECHO, SGA, NAWS (window size), TTYPE `XTERM-256COLOR`. It refuses every other option, and IAC bytes are escaped both ways.
    - **Offline mode applies,** as for SSH. The status line and the environment strip say "⚠ Telnet: unencrypted". Connections are audited.
    - **Credentials are never typed automatically,** because a Telnet login prompt is just text from the server.
    - Telnet hosts get no Files / Manage / Commander; those need SSH and are refused with the reason.
- **Alternatives considered:**
  - **xterm's search addon:** another third-party file to vendor and keep updated. The buffer API covers it in about 60 lines.
  - **Plaintext session logs (like PuTTY):** they break the "encrypt everything" requirement. An explicit export gives the plaintext when wanted.
  - **Broadcast input by forwarding raw key events:** it would bypass the paste guard and the production confirmation. Data-level broadcast reuses both.
  - **Saving tab groups in the workspace:** deferred to §39 saved layouts; groups would need stable tab identities across restarts.
  - **Telnet as a separate session list (like serial):** hosts already carry folders, tags, environments and jump-host-free connections. A protocol value reuses all of that.
- **Not done:** emulation choice beyond xterm-256color (VT100-only mode), log rotation by age, scripting/expect, Telnet over a jump host, and tab groups across restarts.

### ADR-020 Cloud provider layer and Contabo (2026-10-18)
- **Problem:** DarkCipher0x wants Contabo as a first-class VPS provider, at the same level as Vultr, through one provider abstraction rather than a bolted-on section.
  - **Vultr is the only cloud provider today.** Its screens are a generic, data-driven catalog engine (`ResourceDef` / `ActionDef` / `Field` / `Lookup`, rendered by one resource view and one form), but that engine is hard-wired to `VultrClient`.
  - **Vultr-specific code is spread across the app:** the header account picker, Settings, host import (`VultrInstanceId`), the server view's power cycle, the dashboard, the AI operator, backup and the Privacy view.
- **Decision:**
  - **A provider-neutral cloud layer (`Cloud/`):**
    - The catalog engine moves here unchanged in spirit: `CloudCatalog.cs` (definitions), `CloudResourceView`, `CloudFormWindow`, `Tpl`, `J` and `JRow`.
    - **`ICloudApi`** is one signed-in account of a provider: `SendAsync`, `GetAsync`, `ListAsync` (pagination is the provider's business) and `UnwrapItem`. Both `VultrClient` and the new `ContaboClient` implement it.
    - **`CloudProvider`** describes a provider:
      - id, title, its catalog and groups, accounts and the active account;
      - capabilities (`CloudCaps` flags);
      - `Normalize(json)` → a typed `CloudInstance` (id, name, IPv4/IPv6, region, plan, OS, normalised state, default user);
      - power actions, an optional out-of-band console, and provider-only "special" actions.
    - `CloudProviders.All = { Vultr, Contabo }` is the only list the rest of the app reads. The sidebar sections, the header account picker, host import, the server view, the dashboard, the AI operator, backup and the Privacy view all go through it, with no `if provider == …` outside the providers themselves.
  - **Capabilities:** a screen offers only what the provider declared in its catalog, so there are no dead controls.
    - **New engine features** (they help Vultr too):
      - `ActionDef.When` (allowed states, e.g. Start only when stopped; otherwise disabled with the reason);
      - `ActionDef.Await` (after the call, poll the item until it reaches that state: bounded, shown, cancellable by closing the tab);
      - `ResourceDef.StatusField`;
      - `ResourceDef.DeleteWhen`;
      - a busy marker (no second action on a row while one is running);
      - path fields from forms (`{form.x}`, for "add instance to a network").
    - `CloudCaps` is also shown per provider in Settings and used by the shared code (power cycle, console, import).
  - **Resilience (`Cloud/CloudRetry.cs`, shared by both providers):**
    - **Every request has a finite timeout:** 60 s for API calls, 30 s for sign-in.
    - **429:** retried with back-off for any method, honouring `Retry-After`, because the server didn't process the request. **Not retried for orders** (create instance), whatever the reason.
    - **502/503/504, network errors and timeouts:** retried only for GET.
    - **A timed-out change** is never retried. It is reported as "may or may not have been applied: refresh before trying again", so a slow create can never produce two servers.
    - Every call is cancellable.
  - **Errors:** API errors become `CloudApiException(provider, status, safe message, request id)`.
    - The message is cut to 300 characters and never contains the request, the headers or credentials.
    - The full status, request id, elapsed time and retry count go to the error log and, for changes, to the audit log.
    - A provider that is down only affects its own screens.
  - **Contabo (`Contabo/`), from the official API spec (api.contabo.com, `api-v1.yaml`):**
    - **Sign-in:** OAuth2 password grant at `auth.contabo.com/auth/realms/contabo/protocol/openid-connect/token`, with client id + client secret + API user + API password, all four from the Customer Control Panel.
      - The access token is kept in memory per account, refreshed before it expires and once on a 401.
      - Every request carries a fresh UUIDv4 `x-request-id` (shown in errors for Contabo support) and an `x-trace-id` per user operation.
      - Lists page through `page`/`size` using `_pagination.totalPages`.
    - **Accounts:** `ContaboAccount` (name, the four credentials, disabled) is stored in the encrypted settings like Vultr keys.
      - Several accounts, each with add / test / rename / replace credentials / disable / remove / make active.
      - The header account picker follows the provider of the active cloud tab. (Removed in 0.3.0-preview.4 at DarkCipher0x's request: accounts are switched in the Cloud providers tree or in Settings.)
      - Test connection = sign in, plus a one-item instance list; it reports only "connected, n instances".
    - **Screens (CONTABO in the sidebar):**
      - Instances: list, create (order), rename, start / stop / shut down / restart, rescue, reset password, reinstall, snapshots ›, upgrade options (read), cancel contract;
      - Snapshots: create, rename, delete, roll back;
      - Images: standard and custom; custom images from a URL; rename; delete custom;
      - Secrets: SSH keys and passwords; upload the public half of a vault key;
      - Private networks: create, rename, delete, add/remove an instance;
      - Firewalls: create, edit, rules as JSON, assign/unassign an instance;
      - DNS zones and records;
      - Data centers; action history (audits).
    - **Destructive or billed actions ask with the environment rules:**
      - **Order (create):** a danger confirmation that names the billing period.
      - **Reinstall, rollback and cancel:** type the instance's name.
  - **Hosts:** `HostEntry` gains `CloudProvider` / `CloudInstanceId` / `CloudAccountId` (Vultr's old fields still read).
    - **Import from Contabo** reconciles: it updates the address of linked hosts, and reports linked hosts whose instance no longer exists.
    - The server view's out-of-band buttons (power cycle; a console only where the provider has one) come from the host's provider.
  - **Dashboard / AI / Privacy:**
    - **Dashboard:** the Vultr card becomes "Cloud servers" (all providers, normalised), and "contabo" joins the check sources. It is opt-in (Checks ▾), so adding the provider never starts new automatic traffic. A failing provider shows its error only in its own part of the card.
    - **Account changes:** adding, removing, disabling or replacing credentials reloads that provider's open screens (`CloudProvider.AccountsChanged`).
    - **AI operator:** generic `cloud_list_instances` and `cloud_instance_action` tools (provider + id), approval-gated. The Vultr tools stay.
    - **Privacy view:** api.contabo.com and auth.contabo.com are listed. Offline mode blocks them like any internet API.
  - **No background polling,** as before. Contabo data is fetched when a screen opens, on ↻, by the dashboard's visible check rule, or by a bounded status watch after an action.
- **Contabo API limits (honestly not offered):**
  - **No delete:** "Cancel contract" ends the instance at its cancellation date.
  - **No resize:** there's no product change. The upgrade endpoint only buys add-ons (private networking, backup), which this app doesn't do. "Upgrade options" shows what is available.
  - **No web console endpoint.**
  - **No catalogue of new products:** a product id like V91 is typed (Contabo's product list has them).
  - **No published rate limits** (429 is handled).
  - Region change and object storage aren't exposed.
- **Alternatives considered:**
  - **A separate Contabo screen set:** duplicated list/form/menu code, the opposite of what was asked.
  - **Normalising everything into typed C# models:** Vultr's ~30 resource types show the catalog approach scales. Typed models are used where the app reasons about resources (instances), not for every API object.
  - **Retrying all failures:** unsafe for orders and other changes.
- **Not done:** buying add-ons, region change, object storage, tags, users/roles, and DNS PTR records (all in Contabo's API, none mapped to anything Vultr-side yet).

### ADR-021 Terminal renderer recovery and the single-instance guard (2026-10-18)
- **Problem:** two stability items carried since phase 1.
  - **A WebView2 renderer crash killed a terminal's display.** It happens on GPU driver resets, out-of-memory, or a runtime update under a running app. The SSH/ConPTY session underneath was still alive, but the tab only said "renderer failed — reopen this tab", so the session had to be thrown away.
  - **Two instances shared one WebView2 user-data folder and one encrypted store.** The browser profile is `%LOCALAPPDATA%\…\WebView2` for every process, including the self-test, which runs beside DarkCipher0x's installed app. The same store written by two processes can lose changes, because the last writer wins. The shared profile was the root cause of the phase-1 ConnectionAborted flake.
- **Decision 1: recover the renderer, keep the session.**
  - **What triggers recovery:** `TerminalView` watches `ProcessFailed`. Recovery runs for `RenderProcessExited`, `RenderProcessUnresponsive` and `BrowserProcessExited`; frame, GPU and utility process failures are handled by the runtime itself and only logged.
  - **What recovery does:** the WebView2 control is disposed and a new one created in the same place. The page is loaded again; the profile, font and theme are re-applied on "ready"; then the recent output is replayed.
    - **Recent output** is kept host-side: the last 512,000 characters actually sent to the page, cut at a line break. A note says the display restarted and the session was not interrupted.
    - **The transport (SSH, ConPTY, serial, Telnet) is never touched,** so nothing reconnects and nothing typed is lost.
  - **After a browser-process exit,** the shared environment is replaced once (only the first failing view resets it; the others reuse the new one).
  - **Bounded:** at most 3 recoveries per tab in 10 minutes. After that the status line says the renderer keeps failing, so a crash loop can't spin.
- **Decision 2: one instance per store, and one browser profile per store.**
  - **The instance key** is the store identity: `DCX_DATA_DIR` if set, otherwise the user's installation (its `location.json` decides where the store is). A named `Local\` mutex (SHA-256 of the key) is held for the life of the process.
  - **A second launch with the same key** signals the first through a named event, which brings its window to the front (restored if minimised), and exits before any window, store or browser is opened.
  - **An abandoned mutex** (the first instance crashed) counts as free.
  - **The WebView2 user-data folder:**
    - the default installation keeps `%LOCALAPPDATA%\…\WebView2`, so the Vultr portal sign-in and cookies are kept;
    - a `DCX_DATA_DIR` store uses `<scratch>-webview2`, next to its scratch store (like its `-logs` folder).
    - The self-test therefore never shares a browser process with the installed app, and `build.ps1`'s temporary folder takes its profile away with it.
  - **`--cleanup`** (uninstaller) runs without the guard, as before.
- **Alternatives considered:**
  - **Reconnecting the session after a renderer crash:** this loses the shell's state (running commands, tmux attach, sudo), and the transport wasn't the thing that failed.
  - **One profile per process (random folder):** cookies and the portal sign-in would be lost every start, and folders would pile up.
  - **Blocking a second instance with a message box:** a box the user must dismiss; bringing the running window forward is what they wanted.
  - **A per-machine (`Global\`) mutex:** different Windows users have different stores and profiles, so they mustn't block each other.
- **Not done:** recovery for the other WebView2 hosts (web console windows, the document viewer). They can be reloaded by hand and hold no live session.

### ADR-022 Crash recovery: a recovery journal and one recovery screen (spec §56, 2026-10-18)
- **Problem:** since phase 1 the app has detected an unclean exit (`running.marker`) and offered to reopen the saved-host tabs, disconnected. What a crash, a kill or a power cut still lost:
  - **Unsaved remote-file edits:** `TextEditorWindow` held the text only in the window. A crash lost it. So could a normal exit: the editor has no owner, and when the main window closes the application shuts down without waiting for an answer to the editor's "unsaved changes" question (and Windows log-off never asks it).
  - **Unfinished transfers:** the queue lived in memory. The `.dcxpart` files stayed at the destination, but nothing remembered what was being copied where, so resuming meant redoing the selection by hand.
  - **The layout:** splits, pop-out windows, the active tab and tab groups were not recorded. RDP tabs were not restored at all.
  - **Notes and incidents:** autosave runs 1.2 s after typing stops. Nothing flushed on a fatal error.
  - **Prompts:** the restore question was a plain yes/no for tabs only.
- **Decision 1: one encrypted recovery journal, `recovery.dcx`.**
  - **What it holds:**
    - unsaved editor drafts: the source (`local` / `host:<id>`), the path, the text, a SHA-256 of the text as loaded, and when;
    - unfinished transfer jobs: both ends, items, destination, plan, options and label.
  - **How it's written:** `SecureStore`, like everything else.
    - Debounced: 2 s after a change. Nothing is written while nothing changes, so there is no timer and no idle I/O.
    - Synchronously on a fatal error (`AppDomain.UnhandledException`, the UI exception handler), on Windows session end (log off / shut down), and when the main window closes.
    - It is skipped while the store key isn't loaded.
  - **When it's empty, the file is deleted,** so a clean store has no journal.
  - **Bounded:** a draft over 2,000,000 characters isn't journaled (the editor refuses files over 4 MB anyway), and at most 8,000,000 characters in total. Skipped drafts are logged by name only.
  - **Workspace v2:** `workspace.dcx` entries gain optional fields (old files still read):
    - which main pane a tab was in, or whether it was popped out;
    - the active tab;
    - the tab group label;
    - RDP tabs (kind `rdp`).
- **Decision 2: what gets offered, and when.**
  - **At start,** one "Recover previous work" window when:
    - the journal holds anything (drafts or transfers survive a clean exit too, because shutdown can't ask), or
    - the last exit was unclean and there are tabs to reopen.
  - **Every item has a checkbox:**
    - **Tabs:** reopened disconnected, never auto-connected, in their panes, pop-outs and groups.
    - **Each draft:** reopened in the editor, marked "recovered — not saved yet", so nothing is written anywhere until you press Save.
    - **Each transfer:** queued again, resuming the part files.
  - **Choices:**
    - **Recover selected** applies the ticked items and discards the rest.
    - **Discard all** asks first, because drafts are lost.
    - **Later** keeps everything in the journal and asks at the next start.
  - **On a recovered draft:** when you press Save, the editor first re-reads the file. If it changed since you opened it, it asks before overwriting.
  - **Recovered sync-mirror jobs** never repeat their deletions: the copy resumes, and a warning says to run the sync again to mirror.
  - **During the self-test** the window is never shown; its content is built and rendered off-screen.
  - **The crash marker is now trustworthy:** only one instance runs per store (ADR-021), so a second copy can't fake an "unclean exit".
- **Decision 3: flush what can be flushed.**
  - On a fatal error or session end, open notes and incident editors save immediately (their debounced autosave is forced) before the journal is written.
  - A hard kill or power cut can't run code; the loss window is the debounce: 1.2 s for notes, 2 s for drafts.
- **Alternatives considered:**
  - **Journal every keystroke:** constant encrypted writes and SSD wear for a few hundred milliseconds of protection.
  - **Save drafts to the server automatically:** writes a half-edited config file to a live server, which is unsafe.
  - **Plaintext temp files for drafts:** breaks the encrypt-everything rule.
  - **Re-running mirror deletions:** a deletion decided before the crash may be wrong after it.
  - **Restoring exact split geometry:** the panes return in the same order and grouping, and the ratios are approximated; that is good enough for a recovery.
- **Not done:**
  - the AI conversation (memory-only by decision; optional encrypted history is a follow-up);
  - an unsent AI prompt;
  - Settings/editor dialogs half-filled at the moment of a crash.
### ADR-023 Foundry Local: the `foundry server` CLI, model IDs and a lightweight CPU model (2026-10-18)
- **Problem:** DarkCipher0x's first try of the NPU engine (Copilot+ laptop: Intel Core Ultra, AI Boost NPU, Arc 140V) failed: the current Foundry Local CLI renamed `foundry service …` to `foundry server …` and answers "Unknown command: 'service' — Did you mean 'server'?". Discovery (`service status`) and the Settings start button (`service start`) both used the old verb. Two more gaps showed on review:
  - the OpenAI-compatible `/v1/chat/completions` endpoint wants a model **ID** (`Phi-4-mini-instruct-openvino-npu:2`), not the alias (`phi-4-mini`) the CLI takes, and the default setting was the alias;
  - `RunLocalAsync` read stdout to the end, so a CLI that starts a daemon which inherits the pipe could hang until the daemon exits.
- **Decision 1: try the new verb, fall back to the old one.** `AiClient.FoundryServerAsync(args, oldArgs)` runs `foundry server <args>`; only when the answer is an unknown-command error does it run `foundry service <oldArgs>` (old CLIs have no `--idle-timeout`). Discovery uses `status`; the button runs `server start --idle-timeout 0` so the service doesn't stop when idle. Error texts name the verb that matched.
- **Decision 2: parse every local URL in the status output.** Colour codes are stripped; loopback is preferred over `[::1]`; a wildcard binding (`0.0.0.0`, `+`, `*`) means loopback; port 0 (the CLI's "not bound" placeholder) is ignored. A typed address still skips the CLI.
- **Decision 3: resolve the model ID before each chat.** `PickFoundryModel`: an exact ID (ignoring the `:version`) is used as typed, so a deliberately pinned CPU build stays on the CPU; an alias resolves through `/foundry/list` to its variants, NPU → GPU → CPU, preferring one the server already lists in `/v1/models`; without a catalogue, an ID that starts with the alias (instruct builds first). Cached per address + setting; `ForgetFoundry` clears it. A "not found / not loaded" answer adds "run: foundry model load <model>".
- **Decision 4: a lightweight CPU model** (DarkCipher0x's ask): "Lightweight CPU model" in Settings sets and loads `qwen2.5-0.5b-instruct-generic-cpu` (Qwen2.5 0.5B, about 1 GB RAM, tool calling). It leaves the NPU and GPU free.
- **Decision 5: `RunLocalAsync` returns when the CLI exits,** then gives the pipes 2 s to drain; a child holding them no longer blocks it.
- **Test seam:** `AiClient.FoundryCli` (the self-test swaps in a fake CLI; the service is a real loopback HTTP server).
- **Not verified:** a real Foundry Local install. The status output format is not documented beyond "local service URLs"; the parser accepts both known shapes. Intel NPU acceleration needs the Intel NPU driver (shown in Settings and help).
- **Decision 6: the first-run AI default is Foundry Local** (DarkCipher0x: "ollama shouldnt be default"). `AppSettings.AiEngine` now defaults to `npu`; `AiDefaults.ApplyAsync` (after the main window shows, never in the self-test) detects the hardware once and sets phi-4-mini with an NPU, else the lightweight CPU model, then sets `AiDefaultChosen`. Settings still at the old untouched Ollama default (engine, model, address, no tunnel) move too; anything configured is left alone. Foundry Local is listed first. `Recommend()` suggests the lightweight CPU model where it suggested Ollama CPU-only; GPU recommendations still name Ollama (applied only on request).
- **Same release, unrelated fix:** `Menus.Attach` fills a menu only in `ContextMenuOpening`, so `ContextMenu.IsOpen = true` on a left click opened it empty: the Infrastructure "＋ Add ▾" button (no provider could be added) and the host chips on notes and incidents. `Menus.AttachDropDown` attaches the right-click menu and opens the same items below the button on a left click. During the self-test `ShowBelow` records the item headers instead of opening a popup (no window on screen).
- **Packaging:** `build.ps1`, `tools\write-checksums.ps1` and `tools\make-guide.ps1` write to `dist\<version>\` (DarkCipher0x's ask); 0.1.0–0.1.4 were moved into their folders, with a copy of their release notes and checksums.

### ADR-024 Foundry Local set-up steps, a hardware-sized default model, and the install folder (2026-10-18)
- **Problem (DarkCipher0x, after trying 0.1.5):** the Foundry buttons were a loose row of CLI names, so the order wasn't obvious. He wanted numbered steps, and a default model that suits the PC ("choose the best model based on the hardware"). He also wanted the program folder to read `DarkCipher Systems\ServerManagement`, and the AI side well documented.
- **Decision 1: four numbered steps with their own status.**
  - **The steps:** 1 Install (`foundry --version`; if missing, `Dialogs.Confirm`, then `winget install --id Microsoft.FoundryLocal -e --silent …`; refused in offline mode); 2 Start (`FoundryServerAsync("start --idle-timeout 0", "start")`, then the discovered address); 3 Load (`foundry model load <FoundryModel>`); 4 Test (the existing chat test).
  - **Status:** each step has a ✓ / ✕ line. Opening Settings with the Foundry engine runs only local checks (`--version`, `server status`, `/v1/models`) to tick what's done. Nothing is downloaded or started on open.
  - **Model choice:** buttons set MODEL before step 3: Best for this PC, NPU phi-4-mini, CPU phi-4-mini, CPU lightweight.
- **Decision 2: `AiDefaults.Best(npu, ram)` picks the model, used by both the default and "Detect hardware".**
  - **The tiers:** NPU → `phi-4-mini`; else ≥ 15 GB → `Phi-4-mini-instruct-generic-cpu`; ≥ 7 GB → `qwen2.5-1.5b-instruct-generic-cpu`; else `qwen2.5-0.5b-instruct-generic-cpu`. The thresholds sit 1 GB under 16 / 8 because Windows reports a little less than the installed RAM.
  - **`AiDefaultRevision` (now 2)** re-applies the default once after an update. It does so only where the engine is Foundry with no typed address and a model from the default set, or, on first run, the untouched old Ollama default.
  - **`Recommend()` always returns Foundry with `Best`;** a GPU with ≥ 7 GB VRAM is mentioned as an Ollama option. Ollama never becomes a default.
- **Decision 3: the install folder is `ProgramFiles64Folder\DarkCipher Systems\ServerManagement`.**
  - The MajorUpgrade (same UpgradeCode) removes the old product, including its `Server Management` folder, and installs to the new one. The old location isn't remembered (no RegistrySearch), so the new default applies.
  - Data lives in `%APPDATA%` / `%LOCALAPPDATA%` and is unaffected. Shortcuts are recreated by the installer.
- **Docs:** help.md (AI engines, set-up steps + troubleshooting, models) and a new PDF chapter `docs/guide/15-ai.md`. `guide.txt` now includes every ADR and release note (it had stopped at 0.1.3 / ADR-021).
- **Not verified:** a real MSI upgrade from 0.1.5 (needs UAC); Foundry Local on real hardware.

### ADR-025 Self-test robustness: root causes, not retries (2026-10-18)
- **Problem:** four things made the self-test less trustworthy than it should be:
  - **The add-on "flood" case** sometimes reported "timed out" (s26).
  - **The closed-screen leak check** once kept the AI operator screen alive (s26).
  - **Nothing checked for timers** left running after a screen closes. A running `DispatcherTimer` roots its owner and keeps ticking forever.
  - **One watched run ended `exit -1` with no report at all** (s30).
  - **Reruns only hid these.** A test suite that passes "on rerun" can't tell a regression from noise.
- **Decision: fix each root cause, and add a deterministic test where the flake was timing.**
  - **Flood: the test was the problem, not the detector.**
    - **Cause:** the test add-on built each of its 3,000 messages with PowerShell `ConvertTo-Json` and flushed after every one. Together with PowerShell's start-up at Low integrity, that took longer than the 5 s run timeout on a busy PC, so the timeout won.
    - **Fix:** it now writes one pre-built line as fast as it can, as a real flooder would. The host's limits (2,000 messages / 1 MB, then the timeout) are unchanged.
  - **Assistant leak: a real (small) product bug.**
    - **Cause:** `AssistantView` checked the AI engine on load in an instance `async` method (`ModelsAsync` with 4 s, then `DescribeAsync`, which for Foundry runs a CLI). The Loaded handler awaited it. The pending state machine held the closed screen for up to ~8 s or more, past the leak check's window under load.
    - **Fix:** the check is a static method holding the view through a `WeakReference`, linked to a token cancelled in `Dispose`. The handler no longer awaits it.
    - **New deterministic test:** an AI engine that accepts and never answers; the closed screen must be collectable within about a second, while the check is still waiting.
  - **Timer audit (new test):** every app screen in the sidebar is opened and closed. Then no timer registered with the dispatcher may be running for an element that is no longer loaded.
    - The handler's owner is resolved through lambda closures.
    - The dispatcher's private timer list is read by reflection, in the self-test only, read-only.
    - **Excluded:** the Vultr portal (a real website), cloud screens (provider APIs) and local shells (covered by the endurance loops).
  - **No silent self-test exits.** Every early exit at start-up writes a one-line FAIL report with the reason:
    - another instance on the test store;
    - store can't be opened;
    - store asks for a password;
    - start-up recovery stopped.
  - **No MessageBox in self-test mode:** the store-open failure used to show one, which would be a window on DarkCipher0x's screen.
  - **Watch script and build.ps1:** both keep the process handle (Windows PowerShell can otherwise lose the exit code). The watch script waits until the exe has been quiet for 3 s, the suspected build race. If there is still no report, it prints the timing, the running instances and the error log's location.
- **Alternatives considered:**
  - **Retry flaky tests:** hides real intermittent bugs; the assistant one was real.
  - **Raise timeouts:** slower runs and the same race at a higher load.
  - **A WPF diagnostics API for timers:** none is public. The audit uses reflection only inside the self-test, and it fails loudly if the field ever disappears.
- **Not done:** the `exit -1` cause is not proven (it didn't recur). The changes make any recurrence explain itself.

### ADR-026 Foundry Local: hands-off set-up, self-healing service, models from this PC's catalogue (2026-10-18)
- **Problem (DarkCipher0x, testing on another PC):**
  - **The service lied about its health.** `foundry server status` said Ready, but every `foundry model load` failed with exit 4 until the service was killed and started again from the app.
  - **Set-up needed four manual steps.** The model names were hard-coded IDs that may not exist on another machine (an x64 `…-generic-cpu` ID on an ARM64 Copilot+ PC; a CPU build chosen on a PC with a strong GPU).
  - **The model choice wasn't explained.**
  - **The app parsed text output and showed only its last line** (the generic "Hint").
- **Decision 1: one `FoundryManager` that drives the CLI's JSON output (`-o json`).** Every command of Foundry Local 0.11 has it; older CLIs fall back to the existing text parsing.
  - `foundry status -o json`: versions, NPUs/GPUs/RAM, service state, URLs, `localServiceReachable`.
  - `foundry model list -o json`: this PC's best variant per model, with `device`, `fileSizeMb`, `cached`, `supportsToolCalling`.
  - `--variants`: every device build. `--loaded`: what's in memory.
  - Errors come back as `{error:{code,message,hint}}`, and the code and message are shown, never just the hint.
- **Decision 2: hands-off by default (`FoundryAutoSetup`, on), with every step also a button.** `EnsureReadyAsync` runs in order and reports each step to the status bar and the Settings panel:
  1. **Installed?** If not, it installs Foundry Local with `winget install --id Microsoft.FoundryLocal -e --silent --accept-package-agreements --accept-source-agreements`. That is Microsoft's package from the winget source; the status line says so and the action is audited.
  2. **Running?** If not, `foundry server start`.
  3. **Healthy?**
     - **Test:** `status.connectivity.localServiceReachable` and a real `GET /v1/models` within 5 s.
     - **If not healthy, it heals:** `foundry server restart`; if the service is still broken, it stops it, ends the recorded daemon PID only if that process is `foundrylocald`, and starts it again. One attempt per run.
  4. **Model:** the configured model is resolved against **this PC's** catalogue.
     - **If that exact build isn't offered here,** the same model's best build for this PC is used, and the status says so.
     - **If it isn't downloaded,** `foundry model download` runs (the size is shown first).
     - Then `foundry model load`.
     - **A load that fails with a service error** (exit 4 / daemon codes) triggers the heal step and one retry. That is exactly what DarkCipher0x had to do by hand.
  5. **Verified:** the model appears in `--loaded`.
  - **When it runs:**
    - at start-up, when the engine is Foundry Local, in the background with no dialogs;
    - before an AI request when Foundry isn't reachable (one heal attempt, then the request);
    - from "Set up automatically" in Settings.
  - **Gates:**
    - **Offline mode:** blocks the install and model downloads, but not local start/load.
    - **On battery:** no automatic download over 1 GB (the button still works).
    - **Self-test:** never touches the real CLI or winget (both are swappable test seams).
- **Decision 3: the model guide comes from this PC.**
  - The Settings model list is built from `foundry model list` on this machine, grouped **NPU / GPU / CPU**. Each row shows the size, whether it's downloaded, whether it can use tools (the AI operator needs this), and a short "good for" line for known families (lightest, balanced, best quality, coding, reasoning).
  - **Recommended:** on top, from this PC's hardware:
    - NPU → phi-4-mini on the NPU;
    - a discrete GPU with enough VRAM → the GPU build of a stronger model;
    - otherwise a CPU build sized to the RAM.
  - The default (`AiDefaults`, revision 3) now picks from the catalogue the same way. It is still applied only to settings the user hasn't changed.
- **Alternatives considered:**
  - **Calling Foundry's REST management endpoints directly:** they are undocumented and change between releases; the CLI is the supported interface.
  - **Bundling Foundry Local in our MSI:** it's an MSIX with its own updates; winget keeps it current.
  - **Asking before every step:** DarkCipher0x asked for hands-off; the buttons and the visible status keep it in his control.
- **Not done:** ARM64 Copilot+ hardware is not available here; the catalogue logic is tested with this PC's real catalogue (x64, GPU) and a synthetic NPU catalogue in the self-test.

### ADR-027 Product name "DarkCipher ServerManagement" (2026-10-18)
- **Context:** DarkCipher0x asked for the product to be called "DarkCipher ServerManagement" everywhere it is shown: the app, the top of the window, the installer, the shortcuts.
- **Decision: rename what people see; keep what identifies the installation.**
  - **Renamed:** `App.ProductName`, the window title and header, every window title suffix, the csproj `Product` / `AssemblyTitle` (the exe's file properties), the MSI package name, its summary, messages, feature title and finish-page text, the Start-menu, desktop and repair shortcuts, `License.rtf`, report and document footers, snippet exports, 2FA labels, the screenshot folder, AI prompts, the help, the guide and the docs. The update manifest's `product` is the new name; `Updater` also accepts the old name, and the self-test checks that.
  - **Kept, because changing them would break upgrades or orphan data:** the UpgradeCode, the program folder `DarkCipher Systems\ServerManagement`, `%APPDATA%` / `%LOCALAPPDATA%\DarkCipher\ServerManagement`, the DPAPI entropy, `HKLM\Software\darkcipher0x\ServerManagement`, the AppUserModelID `darkcipher0x.DarkCipherServerManagement` (keeps taskbar pins grouped), the single-instance mutex prefix, the exe name `DcxServerManager.exe`, and the file names `DarkCipherServerManagement-<ver>-*`. (0.4.0, ADR-054: the data folders and the install key moved to `DarkCipher Systems\ServerManagement` with a one-time migration; the rest is still kept.)
- **Consequences:** the major upgrade removes the old shortcuts and creates renamed ones, and *Installed apps* shows the new name. Existing authenticator entries keep the old label (only the label differs; codes still work). New screenshots go to `Pictures\DarkCipher ServerManagement`. Release notes from earlier versions use the new name in their headings; the RESUME-session history files are left as written.

### ADR-028 Providers, one cloud workspace, AI tools and terminal targeting, clean start, Settings categories (2026-10-19, v0.1.11)
- **Context:** DarkCipher0x asked for a point release that adds Hetzner (first priority), VMware and Hyper-V, reorganises the remote-API navigation (Vultr alone filled the sidebar), deepens the AI integration including running commands in a chosen terminal, opens every explorer collapsed, and redesigns the cluttered Settings.
- **Decisions:**
  - **Hetzner Cloud on the existing cloud layer (ADR-020), not a new subsystem.** `Hetzner/HetznerClient.cs` (ICloudApi), `HetznerCatalog.cs` (17 screens) and `HetznerProvider.cs`, registered in `CloudProviders.All`. Every catalog operation was taken from Hetzner's official `cloud.spec.json`, and the self-test checks each one against the spec's endpoint list. One account = one project token; a token the user marks read-only is refused locally for every change. Changes return Hetzner actions; the client follows `GET /actions/{id}` to success or error, reporting progress (`CloudProgress`), bounded and cancellable. `CloudHttp` honours `RateLimit-Reset` when no `Retry-After` is sent. Not offered: the VNC web console (no client), metrics, and Hetzner's separate Robot and Storage Box APIs (different credentials and models).
  - **VMware vCenter and Hyper-V as infrastructure providers (ADR-007), not cloud providers:** their model is hosts, VMs and checkpoints, verified by re-reading. `Infra/VSphere.cs` uses the vSphere Automation REST API (vCenter 7.0+): a Basic sign-in once, then the session id only, TOFU TLS pinning, VM power and guest power, and clone (7.0 U2+). Snapshots, content-library templates, VM creation and vMotion are **not implemented**: snapshot support in the REST API couldn't be confirmed. `Infra/HyperV.cs` drives the Hyper-V PowerShell module locally or over WinRM. The scripts are fixed text sent as `-EncodedCommand`; every value goes as JSON on stdin, so no user, server or AI input can become code. It authenticates with Windows sign-in by default; an optional vault credential goes only on stdin. It never enables Basic auth, unencrypted WinRM or TrustedHosts. `InfraAction.Inputs` adds validated input forms (clone name, checkpoint name, export path, new VM).
  - **One "Cloud providers" workspace** (`Views/CloudWorkspaceView.cs`) replaces the per-provider sidebar sections. It holds a provider tree with each provider's catalog groups, reference lists and tools, plus search and up to six recent screens. Old nav keys (`vultr:Instances`) open inside it, and the header account picker follows the provider shown (that picker was removed in 0.3.0-preview.4). Saved sidebar state naming VULTR or CONTABO is migrated (`MainWindow.MigrateNavSettings`).
  - **AI: a shared tool registry** (`Ai/AiTools.cs`) that features add to. The operator offers it next to its built-in tools. Tools: current context (names only), infrastructure inventory and actions (same checks as the screen), any cloud list from a catalog, terminal sessions and runs, notes and incidents (off by default; each read asks), PDFs, and creating notes (approved). Untrusted data comes back fenced as `<data … untrusted="true">`, masked and bounded. Both prompts state that data is never instructions.
  - **Terminal targeting through a bridge** (`Ai/TerminalBridge.cs`, `Ai/CommandRisk.cs`), never text injection by the model:
    - **Targeting:** the user picks a live session in the AI Engineer. A target is identified by its session id and connect count, so a closed, disconnected or reconnected terminal is refused until the user chooses it again.
    - **Review:** every command is risk-assessed (read-only, change, sensitive, destructive; unknown programs count as changes; the riskiest part wins). It is shown in full with the target, user, directory and environment. Sensitive commands need "RUN" typed; destructive ones, or changes in PRODUCTION, need the session name typed.
    - **Running:** the command is typed into that session between markers that the shell assembles at run time, so the output, exit status and directory can be read back. Stop sends Ctrl+C, and a time limit bounds the wait. One command runs at a time.
    - **Output and audit:** output for the AI is masked and bounded to 8,000 characters. Sends and results are audited without the output. A chat answer never runs anything: each run starts with a click (or an approved `terminal_run`).
  - **A clean start:**
    - **Sidebar:** sections start collapsed. Expansion lasts for the session only, unless the user opts in to remembering it (saved encrypted, existing sections only).
    - **Hosts tree:** its root and folders start collapsed, and expansion of removed folders is dropped.
    - **Cloud workspace tree:** starts collapsed; a search expands only its hits.
  - **Settings in categories:** 8 categories and 17 cards, one category at a time, with a search across everything (titles, descriptions and option labels; never the values in text boxes). Each card shows its scope. Misplaced options moved: screenshots and crash recovery to Data & backups, sidebar layout to Navigation & explorers, the AI approval switch to "AI permissions & safety". The old remembered-collapsed cards are retired, and "Reset this category" exists only where every default is safe (Appearance & navigation, Terminals & connections).
- **Alternatives considered:**
  - Hetzner via a third-party SDK: rejected; it adds a dependency and the shared engine already does the work.
  - VMware via SOAP/vim25: rejected; heavy, and it would add XML parsing of untrusted answers.
  - Hyper-V via WMI from .NET (`System.Management`): it would duplicate PowerShell's semantics, and the cmdlets are the supported interface.
  - Letting the AI type into terminals directly: rejected; no review, no target binding, no exit status.
  - A separate Settings window: rejected; the existing tab plus categories keeps "reveal this setting" links working.
- **Consequences / limits:**
  - **Bridge:** it needs a POSIX shell (SSH to Linux, WSL) or PowerShell; cmd, Windows or network-device SSH, serial and Telnet sessions are listed as unsupported with the reason. An interrupted command reports "exit status unknown". Typing into the same terminal while an AI command runs mixes the output.
  - **Not verified on real systems:** Hetzner, vCenter and remote Hyper-V have only been run against fakes plus the real PowerShell plumbing.

### ADR-029 Free code signing through the Microsoft Store (MSIX), prepared 2026-10-19 (not yet submitted)
- **Context:** DarkCipher0x asked for Microsoft's free code-signing route. Microsoft's code-signing options page lists one free option without SmartScreen warnings: an MSIX package published through the Microsoft Store, which Microsoft signs after certification. Azure Artifact Signing is not open to individual developers in the UK. OV/EV certificates cost money, and self-signed certificates are for testing only.
- **Decision: ship the same binaries a second way, as a Store MSIX; the website MSI is unchanged.**
  - `tools\make-msix.ps1` packages `.\publish` (made by `build.ps1`) with `installer\msix\AppxManifest.xml`. It uses Microsoft's `makeappx`/`signtool`, pinned through NuGet (`tools\msix\MsixTools.csproj`, `Microsoft.Windows.SDK.BuildTools`). The Store package is uploaded unsigned.
  - `-TestSign` makes a self-signed, local-only test package. `-Check` only validates the manifest and packs.
  - The identity comes from Partner Center (`installer\msix\store-identity.json`). The MSIX version is the app version with `.0` added.
  - Full trust (`runFullTrust`). File-system and registry write virtualization are disabled (`unvirtualizedResources`), so a Store install uses the same `%APPDATA%\DarkCipher Systems\ServerManagement` store as the MSI (since 0.4.0; `DarkCipher\…` before), and uninstalling never deletes the vault.
  - `Services\Packaging.cs` detects package identity. When packaged, the in-app updater is off for good (the Store delivers updates). The process keeps the manifest's AUMID instead of setting its own, and a restart goes through `shell:AppsFolder\<family>!App`.
- **Alternatives considered:**
  - Azure Artifact Signing: rejected; not available to individuals in the UK, and about $9.99 a month.
  - An OV certificate: rejected; costs money and needs a hardware token.
  - Store submission of the MSI: rejected; that path requires us to sign it with a CA certificate.
  - A Visual Studio packaging project (wapproj): rejected; it needs Visual Studio, and the script works with the .NET SDK alone.
- **Consequences:**
  - The website MSI and zip stay unsigned, and the updater stays off for them.
  - A Store install has no uninstall data clean-up (MSIX has no custom actions) and needs Windows 10 1903 or later.
  - If certification refuses `unvirtualizedResources`, the fallback keeps the data inside the package, where it is deleted on uninstall (see `installer\msix\STORE-SUBMISSION.md`).
  - The submission itself is DarkCipher0x's to do: the developer account, reserving the name, and certification.

### ADR-030 Theme add-ons (data-only add-ons that replace the palette), 2026-10-19 (0.1.12)
- **Context:** the owner asked for an add-on that gives the app an organisation's own look (its colours, later its logo and name). Add-ons (ADR-012) were programs only: an `entry` and 1–30 commands were required, and nothing an add-on did could reach `ThemeManager`, whose palette was a private table.
- **Decision: a theme is a data-only add-on, applied as an overlay on the built-in palette.**
  - `addon.json` gains `theme` (a `.json` file in the package). With `theme` and no `entry`, the add-on is theme-only: commands and permissions are refused, and the approval screen says "This is a theme", not "An add-on is a program".
  - `Addons/ThemeAddons.cs`: `ThemePack.Parse` accepts only `colors` (known palette keys plus `Accent`, `AccentHover`, `OnAccent`, `Brand`; each `{dark?, light?}` exactly `#RRGGBB`), `uiFont` (≤ 120 characters, no quotes or separators), `name` and `notes`. Anything else is refused at `AddonPackages.Inspect`, before installation.
  - `AppSettings.ThemeAddon` (encrypted settings) names the one theme in use. `ThemeManager.Apply` asks `ThemeAddons.Active()` and uses the theme's colour where it names one, else the built-in value; `Selection` stays derived. `Typography.Apply` uses the theme's `uiFont` only when the user has no UI font and the font is installed (`Resolve` falls back).
  - Files are hash-verified (`VerifyFiles`) whenever the theme is applied; a mismatch disables the add-on with the reason, exactly like a program add-on's run.
  - Install from the Add-ons screen wears the theme at once; **Back to the default theme**, Disable and Remove repaint with the built-in palette (`ThemeAddons.Reapply` / `Use(null)`).
  - **Environment label colours (`Env*`) are not themable**: they are a safety signal (ADR-017).
  - The sample theme lives in `samples/addons/sample-theme/` (since 0.4.0 the neutral "Ocean" palette; until then it carried one organisation's colours taken from its public stylesheet, with body font Roboto). The light accent is the darker shade so accent-coloured text stays readable on white.
- **Alternatives considered:**
  - Letting a theme ship a XAML `ResourceDictionary`: rejected. XAML can instantiate types and reach the file system and network; it would turn a colour change into code execution inside the app, outside the add-on sandbox.
  - A theme as a program add-on that calls a new `ui.setTheme` bridge method: rejected. It would run a program for a colour change and keep the theme alive only while that program runs.
  - Loading font files from the package: rejected for now. Private fonts from a package would be a new parser surface, and redistributing fonts raises licence questions; the theme names an installed font instead.
  - Several themes at once, or per-key merging across themes: rejected as unnecessary; one theme at a time is easy to undo.
- **Consequences:**
  - Themes change colours and the UI font only; shapes (corner radius, pill buttons), terminal colours and RDP are untouched.
  - The self-test (`SelfTestThemes.cs`, `ThemeAddonTests`) packs the repo's sample theme, checks every colour in dark and light, the font rule, Back to default / Disable / Enable, the tamper path, 12 malformed themes and Remove, and renders the Add-ons screen in both modes.
- **Addendum (0.1.13, same day): header branding, and enabled = in use.** The owner looked at 1.0.0 and asked for the organisation's real colours (the orange-led palette on an indigo dark mode read as "purple and orange"), its logo instead of the DarkCipher one, its own product name in the header, and the theme to override the defaults whenever it is enabled.
  - `theme.json` gains `productName` and `tagline` (plain text, ≤ 60 characters), `logo` (a PNG in the package) and `logoBackground` (a plate colour). `MainWindow.ApplyBranding` (on `ThemeManager.Changed`) swaps the header logo, name, tagline and window title, and puts them back from `ThemeManager.ActivePack == null`.
  - Logos are decoded with `PngBitmapDecoder` only (signature checked first), ≤ 1 MB, ≤ 2048 px a side, one frame, frozen; checked at `Inspect` and again from the verified installed file. The PNG decoder is Windows' own (WIC), already used for every image the app shows, so no new parser is added; SVG is not accepted (WPF has no SVG renderer, and SVG can reference external content).
  - `AddonPackages.SetEnabled(on)` → `ThemeAddons.Reapply(a, enabled: true)` puts an enabled theme add-on in use.
  - This reversed 1.0.0's "no logo" choice at the owner's request: the organisation's logo (rendered from its SVG to PNG) shipped in the sample package for personal use only. 0.4.0 replaced that sample with the neutral Ocean theme and a generated logo so the whole tree can be published; an organisation now builds its own theme in Settings → Appearance → Branding & colours (ADR-054) and exports it as an add-on.
  - Still not rebranded: dialogs, `App.ProductName` in messages, the installer, Start menu, taskbar icon and About text.

### ADR-031 Provider controls that cover each management API (2026-10-19, 0.1.14)
- **Context:** DarkCipher0x asked to "implement more server controls with the infrastructure API to cover everything that each service has to offer". Before 0.1.14 the infrastructure providers (ADR-007, ADR-028) offered power only (plus clone on vCenter and checkpoints / export on Hyper-V), and the cloud catalogs (ADR-020) missed several resource types.
- **Decision: one gap table, checked against each provider's own reference, then everything safe and verifiable from it.** `docs/PROVIDER-API-COVERAGE.md` lists, per provider, what the API offers, what the app had, what 0.1.14 adds and what stays out with the reason. Sources: Proxmox `apidoc.js`, Broadcom's vSphere Automation API reference, Microsoft Learn (Hyper-V module), Docker Engine API, `unraid/api` generated schema, Hetzner `cloud.spec.json`, Contabo `api-v1.yaml`, Vultr's official Go client (its OpenAPI file sits behind a bot challenge).
  - **Proxmox:** pause / resume (QEMU, verified by `qmpstatus`), snapshots (list, take with optional RAM, roll back, delete), clone (`cluster/nextid` when empty), migrate, vzdump backup, convert to template, guest agent info, node tasks, storage contents. `Call` now sends form parameters for POST/PUT. Each action maps to its privilege (`VM.Snapshot`, `VM.Clone`, `VM.Migrate`, `VM.Backup`, `VM.Allocate`, `VM.PowerMgmt`), checked on `/vms/<id>` → `/vms` → `/` and named when missing; `CanControl` = any of them. Snapshot names are `pve-configid`, storages `pve-storage-id`, VMIDs 100–999999999, all validated before a request. A shared `WaitTask` requires the task to end OK (or with WARNINGS) before the re-read.
  - **vCenter:** vCPU count and memory (`PATCH …/hardware/cpu|memory`, re-read), delete (powered off, verified by 404 — `NotFoundException`), guest identity / interfaces / Tools (read), host disconnect / reconnect (verified by `connection_state`; host ids validated like VM ids). Snapshots and maintenance mode are absent from the REST API and stay out (documented, not faked).
  - **Hyper-V:** vCPU count and startup memory (VM off), rename, connect the first adapter to a switch, delete VM (disks kept), rename checkpoint, details (read). The inventory now carries `cpu`, `memStartup`, `dynamic` and `switch` per VM so each change is verified from it. Scripts stay fixed text; values travel only as JSON on stdin.
  - **Docker:** pause / unpause / kill, inspect / top / stats (read; CPU % with the docker CLI's formula), removal of images, networks and volumes (`ValidObjectId` per kind; built-in networks never offered; verified by 404 / `inspect` failing). Both backends (Engine API, CLI over SSH) implement `ReadAsync`, `RemoveAsync`, `ExistsAsync`.
  - **Unraid:** `DockerOps` (start, stop, restart, pause, unpause) and `VmOps` (start, stop, reboot, forceStop, pause, resume, reset) are discovered from the schema or probed without `$id`, and offered only when present.
  - **Hetzner:** server and load balancer metrics (a provider Special: last hour, repeated `type`, RFC 3339 window, average / peak / latest), alias IPs, network members, LB update service and reverse DNS, record add / remove / protection, secondary-zone primaries. Every catalog path stays checked against the spec list in `SelfTestRelease.cs`.
  - **Contabo:** Object Storage (order, rename, stats, resize, cancel — no delete), VIPs, PTR records, Tags with assignments, instance add-on orders. A new `Storage` group. Every catalog path is checked against the spec list in `SelfTestInfraCoverage.cs`.
  - **Vultr:** IPv6 reverse DNS, default IPv4 reverse, extra-IPv4 removal, ISO status, bare-metal IPv6 / upgrades / VPCs, Block snapshots, VFS with attachments, sub-accounts.
  - **UI:** no new screen code. `Infra.FillDefault` fills `{name}`, `{now}` and any `{prop}` of the object into input defaults (e.g. the current vCPU count); the Infrastructure confirmation strips the new glyphs; the "Allow changes" text lists the new kinds of change.
- **Alternatives considered:**
  - Generic "edit configuration" forms (Proxmox `PUT …/config`, vCenter device editing, Hyper-V `Set-VM` with every switch): rejected for now — dozens of interdependent fields per guest type, and a form that can't be verified field by field would be a fake control. Listed as candidates.
  - Node / host power (Proxmox `nodes/{node}/status`): rejected — the API that would verify it is what goes away, and it takes every guest down.
  - Container create / exec, Docker prune, Unraid array and parity operations: rejected — code execution on the host, bulk deletion, or the ADR-007 array rule.
  - Consoles (Proxmox VNC, vCenter tickets, Hetzner `request_console`): rejected until the app has a VNC-over-WebSocket client.
- **Consequences:**
  - Coverage (user-triggerable operations): Proxmox 4 → 17 actions, vCenter 7 → 13, Hyper-V 13 → 20, Docker 5 → 14, Unraid 6 → 12 mutations, Hetzner 112 → 122 (everything but the console), Contabo 48 → 70, Vultr ~175 → ~201 (26 added).
  - Every new change is gated by Allow changes / a write token, disabled with the reason, confirmed (typed for data loss and PRODUCTION), audited, and verified by re-reading.
  - Self-test: `SelfTestInfraCoverage.cs` (`InfraCoverageTests`) runs every new action against fakes of each API (Proxmox, vCenter, Hyper-V seam, Docker Engine, Unraid GraphQL) and checks the Contabo and Vultr additions against their official operation lists. Nothing real is contacted.
  - Not verified against live systems in this session: Proxmox, vCenter, Hyper-V, Docker hosts, Unraid, Hetzner, Contabo and Vultr accounts were all exercised through fakes only.

### ADR-032 Portal browser add-on: web UIs in tabs (2026-10-19, 0.1.15)
- **Context:** DarkCipher0x asked for "another add-on for opening a browser window inside the application — for portals etc." (router and NAS admin pages, the Proxmox / Unraid UI, provider portals). The app already had `WebWindow` (Vultr portal, noVNC) on the shared WebView2 environment, with no profiles, certificate handling, download control or bookmarks. His answers: **both** tabs and windows; bookmarks **global and per host**; a profile **per environment**; **never** save site passwords.
- **Decision: a data-only add-on that switches on an in-app browser (`Browser/PortalBrowser.cs`, `Views/BrowserView.cs`, `Views/PortalsView.cs`).**
  - **The add-on** (`addon.json` `browser` → `browser.json`: name, notes, ≤ 50 starter bookmarks) is data like a theme (ADR-030): no entry, theme, commands or permissions; `BrowserPack.Parse` is strict; files are hash-verified whenever it is read, and a changed package disables it. `PortalBrowser.Changed` (raised from install / enable / disable / remove) rebuilds the sidebar: **Portals** (SERVERS) and the hosts' **🌐 Open web UI** exist only while it is enabled. The browser is app code because a web engine can't run in the Low-IL sandbox and still be a tab here, and the add-on must never see cookies, pages or vault secrets.
  - **Bookmarks** (`PortalBookmark` in `AppData.Portals`, data.dcx): title, URL, folder, optional `HostId` (a host's web UI), environment, named profile, sign-in credential id. `Portals.ValidateUrl`: absolute http(s) only, no `user:password@`, ≤ 2048. Audited on add / change / remove.
  - **Profiles:** `CoreWebView2ControllerOptions.ProfileName` = `dcx-<environment>` (the bookmark's own, else its host's, else `dcx-general`), or `dcx-p-<name>` for a named profile. One shared environment / user data folder (ADR-021) with separate profiles keeps cookie jars apart without extra browser processes.
  - **Tabs and windows:** `PortalUi.Open` adds a `MainWindow.AddTab` tab and, with `AppSettings.PortalOpenIn = "window"`, pops it out with the existing `PaneHost.PopOut` — one code path, dock back any time. `Envs.ForView` knows `BrowserView`, so portal tabs carry the environment label.
  - **Address bar:** `Portals.Display` drops user info and masks query values whose names look like secrets (token, ticket, vncticket, password, sig, code…); fragments stay (Proxmox routes). A security chip: Secure / Pinned / Not secure (http) / Untrusted / CERTIFICATE CHANGED.
  - **Self-signed certificates (TOFU, the `TlsPin` rule):** `ServerCertificateErrorDetected` → SHA-256 of the DER → `Portals.Decide` = `TlsPin.Check(pin, seen, chain error)`. A matching pin → `AlwaysAllow`; anything else → `Cancel` and a banner with the fingerprint and **Trust this certificate…** (a typed host:port to replace a pin). Pins (`AppData.WebPins`, per host:port) are encrypted and audited. Every new tab calls `ClearServerCertificateErrorActionsAsync` first, and Forget clears every open tab's cache, so a forgotten pin is never still accepted from earlier in the browser session.
  - **Downloads:** `DownloadStarting` is always handled (no browser flyout): the user confirms file name, size, type and folder (risky types flagged); the folder is `AppSettings.PortalDownloadFolder` (asked at the first download); `Portals.SafeTarget` strips paths and reserved names and never overwrites. Audited.
  - **Pop-ups:** `NewWindowRequested` → a new `BrowserView` tab in the opener's profile receives `e.NewWindow` (so `window.opener` / SSO pop-ups work); `WindowCloseRequested` closes it; > 20 a minute are blocked.
  - **Navigation policy:** http(s), about:blank, data:/blob: only; file: and every external protocol (`LaunchingExternalUriScheme`) are refused.
  - **Passwords:** `Profile.IsPasswordAutosaveEnabled` and `IsGeneralAutofillEnabled` off. **⚿ Fill sign-in** injects a vault password (JSON-encoded into a fixed script) only on the bookmark's own origin, never over a refused certificate, after a warning on plain HTTP; audited.
  - **Privacy:** `IsReputationCheckingRequired = false` (no SmartScreen look-ups); `WebHost.GuardOffline` (shared with `WebWindow` now) checks every navigation and sub-request against offline mode; the Privacy report lists the portal browser posture and saved portals by network class. Cookies and site storage are kept by WebView2 in its user data folder (Chromium's DPAPI-protected store), **not** in the app's store — stated in the UI and help.
- **Alternatives considered:**
  - The browser as a program add-on (its own WebView2 process, sandboxed): rejected. A Low-IL child can't host a tab in this window, and the bridge would have to carry vault passwords for sign-in, or the add-on would have to see them.
  - The browser always on, no add-on: rejected because DarkCipher0x asked for an add-on, and it keeps the feature off (no extra surface) until wanted.
  - One profile per bookmark: rejected by DarkCipher0x (sign in once per portal); named profiles cover the cases that need isolation.
  - Saving passwords in WebView2's own manager: rejected by DarkCipher0x; it would be a second password store outside the vault.
  - Ignoring certificate errors for "local" addresses: rejected; RFC 1918 isn't proof of the device.
- **Consequences:**
  - Not covered: a certificate that Windows trusts is accepted by the browser even if a different one is pinned for that host:port (WebView2 only reports failing certificates). HTTP Basic / NTLM sign-in uses WebView2's own prompt and isn't filled from the vault. Script dialogs (alert / confirm) are the browser's own. No renderer recovery beyond a banner (ADR-021 covers terminals). Workspace restore doesn't reopen portal tabs.
  - Self-test `SelfTestPortals.cs` (`PortalTests`) runs only against a loopback HTTP server and a self-signed HTTPS server on 127.0.0.1 (the one TEST-NET address is refused by offline mode before connecting): package and 13 malformed packages, rules, encrypted bookmarks / pins, profile, password settings, pop-up tab, refused and accepted downloads, Fill sign-in on / off origin, window mode, trust → change → replace → forget, offline, Privacy rows, tamper.
- **Addendum (0.1.17, built in):** DarkCipher0x: "I love the portal addon that you built so much, that i want it natively in the app".
  - `PortalBrowser.Enabled` is now `AppSettings.PortalBrowserBuiltIn` (default **on**) **or** an enabled portal browser add-on.
  - Settings → Integrations → **Portal browser** is the switch (`PortalBrowser.SetBuiltIn`, audited). It raises `PortalBrowser.Changed`, so the sidebar's Portals and the hosts' 🌐 Open web UI follow it.
  - **The add-on stays valid:** existing installs keep working, it turns the browser on by itself, and its starter bookmarks are still offered. Its tamper rule (a changed file disables it) now only affects the add-on, not the built-in switch.
  - Nothing about the browser's safeguards changed: per-environment profiles, certificate pins, no saved passwords, downloads ask, offline gate.
  - `PortalTests` checks the built-in switch (on with no add-on; Portals shown / hidden) and runs the add-on checks with the switch off.

### ADR-033 Shared library add-on: team hosts and credentials over a file share (2026-10-19, 0.1.15)
- **Context:** DarkCipher0x asked for "an add-on that allows for shared libraries of local hosts and team credentials". His answers: the library lives on a **file share or folder** (SMB / NAS / a synced folder), the team has **10–50 people**, and it **must work offline**. Design proposal (approved by DarkCipher0x): `docs/SHARED-LIBRARY-DESIGN.md`.
  - Before this, add-ons could read hosts without secrets (`hosts.read`) and ask for one credential per dialog. Nothing could add hosts or credentials.
  - A Low-IL add-on process holding team secrets would contradict ADR-012, because such a process can read files and reach the network.
- **Decision: a data-only add-on kind; the engine is app code; the share is untrusted.**
  - **The add-on:**
    - `addon.json` gains `library`: a `library.json` in the package with `libraryId`, `name`, `path` and `ownerFingerprint`, parsed strictly by `LibraryProfile.Parse`.
    - A library add-on has no entry, theme, browser, commands or permissions.
    - It holds no secret, so the owner hands the same package to the whole team; installing it is how someone joins.
  - **Where the code lives:** none of it runs in an add-on process.
    - `Library/LibraryClient.cs` does all cryptography, verification, merging and writing.
    - `Library/SharedLibraries.cs` holds one client per enabled, hash-verified library add-on and materialises the shared items.
    - `Views/SharedLibraryView.cs` and `Views/SharedLibraryUi.cs` are the UI.
  - **Crypto** (.NET 8 built-ins only):
    - **Member keys:** each member has a P-256 ECDH key (to receive keys) and a P-256 ECDSA key (to sign). Both are generated on their PC and kept in `library-<id>.dcx` in their own encrypted store.
    - **Fingerprint:** SHA-256 over both public keys, shown as 8 groups of 4 hex characters and checked out of band before approval.
    - **Collection keys:** each collection has a random 256-bit key per **epoch**. It is wrapped per reader with ECDH-ES → HKDF-SHA256 (context-bound) → AES-256-GCM, the HPKE base-mode construction.
    - **Items:** each item revision is one file, encrypted with AES-256-GCM under the collection key and signed by its author. The AAD is the library, collection, item, revision and epoch.
    - **Metadata key:** wrapped per member; it seals the library name, the collection names and the member names.
    - **What the share holds:** ids, public keys, wrapped keys, ciphertext and signatures only.
  - **Trust:**
    - `library.json` holds the owner's public keys, signed by the owner. It must match the fingerprint in the add-on.
    - Every roster entry carries a grant signature by the owner or an admin, chained back to the owner. Only the owner can grant admin.
    - The roster, metadata and key files are signed by an admin and carry sequence numbers. A client never accepts a lower sequence than one it has seen, which detects roll-back.
    - An item counts only if it is signed by a current editor or admin of its collection, or by a removed member for epochs below their removal.
  - **Members:**
    - **Joining** writes a self-signed request. The name in it is sealed to the audit key, so only admins can read it. Approval checks the typed fingerprint against the request.
    - **Removal, or losing access to a collection:**
      - each affected collection moves to a new epoch;
      - the leaver's wraps are dropped from older epochs and every current item is re-encrypted;
      - on removal, the metadata key is also replaced;
      - every credential they could read goes on a **rotation checklist**, because re-keying only protects future changes.
    - A removed member's PC wipes its cached copy on its next sync.
    - Losing admin rotates the audit key.
  - **Roles:** owner > admin > member, and editor or reader per collection.
    - A reader holds the collection key, so the rule is enforced by every client rejecting items not signed by an editor.
    - The UI and docs say this.
  - **Conflicts:**
    - Each item revision is its own file (`<item>.<rev>.<author8>.dcxi`) and names its `base` revisions.
    - Heads are revisions that no other revision names as base. Two heads is a CONFLICT: both are kept and shown, and choosing one writes a revision that supersedes both.
    - Deletes are signed tombstones.
    - A sync tool's conflict copies are just more revisions and are handled the same way.
    - There are no locks and no shared manifest, so concurrent writers never contend for one file.
  - **Roll-back and vanishing:**
    - If an item's revision is lower than one already seen, the cached newer one is kept with a warning.
    - An item that disappears without a signed delete is kept and flagged until an admin accepts the removal.
  - **Offline:**
    - The cached copy, sealed in the member's store, stays usable.
    - Edits are signed, sealed into an outbox and sent on the next sync.
    - Audit lines are sealed with the cached audit key and queued.
    - A UNC share goes through the Offline gate (`LibraryClient.Gate`).
  - **When it syncs:**
    - Local folders are watched with a `FileSystemWatcher`.
    - Shares sync at start, on Sync, after admin changes, and on an optional per-library interval, which is off by default (no silent polling).
  - **Audit:**
    - Each member appends lines to `audit/<member>-yyyy-MM.dcxa`. Each line is sealed to the audit public key (only admins hold the private key), signed, sequence-numbered and hash-chained.
    - Admins read and verify them (Shared audit…).
    - Every action is also written to the local encrypted audit log, including:
      - each use of a shared credential to connect (at most once per 10 minutes per credential);
      - every copy of a shared secret into a local vault or to an add-on.
  - **Shared items sit beside local items, never over them:**
    - **Storage:** shared hosts and credentials are separate objects with ids `lib_<library>_<item>` and a `SharedFrom` label that is never saved. They are never in `data.dcx`.
    - **Lookups:** `AppData.FindHost` / `FindCredential` fall back to them, so connecting, jump hosts and workspace restore work.
    - **Display:** they are marked **⇄**:
      - in Hosts: a Shared node per library and collection, and under All hosts;
      - in Credentials: a Source column;
      - in every credential picker.
    - **Changes:** editing or deleting a shared item goes to the library (editors only). Folder, favourite and environment changes are refused for shared rows.
    - **Copy to my hosts / vault** makes new local items.
    - **Publish to a shared library** previews exactly what goes, then copies local items (and the credentials they use) into a collection. The local ones are left unchanged.
    - A local host may use a shared credential, so a rotated team password reaches it.
  - **Program add-ons:**
    - The new permission `library.read` adds shared hosts to `hosts.list`, flagged `shared` and never with secrets.
    - `credentials.request` offers shared credentials labelled with their library, and the hand-over is audited in the library too.
    - No write permission was added.
- **Alternatives considered:**
  - Git repository or a small sync server: rejected for v1 by DarkCipher0x's choice (a folder needs nothing to run). The file layout would work in a git repository unchanged.
  - Engine inside the add-on (a program add-on with a sync loop): rejected; see ADR-012. Decrypted team secrets must not cross the bridge.
  - One encrypted file for the whole library: rejected; every edit would conflict with every other.
  - A shared per-collection manifest for roll-back detection: rejected; it would be a write hotspot for 50 people. Per-item revisions plus roster/meta sequence numbers detect roll-back without one.
  - A symmetric audit key held by every member: rejected; every member could read everyone's audit. Lines are sealed to an admin-only key instead.
- **Consequences:**
  - Removing a member re-encrypts the affected collections at once. That is fine at the expected size (hundreds of items), but the share must be reachable.
  - **Known limits:**
    - A rogue admin can do anything an admin can.
    - Anyone with write access to the share can delete files; this is detected and flagged, not prevented.
    - Reads are audited by each member's own app, so they are self-reported.
    - A first-time joiner can't detect a roll-back older than their first sync.
  - Losing the owner's store loses the owner key, and the screen tells the owner to back up the store. Handing ownership to another admin is not implemented yet.
  - **Self-test:** `SelfTestLibrary.cs` (`LibraryTests`, 14 checks) runs an owner, an editor, a reader and a never-approved requester against a scratch folder, and renders the screens.
  - No real share or second PC was used in this session.
- **Addendum (0.1.16, same day): finishing the shared library.**
  - **Ownership hand-over:**
    - The owner writes an offer (`transfer-offer.json`), signed with the owner's key, naming an admin. Nothing changes until that admin presses Accept ownership on their PC.
    - Accepting appends the signed offer to `library.json` (`LibraryHeader.Transfers`).
    - The new owner then re-signs:
      - their own entry, as owner;
      - the previous owner's entry, as admin;
      - every grant the previous owner made.
    - Clients walk the chain link by link from the first owner, and the add-on's fingerprint may be any owner in it. A forged or truncated chain is refused and the cached header is kept.
  - **Key backup / restore:** `.dcxkey` files are sealed with PBKDF2-SHA256 (600,000 iterations) → AES-256-GCM. Passwords need at least 12 characters; a wrong password is refused. Restore needs the library's add-on to be installed, and never overwrites other member keys that are already on the PC.
  - **Demotion and removal keep the library verifiable:**
    - When an admin loses admin, their grants and the key files they signed are re-signed by the owner.
    - When someone loses edit rights but keeps read access, their revisions are re-issued by the admin making the change.
    - Before this fix, the self-test found that demoting an admin made a collection's keys unreadable.
  - **Add-ons screen:** describes library add-ons correctly (no longer as a theme), with a SHARED LIBRARY section showing the join state, role and sync time.
  - **Self-test:** 15 library checks (the new one covers hand-over, forged offers and header links, re-signed grants and key backups).

- **Addendum (0.1.17): the general Shared library add-on.**
  - **Why:** DarkCipher0x expected an installable add-on in `dist\addons`, like the theme and the portal browser.
  - **The add-on:** `com.darkcipher.shared-library` (`samples/addons/shared-library`) has a `library.json` with only a `name`. `LibraryProfile.IsGeneral` names no library: it switches the Shared library screen on.
  - **The sidebar item:** like Portals, the item appears only while a library add-on is enabled, whether the general one or a team's (`SharedLibraries.Enabled`, `FeatureChanged` → `BuildNav`).
  - **What it can't do:** it can't be joined. Each team library still has its own generated add-on, which carries the library id and the owner's fingerprint.

### ADR-034 AI speed, agents, the AI Engineer in its terminal, more tools, and the memory guard (2026-10-19, 0.1.17)
- **Context:** DarkCipher0x: "i need the AI sped up a bit and the integrations a bit better. the AI engineer should be able to directly command session windows too and should support agentic models", and later the same evening "terminal typing is laggy and slow too".
  - The same evening his app froze in the AI operator. Ollama's gpt-oss:20b at a 64K context held 12 GB of the 16 GB RX 9070 XT and Foundry Local held 6.7 GB. GPU memory overflowed by 7.6 GB into system RAM, and the commit charge reached 60 of 63 GB.
  - Before this change:
    - every answer arrived whole (no streaming);
    - the AI Engineer had no tools at all;
    - the operator ran tools one at a time in a fixed 10-round loop;
    - the system prompt changed every question, because it carried what the user was looking at, so local engines couldn't reuse the processed prompt;
    - the operator's tool lines used a blocking `Dispatcher.Invoke`.
- **Decision:**
  - **Speed (`Ai/AiClient.cs`):**
    - **Streaming on every engine:**
      - `ChatAsync(…, onDelta)` streams Ollama `/api/chat` (NDJSON), OpenAI-compatible servers and Foundry Local (`/v1/chat/completions`, SSE, with `delta.tool_calls` fragments assembled by index), and the Anthropic Messages API (SSE, `text_delta` + `input_json_delta`).
      - A server that ignores `stream` and answers with one JSON document is still read.
      - An OpenAI-compatible server that refuses streaming (400/422/501, not about tools) is asked again without it.
      - Pieces are read with `ConfigureAwait(false)`. The views coalesce them: one pending `BeginInvoke` at Background priority, never one Invoke per token.
    - **Timing:** each call records `AiTiming` (time to the first text, total, streamed?). The AI Engineer step log and the operator's status line show it.
    - **Connections:** one pooled `SocketsHttpHandler` (10-minute idle timeout, 30-minute lifetime) for every engine.
    - **Warm-up:** the first character typed in either AI input loads the model. Ollama gets an empty `/api/generate` with the keep-alive; Foundry Local gets its model id resolved. This happens at most once a minute, never under memory pressure, and never to an internet provider.
    - **Keep warm only while in use:** Ollama `keep_alive` is the "keep the model warm" minutes (default 10) after the last use, and `0` under pressure.
    - **Prompt prefix reuse:** the instructions and the tool list are identical from question to question. The tool list is built once while the registry is unchanged. What changes (time, what the user is looking at) goes with the question, so llama.cpp-based engines reuse the processed prefix.
  - **Agents (`Ai/AiAgent.cs`):** one loop for both AI surfaces.
    - **The loop:** plan, call tools, observe, continue, up to `AiMaxSteps` (default 12). At the limit, one last call without tools asks for a summary of what was found.
    - **Stop and limits:** ■ Stop cancels at any point. In the AI Engineer it also sends Ctrl+C to a command the agent is running in the target. The same call is refused after 3 repeats.
    - **Parallel tools:** consecutive calls that only read and won't ask (`Assistant.ParallelSafe`: auto-approve on, not terminal tools, not this PC's files) run at the same time. Results go back in the original order with their ids.
    - **Step log:** a visible log of `AgentStep` entries: plan, tool, result (with ms), answer, notes.
    - **Tool support:** Ollama says whether a model can call tools (`/api/show` `capabilities`). Other engines are assumed to until a call is refused (`ToolsRefused`). Models without it get the **text tool protocol**: the tools are described in a second system message, `<tool_call>{json}</tool_call>` blocks are parsed, and the results go back as `<tool_result>` user turns. The protocol text is never streamed to the user.
    - **Per-chat model:** `AiClient.UseModel` is an AsyncLocal override within the current engine. AI Engineer conversations and the operator have a model picker; Settings are unchanged.
  - **The AI Engineer as an agent (`AiWorkspace`, `Views/AiPanel.cs`):**
    - **Agent mode** is per conversation and on by default.
    - **Tools:** the operator's full set (built-ins + registry), run through an `Assistant` instance whose approvals are the panel's: `Envs.Confirm` with the host's environment banner, and the name typed for PRODUCTION.
    - **Answers** stream into a live bubble, and a STEPS expander shows the log.
    - **Canned answers:** the self-test's canned `Chat` seam still gives the old plain question and answer.
  - **Direct control of the chosen session (`TerminalBridge`, `AiTools`):**
    - **Auto-run read-only commands in the target session** (`AiAutoRunReadOnlyTarget`, off by default, in TARGET TERMINAL and Settings, audited when changed). `TerminalBridge.WhyNotAuto` allows a command without a review only when all of these hold:
      - `CommandRisk` rates it read-only;
      - it isn't interactive;
      - it isn't endless (new `RiskAssessment.Endless`: follow / watch / ping without a count / long sleeps);
      - the target isn't PRODUCTION.
    - Change, sensitive and destructive commands keep the ADR-028 review and typed confirmation. Every auto-run is audited as such.
    - **CommandRisk now also catches** sed's `w` command and `-i`, and awk `system()`.
    - **New tools, each limited to the target the user chose:**
      - `terminal_read_screen` (xterm's visible rows, else the recent output; masked, bounded, fenced; asks unless auto-run is on);
      - `terminal_send_keys` (`{ENTER}`, `{CTRL+C}`… parsed strictly; always reviewed; a line ended with Enter is judged like a command);
      - `terminal_interrupt` (the agent's own command at once, anything else after a yes);
      - `terminal_switch_target` and `terminal_open_session` (only the user's yes changes the target; audited as theirs).
    - All ADR-028 safeguards stay: target bound by session id and connect count, risk rating, typed confirmation, output masked and bounded.
  - **More of the app as tools (`AiTools.RegisterIntegrations`):**
    - `infra_actions`: the actions possible on an object now, including the 0.1.14 coverage, with why not and the inputs they need.
    - `cloud_action`: any catalog action of a cloud screen, filled like the form, with its `When` states checked. Not offered: deletes, Special actions (console, SSH), type-to-confirm actions, and actions with password fields.
    - Notes and incidents: `append_to_note` (history kept), `create_incident` and `add_to_incident`.
    - `transfer_status`.
    - Portal browser add-on: `portal_list` and `portal_open` (never cookies, pages or sign-ins).
    - Shared library add-on: `library_list` (hosts only, never credentials).
    - Every change asks through the surface's approval and is audited. Data comes back fenced as untrusted and masked.
  - **Memory guard (`Ai/AiResourceGuard.cs`):**
    - **What it reads:** the commit charge (`GlobalMemoryStatusEx`), and for Ollama how much of the app's model is outside GPU memory (`/api/ps`, size − size_vram).
    - **High** (commit ≥ 85 %, or more than 1 GB spilled): 8K context and `keep_alive 0`.
    - **Critical** (≥ 93 %): 4K context.
    - **Foundry Local unload:** a minute timer (not in the self-test) unloads Foundry Local's loaded models (`foundry model unload`) in either case:
      - memory is tight and Foundry isn't the engine in use, or the user isn't active;
      - Foundry is the engine but the app's own use has been idle past the keep-warm time.
    - **Never** stops or kills an Ollama or Foundry process, and never unloads another Ollama model. Actions are logged and audited, and shown in Settings → AI speed, agents & memory.
  - **Terminal typing latency (`Views/TerminalView.cs`):**
    - **Echo:** small output (≤ 4 KB pending, one request at a time) is flushed to the page at once at Normal priority. Before, it waited for the 16 ms Background-priority timer, which other UI work could delay. Floods stay on the timer.
    - **Writes:** keystrokes are written by one ordered background writer per view, off the UI thread. An SSH write + flush, or a ConPTY pipe the shell isn't draining, could stall the window on every key. A write for a replaced transport is dropped, never typed into a new session.
- **Measured** (self-test, fake engine: 150 ms before the first token, then 15 ms per token, 40-token answers; see `ai-timings.txt` next to the report): time to the first text 692 ms → 151 ms (whole answers vs streaming; the whole answer takes about the same, 692 → 711 ms); three independent 300 ms read-only tools 1,352 ms → 743 ms; 0 new connections for 6 questions (the pooled connection is reused). Terminal echo with background UI work (local PowerShell): 469 ms → 13 ms.
- **Tests:** `SelfTestAgent.cs` (`AgentTests`, `FakeAi` = a loopback Ollama/OpenAI/Anthropic/Foundry engine with keep-alive, chunked NDJSON/SSE and scripted tool calls) covers:
  - streaming and fragment assembly on all four engines;
  - before/after timings and connection reuse;
  - the agent loop with parallel tools, the step limit, the repeat guard and cancel;
  - both tool-calling fallbacks;
  - the per-chat model;
  - the memory guard with a fake Foundry CLI;
  - the AI Engineer agent end to end through the panel;
  - auto-run rules, screen, keys, interrupt, switch and open in a real local PowerShell session;
  - the new tools;
  - echo latency with and without the quick flush.
  `SelfTestRelease` checks `cloud_action` against the Hetzner fake.
- **Not done / untested:**
  - Nothing has run against a real Ollama, Foundry Local, LM Studio or the Claude API with tools; the formats follow their documentation.
  - The GPU spill reading comes from Ollama only. VRAM held by other programs is seen only through the commit charge.
### ADR-035 Encrypted add-on bridge, a sealed sandbox process, and data-only add-ons encrypted at rest (2026-10-19, 0.1.17)
- **Context:** DarkCipher0x asked to "review the addons to make sure that all communication between the application and the add-on is encrypted", then chose to harden the add-on process AND encrypt the bridge, with an SDK helper for PowerShell and plain-text add-ons refused. He also asked that both of his add-ons be updated.
  - **The review found:**
    - API 1 sent JSON lines in plain text over anonymous pipes. That included a credential handed over by `credentials.request`.
    - Pipe handles were created inheritable, and CreateProcessAsUser inherited every inheritable handle of the app, not just stdio.
    - The add-on's Low-IL process had the default DACL, so another Low-IL process of the same user (another add-on) could open it, read its memory and duplicate its pipe handles.
    - Data-only add-ons (themes, the portal browser, shared libraries) have no bridge at all, but their installed files were plain text on disk.
- **Decision:**
  1. **Encrypted bridge, API 2** (`Addons/AddonBridge.cs`; SDK `samples/addons/sdk/DcxBridge.cs`, `DcxBridge.dll`, `DcxAddon.ps1`):
     - **Handshake:** the app and the add-on exchange ephemeral P-256 keys in a plain-text hello; `ikm = SHA-256(Z)`; HKDF-SHA256 (32-byte random salt, info bound to both keys) gives one AES-256-GCM key per direction.
     - **Frames:** each line is base64(ciphertext‖tag). The nonce is implicit (direction ‖ 64-bit counter), so a frame that is changed, dropped, replayed or reordered, or a plain-text line, fails to decrypt and ends the run.
     - **SDK:** the DLL targets .NET Framework 4.x so Windows PowerShell 5.1 can use it. GCM goes through CNG (`bcrypt.dll`) and ECDH through `ECDiffieHellmanCng` (Hash KDF = SHA-256(Z), the same value as .NET 8's `DeriveKeyFromHash`). The same source also builds for .NET 6+.
     - **Interop:** tested between .NET 8 and a real Windows PowerShell 5.1 at Low IL.
  2. **API 1 program add-ons are refused** at inspection and blocked if already installed. Data-only add-ons may declare API 1 or 2. *(Superseded in 0.1.18 by ADR-036: every package must declare API 2 to install; API 1 data-only add-ons already installed keep working.)*
  3. **Sandbox hardening** (`SandboxProcess`):
     - `STARTUPINFOEX` with `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` limits inheritance to the three pipe ends.
     - The process and thread security descriptors are `D:P(A;;GA;;;SY)(A;;0x101400;;;WD)(A;;0x101400;;;OW)` (thread: `0x100840`). Only SYSTEM has full access; everyone else, including the owner via OWNER RIGHTS, gets SYNCHRONIZE + query information.
     - .NET inside the add-on needs that much on itself: with query-limited only it failed at start with 0x80131623. So nothing but SYSTEM can read or write the add-on's memory, duplicate its handles or create threads in it.
     - The app keeps full access through the handles CreateProcess returns.
  4. **Data-only add-ons are sealed at rest:**
    - Installed files are AES-256-GCM under the store key, with `addon|id|version|path` as AAD.
    - `AddonPackages.ReadInstalled` decrypts and checks the SHA-256 approved at install; themes, the portal browser and shared libraries read only through it.
    - `SealExisting()` encrypts earlier installs in place at start-up (two-phase, only when every file still verifies).
    - Program add-ons stay readable on disk (Windows must load them) and are hash-checked before every run.
  5. **Packages:**
    - hello-hosts 1.1.0 (API 2, with the SDK files); portal browser 1.0.1, Shared library 1.0.1 and the sample theme (API 2 manifests, sealed when installed).
    - Team library add-ons are written as API 2.
    - `samples\addons\pack.ps1` also writes `dist\addons\<id>.zip` (DarkCipher0x: a zipped copy of every package).
- **Alternatives considered:**
  - Keeping plain text and relying on the pipe being local: rejected at DarkCipher0x's request. The handle-list and DACL fixes are what stop other processes; encryption additionally keeps every message unreadable to anything that obtains a pipe handle.
  - Authenticating the handshake with a secret passed on the command line or in the environment: rejected. Both are readable by anything that could attack the pipe, so it would add nothing over ephemeral ECDH once the process can't be opened.
  - AppContainer per add-on: still the stronger isolation (ADR-012). It needs per-folder ACLs and breaks many PowerShell scripts; it stays a candidate.
  - Compiling the SDK with `Add-Type -TypeDefinition` on every run: rejected. It took about 1.5 s per run against the 5 s test timeout, and would write compiled code to the Low-writable data folder. A hash-verified DLL in the package is used instead.
- **Consequences:**
  - **Existing add-ons:** every program add-on written for API 1 must be rebuilt with the SDK. The shipped add-ons are all data-only (the sample theme, the portal browser, the shared library) and keep working; installed copies are sealed at the next start.
  - **Still possible:** SYSTEM, an elevated debugger (SeDebugPrivilege), or the add-on itself can still see what the app gives that add-on. Permissions and the per-request credential prompt remain the control for what an add-on receives.
  - **Self-test:**
    - the probe add-on runs over the encrypted bridge;
    - the running add-on can't be opened for VM_READ / VM_WRITE / DUP_HANDLE;
    - a plain-text line mid-run ends it with a protocol error;
    - an add-on that skips the handshake gets nothing;
    - API 1 program add-ons are refused, and blocked when already installed;
    - data-only add-ons are sealed, and tampering is still detected.

### ADR-036 Add-on full-control API, API 2 for every add-on, and a readable AI terminal wrapper (2026-10-19, 0.1.18)
- **Context:**
  - DarkCipher0x: "ensure that API for addons can control every aspect of the software if needed … I want to be able to pipe into everything", "all communications must be encrypted", and "I don't ever want to have the incident where I need to create an urgent addon and the functionality isn't there".
  - Before this, an add-on could call 11 fixed bridge methods (hosts, incidents, notes, terminal screen, a file, a credential). Everything else in the app was out of reach.
  - Earlier the same day (s40) the AI Engineer's PowerShell wrapper echoed as an unreadable Base64 line, and reading cmdlets were labelled "CHANGES the system".
- **Decision:**
  1. **One capability surface (`Addons/AddonApi.cs`).**
     - `app.capabilities` lists every bridge method and every app capability, each with its risk, the permission it needs and whether the add-on has it.
     - `app.call {tool, args}` runs any of them through the same code path as the AI (since 0.2.0 phase 2 `Capabilities.RunAsync` with the add-on as actor; before that `Assistant.RunToolForAsync`). That covers the operator's built-in tools (SSH commands, services, logs, files and transfers, cloud instances) and every registry tool (infrastructure, cloud screens, terminal target, notes, incidents, transfers, portals, libraries).
     - A feature that registers a `Capability` (was `AiTool`) is therefore reachable by the AI and by add-ons with no further change.
     - Add-on-only abilities are `AddonMethod`s registered with `AddonApi.Register`. Built in so far: `hosts.add`, `hosts.update`, `hosts.remove`, `ui.open` and `ui.openHost`.
  2. **Permissions by risk.**
     - `app.read`: reads.
     - `app.private`: notes, incidents and documents; the handlers ask each time.
     - `app.control` (HIGH): any change. The user approves the exact action through `AddonApi.Approver` (the environment banner; PRODUCTION needs the name typed), with the add-on named in the dialog.
     - `hosts.write`: each host change shows the exact fields before anything changes. The validation is a dry run first.
     - `ui.control`: opening screens and host views.
     - Every `app.call` is audited as `addon:<id>`. A decline returns -32003.
  3. **Encrypted only.**
     - None of this exists outside the API 2 bridge (ECDH P-256 + AES-256-GCM, ADR-035).
     - Every package must now declare `"api": 2` to install, data-only ones too.
     - API 1 data-only add-ons that are already installed keep working: they have no channel to the app and are sealed at rest.
     - The child's `DCX_ADDON_API` is now `2`.
  4. **SDK and sample.**
     - `DcxAddon.ps1` gains `Get-DcxCapabilities` and `Invoke-DcxTool`.
     - hello-hosts 1.2.0 adds "What can add-ons call?".
  5. **AI terminal bridge (s40).**
     - PowerShell targets get a once-per-session `__dcx` helper, so each command shows as `. __dcx <id> '<command>'`. The call is dot-sourced, so variables stay. A missing helper is typed again.
     - `CommandRisk` judges `powershell|pwsh -Command "…"` by the inner command, lists reading cmdlets such as `Get-WmiObject` and `Get-Member` as read-only, and treats method calls on objects as changes.
- **Alternatives considered:**
  - **A generic reflection bridge into app objects:** rejected. It is unbounded, can't be permissioned or audited, and would break on every refactor.
  - **The local HTTP API for add-ons:** rejected. It is loopback HTTP with tokens, not the encrypted per-run bridge, and it covers less.
  - **Auto-approved changes for trusted add-ons:** rejected. Changes keep the same per-action approval the AI gets.
  - **Settings changes:** left out for now. Security settings stay the user's; a settings capability can be registered later.
- **Consequences:**
  - An urgent add-on can do anything the app can, within its granted permissions and the user's approvals.
  - New features should expose their actions as capabilities (`Capabilities.Register`; or `AddonMethod`s) so that both the AI and add-ons can reach them.
  - **Self-test:** the probe add-on calls `app.capabilities` (more than 30 tools listed); `list_hosts` runs with `app.read`; `run_command` gets -32001 without `app.control` and reaches the approval with it (declined, -32003); `hosts.add`, `hosts.update` and `hosts.remove` are each approved and validated; unknown screens are refused; calls are audited; an API 1 theme is refused at install.

### ADR-037 0.2.0: ten projects, AI as a module that can be switched off and locked, Azure and Google Cloud (2026-10-20, 0.2.0)
- **Context:**
  - DarkCipher0x (2026-10-19): split the app into organised projects, make the AI features one switch that leaves nothing AI
    behind when off, and protect that switch with a password. Plan: `docs/history/PLAN-0.2.0.md` (four passes, the audit in
    `docs/history/AUDIT-0.2.0.md`).
  - Before 0.2.0 the app was one project of about 54,000 lines; nothing but review kept the layers apart, the AI was
    reached from 28 places in the screens, and the biggest files ran to 1,000–2,700 lines.
  - Added during the work (2026-10-20): Microsoft Azure and Google Cloud, read-only until allowed. Windows management
    over WinRM / CIM, a MaskedMail module and mail utilities were planned and then left out (later releases or add-ons).
- **Decision:**
  1. **Seams first, split last.** Phase 1: lower code reaches the UI only through `Services\Ui.cs` (`Ui.Dialogs`,
     `Ui.Window`, `Ui.Thread`, `Log`). Phase 2: everything the app can do on request is a `Capability`, run through one
     registry by the AI, the operator and add-ons. Phase 3: screens show what registered modules contribute
     (`IAppModule` / `IAppHost`: sidebar, Settings, menus, error box, dock, shortcuts, hooks, dashboard, privacy, help).
     Phase 4: the AI became one such module behind `IAssistant` / `IReportAnalyst`. Phase 5: the switch and the lock.
     Phase 6: the projects, each step a prep commit (references cut in place, a throwaway probe project compiling the
     candidates) and a byte-identical move commit. Phase 6½: the five biggest files split into partial classes.
  2. **Projects** (references down only): `Dcx.Core` (no WPF, nothing of ours), `Dcx.Connectivity`, `Dcx.Ui`,
     `Dcx.Infra`, `Dcx.Cloud`, `Dcx.Addons`, `Dcx.Library`, `Dcx.Browser`, `Dcx.Ai`, and the app. Namespaces stay
     `DcxManager.*`; the exe name, data folders, store format, UpgradeCode and add-on API are unchanged. Lower projects
     expose hooks (`Func` / `Action` / interfaces) and `App.WireLayers()` connects them. Deviations from the first plan,
     with reasons in the PLAN: the self-test and the theme XAML stay in the app; the app folder stays `DcxManager`.
  3. **AI off means not loaded.** `AiFeatures.DecideAtStartup()` (machine policy → `DCX_AI_FEATURES` unless locked →
     the setting; new installs off, upgrades keep it on only where an engine was set); only then does the never-inlined
     `App.LoadAiModule()` touch `Dcx.Ai`. With AI off the assembly is never loaded (start-up 0.74 s / 95 MB against
     ~0.76 s / 108 MB before).
  4. **The lock** (`Core.FeatureLock`, `ai-lock.dcx`): one password (≥12 characters, PBKDF2-SHA256 verifier only,
     throttled and audited) gates AI on/off and the internet AI providers. Settings that arrive from elsewhere keep what
     it guards: a snapshot restore and a backup's "replace everything" keep the current values; a damaged settings file
     recovered from a snapshot at start-up comes back with both switched off (fail closed). A source scan fails when a
     new writer of `settings.dcx` doesn't go through one of these guards.
  5. **Azure and Google Cloud** in `Dcx.Cloud`, on the existing cloud layer: Azure by an Entra app registration with a
     certificate (no client secrets), Google by a service-account key (the read-only scope while read-only); both
     read-only until changes are allowed per account; no secrets ever read; hosts allow-listed; fakes in the self-test.
     The engine gained segment-escaped resource paths (`{path:field}`), nested form bodies and PUT creates.
  6. **Guards and budgets (phase 7):** the projects check on the built assemblies, the AI-off assembly check, the
     layering / assistant / AI-boundary / settings-writer source scans, the .NET security analyzers as errors (the
     taint-tracking set stays off: it took the compiler server to 10 GB), NuGet audit with locked restore,
     `tools\budgets.json` checked by `build.ps1` (start-up time and memory of both runs, exe and MSI size), a CycloneDX
     SBOM per release (`tools\write-sbom.ps1`, from the lock files), and the published single-file exe self-tested
     before packaging.
- **Alternatives considered:**
  - **Split first:** rejected. Without the seams the split would have dragged every UI call across project lines; with
    them it was mostly mechanical.
  - **A separate self-test assembly:** rejected. It drives the main window and every screen, so it would have to sit
    above the exe.
  - **AI off as a runtime flag only (everything loaded, hidden):** rejected. "Nothing AI when off" is only provable when
    the code isn't there.
  - **A "no-AI" installer without `Dcx.Ai.dll`:** possible now, not built (one installer, DarkCipher0x's decision).
  - **Microsoft / Google SDKs for the clouds:** rejected (size, transitive packages, and the app's own hardened HTTP
    already covers retries, offline mode and auditing).
- **Consequences:**
  - The compiler now enforces most layering rules; the self-test checks the rest.
  - A feature module can be added the way the AI was (an `IAppModule` in its own project, loaded on a switch).
  - Known leftovers (PLAN §6): `AppSettings` stays one flat record with the AI fields; `MainWindow.Instance` and
    `AppSettings.Current` stay static; namespaces don't name projects.
  - **Self-test:** AI on and AI off suites in every build, plus the published exe; 331 checks with AI on at the
    Azure / Google step.

### ADR-038 The house theme (shared across the DarkCipher Systems apps) and grouped right-click menus (2026-10-20, 0.2.1)
- **Context:**
  - DarkCipher0x (2026-10-20): the Settings layout, fonts, icons and colours of his other DarkCipher Systems app are the look he
    wants for every DarkCipher Systems app (the house theme); ServerManagement's Settings above all ("much cleaner and
    easier to nav"). Theme add-ons must keep working.
  - Also: the right-click menus had grown to 30–40 rows (terminal, hosts, tabs) and needed expandable groups.
- **Decision:**
  1. **Palette:** the existing token names stay; added `Surface2`, `FgStrong`, `LineStrong`, `Info`, `Experimental`,
     `Critical`, `Unknown` plus derived `AccentDim`, `HeaderMuted`, `FgDisabled`, `AltRow`, `DangerLine`. Presets
     DarkCipher / Graphite / Teal / High Contrast (dark) and Light; accent swatches or a custom hex that must reach 3:1
     against the background; status intensity; Windows high contrast followed when asked. A theme add-on overrides the
     palette keys it names; extra keys it doesn't name are derived from its own colours.
  2. **Shape and type:** `Radius` / `RadiusCard` tokens (sharp / 2 px / rounded), Segoe UI Variable Text and Cascadia
     Mono, 9 px scrollbars, headings in `FgStrong`, Segoe Fluent Icons in the sidebar through `Icons.For(glyph)` (the
     old glyphs stay the keys, so add-ons and modules need no change). Motion: follow Windows / standard / reduced.
  3. **Settings:** search at the top, a tab strip (one tab per category), one column of cards up to 900 px, each card
     titled with an "Applies to" caption and its own Reset. `Restyle` turns every plain CheckBox into a switch, makes
     drop-downs 340 px and turns tooltips into captions, so modules and add-ons get the look without changes. New tabs:
     Appearance, Navigation (sidebar style expanded / compact / icons, width, landing page, up to 6 pinned pages, shown
     sections and, since 0.2.2, shown pages: `HiddenNavItems`, Settings never hidden), Import & export (`AppearanceTransfer`, format `dcx-appearance` v1: appearance and navigation only, never
     secrets, at most 64 KB, validated and previewed before it applies) and About.
  4. **Menus:** `Menus.Group(name, items)` declares a named group. `Menus.Fill` shows groups inline between separators
     when the whole menu is 12 rows or fewer and folds every group of two or more items into a submenu otherwise. The
     everyday actions for the thing under the mouse stay at the top. `Menus.ForList` puts a list's own row actions first
     and the mirrored toolbar under "Screen actions" and "Copy & export".
- **Alternatives considered:**
  - **Renaming tokens to the other app's names:** rejected; it would break every theme add-on.
  - **Expanders for menu groups:** rejected; WPF context menus already have submenus, which keyboards and screen
    readers know.
  - **Always folding groups:** rejected; short menus read better flat.
- **Addendum (0.2.2):** dark-theme text dialled back at DarkCipher0x's request (near-white on near-black glared): `Fg`
  #BCD8D5 and `FgStrong` #D6EEEB on DarkCipher (≈13:1, was ≈17:1), Graphite and Teal alike; High Contrast stays white.
  Rule for the house theme: body text at least 7:1 and no brighter than about 13:1 on the dark themes.
- **Consequences:**
  - New apps start from the Design System artifact's README and keep these token names.
  - **Self-test:** `SelfTestHouseTheme` checks presets and contrast, the Settings layout (tabs, 340 px drop-downs,
    switches, captions, resets), import / export, the sidebar styles, and every menu the app builds (none over 12 rows at
    the top level); a guard fails the run if the self-test opens any popup menu or tooltip on the screen.

### ADR-039 Admin-protected security settings (UAC) and Windows Hello (2026-10-20, 0.3.0-preview.1)
- **Context:**
  - DarkCipher0x (2026-10-20): every security setting (AI, encryption, auditing…) behind an administrator (UAC) prompt;
    Windows Hello, with its prompt always in front. He chose the admin-only store (not a prompt in front of the user's
    own settings file) and Hello for unlock, for confirming sensitive actions and in place of the AI lock password.
  - Before: these settings lived in the user's encrypted settings file. Anything running as the user can decrypt it
    (DPAPI), so a prompt in front of that file would be theatre.
- **Decision:**
  1. **The store:** `HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Protected`, one REG_SZ JSON record per Windows
     user (by SID): the values and the last 20 changes. Created with an explicit ACL (Administrators and SYSTEM full,
     Users read, inheritance off). The app checks the ACL on every read (`ProtectedSettings.AclProblem`): if a
     non-administrator can write it, or it isn't owned by one, the record is ignored and Settings says why.
  2. **What:** `ProtectedSettings.Items` (15): AI features, internet AI providers, secret masking, AI auto-run; offline
     mode, its approved list and add-ons offline; the local API and its port; Telnet; lock on Windows lock, auto-lock,
     sandbox mode; Hello sign-in and Hello confirm. Secrets never go there. While a record exists its values override
     the user's file at start-up and after every change; before the first change nothing is protected (Settings →
     Security & privacy → Admin protection offers "Protect these settings now").
  3. **Changing:** `ProtectedSettings.ChangeAsync` → `AdminGate`: Windows Hello first when "confirm sensitive actions"
     is on; a stricter value (offline on, Telnet off, …: `Item.Tighten`) applies for the session at once; then a
     one-time request file (format `dcx-protected-change` v1, 16 KB max, 10-minute lifetime) and the installed exe
     started with `runas` and `--protected-change <file>`. The elevated copy (no store, no single-instance check)
     checks it is elevated and runs from Program Files, validates every key and value, shows `AdminChangeWindow`
     (what changes, for which Windows user, why; Cancel is the default) and writes the record. Exit 0 / 1 / 2.
  4. **Only the installed copy elevates:** elevating a copy in a user-writable folder would let anything running as the
     user replace it (or a DLL next to it) first. A portable or development copy says it can't protect settings, and
     refuses to change them once an administrator has.
  5. **Windows Hello:** `WindowsHello` (Dcx.Ui). Consent through `IUserConsentVerifierInterop::RequestVerificationForWindowAsync`
     with the app's window handle (vtable slot 6, as .NET 8 has no built-in WinRT COM interop), so the prompt opens in
     front; key signing has no window-aware call in the 19041 projection, so the app calls `AllowSetForegroundWindow`
     and brings the "Credential Dialog Xaml Host" forward. Sign-in: a Hello key (TPM-backed RSA-2048) signs a random
     32-byte challenge; PKCS#1 v1.5 signatures are deterministic, so HKDF-SHA256(signature, salt) gives the same key
     every time and seals the data key (`keyring-hello` slot). The Windows-account (DPAPI) copy is removed while Hello is
     on; the master password must exist and stays the fallback.
  6. **The AI features lock** can be a Hello lock (`FeatureLock` mode "hello": no verifier, Hello confirms each gated
     change). **Sensitive actions** (`SensitiveAction.Confirm`): opening a credential, exporting a private key,
     exports and backups ask Hello when "confirm" is on, the master password when Hello isn't available.
- **Alternatives considered:**
  - **UAC consent in front of the user's settings file:** rejected (anything running as the user could change the file).
  - **A Windows service holding the settings:** stronger isolation of reads too, but a service to install, update and
    secure; the registry already gives admin-only writes. Possible later.
  - **Hello consent only (no key) for sign-in:** rejected; the key must be cryptographically bound to Hello, or skipping
    the prompt would open the store.
  - **Moving every project to the 22000+ Windows SDK projection** for the managed interop classes: rejected for now
    (ten projects and lock files for one call).
- **Consequences:**
  - On an administrator account UAC is a consent click unless Windows asks for credentials; on a standard account an
    administrator's password is needed. Protected settings apply to that Windows user on this PC only.
  - Not tested by hand yet: the real UAC flow, the elevated window, Hello sign-in and the prompt in front.
  - Fixed on the way: "Set / change master password" let a cancelled current-password prompt through.
  - **Self-test:** `SelfTestProtected.cs` with a fake elevation (the elevated side's checks and write against a scratch
    store) and a fake Hello; the real HKLM record is never read during a test run.

### ADR-040 Group Policy for every setting, and an uninstaller that leaves nothing behind (2026-10-20, 0.3.0-preview.2)
- **Context:** DarkCipher0x (2026-10-20): "basically every setting" controllable by registry keys for Group Policy; uninstalling
  must clean those keys up and never leave anything behind, asking about the shared library too, with silent switches;
  "very easily manageable and thought out, don't re-engineer anything, but make sure it's organised".
- **Decision:**
  1. **One catalog, derived:** `PolicySettings.Catalog` is every `AppSettings` property with a simple type (on/off,
     whole number, decimal, text, list), minus `Excluded` (35, each with its reason: accounts, secrets, per-PC state,
     records edited in the app), grouped into 8 categories by name. A new setting becomes a policy by existing; the
     self-test fails if one is neither in the catalog nor excluded, or has no category, or looks like a secret.
  2. **Standard keys, the setting's own name:** `HKLM` / `HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement`,
     value name = property name (the existing `AiFeatures` policy already fit; `AiFeatures.ReadPolicy` now reads through
     the catalog, so there is one reader). Types checked strictly; ranges and choice lists for the settings that need
     them; invalid values ignored and listed.
  3. **One enforcement point:** `AppSettings.Enforce` (= `PolicySettings.ApplyTo`) runs after every load and before every
     save; `AppSettings.Reverted` lets the status bar say why a change didn't stay. No screen had to change; 🛡 switches
     and the admin gate refuse managed settings explicitly. Order: computer → user → admin-protected → user's own.
  4. **Templates from the catalog:** `PolicyTemplates` writes the ADMX, the en-US ADML and `POLICIES.md`;
     `--export-policy-templates <dir>` runs it and `build.ps1` puts the result in `dist\<ver>\policy` (+ a zip). The
     self-test checks every name the ADMX uses has ADML text, and that `docs/POLICIES.md` is current.
  5. **Uninstall:** the old `--cleanup` logic moved into `Services\Uninstall.cs` unchanged in behaviour and extended:
     always the app's temp files and HKLM key; by choice the store (+ its Windows Hello key, read from the plain-JSON
     keyring), the shared library membership (`library-*.dcx`), the cache, settings enforced on the PC (protected
     records, policy keys for the computer and signed-in users) and every user profile. Two MSI custom actions: as the
     user (`--cleanup`) and as SYSTEM (`--cleanup-machine`), on every full uninstall, never on an upgrade. Properties
     `REMOVE_DATA / LIBRARY / CACHE / SETTINGS / ALLUSERS` and `REMOVE_EVERYTHING`, in the dialog and silent.
- **Alternatives considered:**
  - **Attributes on every setting** for policy metadata: rejected (re-engineering 139 properties for what names and a
    small table already give).
  - **Policies only for "important" settings:** rejected; DarkCipher0x asked for nearly all, and a derived catalog costs nothing
    per setting.
  - **Deleting other users' Windows Hello keys:** not possible outside their sessions; documented.
- **Consequences:** settings changed by policy are visible in Settings and in the audit-free status bar; a domain GPO
  re-applies after uninstall (documented). Not tried yet: the ADMX in the Group Policy editor, a real uninstall with
  the new switches.

### ADR-041 Dcx.Notify: syslog over TLS and e-mail notifications (2026-10-20, 0.3.0)
- **Context:** the audit trail and warnings must reach a company's collector and inbox; nothing may travel in clear.
- **Decision:** one small project (`Dcx.Notify`, references Core only): RFC 5424 messages over RFC 5425 (TLS only, pin
  or Windows trust, optional client certificate) with an encrypted spool; SMTP with implicit TLS or STARTTLS only, the
  password in the encrypted settings; a `Notifier` with per-kind switches and a per-hour cap. Server, recipients and
  the switches are admin-protected (ADR-039) so nothing running as the user can redirect or silence the trail.
- **Consequences:** every later feature reports through `Notifier.Report` (LAN monitors, dashboard checks); no UDP
  syslog exists at all, by design.

### ADR-042 LAN tools and monitoring (2026-10-20, 0.3.0)
- **Decision:** `Net/` in Connectivity holds UI-free scanners (ping sweep, TCP port scanner with service detection,
  Nmap runner, a client-only connection tool, DNS records through dnsapi); the LAN screen is one tab per file. Private
  ranges only, a single public address only when typed and confirmed. Monitoring is explicit: monitors the user created,
  with their interval shown, each run compared with the last and the differences audited and notified. The last SSH
  scan is kept in the encrypted store.
- **Consequences:** nothing scans or polls by itself; the same scanner code serves the toolbox and the dashboard checks.

### ADR-043 Selective export / import, the SSH key tool and quick connect (2026-10-20, 0.3.0)
- **Decision:** `BackupCustom.Narrow` is the one place that decides what travels (hosts pull their jump hosts; an
  unticked credential is stripped from everything that referenced it; working data such as toolbox history and LAN
  results never travels); keys are generated in-process, exported with a passphrase through ssh-keygen and the app's
  own askpass helper over a one-shot pipe; quick connect lines carry their protocol and are pinned in the store.

### ADR-044 Shared library short codes, permissions UI and invites (2026-10-20, 0.3.0)
- **Decision:** a join is checked with a 6-digit code (commit / challenge / reveal files on the share, signed) instead
  of a 32-character fingerprint read aloud; per-member access per collection in one window and a grid; "limit who can
  see this" moves an item into a collection of its own. Removal and loss of access re-key as before.

### ADR-045 DarkCipher portals (dcx-ctl) and ADR-046 Synology DSM as Infrastructure providers (2026-10-20, 0.3.0)
- **Decision:** both follow ADR-007/031: one file per provider, read-only until "Allow changes", changes verified by
  re-reading, credentials from the vault, https with TOFU pinning, every action audited.

### ADR-047 Editors: Vim engine, profiles, hex editor, gutter and syntax colours (2026-10-20, 0.3.0)
- **Decision:** the editor stays a TextBox (undo, IME, selection, accessibility for free) with the Vim engine over an
  `IVimBuffer` (testable on a string buffer), a vimrc-subset profile system that can't run anything, a line-number
  gutter drawn beside the box and syntax colours drawn as an adorner over it (the box's own text transparent while on)
  by a one-line tokenizer for the formats engineers edit on servers — no third-party editor control. Both switches are
  remembered and policy-managed; colours are only on when the file name names a format.
- **Alternatives:** AvalonEdit (would need a new dependency and a review; deferred).

### ADR-048 Network & mail toolbox (2026-10-20, 0.3.0)
- **Context:** DarkCipher0x: "tests like telnet n stuff", "mtr", "all the different tools that mxtoolbox has", "spf checks",
  "email header checks".
- **Decision:** every check is a UI-free function (`Net/NetTests`, `Net/DnsClient`, `Net/Whois`, `Mail/*`) behind
  fakeable seams (`IMailDns`, an injectable policy fetch, a raw WHOIS exchange, an HTTP handler), with every target
  validated (`NetTests.CheckHost / CheckUrl`, `MailNet.CheckDomain`) before a command or URL is built, offline mode on
  every destination, certificates validated by Windows except in the labelled inspection-only TLS check, answers made
  printable and masked. Remote runs use quoted values with tools tried in order. The screen is one tab per file;
  results go to an encrypted History (never exported). The DNS client speaks RFC 1035 itself (unguessable ids, only
  the asked server's answer counts, TC → TCP) because Windows' resolver can't be pointed at a server per query.
- **Consequences:** the same functions drive the dashboard checks and the mail server checks.

### ADR-049 Ansible on a control node, the job on stdin (2026-10-20, 0.3.0)
- **Decision:** Ansible runs where it runs (a WSL distribution or a saved Linux host), never emulated on Windows. A job
  is a fixed bootstrap on the command line (`sh -c 'IFS= read -r s && eval "$(printf %s "$s" | base64 -d)"'`) with the
  script, the vault password and the become password as the first three stdin lines; the script makes a mode-700
  temp dir, writes the inventory and two one-line password scripts that print the variables read from stdin, runs
  ansible with `</dev/null` and removes the dir on exit. Patterns, tags and module names are validated; paths and
  arguments quoted. A changing run asks (PRODUCTION typed). Runners are an interface (`IAnsibleRunner`) so the
  self-test uses a fake.
- **Consequences:** nothing of a run, least of all a password, is ever in argv, a file, a setting or the audit log.

### ADR-050 Whole-app encryption as a proof, profiles, directory sign-in and the first-run setup (2026-10-20, 0.3.0)
- **Context:** DarkCipher0x: "when people do encrypt it on setup (or after), the entire application is encrypted"; "multiple
  profiles in one servermanager", "login sync with Active Directory / SASL / LDAPS / Entra … very secure, double
  audited", "don't remove local user profiles".
- **Decision:**
  1. Encryption was already universal (every writer goes through `SecureStore`); 0.3.0 adds the **proof**: `EncryptionAudit`
     walks the store and the logs, classifies every file (sealed / sealed lines / plain by design with the reason /
     PLAIN) and the self-test fails on PLAIN; Settings shows the same check. "Require at start" (or Hello) removes the
     DPAPI copy, which is what makes the whole application unreadable without the password.
  2. **Profiles** are stores: the existing store is the "default" profile (nothing moves); more live under
     `profiles\<id>` with their own keyring. The index (`profiles.json`) holds names and kinds only. `SecureStore.DataDir`
     resolves lazily so start-up can choose first; switching restarts the app with `--profile`.
  3. **Directory sign-in** is a gate on opening a profile, never a replacement for its own key: `IDirectorySignIn`
     per kind — the Windows session (Kerberos / Negotiate already happened; bound to the SID; groups from the token),
     LDAP only over TLS (certificate validated and host-matched, or pinned; SASL / Negotiate or a simple bind inside
     TLS with a password asked each time), Entra through OIDC authorization code + PKCE in the system browser with a
     loopback listener, the id_token verified RS256-only against the tenant's JWKS by kid (issuer, audience, expiry,
     nonce, tenant). Groups map to admin / operator / viewer (viewer: `AppData.Save` refuses); a required group; a
     sealed, MAC'd proof (key derived from the store key) opens the profile offline within a grace. `RequireDirectorySignIn`
     is a machine policy read before any store opens.
  4. **First-run setup**: pages that call the same code Settings uses (`SecuritySetupUi`), all optional, remembered by
     `SetupDone`.
- **Alternatives:** storing a directory password or refresh token for silent re-sign-in: rejected (nothing is stored;
  the proof is a time-limited local attestation, not a credential).
- **Consequences:** the "double audit" is the self-test (fakes for every provider, tamper and grace cases) plus this
  record; a real tenant test is listed as a hand test.

### ADR-051 Team features built in, groups (2026-10-20, 0.3.0)
- **Decision:** the Shared library engine (ADR-033) no longer needs an add-on to exist: a `TeamFeatures` setting
  (admin-protected, policy) switches the screen on; libraries are known from the add-ons' profiles or from
  `AppData.TeamLibraries` (joined from a share with the owner's fingerprint, created, or restored from a key backup).
  **Groups** live in the signed roster (`RosterGroup`: members + per-collection access; the canonical form grows only
  when groups exist, so old rosters keep their signatures); every access decision (`Roster.Readable / Editable`) and
  every key wrap goes through the roster, so group access is enforced by the encryption exactly like a direct grant;
  a removed member leaves every group first and their `RemovedEntry` snapshots the effective access so earlier writes
  still verify.
- **Consequences:** the add-on format stays supported (older members); Windows Hello already confirms reveals / exports.

### ADR-052 Dashboard checks engine (2026-10-20, 0.3.0)
- **Decision:** a `DashboardTile` (kind, target, thresholds, interval, last result, history) in the encrypted store and
  one engine (`Checks`) that runs tiles with the toolbox's own functions (ping, TCP, HTTP, TLS days, `HostPerf` over
  SSH, SPF + DMARC). It never schedules itself: the dashboard calls it by its visible rule (manual / on open / interval
  while on screen; each tile may set its own minimum interval). A change of state is audited and notified. Pinned from
  the dashboard or 📌 in the toolbox.
- **Consequences:** "no silent polling" (ADR-018) holds; LAN monitors keep their own scheduler since they are explicit
  schedules the user created.

### ADR-053 VPN tunnels: WireGuard and OpenVPN with route steering (2026-10-20, 0.3.0)
- **Context:** DarkCipher0x: "use VPNs in the app and route traffic down it for certain routes etc.. wireguard and openvpn".
- **Decision:** a profile keeps the configuration *without* secrets (WireGuard private / pre-shared keys and the OpenVPN
  private key go to the vault as credentials; the auth-user-pass password is a vault credential sent over OpenVPN's
  management channel). Import refuses or strips anything that runs programs (PostUp…, up / down / plugin /
  script-security) and file references. Routes are the app's choice (AllowedIPs / `route-nopull` + `route`), split
  tunnel by default, "everything" explicit and warned; the installed routes are read back from the routing table
  (`GetIpForwardTable2`). Engines drive the official clients — WireGuard's tunnel service and openvpn.exe — which need
  administrator approval (UAC) each time; the rendered file exists only while the tunnel is up, in a user-only ACL'd
  folder. `SshConnector.BeforeConnect` brings a host's tunnel up before any connection; RDP does the same.
- **Alternatives:** wintun / wireguard-go in-process (no UAC per up/down, a kill switch via WFP): deferred — it means
  shipping a driver and far more code to audit.
- **Consequences:** no kill switch yet (stated in help); a viewer profile can't bring tunnels up.

### ADR-054 Named and filed as DarkCipher Systems: folders, keys, title, one-time migration (2026-10-21, 0.4.0)
- **Context:** DarkCipher0x: "ensure that all my regkeys, directories are all labeled correctly with DarkCipher Systems/ServerManagement"
  and "in the title bar it should say ServerManagement — by DarkCipher Systems". ADR-027 had kept the old identifiers
  (`%APPDATA%\DarkCipher\ServerManagement`, `HKLM\Software\darkcipher0x\…`) to avoid orphaning data.
- **Decision:** `AppInfo` is the one place that names folders (`RoamingDir`, `CacheDir`, `LocalLowVendorDir`, the
  requests temp folder) and the company; everything that built a path from a vendor string uses it. At start-up, before
  the profile is chosen, `AppInfo.MigrateLegacyFolders()` moves each old folder to its new name once (only when the new
  one doesn't exist; the Roaming store move failing is fatal and explained; the move is audited after the store opens).
  The uninstaller cleans both generations of folders and keys (`Uninstall.Profile.Legacy`, `CleanMachine` deletes the
  new `…\DarkCipher Systems\ServerManagement\Install` key and the old `darkcipher0x` key, walking empty parents). The
  installer's registry key, manufacturer and contact say DarkCipher Systems; every assembly's company too. The window
  title is `MainWindow.DefaultTitle`. **Kept** (changing them would break upgrades or pins): the UpgradeCode, the program
  folder, the AppUserModelID, the mutex prefix, the DPAPI entropy, the exe name and the release file names.
- **Alternatives:** a junction from the old folder to the new (rejected: two names for one store, and the uninstaller
  would have to understand it); migrating lazily on first write (rejected: the store opens before anything else, so the
  move has to happen before the keyring is read).
- **Consequences:** a user with a moved store (`location.json`) keeps it where it is; only the default folders move.

### ADR-055 Branding & colours in Settings, theme add-on export, the neutral sample theme (2026-10-21, 0.4.0)
- **Context:** "an easy way in the settings to customize the theme and the logos and the main logo/text etc and all the
  colours … with full support of theme add-ons as well", and the public source must not carry one organisation's
  branding (ADR-030's sample did).
- **Decision:** `Dcx.Ui\Services\Branding.cs` holds a draft (name, tagline, UI font, logo plate, every colour key for
  dark and light) that composes to the same `theme.json` text a theme add-on carries, validated by `ThemePack.Parse`
  (relaxed to accept branding without colours). It is stored in the encrypted settings (`BrandingTheme`; the logo as
  `BrandingLogoPng`, excluded from policy for size) and applied as the active theme source when no theme add-on is in
  use; a company pushes it by policy (`BrandingTheme` + `BrandingLogoPath` on a share). **Export as theme add-on** writes
  a theme-only `.dcxaddon` (API 2) from the live draft. The repository's sample theme became the neutral **Ocean** theme
  with a generated logo (`samples/addons/sample-theme`).
- **Alternatives:** editing the add-on's JSON by hand (kept as Import / Export); a separate branding file on disk
  (rejected: the settings are already encrypted and policy-enforced).
- **Consequences:** environment colours stay fixed (ADR-017); the About card and theme texts no longer describe internal
  design material (DarkCipher0x: "people don't need to know").

### ADR-056 Observability: log sinks and dashboards (2026-10-21, 0.4.0)
- **Context:** "a graphing and logging section … Grafana … Azure Sentinel, Elastic, etc."
- **Decision:** in `Dcx.Notify` (the project for what leaves the app to tell someone, ADR-041) a second shipper beside
  syslog: `LogEvent` (one masked JSON document per audit line, error or session line), `LogSink` (the model, its rules —
  https only, no user info, bounded fields, Azure Monitor needs the Entra sign-in + a `dcr-` id + a `Custom-` stream —
  and the payload per kind: Elasticsearch / OpenSearch bulk NDJSON, Loki streams with nanosecond stamps, Azure Monitor
  Logs Ingestion rows, Splunk HEC, GELF, a webhook array), `LogShipper` (the queue with an encrypted spool, per-sink
  back-off, one `HttpClient` per sink on the offline-mode handler with TLS 1.2+ and SHA-256 pinning, per-sink status, the
  per-minute activity counters). The sink list is admin-protected and a policy (`ObserveSinks`, JSON without secrets,
  checked by `LogSink.Check` through `ProtectedSettings.SinksCheck`); secrets live in `ObserveSecrets` (excluded from
  policy). Azure Monitor signs in with an Azure subscription's app registration for the Monitor scope
  (`AzureAuth.TokenAsync(scope)`, cached per scope; `AzureClient.MonitorTokenAsync` wired in `App.WireLayers`).
  `GrafanaClient` lists dashboards through the search API and builds dashboard URLs only on the Grafana server itself.
  The Observability screen shows state, activity bars, the Grafana list and the organisation's links (`ObserveLinks`,
  `Label=https://…`), opening everything in the app's own browser window.
- **Alternatives:** OpenTelemetry / OTLP (deferred: an exporter protocol the app's own hardened HTTP stack would have to
  implement; the SIEM-native APIs above cover the asked-for providers directly); embedding Grafana panels (rejected: a
  foreign origin inside the app's window; the browser window keeps the boundary visible).
- **Consequences:** a failing sink is notified like failing syslog; log sinks connect directly (`UseProxy = false`) so a
  proxy never sees the audit trail; tested against TLS fakes on 127.0.0.1 and an in-process Grafana only.

### ADR-057 Reports for this PC and the Event Viewer (2026-10-21, 0.4.0)
- **Context:** "more reports — including own local system reports", "integration to export from Windows Event Viewer",
  "AI analysis on the reports too".
- **Decision:** `Dcx.Connectivity\Reports\LocalReport.cs` collects Windows facts (WMI through `System.Management`, the
  registry, `DriveInfo`, `IPGlobalProperties`, processes) into the same evidence sections the Linux report uses, and
  `WindowsEvents.cs` reads event logs through `wevtutil qe … /f:RenderedXml` (a fixed argument list; log names validated
  by `CheckLog`; the XML parsed with `DtdProcessing.Prohibit`, no resolver, a bounded document), digests them like the
  Linux log digest, exports `.evtx` (`wevtutil epl`) and a formula-safe CSV. `LocalReport.Rules` adds Windows findings
  with PowerShell suggestions (Copy only, never Run). The report engine includes "This PC" first; the AI analyst's prompt
  knows both operating systems.
- **Alternatives:** `EventLogReader` (`System.Diagnostics.Eventing.Reader`): rejected for now — the published single-file
  exe stays smaller with the in-box tool, and `wevtutil` is on every supported Windows.
- **Consequences:** the Security log and BitLocker need the app to run as administrator (stated in the UI); exports are
  plain files outside the store (stated and confirmed).

### ADR-058 The release bundle and the penetration-test suite (2026-10-21, 0.4.0)
- **Context:** DarkCipher0x's rule for every build: the full documentation set as PDFs (user, developer, admin / business with
  Active Directory, syslog, team features, Group Policy), a PDF of the project tree and how it is built, the full
  security report with the penetration tests done, and the source sanitised and confirmed for public use.
- **Decision:** `build.ps1` step 6 produces six PDFs through one manifest each (`docs\guide\guide*.txt`; the guide tool
  gained `TITLE` / `SUBTITLE` / `COVERS`), the generated tree table (`tools\write-tree.ps1`), and the public source
  archive (`tools\write-public-source.ps1`: every tracked file minus `docs\history\`, scanned for local user paths,
  private addresses, keys and the terms in a local, git-ignored list; a hit fails the build). The penetration tests are a
  self-test unit (`SelfTestPentest.cs`) that runs on every build; `docs\SECURITY-REPORT.md` describes the method, every
  probe family and the result, and is one of the PDFs. The build fails when any bundle file is missing. The maintainers'
  session notes moved to `docs\history\` (in git, out of the archive).
- **Alternatives:** an external scanner (rejected: nothing to scan over a network — the product has no server side; the
  attack surface is parsers, files, policies and provider answers, which in-process probes reach exactly).
- **Consequences:** a release takes a few minutes longer (six Edge print jobs); the sanitised archive is the tree people
  can build and audit.

### ADR-059 Settings say what Group Policy set and what needs an administrator (2026-10-21, 0.5.0)
- **Context:** DarkCipher0x: "the settings like the syslog TLS config all need GPO controls and locking behind UAC". Values were
  already enforced (policy values win on every load and save; protected settings change only through the admin gate),
  but the cards didn't say so: a field set by policy stayed editable until Apply quietly put it back.
- **Decision:** every Settings control can carry the setting it edits (`SettingTag`, set by `Bound(...)`, worked out by
  `Check(...)` from its value expression with `CallerArgumentExpression`, or set by `ProtectedCheck`). When a card is
  built and every time it is shown, `ApplyLocks` greys out policy-set controls (tooltip: scope, value) and marks
  admin-protected ones with 🛡, and the card lists both. A plain `Check` for a protected setting is routed through the
  admin gate automatically. More settings are protected (confirmations, paste protection, session logging, automatic
  snapshots, Grafana address, backups, single sign-on, Puppet changes, IDE SSH config, Teams, BeyondTrust). Computer-only
  policies (`RequireDirectorySignIn`, `AutoProfileForWindowsAccount`) are in the ADMX templates.
- **Consequences:** a self-test fails when a protected setting has no bound control; the share of all policies with a
  bound control is reported every run.

### ADR-060 Backup & restore in Data & backups, enforced below the UI (2026-10-21, 0.5.0)
- **Context:** DarkCipher0x asked several times for backup and import with tick boxes under Data & backups, manageable by policy.
- **Decision:** the pick-what export / import (ADR on 0.3.0 §11) leads Data & backups as "Backup & restore", plus
  scheduled backups (`ScheduledBackup`: a folder, every N days, newest N kept, the password in its own encrypted store
  file, read back before it counts, pruned only after a newer one verified). `BackupPolicy` holds the rules (exports,
  imports, secrets, minimum password) and is called by `Backup.Write`, `Merge`, `ReplaceAll` and the connection importer
  — no screen, add-on or API can go around it. "No secrets" strips credentials and every secret-bearing setting.
- **Consequences:** test passwords shorter than 12 characters were raised; snapshots stay unchanged.

### ADR-061 Imports from other connection managers (2026-10-21, 0.5.0)
- **Decision:** `Dcx.Connectivity/Import/` — one file per source (mRemoteNG, SecureCRT, Royal TS, MobaXterm, PuTTY /
  KiTTY, WinSCP, RDCMan, .rdp, Devolutions RDM), parse-only, 32 MB cap, XML with DTDs prohibited and no resolver, every
  value cleaned (`ConnectionImport.Add`). mRemoteNG's AES-GCM uses a 128-bit nonce that .NET's `AesGcm` refuses:
  `GcmAnyIv` implements SP 800-38D directly (GHASH-derived J₀, constant-time tag check before any plaintext) and is tested
  against `AesGcm` on 96-bit nonces. Passwords the source apps encrypt with keys of their own are reported, not guessed.
- **Consequences:** formats without published schemas (Royal TS documents, RDM exports) are read best-effort; tested on
  made-up files, hand tests on real exports listed in OUTSTANDING.

### ADR-062 Identity & directory, automatic sign-in and single sign-on (2026-10-21, 0.5.0)
- **Decision:** a Settings tab for the Windows account (`WindowsAccount`: identity, `dsregcmd /status`, `klist` counts),
  directory connections (view, edit with `SensitiveAction`, real test sign-in) and profiles. `Profiles.ForWindowsAccount`
  opens the one profile bound to the signed-in account (SID, UPN or DOMAIN\name) without the picker unless switched off
  (the profile list's own switch, overridden by the computer policy). SSH single sign-on runs Windows' OpenSSH with
  GSSAPI in a terminal tab (`KerberosSsh`; SSH.NET has no GSSAPI; only checked values on its command line; delegation off
  unless allowed). RDP sign-on modes: CredSSP default credentials, Remote Credential Guard (`RedirectedAuthentication`)
  and Restricted Admin (`RestrictedLogon`) through `IMsRdpExtendedSettings`.
- **Alternatives:** a GSSAPI implementation inside SSH.NET (rejected: SSPI interop for a niche path; OpenSSH is in-box).

### ADR-063 AUTOMATION section and Puppet (2026-10-21, 0.5.0)
- **Decision:** Ansible moves to a new AUTOMATION section beside the new Puppet screen. Puppet runs over the saved hosts'
  SSH connections (`PuppetCommands`: certnames, environments, tags, keys and Bolt names validated or shell-quoted; sudo
  through `RemoteExec`'s stdin). PuppetDB is queried on its own host with curl and that host's agent certificate, so no
  Puppet key is ever copied to the PC. Bolt reuses the Ansible control-node bootstrap. Read-only until
  `PuppetAllowChanges` (admin-protected, policy).

### ADR-064 WSL management (2026-10-21, 0.5.0)
- **Decision:** `WslManager` runs wsl.exe / wslc.exe from fixed paths with argument lists, `WSL_UTF8=1`; names, ids and
  images validated; only /etc/wsl.conf, /etc/hosts and resolv.conf can be read or written (as root through stdin, backed
  up); `.wslconfig` is edited key by key in [wsl2] only. WSL Containers' subcommands are discovered from `wslc --help`
  (no published reference yet).

### ADR-065 IDE integration (2026-10-21, 0.5.0)
- **Decision:** editors are found by their registered URL scheme and opened through it (no command line). Remote-SSH gets
  host aliases from the app's own `~/.ssh/dcx_config` (no secrets), included once with consent; keys are loaded into the
  Windows ssh-agent from stdin with a lifetime (`ssh-add -t … -`), passphrases answered through the existing askpass pipe.

### ADR-066 Microsoft Teams, Pingdom and BeyondTrust integrations (2026-10-21, 0.5.0)
- **Decision:** each is off until configured and (Teams, BeyondTrust) switched on by an administrator; every setting is a
  policy; secrets live only in the encrypted settings. Teams: Microsoft Graph with a delegated token (`EntraDelegated`:
  the Entra sign-in's PKCE loopback flow, refresh token in its own encrypted file) or Workflows webhooks restricted to
  Microsoft's hosts; a preview shows exactly what is sent. Pingdom: read-only GETs to api.pingdom.com as a dashboard
  check kind. BeyondTrust: PRA / Remote Support read-only (OAuth client credentials; configuration and reporting APIs;
  Jump Items open through the client-scripting URL); Password Safe checkouts (`PasswordSafeSessions`) give a session an
  in-memory credential (`HostEntry.Transient`, never serialised) and are checked in when the tab closes, at expiry or at
  exit. Privilege Management (EPM) is not integrated: no suitable public read API was confirmed.

### ADR-067 Locks and restrictions: categories, items, policy (2026-10-21, 0.5.1)
- **Context:** DarkCipher0x: lock any category (credentials, team credentials, known hosts, the audit log…) behind UAC or a
  password, padlocks on single hosts, folders and other items, the option itself under Group Policy, and policies that
  turn off adding hosts, cloud accounts, infrastructure and the rest.
- **Decision:** `Locks` (Core) holds the rules (`AppSettings.UserLocks`, excluded from policy and kept across restores) and
  the open state (memory only, by rule target, for `LockOpenMinutes`, closed by `CloseAll` when the app locks, Windows
  locks or the PC sleeps). Targets: `cat:<key>` (21 categories, each a set of screens), `host:`, `folder:` (covers its
  hosts and sub-folders), `cred:`, `infra:`, `cloud:<provider>:<account>`, `portal:` and `note:`. Modes: `uac` (an
  elevated `--verify-admin` copy shows `AdminApproveWindow`; exit code 0 = approved), `signin` (Windows Hello, else
  CredUI + LogonUser with the SID checked) and `password` (PBKDF2-SHA256 600,000 rounds, constant-time, 3 free tries then
  30 s doubling to 10 min, kept in the encrypted settings). Group Policy: `LockedCategories` with `LockPolicyMode`,
  `LockAuditLog` (default on), `AllowUserLocks`, `AllowItemLocks`, `LockOpenMinutes` — the last five admin-protected.
  **A category lock guards its screens** (and the local API and add-on routes into it); **an item lock guards the item
  everywhere**: the screens, the SSH connector (host, credential), `CloudProvider.ApiFor` (accounts), the AI
  capabilities (host / provider / note / portal arguments refused, lists show the name with `locked: true`), notes
  search and incident Markdown. Locking the Credentials category does not stop saved hosts from signing in with their
  credentials. **Add-ons and the local API never get a locked item, open or not:** `Capabilities.RunAsync` sets
  `Locks.Strict` (an AsyncLocal) for `addon:` and `api` actors, `Locks.Demand` and `Locks.Hidden` honour it, and the
  bridge methods (`hosts.*`, `notes.search`, `credentials.request`, `ui.open*`) and the local API check it directly.
  `Restrictions` (Core) maps 21 actions (`host.add` … `addon.install`, `network.scan`) to `Allow*` settings: `Check` in the
  screens (says why), `Demand` below them (throws `UnauthorizedAccessException`, -32001 on the bridge). Deleted items drop
  their own lock (`Locks.Forget`).
- **Consequences:** `LockGate` (the prompts) lives with the screens; the self-test can never raise a UAC or sign-in prompt
  (refused and reported). The run turns `LockAuditLog` off while it walks every screen; `Release051Tests` checks the
  default, the lock and its prompt.

### ADR-068 Add-ons behind UAC and policy (2026-10-21, 0.5.1)
- **Decision:** `AddonPackages.PolicyRefusal` (`AllowAddonInstall`, `AddonBlockedIds`, `AddonAllowedIds`,
  `AddonRequireSigned` — Authenticode on the program; data-only add-ons have none) is applied at inspect and again at
  install; `UseRefusal` (the same lists, without the install switch) before every run, theme application, portal-browser
  pack and shared-library profile — a policy refusal never disables the add-on, so lifting the policy brings it back.
  Installing, updating, enabling, granting a permission and removing need `LockGate.VerifyAdminAsync` while
  `AddonsNeedApproval` is on (the default; admin-protected). Disabling and revoking never do.

### ADR-069 VNC: the app's own RFB client, tunnelled through SSH (2026-10-21, 0.5.1)
- **Context:** DarkCipher0x: VNC that connects to Macs and Linux with a click, older and newer VNC, Apple's modes.
- **Decision:** a native RFB client (`Dcx.Connectivity/Vnc`) instead of noVNC in WebView2 plus a WebSocket relay: no
  third-party JavaScript, no listening relay, one dedicated reader thread per session, and every server-supplied
  length bounded before allocation. RFB 3.3 / 3.7 / 3.8 (Apple's 003.889 answered as 3.8). Security types, best first on
  a direct connection: VeNCrypt X.509 (TLS 1.2+, certificate pinned on first trust in `VncSettings.TlsSha256`; inside it
  None, VNC or Plain), Apple Remote Desktop (type 30: DH with the server's prime, MD5 of the shared secret as the AES-128
  key, the macOS user name and password random-padded), the VNC password (DES challenge), None. VeNCrypt Plain is
  allowed only inside an SSH tunnel; anonymous TLS (TLSNone/TLSVnc/TLSPlain) is refused — Windows can't do it and it
  proves nothing. Encodings Raw, CopyRect, RRE, Hextile, ZRLE (one zlib stream per connection, kept across
  rectangles), DesktopSize and LastRect; 32-bit BGRX so the framebuffer copies straight into a `WriteableBitmap` at up
  to 30 fps. Sessions go **through SSH by default** (`TunnelManager.EnsureEphemeralAsync` to `TunnelTarget:port` on the
  machine's SSH host), so the stream is encrypted whatever the server offers. Apple's proprietary "High Performance"
  mode (Apple's own types 33/35/36) is not supported: it is undocumented.
- **Consequences:** a host protocol `vnc` with `VncSettings`; the platform `macos`; `MainWindow.OpenVnc`; VNC tabs in
  the workspace and the dashboard's session counts. The self-test drives eight scenarios against loopback fakes,
  including the ARD exchange decrypted by the fake.

### ADR-070 Host import from a network scan, AI optional (2026-10-21, 0.5.1)
- **Decision:** `HostDiscovery` (Connectivity) runs the existing TCP connect scanner (private addresses only;
  `AllowNetworkScan` gates every scanner) on the ports the app can open, groups the answers by address, reads the
  reverse-DNS name, and guesses the platform from banners (RFB 003.889 → macOS, OpenSSH for Windows, network-device SSH
  banners, distributions). `Candidates` turns a machine into host entries (SSH, RDP, VNC — a Mac's desktop tunnelled
  through its SSH — Telnet when allowed), `AlreadySaved` marks duplicates. The AI part is a Core seam
  (`IHostIdentifier`, registered by the AI module, absent when AI is off): names, ports and banners go to the model as
  untrusted data with a JSON schema; addresses that weren't scanned are dropped, names and folders cleaned. The review
  grid (`HostImportWindow`) is the only place anything is saved.

### ADR-071 Dashboard card catalogue (2026-10-21, 0.5.1)
- **Decision:** added cards are lines in `DashboardCards` (`id|type|target|title|size`, ids `x:…`), so Group Policy can
  force a layout as a plain list; `DashboardLocked` stops every change. `DashboardCatalog` (no WPF) holds the types —
  host, folder, infrastructure provider, cloud provider, Grafana, Sentinel, Pingdom, BeyondTrust, Puppet, Entra ID —
  and Reset; `Overview` folds the added ids into the order. Cards read only what the app already knows (last known
  provider state, cloud check results, sink status, open checkouts); contacting a service is a button on the card or
  the dashboard's check rule. A locked host, folder, provider or account shows its name and padlock only.

### ADR-072 Dispatcher watchdog: WPF's work queue can't stall for good (2026-10-21, 0.5.3)
- **Context:** 0.5.1 and 0.5.2 stopped taking clicks after a right-click in LOGS (and closing the window left the process
  running). A live, non-invasive look at the frozen 0.5.2 showed the UI thread idle in `GetMessage`, 138 operations
  queued up to Send priority, `Dispatcher._postedProcessingType` set and no wake-up message on its way. WPF posts one
  "DispatcherProcessQueue" message to its hidden window and posts no other while it thinks that one is pending. A
  message reaching that window while processing is suspended (a layout or render pass, e.g. the one a menu starts) is
  consumed and WPF throws "Dispatcher processing has been suspended…", which `UiErrorGuard` (s37) logs and ignores. When
  the consumed message was the wake-up, the queue never ran again.
- **Decision:** `DispatcherWatchdog` (Dcx.Ui) re-posts WPF's own registered wake-up message to WPF's own window (found
  by reflection: `Dispatcher._window` → its HwndWrapper's `Handle`): right after that exception is caught
  (`KickSoon`, 50 ms later, once the pass is over), and from a background heartbeat that queues an empty operation every
  500 ms and posts the wake-up when it hasn't run after 2 s. A spare wake-up is harmless (WPF processes its queue). A
  stall is logged only when the UI thread is idle in its message wait (a lost wake-up), not while it is busy.
- **Consequences:** started in `App` with the other UI hooks; the self-test compares the message number with
  `Dispatcher._msgProcessQueue`, finds the window, forces the stalled state at idle priority and requires queued work to
  run again through the watchdog — a WPF update that renames those internals fails the build instead of silently
  turning the protection off. Nothing leaves the process.

### ADR-073 Capture guard: the mouse is never left captured once every button is up (2026-10-21, 0.5.4)
- **Context:** after a right-click in LOGS → Error log, the mouse stayed captured on that pane in subtree mode: the
  pane's own buttons worked (a button takes the capture and gives it back), the sidebar and every other screen got no
  clicks. The app's own code captures the mouse nowhere near it (only the AI screenshot cropper captures at all), so the
  capture was left behind inside WPF.
- **Decision:** `CaptureGuard` (Dcx.Ui) releases a capture that is still held when no mouse button is pressed, unless a
  menu, context menu, open drop-down, tooltip or popup holds it (they keep the mouse on purpose, to close on an outside
  click). It checks after every button-up and context-menu close (at Input priority, once the click has been handled)
  and on the next mouse move. Each release is logged once per element type with its ancestry (types only), so the
  error log names the control that held on.
- **Consequences:** installed in `App` with the other UI hooks. The self-test checks the rule (plain elements and text
  boxes are not legitimate holders; menus and their items are) and releases a real stuck capture in the app's window.
  Drags are unaffected: a button is pressed for their whole length.
- **0.5.5 amendment:** the "button is up" test uses the physical buttons (`GetAsyncKeyState`), not WPF's cached state:
  a left-click that closes a context menu goes down on the text box (which captures to select) but up on the closing
  popup, so WPF still reported the button pressed and the guard held back. The self-test fakes both states.

### ADR-074 The app shows the profile's name, not PC\user (2026-10-21, 0.5.7)
- **Context:** Settings → Security & privacy → Admin protection listed "approved by PC\user" (the real names): the Windows
  account names the PC and the person. DarkCipher0x: "it should be saying approved by DarkCipher0x".
- **Decision:** `AppIdentity` (Core) is the one name the app shows for the person using it: the profile's name (Settings
  → Profiles → Rename, Quick Start), cleaned of control characters and capped at 64. Protected-setting and lock requests
  carry it (`Name`, display only; the SID is still what is checked). The history records the profile's name only when
  the approving Windows account is the one that asked; a different administrator is recorded under their own account,
  because who approved is the fact. Older records written with this account show as the profile's name. The admin
  approval windows lead with the name and keep the account beside it (a name is self-chosen and could imitate an
  administrator). The 2FA account label, recovery codes, default SSH key names and shared-library names use it; the
  Windows sign-in prompt no longer names the account.
- **Consequences:** log sinks and syslog keep the Windows account and PC (an organisation's records need the real
  account). The self-test checks the mapping, the cleaning and both approval cases.

### ADR-075 Hosts beside the sessions: pinned or auto-collapsing (2026-10-21, 0.5.8)
- **Context:** connecting from Hosts opened the session tab in front of Hosts, so the list disappeared. DarkCipher0x: keep the
  Hosts pane pinned when connecting, with an option for auto-collapsing.
- **Decision:** `AppSettings.HostsDock` = tab (default, unchanged behaviour) / pinned / auto, a Group Policy with those
  choices. When pinned or auto, the one `HostsView` moves out of its tab into a panel left of the documents
  (`MainWindow.HostsDock.cs`; the panel sits inside the documents column, so the sidebar and AI-dock splitters are
  untouched). Auto folds the panel to a 30-pixel strip when a tab opens or a click lands in the documents; navigating to
  Hosts or clicking the strip unfolds it. The width is a per-PC setting (excluded from policy like the AI panel's).
- **Consequences:** Hosts is never loaded twice (the view moves; the empty tab is closed without disposing it). The
  self-test drives pinned, auto and back to a tab on the real window and checks the policy's choices.

### ADR-076 Opening something already open switches to its tab (2026-10-21, 0.5.9)
- **Context:** every connect opened another tab, even to a host that was already open; the tab bar filled up. DarkCipher0x:
  "Id prefer it to open the same active connection i have open ... this should be a setting ... but id like the default
  changed. This should go for most things".
- **Decision:** `AppSettings.ReuseOpenTabs` (default on, a Group Policy). `MainWindow.ReuseKey` names what a tab is the
  same thing as: a host's terminal (per session type), Remote Desktop, VNC, Files or Commander, or a local shell profile;
  saved hosts match by id, ad-hoc ones by endpoint. Every open path asks `SwitchToOpen` first. Not reused: a terminal
  opened to run a command (`initialCommand`), Files or Commander at a given folder, mstsc. Shift held, `forceNew`
  ("⧉ New connection", "⊞ Duplicate session") and workspace restore always open another.
- **Consequences:** a dropped session is switched to, not replaced; the status line says to press Enter or Reconnect.
  The self-test checks reuse, Shift/forceNew, the setting off and the policy. Same release: the MSI ticks winget, Nmap
  and Foundry Local only when missing (HKCU App Paths for the packaged apps, Nmap's uninstall entry and nmap.exe).
- **ADR-075 amendment, 0.6.0 (DarkCipher0x: "i only want that host pane pin-able... not the whole hosts frame ... like securecrt"):** the
  pane is a separate `SessionManagerPane` (filter, Connect / ＋ Host / Properties, folders with their hosts, ★ Favourites,
  ＋ Folder), not the moved Hosts page; the Hosts page is always a tab. Add / edit / new folder moved to `HostActions`,
  shared with the Hosts page. Closed folder locks hide their contents; locked hosts show no address.

### ADR-077 DarkCipher Setup: a themed setup program around the MSI, as a template (2026-10-21, 0.6.0)
- **Context:** DarkCipher0x asked for an installer that looks modern, in the house theme, reusable by his other apps. The MSI's
  dialogs (WixUI) can't take the theme, and the MSI must stay the silent / Group Policy / Intune package.
- **Decision:** a WiX 5 Burn bundle (`installer\Setup\Bundle.wxs`) around the MSI with its own bootstrapper application
  (`DarkCipher.Setup.exe`, .NET Framework 4.8 WPF, WiX 5's out-of-process API `WixToolset.BootstrapperApplicationApi`).
  Template and app are split: the template names no app; each app supplies `installer\setup-app.wxi` (name, permanent
  BundleUpgradeCode, icon), `setup-app.properties.wxi` (MSI properties passed through) and `setup-app.json` (texts,
  options, detection). The setup program only sets bundle variables (the MSI properties) and asks Burn to install,
  repair or uninstall; `/quiet` runs the same with an invisible window handle (WiX 5 refuses Apply without one). The
  main thread is left to WiX's runner (COM MTA) and the window gets its own STA thread. One Add/Remove Programs entry
  (the bundle's; ARPSYSTEMCOMPONENT=1 on the MSI under it).
- **Consequences:** build.ps1 step 3b builds it, runs `--check` on the app's config (a broken config fails the build),
  and signs engine and bundle when a certificate is set (`wix burn detach / reattach`). `--preview` draws every page to
  PNG without a window for design checks. `Setup.exe` joins the release assets. Hand tests: install, update, repair,
  uninstall (keep / remove data), `/quiet` on a VM.
- **0.6.1 amendment (extra packages; DarkCipher0x: "the installer isn't getting nmap installed"):** an option in
  `setup-app.json` may name a `"package"`: an ExePackage in the app's `installer\setup-app.packages.wxi` (included in the
  chain after the MSI when the app defines `AppPackages`). The setup app sets `Pkg_<Id>_Found` before detection and
  `Pkg_<Id>_Run` = 1 only when the switch is on, it isn't installed and setup has its window (never `/quiet`), for the
  package's DetectCondition / InstallCondition. ServerManagement uses it for Nmap: free Nmap / Npcap can't install
  silently and winget's package is 7.80 (2019), so the bundle downloads nmap.org's installer pinned by size and SHA-512
  (a remote payload: nothing to ship, Burn refuses any other bytes), runs its wizard elevated, not vital, permanent.
  The MSI's own Nmap action (an unelevated winget call whose failure was ignored) is gone.

### ADR-078 The host pane pins and auto-hides like SecureCRT's Session Manager (2026-10-21, 0.6.1)
- **Context:** 0.6.0's pane pinned from a top-right button at 640 px ("pins a new one half way through the screen")
  while the Hosts page kept its own folder pane. DarkCipher0x: the pin belongs on the left pane itself, pin directly, pop next to
  the terminal, "look at how SecureCRT does it ... they have a way to auto-collapse or pin".
- **Decision:** the 📌 is on the Hosts page's folder pane; one click sets HostsDock = pinned. While docked the page's
  folder pane hides (popped out) and the dock's folder choice filters the page (SessionManagerPane.FolderPicked). Pinned
  width defaults to 260 (0.6.0's saved 640 reads as 260; clamp 180–600). The pane header carries its controls on the
  left: 📌 ⇄ 📍 (SecureCRT's push-pin) and ✕. Auto-hide keeps a 30 px strip; hovering or clicking it shows the pane as a
  flyout over the documents (HostsFlyout, z-order above DocArea) so terminals are never resized; it slides back on
  pointer leave (450 ms, not while a menu holds the capture), on a new tab or a click into the documents.
- **Consequences:** SelfTest051 checks the width bounds, the page's folder pane hiding and returning, and that the open
  auto pane is a flyout with the column still the strip. Hand test: OUTSTANDING "0.6.1 host pane like SecureCRT".

### ADR-079 Updates from GitHub releases, trusted by DarkCipher0x's release key (2026-10-21, 0.6.1)
- **Context:** DarkCipher0x asked for "check for updates" against the GitHub repo, pulling the update to install, with optional
  automatic checks. The ADR-008 updater was off: it only accepted Authenticode packages from a pinned DarkCipher
  certificate, and none exists. A private repository can't be read without a token, which the app must never carry.
- **Decision:** trust moves from the server to a key. `tools\sign-update.ps1 -NewKey` created an ECDSA P-256 key in
  DarkCipher0x's Windows CNG key store (`DarkCipherServerManagement-UpdateSigning`, export only encrypted, never replaced); its
  public point is pinned in `Updater.ReleaseKeyX/Y`. build.ps1 signs `update.json` → `update.json.sig` (P1363 r||s over
  the exact bytes) and refuses to finish without the key; release-check verifies it against the pinned key; both files
  are release assets. The app reads `github.com/<repo>/releases/latest/download/update.json`, takes only the version
  from it (digits and dots) to fetch `releases/download/v<ver>/update.json.sig`, verifies, then parses strictly (newer
  only, file names tied to the version, size, SHA-256). Redirects are followed manually and only between github.com and
  `*.githubusercontent.com` (https). A signed manifest vouches for the package bytes, so Authenticode becomes optional;
  an MSI must still have our UpgradeCode and version. PCs installed with the setup program (a registered bundle with its
  BundleUpgradeCode) update with the new Setup.exe (kind "setup", accepted only when signed). The verified file is copied
  to `%LOCALAPPDATA%\…\update-ready`, verified again and launched, so the exit wipe of the share folder can't race it.
  Settings: `UpdateSource` (github / url), `UpdateCheckInterval` (weekly / daily), `AutoDownloadUpdates`; all policies.
- **Consequences:** Updater.Enabled is true outside the Store; nothing contacts GitHub until you click or opt in. While
  the repository is private and publishing is off, a check reports no release. Losing the key means installed copies
  can't be updated in place (reinstall); escrow it encrypted. SelfTest.UpdatesAndServices covers signatures (good, changed,
  other key, garbage), the fake GitHub (wrong key, unsigned, redirect off GitHub, no release), the setup package choice,
  a download via GitHub's host and refusals. Hand test: OUTSTANDING "0.6.1 updates from GitHub".

### ADR-080 The Hosts folder pane pins or auto-collapses inside the Hosts page (2026-10-21, 0.6.2; supersedes ADR-075 and ADR-078)
- **Context:** 0.5.8–0.6.1 put a host pane beside the sessions (first the whole Hosts page, then a separate
  SessionManagerPane, then a flyout). DarkCipher0x: "i only want it to be auto collapsing etc INSIDE the tab/window of hosts...
  so like it was before, but able to collapse within itself".
- **Decision:** the main window has no host dock again (MainWindow.HostsDock.cs, SessionManagerPane.cs, the dock
  columns and flyout removed). The Hosts page's own folder pane (`HostsView.FolderPane.cs`) carries the push-pin on its
  header: pinned = its column beside the list (width saved in HostsDockWidth, default 230); auto = the column is a 26 px
  "FOLDERS" strip and the pane (FolderHost) floats over the list (column span 3, z-order, shadow) while the pointer is
  on it or the strip, closing 450 ms after it leaves (not while a menu or drag holds the mouse) or on a click into the
  list; dragging hosts onto the strip opens it. HostsDock keeps its name as the policy, now pinned / auto ("tab" reads
  as pinned).
- **Consequences:** SelfTest051 checks the pinned column, the strip, the overlay (list column unchanged) and the policy
  choices. Hand test: OUTSTANDING "0.6.2 Hosts folder pane".
- **0.6.2, updates from a private repository (ADR-079 amendment):** an optional GitHub access token
  (AppSettings.UpdateGitHubToken; encrypted with the settings, excluded from policy, stripped from secret-free backups).
  With it the check reads `api.github.com/repos/<repo>/releases/latest`, downloads update.json / update.json.sig /
  the package as release assets by id (Accept: application/octet-stream), and requires the signed version to equal
  the tag. The token header is added per request only for api.github.com, so the redirect to the download host never
  carries it (self-tested).

### ADR-081 Rearranging and pinning sidebar pages (2026-10-22, 0.6.3)
- **Context:** DarkCipher0x: "the items on the left main sidebar, should be rearrangable and pinable". Sections could already
  be dragged and pages pinned from the right-click menu, but the pages inside a section kept a fixed order and pinning
  was hard to find.
- **Decision:** `AppSettings.NavItemOrder` (page keys; a policy beside NavSectionOrder) orders the pages inside each
  section through `NavLayout.Order`, so a page never leaves its section and unknown or new pages keep their default
  place; `NavLayout.WithSection` writes one section's new order back. Every page is a drag source (data
  "dcx-nav-item", "p:" or "s:" + key) and a drop target: within its section it reorders; on PINNED (the header, or a
  temporary "PINNED — drop here to pin" zone while dragging with nothing pinned) or a pinned page it pins at that place
  (MaxPinned 6); among the pins it reorders; a pin dropped on a section page unpins. A hover 📌 (Segoe Fluent Pin /
  Unpin; always shown in the accent colour on a pinned page) pins in one click; the right-click menu gains Move up /
  Move down. Reset sidebar layout also clears the page order.
- **Consequences:** SelfTest051 checks Move up, a drag within a section, a refused cross-section move, pinning at a
  place, pin reorder, unpinning by dragging out, persistence across a rebuild and the reset. Hand test: OUTSTANDING
  "0.6.3 sidebar rearrange and pin".
- **0.6.3, the Hosts folder pane (ADR-080 amendment):** the collapsed strip is a flush 22 px "Hosts" tab (icon and
  small rotated text, muted, accent edge on hover) instead of a framed "FOLDERS" button; the sliding pane has no
  DropShadowEffect any more (an Effect renders the subtree as a bitmap without ClearType, which DarkCipher0x saw as blurry), only
  a Surface background and a Line edge.

### ADR-082 Extra windows for other monitors, with their own panes and host pane (2026-10-22, 0.6.4)
- **Context:** DarkCipher0x: "multi-monitor support, where multiple monitors could have multiple tabs, plus an extra host
  pane". A popped-out tab got a bare window (one pane, no host list, new tabs still opened in the main window, not
  remembered).
- **Decision:** `FloatWindow` (Views/FloatWindow.cs) is a workspace window: a host pane column (the 0.6.0
  `SessionManagerPane`, restored, with a `BeforeOpen` hook), a splitter and `DocRoot`, a Border holding a `PaneFrame` or
  a `SplitNode` tree. `PaneHost` treats every window's tree as panes (`Panes`, `PanesOf`, `FloatOf`, `WindowOf`):
  Split, Collapse and MoveToNextPane work inside a window (ReplaceInParent knows window roots). The active pane follows
  the active window (`ActivateWindow` on Activated and before anything opens from a window's host pane; `ActivateMain`
  on the main window's Activated), so `AddTab` lands in the window you are in. `NewWindow` makes a window that stays
  open empty (`KeepOpen`); `PopOut` keeps closing its window with its last tab; `DockBack`, `MoveToWindow`,
  `CloseWindow`, `WindowMenuItems` (tab menu and the ⊞ menu: Move to window ▸ Main / Window N / New window) and a
  desktop drop (DoDragDrop returns None and the cursor is outside every app window) round it off. Windows are not owned
  by the main window (an owned window minimises with it and stays above it). Closing a window with tabs asks: move
  them to the main window, close them, or cancel. Placements (`WindowPlacement`, normal bounds + maximised) are saved
  at exit in `AppSettings.WorkspaceWindows` and reopened at start when `RestoreWorkspaceWindows` (a policy) is on, only
  where a monitor still is. The host pane's mode `WindowHostPane` (pinned / auto; a policy) uses the Hosts page's
  pattern: a 22 px "Hosts" tab and an overlay without a drop shadow. Crash recovery records `WindowPane` per tab and
  reopens tabs into the restored windows, split again per saved pane.
- **Consequences:** SelfTest051 covers New window, tabs opening in the active window, a split inside it, Move to window,
  dock back keeping a New window, a pop-out still closing, the host pane modes, the placement and the policies; the
  older pop-out tests keep passing. Hand test: OUTSTANDING "0.6.4 extra windows".

### ADR-083 Cloud PCs through the Windows App (2026-10-22, 0.6.4)
- **Context:** DarkCipher0x: "integrate the Windows App into ServerManagement... so i could see my Cloud PCs and connect". The
  Windows App can't be embedded, and Cloud PC sessions use Microsoft's own sign-in and gateway, which the app's RDP
  control can't do.
- **Decision:** a Cloud PCs page (Views/CloudPcView.cs, `CloudPcEnabled`) and `Dcx.Cloud/CloudPc/CloudPcClient.cs`.
  Without set-up: launch the installed Windows App, found in shell:AppsFolder by its package
  (MicrosoftCorporationII.Windows365…) or name, never a guessed id (the Store when missing); or open Microsoft's
  Windows App web client in a tab of the app's browser (portal tab, profile "Cloud PCs"; the default browser while the
  portal browser is off). With `CloudPcTenant` / `CloudPcClientId` (admin-approved protected settings): Microsoft Graph
  `GET /v1.0/me/cloudPCs` (delegated CloudPC.Read.All) through `EntraDelegated` (now with a feature label), paging only
  on graph.microsoft.com; each Cloud PC connects with the documented direct launch link
  `https://windows.cloud.microsoft/webclient/ent/<id>` (GUID-checked) plus the signed-in account as login hint.
- **Consequences:** a power action (restart, etc.) needs CloudPC.ReadWrite.All and is left for later; Azure Virtual
  Desktop resources are reached through the web client (their `ms-avd:connect` link needs workspace and resource ids
  from the AVD feed, a later step). SelfTest051 covers the list (paging, a foreign next link ignored, 403), the link
  checks and the page without set-up. Hand test: OUTSTANDING "0.6.4 Cloud PCs".
- **0.6.5 amendment (actions; DarkCipher0x: "add the Cloud PC restart and power actions too"):** `CloudPcClient.RunAsync` with
  `CloudPcAction` Restart / Rename (Graph beta `POST /me/cloudPCs/{id}/reboot|rename`, the signed-in user's own Cloud
  PC) and Troubleshoot (v1.0) / PowerOn / PowerOff (beta, Frontline) on `/deviceManagement/virtualEndpoint/cloudPCs/{id}`
  for administrators; 204 expected; 401/403/404/400 explained. Gated by `CloudPcAllowActions` (protected, safe value
  off, a policy); CloudPC.ReadWrite.All is requested in its own token cache (`cloudpc-graph-actions.dcx`) so the list's
  sign-in stays read-only. Every action confirms (power off by typing the name) and is audited. SelfTest051 checks the
  refusal while off, the exact paths and rename body, an unchecked id and a control character refused, and a 403.

### ADR-084 Microsoft 365 admin: Intune, Defender and Entra, read-only (2026-10-22, 0.6.6)
- **Context:** DarkCipher0x: "Integration into Microsofts Defender portal/intune etc would be very handy ... for the admin
  management ... same with entra"; scope chosen: all three, read-only first; no test tenant yet.
- **Decision:** `Dcx.Cloud/Microsoft365/` with one file per service (`IntuneAdmin`, `DefenderAdmin`, `EntraAdmin`), a
  shared `GraphReader` (GET with the delegated token, paging only on graph.microsoft.com, cap per list, 401/403 explained
  with the P1 / P2 licence notes, values sanitised, ids GUID-checked) and `M365Admin` (the list registry, the
  permissions, portal-link host checks). Each service has its own scopes and `EntraDelegated` cache, so consent is per
  service. `M365AdminView` (SERVERS → Microsoft 365 admin) picks a list, loads, filters, opens items in the admin centre
  through a portal tab (profile "Microsoft 365 admin"). Settings: `M365AdminEnabled` (policy), `M365Tenant` /
  `M365ClientId` (admin-approved protected settings). Nothing read is persisted; loads are audited.
- **Consequences:** actions (Intune sync / restart / retire / wipe, Defender incident updates and device isolation,
  Entra disable / revoke sessions) are the next step, behind an admin-approved switch like Cloud PC actions.
  SelfTest051 runs Intune devices, Defender incidents and Entra users through a fake Graph (sorting, links, a foreign
  next link and a foreign portal link dropped), a 403 with the licence note, and the page. Hand test: OUTSTANDING
  "0.6.6 Microsoft 365 admin".

### ADR-085 tmux from the terminal (2026-10-22, 0.6.6)
- **Context:** DarkCipher0x: "press the tmux button in the terminal window and it will launch me into tmux... if it doesnt have
  it, it will install it. but have in the settings a field i can paste my custom tmux config".
- **Decision:** a ⧉ tmux button on SSH sessions to Linux / macOS hosts (`TerminalView.Tmux.cs`). Over the connection's
  exec channel (`SshPool`): probe for tmux and write the user's config (`Ssh/Tmux.cs`: base64 into
  `~/.config/dcx-servermanagement/tmux.conf`, umask 077, never ~/.tmux.conf). Then one line typed into the interactive
  session: when tmux is missing and the user agreed (`TmuxInstall`, asked every time, PRODUCTION banner), install with
  apt / dnf / yum / zypper / pacman / apk / brew (sudo unless root, so the password prompt is in the terminal); then
  `tmux new-session -A -s <name>` (with `-f` and a `source-file` reload when there is a config), refusing to nest inside
  $TMUX. The session name is checked (`[A-Za-z0-9_.-]{1,32}`). Settings → Terminals & connections → tmux holds the
  switch, the name and the config (`TmuxConfig` is excluded from policy: free text, often longer than a policy value).
- **Consequences:** a host whose exec channel can't be opened still gets the typed line (it checks for tmux itself; the
  config is skipped). SelfTest051 checks the lines, the base64 round trip, name and size limits, and where the button
  shows. Hand test: OUTSTANDING "0.6.6 tmux".

### ADR-086 Sessions inside the Hosts tab (2026-10-23, 0.6.7)
- **Context:** DarkCipher0x: "the terminal windows you open, should open within the tab... so that you can still pop out the
  hosts while viewing the terminal... it seems to default to creating a new tab when opening a terminal, so please fix
  that and give the options to change it".
- **Decision:** `PaneHost` gains embedded document areas: a Border inside a tab's content registered with
  `AttachEmbedded(root, owner)`; its panes are ordinary panes (`Panes`, `PaneOf`, split, collapse, drag, Select), and
  `ShowOwner` brings the owner tab forward when one of them is selected or filled. `HostsView.Sessions.cs` moves the
  host list into the area's fixed first tab "All hosts" (excluded from `AllTabs`, never dragged or closed) when
  `HostsOpenIn` = "hosts" (default; a policy with "tab"). `MainWindow.AddTab` passes `session:` for terminals, files,
  server views, RDP, VNC and the commander; `PaneHost.Add` sends a session to the Hosts area when the page armed it
  (`ArmSessionTarget`, 20 s, on clicks / Enter in the page's toolbar, list and folder pane) or when you are already
  working in that area, and never sends anything else there. Only the innermost pane under the mouse / focus becomes
  active (nested panes). The page's header and toolbar collapse while a session tab is in front. Detaching (setting
  "tab", or closing / disposing the page) moves the sessions to the pane holding the Hosts tab. Because WebView2 and
  ActiveX sessions are native windows WPF can't draw over (airspace), auto-collapsed panes over a session (the Hosts
  folder pane, and extra windows' host pane) widen their column while open instead of overlaying.
- **Consequences:** crash recovery reopens such sessions as ordinary tabs. SelfTest051 checks a session from Hosts
  landing inside, a non-session landing outside, the toolbar stepping aside and back, a split inside, an unarmed session
  from the main window outside, and switching to separate tabs moving everything out. Hand test: OUTSTANDING
  "0.6.7 sessions inside Hosts".
- **0.6.8 amendment (DarkCipher0x: "in this view you cant expand the folders ... it needs to be a fully working hosts pane"):**
  the Hosts page's tree (`HostsView.Tree.cs`) adds host leaves (Tag = HostEntry) under folder / category / Favourites /
  shared-collection nodes, skipping folders behind a closed lock (an "expand to unlock" child; expanding asks LockGate).
  Double-click / Enter connects (arming the Hosts area); right-click selects the host in the list and returns the list's
  HostMenu; dragging a host node carries HostEntry[] like the list (a drop onto a host targets its folder); a filter box
  rebuilds the tree with matching hosts only (empty nodes hidden); ＋ Host adds to the selected folder; selecting a host
  sets the list to its folder and selects it; the selected host survives rebuilds. SelfTest051 checks leaves under their
  folder, the parent key, selection → list folder, and the filter.

### ADR-087 Built-in theme packs with their own dark and light palettes (2026-10-24, 0.6.9; amends ADR-038)
- **Context:** DarkCipher0x (2026-10-24): "add some normal looking colours like dark and light mode theme packs ... default to
  my darkcipher theme, but in the theme settings, it should have a few built in ones". Until now the presets were dark
  only and light mode always painted DarkCipher Light.
- **Decision:** `ThemeManager.Presets` is a list of `ThemePresetInfo` (id, name, description, `HasLight`, `AlwaysDark`,
  own `Accent`, optional `LightAccent`). `DarkPresets` and the new `LightPresets` hold the base colours;
  `PresetColour(pack, key, dark)` falls back to the DarkCipher palette for anything a pack leaves out, and never changes
  the Env* keys (ADR-017). New packs Neutral, Slate and Arctic set every base key in both modes and give light mode a
  light header. DarkCipher stays the default (and `Normalize` falls back to it). A pack's own accent is painted as its
  `LightAccent` in light mode; any other accent follows the shared darken rule. Settings → Appearance shows the packs as
  preview tiles (dark half, light half) that drive the policy-aware dropdown; choosing a pack hands over its accent
  only when the accent in use is the previous pack's own and `AccentColor` isn't set by policy. Policy `ThemePreset`
  accepts the new ids.
- **Consequences:** SelfTestHouseTheme checks every pack in both modes (body / strong / header text 7:1, secondary 4.5:1
  on background and cards, status 3:1, its own accent and every swatch 3:1 as painted, Env* unchanged), that each pack
  paints in both modes, the light-accent hand-over, the tiles (one per pack, the chosen one outlined) and renders the
  three everyday packs in both modes. Hand test: OUTSTANDING "0.6.9 theme packs".

### ADR-088 Loose written tool calls from small models (2026-10-24, 0.6.10; amends ADR-034)
- **Context:** DarkCipher0x's screenshot: the AI Engineer in agent mode with phi-4-mini on Foundry Local and a target
  terminal answered with "terminal_run terminal_run -- session … -- display top", a bare "terminal_read_screen" heading
  and "[No response from the target terminal …]" lines it made up. STEPS showed 0 tool calls: the salvage of written
  calls (s45) only took replies that were nothing but calls in the shapes it knew, so nothing ran and the made-up lines
  read like results.
- **Decision:** `AiAgent.SalvageTextCalls` gets a last pass, `TidyLooseCalls`: lines that are only a tool's name and
  lines wholly in [brackets] are dropped, a repeated tool name and "-- key" with a space are joined; the rest must still
  be nothing but calls (prose keeps the reply from running). In the command-line form a lone "-" / "--" joins the word
  after it, so "-- session <id>" can no longer put the id into a free parameter (the command). Calls run through the
  same tools and approvals; unknown arguments are dropped and a missing command returns the existing error, so the model
  can correct itself. A reply with no call that names a tool with call-like arguments (`MentionsToolUse`) gets a STEPS
  note that nothing ran.
- **Consequences:** SelfTestAgent replays the screenshot's reply (two calls, empty arguments, the error naming
  "command", the corrected call runs), checks prose around such lines still never runs, the lone "--" case, and the
  "nothing ran" note.
- **0.6.12 amendment (DarkCipher0x's second screenshot: "(terminal_run session 79e25fd0 command: top -l 1 …)" inside
  prose, then "please review and provide approval" for a call never made):** reading every shape a small model invents
  doesn't scale. When a reply makes no call but `MentionsToolUse` (now also `(name …)`, `name key: value` and
  `name session|command|target …`), the loop adds a user turn saying nothing ran and asking for the call, and switches
  the rest of the run to the text protocol (`<tool_call>{json}</tool_call>`); at most twice, then the "nothing ran"
  note. SelfTestAgent replays the screenshot: the corrected call runs, the retry carries no native tools and the
  protocol, and a reply that keeps only mentioning a call ends with the note.

### ADR-089 Typed confirmations confirm on Enter; the AI Engineer's target follows the terminal in front (2026-10-24, 0.6.11; amends ADR-017 and ADR-028)
- **Context:** DarkCipher0x: "it comes up to prompt me to allow the command to be run, but when i put in the name of the
  server like it asks and approve... it says i declined", and "make sure the target terminal defaults to the open
  terminal". A danger dialog made Cancel its default button, so Enter after typing the host's name cancelled; the match
  was also case-sensitive. The target started at "none" and had to be picked for every new session.
- **Decision:** `Dialogs.Confirm` with a typed confirmation: Enter in the box confirms once `Dialogs.TypedMatches` (trimmed,
  case-insensitive) holds and otherwise does nothing; Cancel is no longer the default there; a "✓ matches" hint shows.
  `TerminalBridge.Following` (default true): `Follow(view)` from `MainWindow.OnActiveTab` and `Terminals.Front` (the app
  registers the active tab's terminal) makes the terminal in front the target, picking up a reconnected session too;
  `Choose` (an explicit pick or none, or an approved AI switch) stops following; "↻ Follow the terminal in front" in the
  dropdown resumes it. `HoldTarget()` (taken for every agent run) freezes the target until the run ends.
- **Consequences:** SelfTestAgent checks following, the explicit pick and "none" sticking, the hold, and the typed
  matching; the dialog itself is never shown in the self-test. PRODUCTION targets still review every command.

### ADR-090 Post-quantum across the app: hybrids everywhere something is saved or travels (2026-10-24, 0.6.12)
- **Context:** DarkCipher0x: "implement post-quantum across the entire app", "make sure everything that is saved is post
  quantum encryption", "make sure its still seamless with Windows Hello". The threat is "harvest now, decrypt later":
  ciphertext or public keys captured today, broken by a future quantum computer.
- **Decision:**
  1. **Primitives** (`Dcx.Core\Services\Pqc.cs`): ML-KEM-768 (FIPS 203) and ML-DSA-65 (FIPS 204) from BouncyCastle 2.7.0,
     the version SSH.NET 2026.0.0 already pins (now a direct Core reference, same lock-file hash). Works the same on
     Windows 10 and 11. Private keys are kept as seeds (64 / 32 bytes). Never used alone: `Pqc.Combine` (HKDF-SHA256 over
     the classical and post-quantum secrets) makes every key exchange a hybrid; signatures are hybrid by requiring both.
  2. **At rest:** AES-256-GCM is quantum-safe (Grover leaves 128-bit security). Of the data key's protectors, DPAPI and the
     master password (PBKDF2 + AES) are symmetric; the Windows Hello wrap (a key derived from an RSA / ECC signature)
     wasn't, since the Hello public key is readable from a disk image. It now derives from the signature AND a random
     32-byte secret sealed with DPAPI for this Windows account (`HelloWrap.Pepper`, info "dcx-hello-kek-v2-pq"); the
     DPAPI half opens silently, so unlocking is still one Hello prompt. A pre-0.6.12 wrap opens and is re-sealed on the
     next Hello unlock; if DPAPI can't open the secret (another account), the master password is the fallback as before.
  3. **SSH** (`Dcx.Connectivity\Ssh\SshPq.cs`): `BuildConnectionInfo` reorders the key exchanges so
     `mlkem768x25519-sha256` and `sntrup761x25519-sha512(@openssh.com)` come first; `PqSshRequired` (admin-protected,
     policy) leaves only them and turns "no matching key exchange" into a plain explanation. The session strip shows the
     negotiated exchange ("post-quantum …" / "classical …").
  4. **Shared library** (`LibraryCrypto.cs`): `KemKey` (P-256 ECDH + ML-KEM-768) and `SigKey` (P-256 ECDSA + ML-DSA-65)
     replace the raw curve objects for members and audit generations; public / private / signature forms are
     `pq1:<classical>.<post-quantum>` in the same fields, so formats, canonical strings and fingerprints carry them
     unchanged. A hybrid wrap adds `Wrap.Kem` (in the canonical form when present) and derives the key from both
     secrets bound to the ephemeral key, the recipient's whole public key and the ML-KEM ciphertext. A hybrid key refuses a
     classical wrap or a classical-only signature. New libraries, members and audit generations are hybrid; classical
     members keep working until they join again.
  5. **Updates** (`Updater.cs`, `tools\sign-update.ps1`): a second detached signature, `update.json.pq.sig` (ML-DSA-65),
     by a new release key created once with `-NewPqKey` (32-byte seed DPAPI-protected in
     %LOCALAPPDATA%\DarkCipher\keys, never in the repository; CNG can't hold ML-DSA keys yet) and pinned as
     `ReleasePqKey`. All three update paths require both signatures. The ECDSA release key is unchanged (never replaced).
  6. **Not post-quantum, by design or outside the app:** the add-on bridge (a local pipe: recording it means being on the
     PC already; the add-on SDK would need BouncyCastle), TLS to vendors (Windows SChannel), Remote Desktop, SSH host /
     user key signatures (no OpenSSH post-quantum signature yet), Entra tokens. Listed in Settings → Security &
     encryption → Post-quantum and in the security report, never claimed.
- **Consequences:** SelfTestPqc (primitives, library hybrids with downgrade / tamper attempts and old-key
  compatibility, SSH order and requirement), SelfTest.UpdatesAndServices (both signatures, with test keys through the
  `Updater.TestKeys` seam), SelfTestProtected (Hello hybrid, one prompt, legacy upgrade), and LibraryTests end to end on
  hybrid keys. The new release key needs escrow like the ECDSA one.

### ADR-091 Docs: a Markdown editor with folders and pins (2026-10-24, 0.6.12)
- **Context:** DarkCipher0x: "a powerful but lightweight and efficient markdown editor (separate from notes), but allows me
  to pin to servers or clusters etc, and store in organised directories in a db".
- **Decision:** `Dcx.Core\Docs\DocStore.cs`, the notes model: each document its own encrypted file (`doc-<id>.dcx`),
  folders as '/'-paths on the document plus `docs-folders.dcx` for empty ones (8 levels, cleaned of `..`), pins as
  `host:` / `hostfolder:` / `infra:` keys (a host also sees its folders' documents), revisions, trash, search, and
  backups with notes. `Views\Docs\MarkdownRenderer.cs` is our own Markdown → FlowDocument renderer (no HTML engine, no new
  library): only https / mailto links are live and go through a confirmation and offline mode, images are never
  fetched, raw HTML is text; theme and type-ramp resources throughout. `Views\Docs\DocsView.cs`: library tree, editor
  with split / edit / preview, toolbar and shortcuts, smart lists, outline, revisions, import / export; "📄 Docs" in the
  host and infrastructure menus (`MainWindow.OpenDocs`).
- **Consequences:** SelfTestDocs (store, renderer, screen; renders docs.png). Cloud-account pins were left out (the
  accounts live in the cloud modules, not Core); `DocStore.PinNames` is the hook if they're wanted later.

### ADR-092 .NET 10 (2026-10-24, 0.6.13)
- **Context:** .NET 8 support ends on 10 November 2026, two weeks after the public beta. A security tool must not ship on
  an unpatched runtime.
- **Decision:** every project targets `net10.0-windows10.0.19041.0` (the guide and MSIX tools `net10.0`), built with
  SDK 10.0.402; .NET 10 is the LTS release, supported until November 2028. Microsoft's packages move with it
  (System.IO.Ports, System.Management, ProtectedData and DirectoryServices.Protocols 10.0.13); ProtectedData is dropped
  from the app project (the framework already brings it, NU1510). WPF on .NET 9+ has its own `System.Windows.ThemeMode`,
  so files that use ours alias it (`using ThemeMode = DcxManager.Services.ThemeMode;`) and `App` (where the name means
  the inherited property) spells it `Services.ThemeMode`. Certificates from bytes load through `X509CertificateLoader`
  (Azure .pfx, self-test certificates); `X509Certificate.CreateFromSignedFile` has no loader replacement and stays,
  warning suppressed, only to read the Authenticode signer of a file WinVerifyTrust has just verified. Socket reads the
  new CA2022 analyser flagged read at least one byte explicitly.
- **Consequences:** 0 errors, 0 warnings; the full and AI-off self-tests pass unchanged. No data format or setting
  changed, so 0.6.12 data opens as-is.

### ADR-093 Bitwarden: the vault and Secrets Manager through Bitwarden's CLIs (2026-10-24, 0.6.14)
- **Context:** DarkCipher0x: "id also like to integrate bitwarden support" (both products, in the launch release). Hosts
  should sign in from a Bitwarden vault the way they do from BeyondTrust Password Safe.
- **Decision:**
  1. **Bitwarden's own CLIs, not the vault's API.** The Password Manager's client API is internal to Bitwarden and needs
     their end-to-end crypto re-implemented (KDFs, key hierarchy, EncString formats); the CLIs `bw` and `bws` are the
     supported way in for other programs and do all decryption themselves. `bw serve` was rejected: its local REST API
     has no authentication, so any process of the user could read an unlocked vault.
  2. **The CLI is verified before every start** (`Dcx.Connectivity\Bitwarden\BitwardenCli.cs`): Authenticode through
     `Services\Authenticode.cs` (moved out of Updater) with the signer's name one of Bitwarden's, or a SHA-256 in the
     admin-protected `BitwardenCliPins`; the file is held open with writers locked out from the check to the process start.
     Only `.exe` files (the npm `bw.cmd` runs Node from PATH and is refused).
  3. **Secrets only as environment variables** of that one child process (`BW_CLIENTID` / `BW_CLIENTSECRET`, `BW_PASSWORD`
     via `--passwordenv`, `BW_SESSION`, `BWS_ACCESS_TOKEN`): command lines are visible to every process and recorded by
     process auditing. The inherited environment is cleaned (`BW_*`, `BWS_*`, `BITWARDENCLI_*`, `NODE_*` except
     `NODE_EXTRA_CA_CERTS`), `bw` gets `BITWARDENCLI_APPDATA_DIR` = a folder of the app's own and `--nointeraction`.
  4. **Sign-in with the personal API key** (no second factor or captcha can stall a program), **unlock with the master
     password** asked when needed (stored only when "Remember on this PC" is ticked, ADR-100); the session key lives in memory (`BitwardenVault`) until
     `BitwardenLockMinutes` unused, the app lock (`AppLock.Lock`) or exit (`App.OnExit`). Changing the server signs out first.
  5. **Lists without secrets** (`VaultItem`, `SmSecret` have no secret fields; logins and SSH keys only, notes / cards /
     identities never listed); a value is read per use (`SecretAsync`, `ValueAsync`). Hosts store `BitwardenItem`
     ("pm:" / "sm:" + id + a cleaned label); `Services\BitwardenSessions.cs` builds the in-memory `HostEntry.Transient`
     the connectors already use (SSH password or key, Remote Desktop, VNC). Copies go through `Views\SecretClipboard.cs`
     (excluded from clipboard history and cloud sync, cleared after `BitwardenClipboardSeconds`).
  6. **Policies and approvals:** every setting is a Group Policy (category Bitwarden); `bw.enabled`, `bw.server`,
     `bw.sessions`, `bw.copy` and `bw.pins` need admin approval. Offline mode governs sign-in, sync and every `bws` call;
     reading an unlocked vault's cache stays allowed.
- **Consequences:** SelfTestBitwarden (the runner on a real process with a copy of cmd.exe as bw, the protocols against
  scripted CLIs, sessions, screens). Not run against a live Bitwarden account in this build (testers welcome). Vault
  items are read-only: nothing is created or changed in Bitwarden.

### ADR-094 UniFi: the Network Integration API and Site Manager (2026-10-24, 0.6.16)
- **Context:** DarkCipher0x: "integrate unifi management with their API key … a very mature API integration".
- **Decision:** two Infrastructure providers on Ubiquiti's official API-key APIs (no username / password cookie API):
  1. `unifi` (`Dcx.Infra\Infra\UniFi.cs`): the Network Integration API (Network 9.0+, 73 operations in 10.6). Reached on the
     console's address or through Ubiquiti's Cloud Connector (`api.ui.com/v1/connector/consoles/{id}/…`); the API root is
     discovered (`/proxy/network/integration/v1`, a self-hosted server's `/integration/v1`, the connector's two forms).
     The key goes only in `X-API-KEY`; https only; a console's self-signed certificate is pinned on first use (TlsPin, the
     same as Proxmox); the connector uses Windows' trust; no redirects (Offline.Handler). Inventory: sites, devices with
     details and latest statistics (ports, PoE, radios, uplink, uptime, CPU / RAM), pending adoption, clients, networks,
     WiFi, WANs, VPN servers and site-to-site tunnels, firewall zones and policies, ACL rules, DNS policies, traffic lists,
     vouchers, LAGs / MC-LAG / stacks; paged 100 at a time up to a cap that is said in a note. Changes only with "Allow
     changes", each verified by re-reading: restart (a new uptime), PoE power-cycle (PoE back up), adopt, unadopt, guest
     access, vouchers, WiFi / network on-off (GET → PUT without read-only fields), user firewall policies (PATCH);
     system-defined items and the default network are refused.
  2. `unifi-cloud` (`UniFiCloud.cs`): the Site Manager API, read-only fleet view (consoles, site health counts, devices
     with firmware status, ISP metrics, SD-WAN), `nextToken` paging, one 429 waited out; "Add as a UniFi Network
     provider" creates a `unifi` provider through the Cloud Connector with the same key (`ProviderConfig.ConsoleId`).
  3. Every id from the API is a GUID before it goes in a path (`UniFiProvider.Ids`); answers are parsed strictly (no
     duplicate keys, depth 64) and read through `At` (objects only), because list and detail shapes differ.
- **Consequences:** SelfTestUniFi (fake console and api.ui.com), pentest probes, fuzzing (`unifi-inventory`,
  `unifi-sitemanager`, `unifi-error`). Not run against a live console in this build (testers welcome).

### ADR-095 Pre-launch fixes: AI tool-call closings, Bitwarden set-up, sidebar sections (2026-10-24, 0.6.16)
- **AI Engineer:** `AiAgent.TaggedCalls` replaces the `<tool_call>…</tool_call>` regex: the JSON after the tag is read by
  matching braces and any closing a small model writes is taken (DarkCipher0x's screenshot: phi-4-mini closed with
  `}}>` and nothing ran, even after the correction). Also used when native tools were given.
- **Bitwarden:** the screen opens with a set-up checklist (CLI, API key, vault, Secrets Manager) and an "Install Bitwarden
  CLI" button (`winget install --id Bitwarden.CLI --scope user`); Unlock checks bw and the API key before it asks for the
  master password (DarkCipher0x tried it without bw and nothing said why). Bitwarden moves to VAULT.
- **Sidebar:** TOOLS (File commander, Snippets & multi-run, Tunnels, LAN discovery, Network & mail toolbox) and
  DOCUMENTS (Notes, Docs, Incidents, Reports); Help moves to SYSTEM, so the AI section exists only with the AI module.
  Saved orders keep working (new sections slot in after SERVERS).

### ADR-096 Vault credentials kept in Bitwarden or Password Safe, used everywhere (2026-10-24, 0.6.17)
DarkCipher0x: "the password safe and bitwarden creds to be able to be passed to all portions of the software, so hosts,
hosts under cloud providers, infra, portals, etc". Until 0.6.16 a Bitwarden item or Password Safe account could only be
linked to a host, and only its session tabs used it.
- **A vault credential can be external.** `Credential.External` holds `bw:pm:<id>|label`, `bw:sm:<id>|label` or
  `ps:<system>:<account>|label`: ids and a label only, `Secret` stays empty. VAULT → Bitwarden (logins, SSH keys,
  secrets) and VAULT → BeyondTrust (managed accounts) add one with "＋ Add as a vault credential" (once per item). It is
  then picked like any credential: hosts (and cloud hosts), sudo, infrastructure providers, portals, VPN keys, the
  quick-connect default key, add-on credential requests the user approves.
- **One seam.** `ExternalCredentials` (Core) resolves every use: `Use` / `UseAsync` by id, `Resolve` for a credential in
  hand. Every place that read `Secret` from the vault now goes through it: `SshConnector.CredentialFor` (SSH, SFTP,
  Files, Manage, tunnels, checks, Remote Desktop, VNC, IDE links), `RemoteExec.SudoPassword`, portal "Fill sign-in",
  `VpnManager`, and every infrastructure provider. The resolver itself (`CredentialSources`, app) reads Bitwarden through
  the existing CLIs (master password asked when the vault is locked) and Password Safe through a **lease**: one checkout
  shared by every use until it runs out, then checked in by the existing timer, the Password Safe screen or app exit.
  What was read is an in-memory copy (External cleared, never saved), held for 2 minutes at most (Bitwarden) or 30 s
  (Password Safe, the lease answers after that), forgotten when the vault or the app locks.
- **Host links reach every use.** A host linked straight to an item (0.6.14) or account (0.5.0) now signs in with it
  outside its session tabs too (`ExternalCredentials.LinkedTo`): Files, Manage, tunnels, sudo, checks, its portals.
- **Unattended never asks.** `ExternalCredentials.Background()` (an AsyncLocal scope) marks dashboard and scheduled
  checks, the infrastructure screen's auto-refresh and the server dashboard's polling: a locked Bitwarden vault or a
  Password Safe account without an open checkout fails that check with a reason instead of prompting. Check now asks.
- **Infrastructure is signed at send time.** Providers that set their auth header in the constructor (Proxmox,
  Satellite, UniFi, UniFi Site Manager, Unraid) now wrap their handler in `CredentialAuthHandler`, which reads the
  credential when a request is sent: building a provider to draw a screen never prompts, an edited credential is picked
  up, and the background scope flows into the send. The others (Hyper-V, vCenter, Synology, DCX portal) read it at
  sign-in with `UseAsync`. Detect checks accept an external credential without reading it.
- **Synchronous callers.** `Resolve` runs the resolver off the caller's context; on the UI thread it keeps the
  dispatcher pumping (`WaitOnUi`, set by `WpfUi`) so the unlock prompt can show; prompts are marshalled to the UI thread.
  A cancelled prompt answers the uses queued behind it for 10 s.
- **Policies before the hold.** Bitwarden off, `BitwardenAllowSessions` off or Password Safe checkouts off refuse even a
  value held in memory.
- **Shared libraries never point into this PC's vaults.** Publishing leaves external credentials and hosts' direct links
  behind; loading a library clears `External` on its credentials and both links on its hosts (a member could otherwise
  publish an item that makes this PC read its own Bitwarden item and sign in to a host the member chose); the resolver
  refuses a shared external credential and `LinkedTo` ignores shared hosts as a backstop.
- **Not here:** a key kept in Bitwarden isn't exported to a file (export it from Bitwarden); the credential editor isn't
  opened for an external one (rename only).
- **AI Engineer:** a reply that claims it can't act ("As an AI … I am unable to execute commands") before any tool ran
  is answered like a described call: the model is told it can, and asked for the call in the `<tool_call>` format
  (DarkCipher0x's fourth screenshot, phi-4-mini). First-person refusals only (`AiAgent.ClaimsNoAccess`).

### ADR-097 The AI deploys Vultr servers (2026-10-24, 0.6.17)
DarkCipher0x: "ask it to deploy a new server with the name and spec that i choose. If it doesnt match the spec, choose the
cheapest one available for the OS described. Then deploy it to the VPS and confirm".
- **One tool, `vultr_deploy_server`** (operator capabilities, so the AI Engineer and the operator both have it): name and
  OS required; region, size (vCPU, RAM, minimum disk), an exact plan id and SSH key names optional.
- **The order comes from Vultr's own lists** (`Dcx.Cloud\Vultr\VultrDeploy.cs`, pure and tested without a network): the OS
  from `/os` (every word of the request in the name; an LTS release first, then the newest; ARM only when asked; apps,
  ISOs, snapshots and backups aren't operating systems); the region by id, city or country (none given → where the
  account's servers mostly are, else the plan's first location); the plan from `/plans`: an exact id when offered, else
  the cheapest plan matching the size exactly (vCPU and RAM, disk at least), else **the cheapest plan that runs the OS**
  (DarkCipher0x's rule), with IPv6-only (`-v6`) and GPU plans left out and Windows from 2 GB. Anything unknown is refused with
  the close matches, before any approval.
- **Approval shows the whole order**: name and hostname, OS, region, plan with its size and monthly price, why this plan
  (and that it didn't match when it didn't), SSH keys or "the root password goes into your vault", and that it is billed.
  `AiAllowServerDeploy` (Group Policy, on by default) can turn the tool off.
- **Created once.** POST /instances is never retried (a slow answer must not become two servers). Then the instance is
  polled until active, running and installed with an IPv4 address (10 minutes at most), saved as a host (once per
  address; Remote Desktop for Windows) and its SSH / Remote Desktop port checked.
- **Secrets never reach the AI.** Vultr's generated root password (only in the create answer) goes straight into a vault
  credential linked to the new host; the AI's answer names the credential only. The audit log records the order
  (plan, OS, region, price, id), never the password.
- Other providers (Hetzner, Contabo, Azure, GCP) can follow the same shape later; Vultr first, as asked.

### ADR-098 The default local model fits the PC: Qwen2.5 on a GPU (2026-10-24, 0.6.17, AiDefaults revision 6)
DarkCipher0x: "change the LLM model if there is something better... id like one that supports all CPU/NPU/GPU, so that it
defaults to foundry local with whatever is best suited for the PC being used". Revision 5 put phi-4-mini on every PC; in
practice it writes tool calls as text, closes them wrongly or refuses (three of his screenshots).
- Still **Foundry Local**, still an **alias** (Foundry Local loads the GPU, NPU or CPU build that this PC has).
- `AiDefaults.Best`: a GPU with 12 GB+ → `qwen2.5-14b`; 6 GB+ → `qwen2.5-7b` (both call tools reliably); an NPU →
  `phi-4-mini` on the NPU; a 4 GB GPU → `phi-4-mini`; a CPU with 30 GB+ RAM and 12+ threads → `qwen2.5-7b`; other CPUs
  → `phi-4-mini` (a lighter model is still only suggested, never chosen by default, 0.5.1 #14).
- `FoundryManager.Recommend` (the model guide and set-up's fallback) follows the same order against the PC's own
  catalogue. Set-up swaps a GPU-tier default that the PC offers only as a CPU build, or a model it doesn't offer at all,
  for the catalogue's pick and says why.
- Applied once (revision 6) and only to defaults nobody changed: an earlier default (phi-4-mini included) is upgraded, a
  model or engine the user picked is kept.

### ADR-099 Finding credentials: one search over the vault, Bitwarden and Password Safe (2026-10-24, 0.6.19)
DarkCipher0x: "it doesnt have the option for me to search for any bitwarden credentials when editing a host or portal
entry … make it easy to search for bitwarden/password safe creds, as there could be a lot in the persons library".
ADR-096 made such credentials usable everywhere, but they only reached a credential box after VAULT → Bitwarden → Add.
- **`CredentialSearch`** (service, no UI): sources this vault, Bitwarden (with the integration and its use as credentials
  allowed), Secrets Manager (once a token is set) and Password Safe (BeyondTrust with checkouts allowed). Each outside
  source is listed once (names, user names, addresses / fingerprints, ids: never a secret) and held 5 minutes, dropped
  with the vault or app lock. `Filter`: every word of the query in the name, user name or address, the kind asked for
  (keys for key sign-in), best first, at most 500 rows. `Use`: the vault credential, or the external one already
  pointing at the item, or a new one (one per item).
- **`CredentialPicker`** (window): source choice (or All), live filtering, ↻, the "in vault" mark; errors (a missing
  CLI, a locked vault the user didn't unlock) in its status line, never a crash. Never shown in the self-test.
- **🔍 Find…** next to the host editor's credential and sudo boxes (fills the user name; a picked credential or a typed
  password replaces a direct Bitwarden / Password Safe link, which would otherwise win; the editor says when a host is
  linked), each infrastructure provider's credential, a portal's sign-in credential and an OpenVPN profile's sign-in;
  VAULT → Credentials → "⛓ From Bitwarden / Password Safe…".
- Found by the pre-release check and fixed with it: infrastructure Save refused external credentials (it checked for a
  stored secret: now `ExternalCredentials.HasSecret`); deleting a VPN profile deleted its sign-in credential (now only
  its imported keys); the AI deploy saved Vultr's root password only after the wait (now before, so ■ Stop can't lose
  it); a background sudo lookup with a locked vault threw instead of running without a password; the key actions
  (copy / upload public key, export) are off for a key kept in Bitwarden. Foundry Local's warm-up now loads the model
  (it only resolved its id), started when the question box is clicked into.

### ADR-100 Bitwarden speed, a remembered master password, fill sign-in in steps, TOTP (2026-10-24, 0.6.20)
DarkCipher0x tested 0.6.19: "the bitwarden search is very very slow. it should also allow you to save your master
password encrypted, so that it remembers the next time you use it and launch ServerManager. … the "Fill sign-in" isnt
working on the webpage … there should be an option to copy password and username and TOTP". And a qwen2.5-14b screenshot
where the AI wrote `terminal_run` then its arguments as JSON on the next lines, and nothing ran.
- **Speed.** Every Bitwarden search started bw, which decrypts the whole vault each time, and unlocking waited for a
  network sync. `BitwardenVault` now reads the item list (names, user names, addresses; never a secret) once per unlock
  from bw's local cache, in the background right after the unlock, holds it until the vault locks and searches it in
  memory (any words, any case: what `--search` did). The sync follows in the background and reads the list again;
  `ItemsChanged` refreshes open lists (VAULT → Bitwarden filters as you type; the 🔍 Find picker; its ↻ syncs first).
- **Remembered master password** (opt-in): a "Remember on this PC" tick box at the unlock prompt. Kept with the app's
  other integration secrets (`ObserveSecrets` "bw:master": AES-256-GCM in the store, never in the registry, a policy
  or a backup), bound to the server URL it unlocks. Used without asking by every unlock and, in the background, at
  start-up and after the app unlocks (`BitwardenSessions.AutoUnlockAsync`, never a prompt). A password bw refuses
  ("Invalid master password": changed since) is forgotten and asked for again; the server changing, signing out and
  "Forget" delete it. Policy `BitwardenAllowRemember` (admin-protected, default on): off, it is unused at once and
  deleted at the next unlock. Trade-off, stated in SECURITY-REPORT §4f: whoever can open the app (its own lock, or the
  Windows account while no master password is required at start) can use the vault without typing its password.
- **Fill sign-in.** The page is probed first (no secret in the probe); the password only goes into a page that shows a
  password box. Inputs are found in open shadow roots and same-origin frames too. A sign-in in steps (Synology DSM,
  Microsoft, Google) gets the user name, then, watched for 90 s on the bookmark's origin only, the password when its
  box appears and the TOTP code when a code box does. A page with no form (or a cross-origin frame) shows ⧉ User name
  / ⧉ Password / ⧉ TOTP code, also on the ▾ beside Fill sign-in: copied the password-manager way (out of clipboard
  history and cloud sync, cleared after the set seconds), under the Bitwarden copy policy for Bitwarden credentials and
  the item lock for vault ones (the fill now checks the item lock too).
- **TOTP.** `Totp.Parse`: otpauth://totp links (SHA1/256/512, 6–10 digits, period), bare Base32, Bitwarden's steam://.
  A vault credential has an optional TOTP secret; a Bitwarden login's is read with its password (never listed, never
  stored here).
- **AI.** `AiAgent.CallsOnly` reads a tool's name followed by its arguments as a JSON object (and the same call written
  twice over); `MentionsToolUse` counts it, so a reply that still isn't read gets the correction. The fuzzer then found
  that a repeated JSON key threw later, outside the parser (JsonNode reads lazily): `AiAgent.SafeParse` opens every
  object inside the catch, for every written-call path.
- **Toolbar (0.6.21).** The copy menu is "⧉ Copy ▾" in the small button style, sized to its label (a fixed 24 px width with
  the big style's padding cut it off, DarkCipher0x's screenshot); below 760 px the sign-in buttons show their icons only and
  below 600 px the profile pill hides, so they stay beside the address. The self-test measures every toolbar button at four
  widths (`BrowserView.ClippedToolbarForTest`).

### ADR-101 Saved hosts follow their cloud servers (2026-10-25, 0.6.24)
- **Context:** DarkCipher0x destroyed a Vultr server and built a new one of the same name; the Vultr screen showed the new
  IP but the saved host kept the old one. Linked hosts (ADR-020) were only reconciled by the manual Hosts → Import from …,
  and a rebuilt server has a new instance id, so nothing ever matched it to the old host.
- **Decision:** `HostsView.SyncLinked(provider, account, list)` takes an account's complete instance list: a linked host
  whose instance is present follows a changed public IPv4 (0.0.0.0 while building is ignored); a linked host whose instance
  is gone is relinked to the one unlinked instance of the same name (case-insensitive; zero or several = reported as gone,
  never guessed). Shared-library hosts, padlocked hosts and a `host.edit` restriction leave the host unchanged (reported
  as locked). `HostsView.FollowCloud` saves and audits ("app" actor, old → new address) and does nothing for a viewer
  profile. It runs when the provider's own Instances screen loads (`CloudResourceView.FollowHosts`: only the provider's
  `InstanceDef` without a parent, since only that list is complete) and when the dashboard's cloud card loads; `Reconcile`
  (Import) runs it first, so a rebuilt server is not added as a second host. Hosts whose server is gone are offered for
  removal once per session from the Instances screen (`host.delete` restriction and padlocks respected; Cancel keeps them).
  Setting / Group Policy `CloudSyncHosts` (default on, not a protected setting: the address comes from the signed-in
  provider API, and SSH's host-key check still asks on the new server's first connection).
- **Tests:** SelfTestContabo §6 against FakeContabo: a rebuilt server of the same name takes the host over (new id, new
  address, one Moved), Import afterwards adds nothing; two servers of that name leave the host reported as gone.

### ADR-102 FreeFileSync-style folder sync: jobs, Two-way memory, versions, schedules (2026-10-25, 0.6.26)
- **Context:** DarkCipher0x asked for FreeFileSync's capabilities in File Commander, "fully flexible" with every option
  switchable. The 0.3.0 compare/sync window had three modes, newer-wins Two-way (a deletion came back as a copy), no
  filter, permanent deletes on servers and nothing saved or scheduled.
- **Decision:** the engine stays in Dcx.Connectivity/Files, one small file per concern:
  `SyncFilter` (include/exclude lines with * ? anchored / and folder / patterns, size and age limits; the versions
  folder and `.dcxpart` are always excluded), `SyncJob` (`SyncOptions`: compare by time+size with tolerance and hour
  shifts / content / size; variants Two-way, Mirror, Update, Custom per `DiffState`; conflict policy; deletion
  Recycle Bin / Versioning / Permanent; version naming; guard — `SyncPairDef` with optional own options, `SyncJobDef`
  with the schedule), `SyncDatabase` (Two-way's last-synced state per pair, `syncdb-<hash>.dcx`, encrypted; only pairs
  that ran update it, untouched entries are kept), `SyncVersioning` (moves into the versions folder by naming style,
  prunes by age and count; a server has no Recycle Bin, so it falls back to versions; Permanent on a server only with the
  protected setting `files.syncPermanentDelete`, UAC), `SyncJobStore` (`syncjobs.dcx`, encrypted), `SyncRunner` (compare,
  plan and execute through the existing `TransferQueue`, which retires an overwritten or deleted target via
  `TransferJob.Retire`), `SyncScheduler` (interval / daily / weekly timer and real-time FileSystemWatcher with a debounce
  and quiet periods so its own writes don't retrigger; server sides are polled). `FolderSync.Plan` folds a one-sided
  folder into one step only when every file in it is covered and goes the same way; a folder with filtered-out content
  (`Partial`) is never deleted whole. Unattended runs (schedule, real-time, Run now) leave conflicts alone, refuse viewer
  profiles, stop a pair on a missing folder and when deletions exceed `MaxDeletePercent` of a side (more than 10).
  `TransferQueue.Jobs` is now changed through `Ui.Thread` because the scheduler queues from a pool thread.
  UI: `SyncWindow` (pairs, "Settings for" job/pair, `SyncOptionsEditor` tabs, preview with per-row actions, kind toggles,
  exclude), `SyncJobsWindow`, Settings → Folder sync card; AppSettings `FileSync*` are Group Policies.
  Easy first, flexible second (DarkCipher0x: "easy to use and intuitive… easy to do regular quick scp functions"): File
  Commander's F-key bar has "⇄ Sync ▾" (`CommanderView.QuickSync`) — Update / Mirror either way or Two-way of the two
  panes in one step with one confirmation (`SyncWindow.ConfirmRun`, shared with the window: totals, where deleted files
  go, typed deletion count, PRODUCTION banner), plus the window and saved jobs. The window compares as soon as it opens,
  has one-click "what to do" buttons with a one-line summary (a click re-compares), and keeps the rest under a collapsed
  "More options".
- **Tests:** SelfTestFolderSync on scratch folders: filter patterns and partial folders, the compare variants and Custom,
  Two-way over three runs (deletions both ways, changed-on-one-side, a deleted folder as one step, conflict skipped then
  right-wins, deleted+changed kept), versions per file / per run / replace and pruning, Mirror and Update, the deletion
  guard and a missing folder, jobs and the database encrypted at rest, schedule maths, Run now through the scheduler, and
  both windows built without being shown.
