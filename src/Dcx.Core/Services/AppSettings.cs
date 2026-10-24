namespace DcxManager.Services;

/// <summary>User preferences and Vultr API keys — encrypted in settings.dcx.</summary>
public sealed class AppSettings
{
    private const string FileName = "settings.dcx";
    /// <summary>The darkcipher0x brand teal (the theme's accent unless the user picks another).</summary>
    public const string DefaultAccent = "36D2CB";

    public string Theme { get; set; } = "System";
    public string AccentColor { get; set; } = DefaultAccent;

    // Terminal
    public int TerminalFontSize { get; set; } = 14;
    public int Scrollback { get; set; } = 10000;
    public string DefaultUsername { get; set; } = "root";
    public int ConnectTimeoutSeconds { get; set; } = 15;
    /// <summary>Offer only post-quantum key exchanges for SSH (0.6.12, ADR-090): a server without one is refused instead of
    /// connected classically. Off by default (post-quantum is always preferred anyway). Admin-protected to turn off.</summary>
    public bool PqSshRequired { get; set; }
    /// <summary>Key tried first for quick connect (user@host) when no saved host matches.</summary>
    public string? DefaultKeyCredentialId { get; set; }

    // Resilience (Phase 1 stabilisation)
    /// <summary>Re-establish a terminal automatically after an unexpected drop (network loss, server restart).</summary>
    public bool TerminalAutoReconnect { get; set; } = true;
    /// <summary>Opening something that is already open in a tab — a host's terminal, Remote Desktop, VNC desktop, files,
    /// commander or local shell — switches to that tab instead of opening another (0.5.9, DarkCipher0x: "Id prefer it to open the
    /// same active connection i have open ... otherwise the tab bar gets wayyyy to over crowded"). Off: every open makes a
    /// new tab, as before. Shift held, or "⧉ New connection (another tab)", always opens another.</summary>
    public bool ReuseOpenTabs { get; set; } = true;
    /// <summary>Cap for the exponential reconnect back-off, in seconds.</summary>
    public int AutoReconnectMaxSeconds { get; set; } = 30;
    /// <summary>After an unclean shutdown, offer to reopen the tabs that were open (terminals reopen disconnected).</summary>
    public bool RestoreWorkspace { get; set; } = true;

    // Vultr
    public List<VultrAccount> VultrAccounts { get; set; } = new();
    public string? ActiveVultrAccountId { get; set; }
    /// <summary>Contabo API accounts (ADR-020), encrypted with the rest of the settings.</summary>
    public List<ContaboAccount> ContaboAccounts { get; set; } = new();
    public string? ActiveContaboAccountId { get; set; }
    /// <summary>Hetzner Cloud projects (ADR-028): one API token each, encrypted with the rest of the settings.</summary>
    public List<HetznerAccount> HetznerAccounts { get; set; } = new();
    public string? ActiveHetznerAccountId { get; set; }
    /// <summary>Azure subscriptions (0.2.0): an app registration's certificate each, encrypted with the rest of the settings.</summary>
    public List<AzureAccount> AzureAccounts { get; set; } = new();
    public string? ActiveAzureAccountId { get; set; }
    /// <summary>Google Cloud projects (0.2.0): a service-account key each, encrypted with the rest of the settings.</summary>
    public List<GcpAccount> GcpAccounts { get; set; } = new();
    public string? ActiveGcpAccountId { get; set; }

    // Ollama
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen2.5:7b";
    /// <summary>Reach Ollama through an SSH tunnel to this saved host (Ollama on a GPU server).</summary>
    public string? OllamaTunnelHostId { get; set; }
    public bool AiAutoApproveReadOnly { get; set; } = true;
    /// <summary>The AI may order new cloud servers (vultr_deploy_server, 0.6.17); each order still needs the user's approval.</summary>
    public bool AiAllowServerDeploy { get; set; } = true;
    /// <summary>AI Engineer terminal targeting (ADR-028): the user picks a live terminal; proposed commands run there only
    /// after review. Off = the AI only shows commands.</summary>
    public bool AiTerminalBridge { get; set; } = true;
    /// <summary>Command output (masked, bounded) goes back to the AI after a bridged command finished.</summary>
    public bool AiShareCommandOutput { get; set; } = true;
    /// <summary>Upper bound for one bridged command before it is reported as still running.</summary>
    public int AiCommandTimeoutMinutes { get; set; } = 5;
    /// <summary>AI tools may search and read notes and incidents (asks before each read). Off by default: notes are where
    /// people paste secrets (AiContext rule).</summary>
    public bool AiAllowNotes { get; set; }
    public int AiContext { get; set; } = 16384;

    // AI engine (pluggable). Engine ids: see Ai.AiEngines. "off" keeps every offline feature (log digests, rule-based
    // reports, help search) and never calls a model.
    public bool AiEnabled { get; set; } = true;
    public string AiEngine { get; set; } = "npu";
    /// <summary>AI features (0.2.0): whether the AI module loads at start — distinct from <see cref="AiEngine"/> "off",
    /// which keeps the AI's screens with no engine. Null = not decided yet: set once at the first 0.2.0 start (on for an
    /// existing store whose AI was in use, off for a new install). Applies on the next start (Services/AiFeatures).</summary>
    public bool? AiFeatures { get; set; }
    /// <summary>Mirror of the AI features lock (Services/FeatureLock), so a lock removed outside the app is noticed.</summary>
    public bool AiLockSet { get; set; }
    /// <summary>The AI default (Foundry Local with the best model for the hardware) has been applied at least once (Ai.AiDefaults).</summary>
    public bool AiDefaultChosen { get; set; }
    /// <summary>Which revision of the default was last applied (Ai.AiDefaults.Revision).</summary>
    public int AiDefaultRevision { get; set; }
    /// <summary>CPU-only Ollama: worker threads (0 = Ollama default).</summary>
    public int AiCpuThreads { get; set; }
    /// <summary>On battery, don't start AI work by itself (manual requests still run).</summary>
    public bool AiPauseOnBattery { get; set; } = true;
    /// <summary>Mask keys, tokens, passwords and private-key blocks before anything is sent to any model.</summary>
    public bool AiRedactSecrets { get; set; } = true;
    /// <summary>Allow server data to go to a non-local (internet) AI provider. Off = local/LAN/tunnel endpoints only.</summary>
    public bool AiAllowCloud { get; set; }

    // Per-feature switches
    public bool AiFeatureOperator { get; set; } = true;
    public bool AiFeatureLogAnalysis { get; set; } = true;
    public bool AiFeatureReports { get; set; } = true;
    public bool AiFeatureHelper { get; set; } = true;
    public bool AiFeatureClaudeCode { get; set; } = true;
    /// <summary>"✦ Explain with AI" on files (Commander/SFTP) and documents (invoices, billing). Content is only sent when you click it.</summary>
    public bool AiFeatureFiles { get; set; } = true;

    // File commander: last source + folder per pane ("local|C:\path" or "host:&lt;id&gt;|/path").
    public string CommanderLeft { get; set; } = "local|";
    public string CommanderRight { get; set; } = "";
    public bool CommanderShowHidden { get; set; }
    /// <summary>A new File Commander tab reopens the places shown last (0.5.1 #4). Off by default: a new tab connects to nothing,
    /// so no server is ever opened by surprise. Group Policy can set it.</summary>
    public bool CommanderRestoreLast { get; set; }

    // NPU — Foundry Local (Copilot+ PCs). Empty URL = discover with `foundry server status` (`foundry service status` on CLIs before the rename).
    // FoundryModel: an alias (best variant for the hardware) or a full model ID such as qwen2.5-0.5b-instruct-generic-cpu.
    public string FoundryUrl { get; set; } = "";
    public string FoundryModel { get; set; } = "phi-4-mini";
    /// <summary>ADR-026: install, start, repair, download and load Foundry Local without being asked (every step is shown
    /// and audited; offline mode and battery are respected). Off = only the Settings buttons do anything.</summary>
    public bool FoundryAutoSetup { get; set; } = true;
    /// <summary>Users may change the AI engine and model (0.5.1 #14; Group Policy sets this off to keep the one it chose).</summary>
    public bool AiAllowEngineChange { get; set; } = true;
    /// <summary>The app may download AI models (Foundry Local's catalogue). Off: only models already on this PC are used.</summary>
    public bool AiAllowModelDownload { get; set; } = true;
    /// <summary>When the AI Engineer loads on a PC with an NPU that isn't used: "ask" (default) offers to set it up,
    /// "always" does it without asking, "never" stays quiet (the "Remember my choice" answer; Ai.NpuSetupOffer).</summary>
    public string AiNpuSetupChoice { get; set; } = "ask";
    // OpenAI-compatible server (LM Studio, llama.cpp, vLLM, LocalAI, OpenAI, Azure OpenAI, gateways)
    public string OpenAiUrl { get; set; } = "http://localhost:1234/v1";
    public string OpenAiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "";
    /// <summary>The user states that the OpenAI-compatible model reads images (the app can't verify it on such servers).</summary>
    public bool OpenAiAcceptsImages { get; set; }
    // Anthropic Messages API (Claude API, or Ollama's Anthropic-compatible endpoint)
    public string AnthropicUrl { get; set; } = "https://api.anthropic.com";
    public string AnthropicKey { get; set; } = "";
    public string AnthropicModel { get; set; } = "claude-sonnet-5-5";

    // AI speed + agents (ADR-034)
    /// <summary>Show answers as they are written (token streaming) on every engine that can.</summary>
    public bool AiStream { get; set; } = true;
    /// <summary>Most model calls one question may make while it uses tools (plan → call → observe → continue).</summary>
    public int AiMaxSteps { get; set; } = 12;
    /// <summary>Keep the local model loaded this many minutes after you last used the AI (Ollama keep_alive); 0 = unload after each answer.</summary>
    public int AiKeepWarmMinutes { get; set; } = 10;
    /// <summary>Watch memory: under pressure, smaller context, unload after the answer, and unload idle Foundry Local models.</summary>
    public bool AiResourceGuard { get; set; } = true;
    /// <summary>Unload Foundry Local's models when they are idle and memory is tight, or the app's own use ended.</summary>
    public bool AiUnloadIdleFoundry { get; set; } = true;
    /// <summary>AI Engineer: read-only commands run in the chosen target session without a review each time (off by default; never in PRODUCTION).</summary>
    public bool AiAutoRunReadOnlyTarget { get; set; }

    // Claude Code with a local Ollama model
    public string ClaudeCodeModel { get; set; } = "";
    public string ClaudeCodeWorkspace { get; set; } = "";

    // Layout
    public double SidebarWidth { get; set; } = 214;
    public bool SidebarCollapsed { get; set; }
    /// <summary>AI Engineer sidebar (Views/AiDock.cs): "docked" (default), "collapsed", "hidden" or "detached" (own window).</summary>
    public string AiPanelState { get; set; } = "docked";
    /// <summary>Docked width of the AI Engineer sidebar, in DIPs.</summary>
    public double AiPanelWidth { get; set; } = 380;
    /// <summary>The Hosts page's folder pane (0.6.2, DarkCipher0x: "auto collapsing etc INSIDE the tab/window of hosts ... like it
    /// was before, but able to collapse within itself"): "pinned" (open beside the host list, as before) or "auto" (folds to
    /// a narrow strip inside the Hosts page; pointing at it slides the pane out over the list). 0.5.8–0.6.1's "tab" (no
    /// pane beside the sessions) reads as "pinned".</summary>
    public string HostsDock { get; set; } = "pinned";
    /// <summary>0.6.7 (ADR-086; DarkCipher0x: "the terminal windows you open, should open within the tab... so that you can still
    /// pop out the hosts while viewing the terminal ... give the options to change it"): where sessions opened from the
    /// Hosts page go: "hosts" (default: tabs inside the Hosts tab, beside its folder pane) or "tab" (separate tabs).</summary>
    public string HostsOpenIn { get; set; } = "hosts";
    /// <summary>0.6.4 (ADR-082): the host pane in extra windows (other monitors): "pinned" (open, default) or "auto"
    /// (folds to a "Hosts" tab at the window's edge and slides out when pointed at).</summary>
    public string WindowHostPane { get; set; } = "pinned";
    /// <summary>Width of that host pane, in DIPs.</summary>
    public double WindowHostPaneWidth { get; set; } = 250;
    /// <summary>Reopen the extra windows where they were (monitor, position, size) when the app starts.</summary>
    public bool RestoreWorkspaceWindows { get; set; } = true;
    /// <summary>The extra windows open at the last exit (state, not a policy).</summary>
    public List<WindowPlacement> WorkspaceWindows { get; set; } = new();
    /// <summary>Width of the Hosts page's folder pane, in DIPs (0.5.8–0.6.1's host-pane widths 640 / 260 read as 230).</summary>
    public double HostsDockWidth { get; set; } = 230;
    /// <summary>Whole-UI zoom (1 = 100%). Terminals keep their own font size.</summary>
    public double UiScale { get; set; } = 1.1;
    /// <summary>UI font size, text only (1 = 100%, 0.85–1.5): multiplies the type ramp (ADR-015). Terminals are not affected.</summary>
    public double UiTextSize { get; set; } = 1;
    // Typography (Services/Typography.cs applies these; a face that is no longer installed falls back to the default).
    /// <summary>Interface font. Empty = Segoe UI Variable Text / Segoe UI.</summary>
    public string UiFont { get; set; } = "";
    /// <summary>Code / monospace font (addresses, ids, data tables, code). Empty = Cascadia Mono / Consolas.</summary>
    public string MonoFont { get; set; } = "";
    /// <summary>Notes and multi-line editors (SFTP/Commander edit, snippets, notes). Empty = Cascadia Mono / Consolas.</summary>
    public string EditorFont { get; set; } = "";
    /// <summary>Terminal-style accents: buttons, tabs, sidebar, labels and headings in the monospace font (the pre-session-7 look).</summary>
    public bool MonoAccents { get; set; }
    /// <summary>Terminal (xterm) font. Empty = Cascadia Mono / Consolas. Independent of every UI font.</summary>
    public string TerminalFont { get; set; } = "";
    /// <summary>Before 0.1.11: sections the user collapsed. Retired (sections now start collapsed); cleared on load.</summary>
    public List<string> CollapsedNavSections { get; set; } = new();
    /// <summary>Keep the sidebar sections the user expanded across restarts (ADR-028). Off = every start is collapsed.</summary>
    public bool RememberNavExpansion { get; set; }
    /// <summary>Expanded sidebar sections, saved only while <see cref="RememberNavExpansion"/> is on (existing names only).</summary>
    public List<string> ExpandedNavSections { get; set; } = new();
    /// <summary>Screens pinned to the top of the Cloud providers tree (nav keys; pruned to existing screens, at most 20).
    /// Replaced 0.1.x's automatic "recent" list, which older settings files still carry and is ignored.</summary>
    public List<string> CloudPinned { get; set; } = new();
    /// <summary>Sidebar section order (header names); empty = default. Sections missing here keep their default place.</summary>
    public List<string> NavSectionOrder { get; set; } = new();
    /// <summary>0.6.2 (DarkCipher0x: "the items on the left main sidebar, should be rearrangable and pinable"): the order of the
    /// pages inside their sections (page keys, dragged or moved by right-click); empty = the default order. A page always
    /// stays in its own section; pages missing here keep their default place.</summary>
    public List<string> NavItemOrder { get; set; } = new();
    /// <summary>Sidebar sections the user hid (header right-click → Hide, or Settings → Appearance).</summary>
    public List<string> HiddenNavSections { get; set; } = new();
    /// <summary>List row spacing: "comfortable" (default) or "compact".</summary>
    public string Density { get; set; } = "comfortable";
    // Appearance and navigation (0.2.1, the DarkCipher house theme shared across the DarkCipher Systems apps; every value is bounded and
    // corrected on load by ThemeManager / NavLayout, so a hand-edited or older file can never break the look).
    /// <summary>Theme pack: "darkcipher" (default), "graphite", "teal", "highcontrast" (always dark), or the everyday packs
    /// "neutral", "slate", "arctic" with their own light palettes (0.6.9). Packs without one use DarkCipher Light.</summary>
    public string ThemePreset { get; set; } = "darkcipher";
    /// <summary>Status colours: "subtle", "standard" (default) or "strong". Status is always written as text too.</summary>
    public string StatusIntensity { get; set; } = "standard";
    /// <summary>Use the High Contrast preset while Windows high contrast is on.</summary>
    public bool FollowWindowsHighContrast { get; set; } = true;
    /// <summary>Corner radius of controls and cards in px: 0, 2 (default) or 4.</summary>
    public int Corners { get; set; } = 2;
    /// <summary>Animation: "windows" (follow the Windows setting, default), "standard" or "reduced".</summary>
    public string Motion { get; set; } = "windows";
    /// <summary>Sidebar: "expanded" (default), "compact" (tighter rows) or "icons" (icons only, with tooltips).</summary>
    public string SidebarStyle { get; set; } = "expanded";
    /// <summary>The screen opened at start (a sidebar key); empty = Dashboard.</summary>
    public string LandingPage { get; set; } = "";
    /// <summary>Sidebar items pinned to a PINNED section at the top (nav keys, at most 6).</summary>
    public List<string> PinnedNavItems { get; set; } = new();
    /// <summary>Sidebar pages hidden from the sidebar (nav keys; Settings can't be hidden). They still open from links,
    /// menus and quick connect.</summary>
    public List<string> HiddenNavItems { get; set; } = new();
    public List<string> CollapsedSettingsCards { get; set; } = new();
    public string HostsView { get; set; } = "folders";
    public List<string> HostsHiddenColumns { get; set; } = new();
    /// <summary>Terminal right-click: "menu" (context menu; Shift+right-click pastes) or "paste" (PuTTY style; Shift+right-click opens the menu).</summary>
    public string TerminalRightClick { get; set; } = "menu";
    // tmux (0.6.6, ADR-085): the terminal's ⧉ tmux button.
    /// <summary>Install tmux with the host's package manager when it is missing (you are asked first each time).</summary>
    public bool TmuxInstall { get; set; } = true;
    /// <summary>The tmux session the button attaches to or creates (letters, digits, _ . -).</summary>
    public string TmuxSession { get; set; } = "main";
    /// <summary>Your own tmux config, written to ~/.config/dcx-servermanagement/tmux.conf on the host and used by the
    /// button (never your ~/.tmux.conf). Empty: tmux's own defaults / your ~/.tmux.conf.</summary>
    public string TmuxConfig { get; set; } = "";
    /// <summary>Show multi-line, risky or control-character pastes before they reach the shell (Services/PasteGuard.cs).
    /// Hidden control characters are always removed, even when this is off.</summary>
    public bool TerminalConfirmPaste { get; set; } = true;
    /// <summary>Where screenshots go (empty = Pictures\DarkCipher ServerManagement). They are plain PNG files.</summary>
    public string ScreenshotDir { get; set; } = "";
    /// <summary>What Ctrl+Shift+S does: "save", "clipboard" or "both".</summary>
    public string ScreenshotDefault { get; set; } = "both";

    // Updates (Services/Updater.cs). Checks are user-initiated; the automatic weekly check is opt-in (external network).
    /// <summary>Official update manifest; empty = the DarkCipher Systems website. https only.</summary>
    public string UpdateEndpoint { get; set; } = "";
    public bool AutoCheckUpdates { get; set; }
    /// <summary>0.6.1 (ADR-079): "github" (default) = the latest GitHub Release of DarkCipher0x/ServerManagement;
    /// "url" = <see cref="UpdateEndpoint"/>.</summary>
    public string UpdateSource { get; set; } = "github";
    /// <summary>How often the opt-in automatic check runs: "weekly" (default) or "daily".</summary>
    public string UpdateCheckInterval { get; set; } = "weekly";
    /// <summary>With the automatic check on: download and verify a new version in the background (installing always
    /// asks first). Off by default.</summary>
    public bool AutoDownloadUpdates { get; set; }
    /// <summary>0.6.2: a GitHub access token (yours, read-only, for this repository) so updates can be checked while the
    /// repository is private. Encrypted with the rest of the settings; sent only to api.github.com; never in the
    /// registry, a policy or a backup without secrets. Empty = anonymous (a public repository).</summary>
    public string UpdateGitHubToken { get; set; } = "";
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>Set when the installer is started; the next start verifies the running version against it.</summary>
    public string PendingUpdateVersion { get; set; } = "";
    public string PendingUpdateFrom { get; set; } = "";

    // Snapshots (Services/Snapshots.cs, ADR-009): rolling encrypted copies of the store, local only.
    public bool AutoSnapshots { get; set; } = true;
    /// <summary>Automatic snapshots kept (older ones are pruned only after a newer one verified).</summary>
    public int SnapshotKeep { get; set; } = 10;
    public int SnapshotIntervalHours { get; set; } = 24;
    /// <summary>Where snapshots go; empty = "snapshots" inside the store folder (they move with the store).</summary>
    public string SnapshotDir { get; set; } = "";

    // BeyondTrust (0.5.0, plan #16): PRA / Remote Support (read-only) and Password Safe check-out for a session.
    /// <summary>The BeyondTrust integration at all (off by default).</summary>
    public bool BeyondTrustEnabled { get; set; }
    /// <summary>The PRA / Remote Support site (https://host) and its API account's client id (the secret stays encrypted).</summary>
    public string BeyondTrustPraSite { get; set; } = "";
    public string BeyondTrustPraClientId { get; set; } = "";
    /// <summary>The Password Safe / BeyondInsight host (https://host) and the run-as user (the API key stays encrypted).</summary>
    public string BeyondTrustPsHost { get; set; } = "";
    public string BeyondTrustPsRunAs { get; set; } = "";
    /// <summary>Password Safe credentials may be checked out to open sessions.</summary>
    public bool BeyondTrustAllowCheckout { get; set; } = true;
    /// <summary>How long a checkout is requested for (minutes, 1–480); it is checked in earlier when you're done.</summary>
    public int BeyondTrustCheckoutMinutes { get; set; } = 30;
    /// <summary>The pinned SHA-256 of the PRA site's and the Password Safe host's TLS certificates (hex; empty = a
    /// certificate Windows trusts is accepted, a self-signed one is refused until trusted). 0.5.1 #2, admin-protected.</summary>
    public string BeyondTrustPraPin { get; set; } = "";
    public string BeyondTrustPsPin { get; set; } = "";
    /// <summary>Ask for the reason of every checkout (it goes into Password Safe's request and the audit log).</summary>
    public bool BeyondTrustAskReason { get; set; }
    /// <summary>Jump Items open in "console" (your access console, through the client-scripting URL) or "web" (the site's
    /// web access console in the portal browser).</summary>
    public string BeyondTrustJumpClient { get; set; } = "console";
    /// <summary>Default filters (comma-separated words): Jump Groups for the Jump Item list, Jumpoints for sessions and
    /// the Jumpoint list. Empty = everything.</summary>
    public string BeyondTrustJumpGroupFilter { get; set; } = "";
    public string BeyondTrustJumpointFilter { get; set; } = "";

    // Bitwarden (0.6.14, ADR-093): the Password Manager vault and Secrets Manager through Bitwarden's own CLIs (bw, bws).
    /// <summary>The Bitwarden integration at all (off by default; admin-protected).</summary>
    public bool BitwardenEnabled { get; set; }
    /// <summary>The Bitwarden server: "" = bitwarden.com (US), "eu" = bitwarden.eu, or a self-hosted https:// address
    /// (Bitwarden or Vaultwarden). Admin-protected: the master password unlocks a vault from this server.</summary>
    public string BitwardenServer { get; set; } = "";
    /// <summary>Vault items and secrets may sign in sessions (handed to the session in memory, never saved).</summary>
    public bool BitwardenAllowSessions { get; set; } = true;
    /// <summary>Passwords and secrets may be copied to the clipboard (cleared again after <see cref="BitwardenClipboardSeconds"/>,
    /// kept out of clipboard history).</summary>
    public bool BitwardenAllowCopy { get; set; } = true;
    /// <summary>Seconds before a copied password or secret is cleared from the clipboard (10–300).</summary>
    public int BitwardenClipboardSeconds { get; set; } = 30;
    /// <summary>Minutes without Bitwarden use before the vault locks again (1–480). It always locks with the app and at exit.</summary>
    public int BitwardenLockMinutes { get; set; } = 30;
    /// <summary>Sync the vault each time it is unlocked.</summary>
    public bool BitwardenSyncOnUnlock { get; set; } = true;
    /// <summary>The master password may be remembered on this PC (0.6.20), encrypted with the app's other secrets, when the
    /// user ticks "Remember" at the unlock prompt; the vault then unlocks without asking, also when the app starts.
    /// Admin-protected; turned off, a remembered password is no longer used and is deleted at the next unlock.</summary>
    public bool BitwardenAllowRemember { get; set; } = true;
    /// <summary>The Bitwarden CLI (bw.exe) and Secrets Manager CLI (bws.exe); empty = found in the usual places.</summary>
    public string BitwardenCliPath { get; set; } = "";
    public string BitwardenSmCliPath { get; set; } = "";
    /// <summary>SHA-256s (hex, comma-separated) of CLI builds an administrator approved because they aren't signed by
    /// Bitwarden. A build signed by Bitwarden needs no entry. Admin-protected.</summary>
    public string BitwardenCliPins { get; set; } = "";

    // Microsoft Teams sharing (0.5.0, plan #13): screenshots, terminal text, incidents and reports to a channel or chat.
    /// <summary>Sharing to Microsoft Teams at all (off by default: it sends data to Microsoft 365).</summary>
    public bool TeamsEnabled { get; set; }
    /// <summary>Screenshots may be shared (text only when off).</summary>
    public bool TeamsAllowImages { get; set; } = true;
    /// <summary>The Entra tenant (id or verified domain) and the app registration (public client) used to sign in to Graph.</summary>
    public string TeamsTenant { get; set; } = "";
    public string TeamsClientId { get; set; } = "";
    // Cloud PCs (0.6.4, ADR-083; DarkCipher0x: "integrate the Windows App ... so i could see my Cloud PCs and connect").
    /// <summary>The Cloud PCs page (Windows 365 / Azure Virtual Desktop through Microsoft's Windows App). Nothing is
    /// contacted until you open it and click.</summary>
    public bool CloudPcEnabled { get; set; } = true;
    /// <summary>Optional, for listing your Cloud PCs here: your Entra tenant (id or verified domain)…</summary>
    public string CloudPcTenant { get; set; } = "";
    /// <summary>…and an app registration (public client, redirect http://127.0.0.1) with the delegated Graph permission
    /// CloudPC.Read.All (admin consent). Empty: the page offers the Windows App and its web client without a list.</summary>
    public string CloudPcClientId { get; set; } = "";
    /// <summary>0.6.5: restart and rename your Cloud PCs, and (Windows 365 / Intune administrators) troubleshoot and
    /// power Frontline Cloud PCs on / off. Asks Microsoft for CloudPC.ReadWrite.All (a separate sign-in from the
    /// read-only list). Off by default; turning it on needs admin approval.</summary>
    public bool CloudPcAllowActions { get; set; }
    // Microsoft 365 admin (0.6.6, ADR-084): Intune, Defender and Entra, read-only, signed in as you through Microsoft Graph.
    /// <summary>The Microsoft 365 admin page. Nothing is contacted until you load a list.</summary>
    public bool M365AdminEnabled { get; set; } = true;
    /// <summary>Your Entra tenant (id or verified domain) for the Microsoft 365 admin page…</summary>
    public string M365Tenant { get; set; } = "";
    /// <summary>…and its app registration (public client, redirect http://127.0.0.1) with the read-only delegated Graph
    /// permissions listed in Settings (admin consent).</summary>
    public string M365ClientId { get; set; } = "";
    /// <summary>The destinations as JSON (names and Teams ids only; a webhook's address is a secret kept elsewhere).</summary>
    public string TeamsDestinations { get; set; } = "";

    // IDE integration (0.5.0, ADR-065): opening hosts, WSL and folders in VS Code and similar editors.
    /// <summary>The editor to open things in ("vscode", "cursor", …; empty = the first one found).</summary>
    public string IdePreferred { get; set; } = "";
    /// <summary>May the app keep ~/.ssh/dcx_config (Host blocks, no secrets) and its Include line for Remote-SSH.</summary>
    public bool IdeSshConfig { get; set; } = true;
    /// <summary>How long a vault key stays in ssh-agent after opening a host in an IDE (minutes, 5–1440).</summary>
    public int IdeAgentMinutes { get; set; } = 60;

    // Puppet (0.5.0, ADR-063): read-only until an administrator allows changes.
    /// <summary>Allow Puppet changes: signing / revoking / cleaning certificates, agent runs without --noop, enabling and
    /// disabling agents, code deploys and Bolt runs. Off: listing, dry runs, lookups, validation and PuppetDB queries only.</summary>
    public bool PuppetAllowChanges { get; set; }
    /// <summary>State: the saved host that is the Puppet server (primary) and the PuppetDB host, by host id.</summary>
    public string PuppetServerHostId { get; set; } = "";
    public string PuppetDbHostId { get; set; } = "";

    // Single sign-on into sessions (0.5.0, ADR-062): the Windows account's Kerberos ticket and credentials.
    /// <summary>Hosts may sign in to SSH with the Windows account's Kerberos ticket (GSSAPI through Windows OpenSSH).</summary>
    public bool SsoKerberosSsh { get; set; } = true;
    /// <summary>Forward (delegate) the Kerberos ticket to SSH servers, so they can reach further services as you. Off by
    /// default: a compromised server could use the ticket.</summary>
    public bool SsoKerberosDelegate { get; set; }
    /// <summary>Remote Desktop hosts may sign in with the Windows account (single sign-on, Remote Credential Guard,
    /// Restricted Admin).</summary>
    public bool SsoRdpAllowed { get; set; } = true;

    // Backup & restore (0.5.0, ADR-060): encrypted .dcxbackup exports and imports with tick lists, and scheduled backups.
    /// <summary>May this user write encrypted exports / backups at all (a policy can forbid data leaving the store).</summary>
    public bool BackupAllowExport { get; set; } = true;
    /// <summary>May this user import .dcxbackup files or other apps' connection files.</summary>
    public bool BackupAllowImport { get; set; } = true;
    /// <summary>May exports carry credentials, SSH keys and cloud API keys (off: hosts and everything else only).</summary>
    public bool BackupAllowSecrets { get; set; } = true;
    /// <summary>The shortest file password an export accepts (12–128).</summary>
    public int BackupMinPasswordLength { get; set; } = 12;
    /// <summary>Scheduled encrypted backups every N days (0 = off).</summary>
    public int BackupScheduleDays { get; set; }
    /// <summary>Where scheduled backups go (a folder; the sync-client check applies).</summary>
    public string BackupFolder { get; set; } = "";
    /// <summary>Scheduled backups kept in that folder (older ones are deleted only after a newer one verified).</summary>
    public int BackupKeep { get; set; } = 7;
    /// <summary>Scheduled backups include credentials, SSH keys and cloud API keys (when exports may carry them).</summary>
    public bool BackupScheduledSecrets { get; set; } = true;
    /// <summary>State: when the last backup was written and verified (UTC).</summary>
    public DateTime? BackupLastUtc { get; set; }
    /// <summary>State: the last scheduled backup's problem ("" = fine).</summary>
    public string BackupLastError { get; set; } = "";

    // Local API (ADR-011): loopback only, off by default. Tokens are stored as SHA-256 hashes.
    public bool LocalApiEnabled { get; set; }
    public int LocalApiPort { get; set; } = 47321;
    public List<Api.ApiToken> ApiTokens { get; set; } = new();

    // File transfer (ADR-014)
    /// <summary>SHA-256 of source and target must match before a copied file replaces the target.</summary>
    public bool TransferVerify { get; set; } = true;
    /// <summary>Keep modified times (and Unix permissions between two servers).</summary>
    public bool TransferKeepTimes { get; set; } = true;
    /// <summary>Attempts after the first for a failing file (0–3), with back-off and fresh connections.</summary>
    public int TransferRetries { get; set; } = 3;

    // Offline / air-gapped mode (Services/OfflineMode.cs, ADR-016)
    /// <summary>Block every destination outside this PC and your private network, except the approved ones.</summary>
    public bool OfflineMode { get; set; }
    /// <summary>Approved destinations while offline: host name, *.suffix, IP or CIDR (one per entry).</summary>
    public List<string> OfflineAllowed { get; set; } = new();
    /// <summary>Allow DNS names whose every address is private (sends DNS queries to the system resolver).</summary>
    public bool OfflineResolvePrivateNames { get; set; } = true;
    /// <summary>Add-ons can use the network themselves (the sandbox can't stop it), so offline mode refuses them unless this is on.</summary>
    public bool OfflineAllowAddons { get; set; }

    // Terminal features (ADR-019)
    /// <summary>Named terminal profiles (colour scheme, cursor, encoding, scrollback, keys, logging). Empty = one default is created.</summary>
    public List<Sessions.TerminalProfile> TerminalProfiles { get; set; } = new();
    public string DefaultTerminalProfileId { get; set; } = "";
    /// <summary>Telnet for legacy devices: unencrypted, so off unless the user turns it on (Settings → Terminal).</summary>
    public bool TelnetEnabled { get; set; }

    // Engineering dashboard (Views/DashboardView.cs, Services/Overview.cs, ADR-018)
    /// <summary>Card order (ids); cards missing here keep their default place.</summary>
    public List<string> DashboardOrder { get; set; } = new();
    public List<string> DashboardHidden { get; set; } = new();
    /// <summary>Cards added with Edit dashboard (0.5.1 #12), one line each: "id|type|target|title|size" (Services/DashboardCatalog).
    /// As a Group Policy it forces those cards.</summary>
    public List<string> DashboardCards { get; set; } = new();
    /// <summary>Users can't change the dashboard (no editing, moving, hiding or reset). Group Policy, with DashboardCards,
    /// DashboardOrder and DashboardHidden, sets the layout.</summary>
    public bool DashboardLocked { get; set; }
    /// <summary>When remote sources are checked: "manual", "open" (when the dashboard opens; default) or "interval".</summary>
    public string DashboardChecks { get; set; } = "open";
    /// <summary>"interval": minutes between checks, only while the dashboard is on screen (5, 15 or 60).</summary>
    public int DashboardCheckMinutes { get; set; } = 15;
    /// <summary>Remote sources included in automatic checks: a cloud provider id ("vultr", "contabo"), "ai", or
    /// "infra:&lt;provider id&gt;". Only Vultr (the old behaviour) and the AI engine are in by default; the rest are opt-in.</summary>
    public List<string> DashboardAutoSources { get; set; } = new() { "vultr", "ai" };

    /// <summary>Open Remote Desktop inside the app (ADR-013); off = Windows' mstsc window.</summary>
    public bool RdpEmbedded { get; set; } = true;

    /// <summary>Installed add-ons with their granted permissions and file hashes (ADR-012).</summary>
    public List<Addons.InstalledAddon> Addons { get; set; } = new();
    /// <summary>Id of the theme add-on painting the app (ADR-030); empty = the app's own theme.</summary>
    public string ThemeAddon { get; set; } = "";
    /// <summary>Portal browser (ADR-032): "tab" (in the main window) or "window" (a popped-out tab).</summary>
    public string PortalOpenIn { get; set; } = "tab";
    /// <summary>The portal browser is part of the app since 0.1.17 (DarkCipher0x): on by default, switchable in Settings → Integrations.
    /// The add-on still works (it also turns it on, and brings starter bookmarks).</summary>
    public bool PortalBrowserBuiltIn { get; set; } = true;
    /// <summary>Where portal downloads are saved (asked before each one); "" = ask for a folder at the first download.</summary>
    public string PortalDownloadFolder { get; set; } = "";

    /// <summary>The incident "Add to incident" goes to without asking (Incidents/IncidentStore.cs).</summary>
    public string? ActiveIncidentId { get; set; }

    // Safety
    public bool ConfirmDestructive { get; set; } = true;

    // App lock / sandbox
    /// <summary>Lock the app after this many minutes without keyboard/mouse input (0 = never). Needs a master password or 2FA.</summary>
    public int AutoLockMinutes { get; set; }
    public bool LockOnWindowsLock { get; set; } = true;
    /// <summary>Strict mode: master password required at start, auto-lock, temp/share files wiped on exit (error logs are sealed in every mode).</summary>
    public bool SandboxMode { get; set; }
    /// <summary>Open the store with Windows Hello at start (0.3.0 §2; the master password stays as the fallback).</summary>
    public bool HelloUnlock { get; set; }
    /// <summary>Ask for Windows Hello before revealing a secret, exporting, or changing a protected setting (0.3.0 §2).</summary>
    public bool HelloConfirm { get; set; }

    // ---- Locks (0.5.1 #8–#10, ADR-067; Services/Locks.cs) ----
    /// <summary>The locks the user set on categories and items (with password verifiers): never a policy, kept by restores.</summary>
    public List<Locks.Rule> UserLocks { get; set; } = new();
    /// <summary>The Audit log needs an unlock to open (on by default; turning it off needs admin approval).</summary>
    public bool LockAuditLog { get; set; } = true;
    /// <summary>Categories Group Policy locks (keys from Locks.Categories: audit, sessionlogs, credentials, team, knownhosts…).</summary>
    public List<string> LockedCategories { get; set; } = new();
    /// <summary>How policy-locked categories open: "uac" (Windows admin approval, default) or "signin" (Windows sign-in).</summary>
    public string LockPolicyMode { get; set; } = "uac";
    /// <summary>Users may lock categories themselves (admin approval to turn off; Group Policy can set it).</summary>
    public bool AllowUserLocks { get; set; } = true;
    /// <summary>Users may lock single items: hosts, folders, credentials, accounts, providers, portals, notes.</summary>
    public bool AllowItemLocks { get; set; } = true;
    /// <summary>How long an unlocked lock stays open, in minutes (0 = ask every time). Everything closes when the app locks.</summary>
    public int LockOpenMinutes { get; set; } = 15;

    // ---- What users may add and change (0.5.1 #11; Group Policy "Allow…"; Services/Restrictions.cs) ----
    public bool AllowHostAdd { get; set; } = true;
    public bool AllowHostEdit { get; set; } = true;
    public bool AllowHostDelete { get; set; } = true;
    /// <summary>Create, rename, move and delete host folders.</summary>
    public bool AllowFolderEdit { get; set; } = true;
    public bool AllowCredentialAdd { get; set; } = true;
    public bool AllowCredentialEdit { get; set; } = true;
    public bool AllowCredentialDelete { get; set; } = true;
    /// <summary>Add cloud accounts (Vultr, Contabo, Hetzner, Azure, Google Cloud).</summary>
    public bool AllowCloudAccountAdd { get; set; } = true;
    /// <summary>Edit and remove cloud accounts.</summary>
    public bool AllowCloudAccountEdit { get; set; } = true;
    public bool AllowInfraAdd { get; set; } = true;
    public bool AllowInfraEdit { get; set; } = true;
    public bool AllowInfraDelete { get; set; } = true;
    public bool AllowPortalAdd { get; set; } = true;
    public bool AllowPortalEdit { get; set; } = true;
    public bool AllowTunnelAdd { get; set; } = true;
    public bool AllowSnippetEdit { get; set; } = true;
    public bool AllowLibraryCreate { get; set; } = true;
    public bool AllowLibraryJoin { get; set; } = true;
    /// <summary>Change integrations (BeyondTrust, Teams, Pingdom, automation, IDE) at all; their switches still need admin approval.</summary>
    public bool AllowIntegrationEdit { get; set; } = true;
    /// <summary>Scan the network at all: ping sweeps, port scans, nmap, the SSH finder and the host import (0.5.1 #18).
    /// Some networks treat a sweep as an intrusion alarm; their administrators turn this off.</summary>
    public bool AllowNetworkScan { get; set; } = true;

    // ---- Add-ons (0.5.1 #7) ----
    /// <summary>Install, update and remove add-ons (each also needs admin approval when AddonsNeedApproval is on).</summary>
    public bool AllowAddonInstall { get; set; } = true;
    /// <summary>Installing, updating, enabling and removing add-ons needs Windows admin approval (UAC). On by default.</summary>
    public bool AddonsNeedApproval { get; set; } = true;
    /// <summary>Only these add-on ids may be installed (empty = any that passes the other checks).</summary>
    public List<string> AddonAllowedIds { get; set; } = new();
    /// <summary>These add-on ids may never be installed or run.</summary>
    public List<string> AddonBlockedIds { get; set; } = new();
    /// <summary>Only add-ons whose program is Authenticode-signed (data-only add-ons have no program and are unaffected).</summary>
    public bool AddonRequireSigned { get; set; }

    /// <summary>Team features built in (0.3.0 §22): the Shared library screen and the TEAM section, without an add-on.
    /// Admin-protected; a company can switch it off by policy.</summary>
    public bool TeamFeatures { get; set; } = true;
    /// <summary>The first-run setup (0.3.0 §19) has been completed or skipped for this store.</summary>
    public bool SetupDone { get; set; }

    /// <summary>The text editor's Vim keys (0.3.0 §13): on or off, remembered.</summary>
    public bool EditorVimMode { get; set; }
    /// <summary>The text editor's line-number gutter and syntax colours (0.3.0 §13).</summary>
    public bool EditorLineNumbers { get; set; } = true;
    public bool EditorSyntaxColours { get; set; } = true;
    /// <summary>Vim profiles (options + key mappings) and the one in use (0.3.0 §13).</summary>
    public List<VimProfile> EditorVimProfiles { get; set; } = VimProfile.Defaults();
    public string EditorVimProfile { get; set; } = "Default";

    /// <summary>Session logs (ADR-019) for every terminal, 0.4.0: "profile" = each terminal profile decides (as before),
    /// "on" = every session is logged whatever its profile says, "off" = never. A Group Policy can force either.</summary>
    public string SessionLogs { get; set; } = "profile";

    // Branding (0.4.0, DarkCipher0x: "make it fit their org"): the app's own look can be recoloured and rebranded from Settings →
    // Appearance → Branding & colours without an add-on. The same theme.json text a theme add-on carries (ThemePack), so a
    // company can push it by Group Policy (BrandingTheme) with a logo file on a share (BrandingLogoPath); a logo picked in
    // Settings is kept inside the encrypted settings (BrandingLogoPng). A chosen theme add-on wins over this.
    /// <summary>theme.json text (colours, productName, tagline, uiFont, logoBackground); "" = no branding.</summary>
    public string BrandingTheme { get; set; } = "";
    /// <summary>A PNG file (≤ 1 MB, ≤ 2048 px) for the header logo; read when the theme is applied.</summary>
    public string BrandingLogoPath { get; set; } = "";
    /// <summary>The logo picked in Settings, base64 PNG (never a policy: too large for the registry — use BrandingLogoPath).</summary>
    public string BrandingLogoPng { get; set; } = "";

    // Syslog over TLS (0.3.0 §3, Dcx.Notify): RFC 5424 over RFC 5425 only — there is no UDP or plain TCP setting at all.
    // Admin-protected (ProtectedSettings) so nothing running as the user can redirect or silence the audit trail.
    public bool SyslogEnabled { get; set; }
    /// <summary>The collector's DNS name or address (its certificate must match it unless pinned).</summary>
    public string SyslogHost { get; set; } = "";
    public int SyslogPort { get; set; } = 6514;
    /// <summary>Optional SHA-256 fingerprint (hex) of the collector's certificate or its CA: when set, only that certificate
    /// is accepted (self-signed collectors); empty = the Windows certificate store decides.</summary>
    public string SyslogPin { get; set; } = "";
    /// <summary>Optional client certificate (mutual TLS): the thumbprint of a certificate with a private key in
    /// CurrentUser\My.</summary>
    public string SyslogClientCert { get; set; } = "";
    public bool SyslogSendAudit { get; set; } = true;
    public bool SyslogSendAppLog { get; set; } = true;
    /// <summary>Also forward every logged terminal session's lines (0.4.0): one message per line, masked like the rest,
    /// msgid "session". Only sessions that are being logged (SessionLogs / the profile) are forwarded.</summary>
    public bool SyslogSendSessionLogs { get; set; }

    // Log sinks (0.4.0, Observability, Dcx.Notify/Sinks): the same events as syslog — audit, errors, logged sessions — to
    // Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk, Graylog or a webhook, HTTPS only. The list
    // (no secrets) is admin-protected and a Group Policy; each sink's key / password / token is kept here, encrypted, by sink id.
    public bool ObserveEnabled { get; set; }
    /// <summary>JSON list of <c>LogSink</c> records without secrets (validated; damaged = none).</summary>
    public string ObserveSinks { get; set; } = "";
    /// <summary>Sink id → API key / password / token; "grafana" → the Grafana service-account token. Never a policy.</summary>
    public Dictionary<string, string> ObserveSecrets { get; set; } = new();
    public bool ObserveSendAudit { get; set; } = true;
    public bool ObserveSendAppLog { get; set; } = true;
    public bool ObserveSendSessionLogs { get; set; }
    // Dashboards (Observability → Dashboards): a Grafana server whose dashboards are listed and opened in the app's browser
    // window, and links to Kibana / OpenSearch Dashboards / Microsoft Sentinel / any SIEM ("Label=https://…").
    public string GrafanaUrl { get; set; } = "";
    /// <summary>Grafana organisation id (0 = the token's default organisation).</summary>
    public int GrafanaOrgId { get; set; }
    public List<string> ObserveLinks { get; set; } = new();

    // E-mail notifications (0.3.0 §4, Dcx.Notify): implicit TLS (465) or STARTTLS (587), never plaintext. The server, the
    // sender, the recipients and the rules are admin-protected; the password lives only here, in the encrypted settings.
    public bool SmtpEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    /// <summary>True: TLS from the first byte (port 465). False: STARTTLS is required (587); a server without it is refused.</summary>
    public bool SmtpImplicitTls { get; set; }
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string SmtpFrom { get; set; } = "";
    /// <summary>Recipients, separated by commas or semicolons.</summary>
    public string SmtpTo { get; set; } = "";
    public bool NotifyIncidents { get; set; } = true;
    public bool NotifyHostDown { get; set; } = true;
    public bool NotifyBackupFailed { get; set; } = true;
    public bool NotifySecurity { get; set; } = true;
    public bool NotifySyslogFailing { get; set; } = true;
    /// <summary>At most this many notification e-mails per hour; the rest are counted and summarised in the next one.</summary>
    public int NotifyMaxPerHour { get; set; } = 10;

    // Data store
    /// <summary>Where the store was before the last move (its copy is kept until "Remove old copy").</summary>
    public string PreviousDataDir { get; set; } = "";
    /// <summary>Data-store locations for which the "synchronised folder" warning was dismissed for good. Moving the store
    /// somewhere else (a path not in this list) shows the warning again.</summary>
    public List<string> SyncWarningDismissedFor { get; set; } = new();

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        ExistedBeforeLoad = System.IO.File.Exists(System.IO.Path.Combine(SecureStore.DataDir, FileName));
        Current = SecureStore.Read<AppSettings>(FileName);
        Enforce?.Invoke(Current);
    }

    /// <summary>Whether settings were on disk when they were last loaded (false = a new install / new store).</summary>
    public static bool ExistedBeforeLoad { get; private set; }

    public static void Save()
    {
        // Group Policy values (0.3.0 §10) win over whatever was changed in memory: put them back before writing.
        if (Enforce?.Invoke(Current) is { Count: > 0 } reverted) Reverted?.Invoke(reverted);
        SecureStore.Write(FileName, Current);
        try { Saved?.Invoke(); } catch (Exception ex) { Log.Write("settings saved event", ex); }
    }

    /// <summary>After every save (0.5.0): screens showing settings-derived state refresh (Live.While).</summary>
    public static event Action? Saved;

    /// <summary>Puts managed (Group Policy) values back over the settings; returns the ones it had to change
    /// (<see cref="PolicySettings.ApplyTo"/>, set at start-up).</summary>
    public static Func<AppSettings, IReadOnlyList<string>>? Enforce { get; set; }

    /// <summary>A save put managed values back (the screens say why the change didn't stay).</summary>
    public static event Action<IReadOnlyList<string>>? Reverted;

    /// <summary>The active, enabled Vultr account (disabled accounts are never used).</summary>
    public VultrAccount? ActiveVultr =>
        VultrAccounts.FirstOrDefault(a => a.Id == ActiveVultrAccountId && !a.Disabled) ?? VultrAccounts.FirstOrDefault(a => !a.Disabled);
}

/// <summary>Where an extra window was (0.6.4, ADR-082): its normal bounds in DIPs, and whether it was maximised.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 720;
    public bool Maximized { get; set; }
}
