# Group Policy reference

Generated from the app's policy catalog (`PolicySettings.Catalog`, ADR-040) by every build; don't edit by hand.

Registry: `HKLM\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` (computer, wins) and `HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` (user). Templates: `policy\DarkCipherServerManagement.admx` and `policy\en-US\DarkCipherServerManagement.adml` in each release folder. A managed setting can't be changed in the app; an invalid value is ignored and listed in Settings → Security & privacy → Managed by your organisation.

265 policies, plus 2 computer-only ones (below). Not available as policies: ActiveAzureAccountId, ActiveContaboAccountId, ActiveGcpAccountId, ActiveHetznerAccountId, ActiveIncidentId, ActiveVultrAccountId, Addons, AiDefaultChosen, AiDefaultRevision, AiLockSet, AiPanelState, AiPanelWidth, AnthropicKey, ApiTokens, AzureAccounts, BackupLastError, BackupLastUtc, BrandingLogoPng, CollapsedNavSections, CollapsedSettingsCards, CommanderLeft, CommanderRight, ContaboAccounts, DefaultKeyCredentialId, DefaultTerminalProfileId, EditorVimProfiles, ExpandedNavSections, GcpAccounts, HetznerAccounts, HostsDockWidth, LastUpdateCheck, LocalSshKeysSeen, ObserveSecrets, OllamaTunnelHostId, OpenAiKey, PaletteRecent, PendingUpdateFrom, PendingUpdateVersion, PreviousDataDir, PuppetDbHostId, PuppetServerHostId, SetupDone, SidebarCollapsed, SmtpPassword, SyncWarningDismissedFor, TerminalHighlightRules, TerminalProfiles, TmuxConfig, UpdateGitHubToken, UserLocks, VultrAccounts, WindowHostPaneWidth, WorkspaceWindows (accounts, secrets and per-PC state).

## Security and sign-in

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AutoLockMinutes` | Auto-lock after minutes idle (0 = never) | REG_DWORD | 0–480 | 0 |
| `ConfirmDestructive` | Ask before every change (confirmations) | REG_DWORD | 1 = on, 0 = off | on |
| `HelloConfirm` | Confirm sensitive actions with Windows Hello | REG_DWORD | 1 = on, 0 = off | off |
| `HelloUnlock` | Sign in with Windows Hello | REG_DWORD | 1 = on, 0 = off | off |
| `LockOnWindowsLock` | Lock when Windows locks or the PC sleeps | REG_DWORD | 1 = on, 0 = off | on |
| `SandboxMode` | Sandbox mode | REG_DWORD | 1 = on, 0 = off | off |
| `TeamFeatures` | Team features (shared libraries) | REG_DWORD | 1 = on, 0 = off | on |

## Network and offline mode

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `LocalApiEnabled` | Local API (loopback only) | REG_DWORD | 1 = on, 0 = off | off |
| `LocalApiPort` | Local API port | REG_DWORD | 1024–65535 | 47321 |
| `OfflineAllowAddons` | Add-ons may use approved destinations in offline mode | REG_DWORD | 1 = on, 0 = off | off |
| `OfflineAllowed` | Approved destinations in offline mode | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `OfflineMode` | Offline mode | REG_DWORD | 1 = on, 0 = off | off |
| `OfflineResolvePrivateNames` | Offline resolve private names | REG_DWORD | 1 = on, 0 = off | on |
| `PortalBrowserBuiltIn` | Portal browser built in | REG_DWORD | 1 = on, 0 = off | on |
| `PortalDownloadFolder` | Portal download folder | REG_SZ | a folder path (REG_EXPAND_SZ allowed) | empty |
| `PortalOpenIn` | Portal open in | REG_SZ | `tab` / `window` | `tab` |
| `TelnetEnabled` | Allow Telnet (unencrypted) | REG_DWORD | 1 = on, 0 = off | off |

## AI

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AiAllowCloud` | Allow internet AI providers | REG_DWORD | 1 = on, 0 = off | off |
| `AiAllowEngineChange` | AI allow engine change | REG_DWORD | 1 = on, 0 = off | on |
| `AiAllowModelDownload` | AI allow model download | REG_DWORD | 1 = on, 0 = off | on |
| `AiAllowNotes` | AI allow notes | REG_DWORD | 1 = on, 0 = off | off |
| `AiAllowServerDeploy` | AI allow server deploy | REG_DWORD | 1 = on, 0 = off | on |
| `AiAutoApproveReadOnly` | AI auto approve read only | REG_DWORD | 1 = on, 0 = off | on |
| `AiAutoRunReadOnlyTarget` | Let the AI run read-only commands in its target terminal without asking | REG_DWORD | 1 = on, 0 = off | off |
| `AiCommandTimeoutMinutes` | AI command timeout minutes | REG_DWORD | 1–120 | 5 |
| `AiContext` | AI context | REG_DWORD | 512–1048576 | 16384 |
| `AiCpuThreads` | AI cpu threads | REG_DWORD | 0–256 | 0 |
| `AiEnabled` | AI enabled | REG_DWORD | 1 = on, 0 = off | on |
| `AiEngine` | AI engine | REG_SZ | text (up to 2,048 characters) | `npu` |
| `AiFeatureClaudeCode` | AI feature claude code | REG_DWORD | 1 = on, 0 = off | on |
| `AiFeatureFiles` | AI feature files | REG_DWORD | 1 = on, 0 = off | on |
| `AiFeatureHelper` | AI feature helper | REG_DWORD | 1 = on, 0 = off | on |
| `AiFeatureLogAnalysis` | AI feature log analysis | REG_DWORD | 1 = on, 0 = off | on |
| `AiFeatureOperator` | AI feature operator | REG_DWORD | 1 = on, 0 = off | on |
| `AiFeatureReports` | AI feature reports | REG_DWORD | 1 = on, 0 = off | on |
| `AiFeatures` | AI features | REG_DWORD | 1 = on, 0 = off | not set |
| `AiKeepWarmMinutes` | AI keep warm minutes | REG_DWORD | 0–1440 | 10 |
| `AiMaxSteps` | AI max steps | REG_DWORD | 1–100 | 12 |
| `AiNpuSetupChoice` | AI npu setup choice | REG_SZ | text (up to 2,048 characters) | `ask` |
| `AiPauseOnBattery` | AI pause on battery | REG_DWORD | 1 = on, 0 = off | on |
| `AiRedactSecrets` | Mask secrets before anything is sent to a model | REG_DWORD | 1 = on, 0 = off | on |
| `AiResourceGuard` | AI resource guard | REG_DWORD | 1 = on, 0 = off | on |
| `AiShareCommandOutput` | AI share command output | REG_DWORD | 1 = on, 0 = off | on |
| `AiStream` | AI stream | REG_DWORD | 1 = on, 0 = off | on |
| `AiTerminalBridge` | AI terminal bridge | REG_DWORD | 1 = on, 0 = off | on |
| `AiUnloadIdleFoundry` | AI unload idle foundry | REG_DWORD | 1 = on, 0 = off | on |
| `AnthropicModel` | Anthropic model | REG_SZ | text (up to 2,048 characters) | `claude-sonnet-5-5` |
| `AnthropicUrl` | Anthropic URL | REG_SZ | text (up to 2,048 characters) | `https://api.anthropic.com` |
| `ClaudeCodeModel` | Claude code model | REG_SZ | text (up to 2,048 characters) | empty |
| `ClaudeCodeWorkspace` | Claude code workspace | REG_SZ | a folder path (REG_EXPAND_SZ allowed) | empty |
| `FoundryAutoSetup` | Foundry auto setup | REG_DWORD | 1 = on, 0 = off | on |
| `FoundryModel` | Foundry model | REG_SZ | text (up to 2,048 characters) | `phi-4-mini` |
| `FoundryUrl` | Foundry URL | REG_SZ | text (up to 2,048 characters) | empty |
| `OllamaModel` | Ollama model | REG_SZ | text (up to 2,048 characters) | `qwen2.5:7b` |
| `OllamaUrl` | Ollama URL | REG_SZ | text (up to 2,048 characters) | `http://localhost:11434` |
| `OpenAiAcceptsImages` | OpenAI accepts images | REG_DWORD | 1 = on, 0 = off | off |
| `OpenAiModel` | OpenAI model | REG_SZ | text (up to 2,048 characters) | empty |
| `OpenAiUrl` | OpenAI URL | REG_SZ | text (up to 2,048 characters) | `http://localhost:1234/v1` |

## Terminals and connections

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AutoReconnectMaxSeconds` | Auto reconnect max seconds | REG_DWORD | 1–3600 | 30 |
| `CloudSyncHosts` | Saved hosts follow their cloud servers (new IP address, rebuilt server of the same name; removed servers are offered for removal) | REG_DWORD | 1 = on, 0 = off | on |
| `ConnectTimeoutSeconds` | Connect timeout seconds | REG_DWORD | 1–600 | 15 |
| `DefaultUsername` | Default username | REG_SZ | text (up to 2,048 characters) | `root` |
| `EditorLineNumbers` | Editor line numbers | REG_DWORD | 1 = on, 0 = off | on |
| `EditorSyntaxColours` | Editor syntax colours | REG_DWORD | 1 = on, 0 = off | on |
| `EditorVimMode` | Editor vim mode | REG_DWORD | 1 = on, 0 = off | off |
| `EditorVimProfile` | Editor vim profile | REG_SZ | text (up to 2,048 characters) | `Default` |
| `LocalSshKeysAutoImport` | At start, copy new SSH keys from the Windows account's .ssh folder into the encrypted store (only keys without a passphrase; the files are only read) | REG_DWORD | 1 = on, 0 = off | off |
| `RdpEmbedded` | RDP embedded | REG_DWORD | 1 = on, 0 = off | on |
| `RestoreReconnect` | Saved sessions reconnect as they reopen (PRODUCTION hosts stay disconnected; locked hosts never reopen by themselves) | REG_DWORD | 1 = on, 0 = off | on |
| `RestoreWorkspace` | Restore workspace | REG_DWORD | 1 = on, 0 = off | on |
| `ReuseOpenTabs` | Switch to an already-open tab of the same host instead of opening another | REG_DWORD | 1 = on, 0 = off | on |
| `Scrollback` | Scrollback | REG_DWORD | 100–1000000 | 10000 |
| `SessionLogs` | Terminal session logs (profile / on / off) | REG_SZ | `profile` / `on` / `off` | `profile` |
| `SessionsOnExit` | Exiting with sessions open: ask (save and exit, or exit without saving), save (always reopen them next time) or never | REG_SZ | `ask` / `save` / `never` | `ask` |
| `TerminalAlertBell` | The terminal bell alerts | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalAlertDesktop` | Alerts also show as Windows notifications (off: the tab badge and the taskbar flash only) | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalAlertDrop` | A session that drops by itself alerts | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalAlertSeconds` | A command running at least this many seconds alerts when it finishes (0 = never) | REG_DWORD | 0–86400 | 30 |
| `TerminalAlerts` | Alerts from terminals you aren't looking at (a long command finished, a bell, a highlight alert, a dropped session) | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalAutoReconnect` | Terminal auto reconnect | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalButtonBar` | The snippet button bar on terminals | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalConfirmPaste` | Confirm multi-line and risky pastes | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalFont` | Terminal font | REG_SZ | text (up to 2,048 characters) | empty |
| `TerminalFontSize` | Terminal font size | REG_DWORD | 6–40 | 14 |
| `TerminalHighlight` | Colour keywords in terminal output (the highlight packs and the user's own rules) | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalHighlightPacks` | The highlight packs that are on: errors, warnings, success, network, http | REG_MULTI_SZ | one entry per line (up to 200) | errors, warnings, success, network |
| `TerminalLinks` | Ctrl+click an IP address, a path or a web link in terminal output for its actions | REG_DWORD | 1 = on, 0 = off | on |
| `TerminalRightClick` | Terminal right click | REG_SZ | `menu` / `paste` | `menu` |
| `TmuxAlways` | Always-on tmux: every SSH terminal to a Linux or macOS host starts inside your tmux session (a host or folder can override; never installs tmux) | REG_DWORD | 1 = on, 0 = off | off |
| `TmuxInstall` | The terminal's tmux button installs tmux when it is missing (asking first) | REG_DWORD | 1 = on, 0 = off | on |
| `TmuxSession` | The tmux session the terminal's tmux button attaches to or creates | REG_SZ | text (up to 2,048 characters) | `main` |

## Appearance

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AccentColor` | Accent color | REG_SZ | text (up to 2,048 characters) | `36D2CB` |
| `BrandingLogoPath` | Branding logo path | REG_SZ | text (up to 2,048 characters) | empty |
| `BrandingTheme` | Branding theme | REG_SZ | text (up to 2,048 characters) | empty |
| `Corners` | Corners | REG_DWORD | 0–8 | 2 |
| `Density` | Density | REG_SZ | `comfortable` / `compact` | `comfortable` |
| `EditorFont` | Editor font | REG_SZ | text (up to 2,048 characters) | empty |
| `FollowWindowsHighContrast` | Follow windows high contrast | REG_DWORD | 1 = on, 0 = off | on |
| `MonoAccents` | Mono accents | REG_DWORD | 1 = on, 0 = off | off |
| `MonoFont` | Mono font | REG_SZ | text (up to 2,048 characters) | empty |
| `Motion` | Motion | REG_SZ | `windows` / `standard` / `reduced` | `windows` |
| `StatusIntensity` | Status intensity | REG_SZ | `subtle` / `standard` / `strong` | `standard` |
| `Theme` | Theme | REG_SZ | `System` / `Dark` / `Light` | `System` |
| `ThemeAddon` | Theme addon | REG_SZ | text (up to 2,048 characters) | empty |
| `ThemePreset` | Theme preset | REG_SZ | `darkcipher` / `graphite` / `teal` / `highcontrast` / `neutral` / `slate` / `arctic` | `darkcipher` |
| `UiFont` | UI font | REG_SZ | text (up to 2,048 characters) | empty |
| `UiScale` | UI scale | REG_SZ | 0.5–3 (as text, e.g. 1.25) | 1.1 |
| `UiTextSize` | UI text size | REG_SZ | 0.5–3 (as text, e.g. 1.25) | 1 |

## Navigation and dashboard

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `CloudPinned` | Cloud pinned | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `DashboardAutoSources` | Dashboard auto sources | REG_MULTI_SZ | one entry per line (up to 200) | vultr, ai |
| `DashboardCards` | Dashboard cards | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `DashboardCheckMinutes` | Dashboard check minutes | REG_DWORD | 1–1440 | 15 |
| `DashboardChecks` | Dashboard checks | REG_SZ | `manual` / `open` / `interval` | `open` |
| `DashboardHidden` | Dashboard hidden | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `DashboardLocked` | Dashboard locked | REG_DWORD | 1 = on, 0 = off | off |
| `DashboardOrder` | Dashboard order | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `HiddenNavItems` | Hidden nav items | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `HiddenNavSections` | Hidden nav sections | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `HostsDock` | The Hosts page's folder pane: pinned open or auto-collapsing (pinned or auto) | REG_SZ | `pinned` / `auto` | `pinned` |
| `HostsHiddenColumns` | Hosts hidden columns | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `HostsOpenIn` | Where sessions opened from the Hosts page go (hosts = inside the Hosts tab, tab = separate tabs) | REG_SZ | `hosts` / `tab` | `hosts` |
| `HostsView` | Hosts view | REG_SZ | text (up to 2,048 characters) | `folders` |
| `LandingPage` | Landing page | REG_SZ | text (up to 2,048 characters) | empty |
| `NavItemOrder` | The order of the pages inside each sidebar section (page keys; empty = the default order) | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `NavSectionOrder` | Nav section order | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `PaletteLearn` | The command palette (Ctrl+Shift+P) puts what is used most at the top | REG_DWORD | 1 = on, 0 = off | on |
| `PinnedNavItems` | Pinned nav items | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `RememberNavExpansion` | Remember nav expansion | REG_DWORD | 1 = on, 0 = off | off |
| `RestoreWorkspaceWindows` | Reopen the extra windows where they were (monitor, position, size) when the app starts | REG_DWORD | 1 = on, 0 = off | on |
| `SidebarStyle` | Sidebar style | REG_SZ | `expanded` / `compact` / `icons` | `expanded` |
| `SidebarWidth` | Sidebar width | REG_SZ | 100–1000 (as text, e.g. 1.25) | 214 |
| `WindowHostPane` | The host pane in extra windows (other monitors): pinned open or auto-collapsing (pinned or auto) | REG_SZ | `pinned` / `auto` | `pinned` |

## Data, backups and files

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AutoSnapshots` | Take automatic snapshots | REG_DWORD | 1 = on, 0 = off | on |
| `BackupAllowExport` | Allow encrypted exports and backups | REG_DWORD | 1 = on, 0 = off | on |
| `BackupAllowImport` | Allow imports (backups and other apps' files) | REG_DWORD | 1 = on, 0 = off | on |
| `BackupAllowSecrets` | Exports may carry credentials, keys and cloud API keys | REG_DWORD | 1 = on, 0 = off | on |
| `BackupFolder` | Scheduled backup folder | REG_SZ | a folder path (REG_EXPAND_SZ allowed) | empty |
| `BackupKeep` | Backup keep | REG_DWORD | 1–100 | 7 |
| `BackupMinPasswordLength` | Shortest export password | REG_DWORD | 12–128 | 12 |
| `BackupScheduleDays` | Scheduled backups every N days (0 = off) | REG_DWORD | 0–30 | 0 |
| `BackupScheduledSecrets` | Scheduled backups include credentials and keys | REG_DWORD | 1 = on, 0 = off | on |
| `CommanderRestoreLast` | File commander reconnects to its last places when it opens | REG_DWORD | 1 = on, 0 = off | off |
| `CommanderShowHidden` | Commander show hidden | REG_DWORD | 1 = on, 0 = off | off |
| `FileSyncAllowPermanentDelete` | Folder sync may delete permanently on servers | REG_DWORD | 1 = on, 0 = off | off |
| `FileSyncDeletion` | Where new folder sync jobs send deleted and overwritten files: RecycleBin (versions on a server), Versioning or Permanent | REG_SZ | `RecycleBin` / `Versioning` / `Permanent` | `RecycleBin` |
| `FileSyncMaxDeletePercent` | New folder sync jobs: a run without a preview stops when it would delete more than this percentage of a side's files | REG_DWORD | 1–100 | 50 |
| `FileSyncScheduled` | Folder sync jobs may run on their schedule or in real time while the app is open (off: only when someone runs them) | REG_DWORD | 1 = on, 0 = off | on |
| `FileSyncVersionsKeepDays` | New folder sync jobs keep versions this many days (0 = forever) | REG_DWORD | 0–3650 | 30 |
| `ScreenshotDefault` | Screenshot default | REG_SZ | `save` / `clipboard` / `both` | `both` |
| `ScreenshotDir` | Screenshot dir | REG_SZ | a folder path (REG_EXPAND_SZ allowed) | empty |
| `SnapshotDir` | Snapshot dir | REG_SZ | a folder path (REG_EXPAND_SZ allowed) | empty |
| `SnapshotIntervalHours` | Snapshot interval hours | REG_DWORD | 1–720 | 24 |
| `SnapshotKeep` | Snapshot keep | REG_DWORD | 1–1000 | 10 |
| `TransferKeepTimes` | Transfer keep times | REG_DWORD | 1 = on, 0 = off | on |
| `TransferRetries` | Transfer retries | REG_DWORD | 0–20 | 3 |
| `TransferVerify` | Transfer verify | REG_DWORD | 1 = on, 0 = off | on |

## Syslog, log sinks and notifications

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `GrafanaOrgId` | Grafana org id | REG_DWORD | 0–1000000 | 0 |
| `GrafanaUrl` | Grafana address (dashboards) | REG_SZ | text (up to 2,048 characters) | empty |
| `NotifyBackupFailed` | Notify backup failed | REG_DWORD | 1 = on, 0 = off | on |
| `NotifyHostDown` | Notify host down | REG_DWORD | 1 = on, 0 = off | on |
| `NotifyIncidents` | Notify incidents | REG_DWORD | 1 = on, 0 = off | on |
| `NotifyMaxPerHour` | Notify max per hour | REG_DWORD | 1–100 | 10 |
| `NotifySecurity` | Notify about security events | REG_DWORD | 1 = on, 0 = off | on |
| `NotifySyslogFailing` | Notify when syslog or a log sink is failing | REG_DWORD | 1 = on, 0 = off | on |
| `ObserveEnabled` | Ship logs to the log sinks (HTTPS) | REG_DWORD | 1 = on, 0 = off | off |
| `ObserveLinks` | Observe links | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `ObserveSendAppLog` | Log sinks: send the application (error) log | REG_DWORD | 1 = on, 0 = off | on |
| `ObserveSendAudit` | Log sinks: send the audit log | REG_DWORD | 1 = on, 0 = off | on |
| `ObserveSendSessionLogs` | Log sinks: send the terminal session logs | REG_DWORD | 1 = on, 0 = off | off |
| `ObserveSinks` | Log sinks (JSON list, no secrets) | REG_SZ | text (up to 2,048 characters) | empty |
| `SmtpEnabled` | E-mail notifications | REG_DWORD | 1 = on, 0 = off | off |
| `SmtpFrom` | Notifications are sent from | REG_SZ | text (up to 2,048 characters) | empty |
| `SmtpHost` | SMTP server | REG_SZ | text (up to 2,048 characters) | empty |
| `SmtpImplicitTls` | SMTP: TLS from the start (465) instead of STARTTLS | REG_DWORD | 1 = on, 0 = off | off |
| `SmtpPort` | SMTP port | REG_DWORD | 1–65535 | 587 |
| `SmtpTo` | Notifications are sent to | REG_SZ | text (up to 2,048 characters) | empty |
| `SmtpUser` | SMTP user name | REG_SZ | text (up to 2,048 characters) | empty |
| `SyslogClientCert` | Syslog client certificate (thumbprint) | REG_SZ | text (up to 2,048 characters) | empty |
| `SyslogEnabled` | Send logs to a syslog server (TLS) | REG_DWORD | 1 = on, 0 = off | off |
| `SyslogHost` | Syslog server | REG_SZ | text (up to 2,048 characters) | empty |
| `SyslogPin` | Syslog server certificate (SHA-256 pin) | REG_SZ | text (up to 2,048 characters) | empty |
| `SyslogPort` | Syslog port (TLS) | REG_DWORD | 1–65535 | 6514 |
| `SyslogSendAppLog` | Send the application (error) log | REG_DWORD | 1 = on, 0 = off | on |
| `SyslogSendAudit` | Send the audit log | REG_DWORD | 1 = on, 0 = off | on |
| `SyslogSendSessionLogs` | Send the terminal session logs | REG_DWORD | 1 = on, 0 = off | off |

## Updates

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AutoCheckUpdates` | Auto check updates | REG_DWORD | 1 = on, 0 = off | off |
| `AutoDownloadUpdates` | Download and verify a new version in the background (installing always asks) | REG_DWORD | 1 = on, 0 = off | off |
| `UpdateCheckInterval` | How often the automatic update check runs (weekly or daily) | REG_SZ | `weekly` / `daily` | `weekly` |
| `UpdateEndpoint` | Update endpoint | REG_SZ | text (up to 2,048 characters) | empty |
| `UpdateSource` | Where updates come from (github = the GitHub releases, url = the update address) | REG_SZ | `github` / `url` | `github` |

## Identity and single sign-on

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `SsoKerberosDelegate` | Forward the Kerberos ticket to SSH servers | REG_DWORD | 1 = on, 0 = off | off |
| `SsoKerberosSsh` | SSH single sign-on with Kerberos | REG_DWORD | 1 = on, 0 = off | on |
| `SsoRdpAllowed` | Remote Desktop single sign-on with the Windows account | REG_DWORD | 1 = on, 0 = off | on |

## Post-quantum cryptography

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `PqSshRequired` | Require post-quantum key exchange for SSH | REG_DWORD | 1 = on, 0 = off | off |

## Automation (Ansible and Puppet)

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `PuppetAllowChanges` | Allow Puppet changes (certificates, agent runs, deploys, Bolt) | REG_DWORD | 1 = on, 0 = off | off |

## IDE integration

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `IdeAgentMinutes` | Ide agent minutes | REG_DWORD | 5–1440 | 60 |
| `IdePreferred` | Ide preferred | REG_SZ | text (up to 2,048 characters) | empty |
| `IdeSshConfig` | Keep an SSH config (~/.ssh/dcx_config, no secrets) for IDEs | REG_DWORD | 1 = on, 0 = off | on |

## Microsoft Teams

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `TeamsAllowImages` | Teams: screenshots may be shared | REG_DWORD | 1 = on, 0 = off | on |
| `TeamsClientId` | Teams: app registration (client id) | REG_SZ | text (up to 2,048 characters) | empty |
| `TeamsDestinations` | Teams destinations | REG_SZ | text (up to 2,048 characters) | empty |
| `TeamsEnabled` | Share to Microsoft Teams | REG_DWORD | 1 = on, 0 = off | off |
| `TeamsTenant` | Teams: Entra tenant | REG_SZ | text (up to 2,048 characters) | empty |

## Cloud PCs

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `CloudPcAllowActions` | Cloud PCs: restart, rename and admin power actions | REG_DWORD | 1 = on, 0 = off | off |
| `CloudPcClientId` | Cloud PCs: app registration (client id) | REG_SZ | text (up to 2,048 characters) | empty |
| `CloudPcEnabled` | The Cloud PCs page (Windows 365 / Azure Virtual Desktop through the Windows App) | REG_DWORD | 1 = on, 0 = off | on |
| `CloudPcTenant` | Cloud PCs: Entra tenant | REG_SZ | text (up to 2,048 characters) | empty |

## Microsoft 365 admin

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `M365AdminEnabled` | The Microsoft 365 admin page (Intune, Defender and Entra, read-only) | REG_DWORD | 1 = on, 0 = off | on |
| `M365ClientId` | Microsoft 365 admin: app registration (client id) | REG_SZ | text (up to 2,048 characters) | empty |
| `M365Tenant` | Microsoft 365 admin: Entra tenant | REG_SZ | text (up to 2,048 characters) | empty |

## BeyondTrust

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `BeyondTrustAllowCheckout` | BeyondTrust: check out Password Safe credentials for sessions | REG_DWORD | 1 = on, 0 = off | on |
| `BeyondTrustAskReason` | Beyond trust ask reason | REG_DWORD | 1 = on, 0 = off | off |
| `BeyondTrustCheckoutMinutes` | Beyond trust checkout minutes | REG_DWORD | 1–480 | 30 |
| `BeyondTrustEnabled` | BeyondTrust integration (PRA / Password Safe) | REG_DWORD | 1 = on, 0 = off | off |
| `BeyondTrustJumpClient` | Beyond trust jump client | REG_SZ | `console` / `web` | `console` |
| `BeyondTrustJumpGroupFilter` | Beyond trust jump group filter | REG_SZ | text (up to 2,048 characters) | empty |
| `BeyondTrustJumpointFilter` | Beyond trust jumpoint filter | REG_SZ | text (up to 2,048 characters) | empty |
| `BeyondTrustPraClientId` | Beyond trust pra client id | REG_SZ | text (up to 2,048 characters) | empty |
| `BeyondTrustPraPin` | BeyondTrust PRA certificate pin (SHA-256) | REG_SZ | text (up to 2,048 characters) | empty |
| `BeyondTrustPraSite` | BeyondTrust PRA site | REG_SZ | text (up to 2,048 characters) | empty |
| `BeyondTrustPsHost` | BeyondTrust Password Safe host | REG_SZ | text (up to 2,048 characters) | empty |
| `BeyondTrustPsPin` | BeyondTrust Password Safe certificate pin (SHA-256) | REG_SZ | text (up to 2,048 characters) | empty |
| `BeyondTrustPsRunAs` | Beyond trust ps run as | REG_SZ | text (up to 2,048 characters) | empty |

## Bitwarden

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `BitwardenAllowCopy` | Bitwarden: passwords and secrets may be copied to the clipboard | REG_DWORD | 1 = on, 0 = off | on |
| `BitwardenAllowRemember` | Bitwarden: the master password may be remembered on this PC (encrypted) | REG_DWORD | 1 = on, 0 = off | on |
| `BitwardenAllowSessions` | Bitwarden: vault items and secrets may sign in sessions | REG_DWORD | 1 = on, 0 = off | on |
| `BitwardenCliPath` | Bitwarden cli path | REG_SZ | text (up to 2,048 characters) | empty |
| `BitwardenCliPins` | Bitwarden: approved CLI builds not signed by Bitwarden (SHA-256) | REG_SZ | text (up to 2,048 characters) | empty |
| `BitwardenClipboardSeconds` | Bitwarden clipboard seconds | REG_DWORD | 10–300 | 30 |
| `BitwardenEnabled` | Bitwarden integration (Password Manager / Secrets Manager) | REG_DWORD | 1 = on, 0 = off | off |
| `BitwardenLockMinutes` | Bitwarden lock minutes | REG_DWORD | 1–480 | 30 |
| `BitwardenServer` | Bitwarden server | REG_SZ | text (up to 2,048 characters) | empty |
| `BitwardenSmCliPath` | Bitwarden sm cli path | REG_SZ | text (up to 2,048 characters) | empty |
| `BitwardenSyncOnUnlock` | Bitwarden sync on unlock | REG_DWORD | 1 = on, 0 = off | on |

## Locks and access

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AllowItemLocks` | Users may lock single items | REG_DWORD | 1 = on, 0 = off | on |
| `AllowUserLocks` | Users may set their own locks | REG_DWORD | 1 = on, 0 = off | on |
| `LockAuditLog` | The Audit log needs an unlock to open | REG_DWORD | 1 = on, 0 = off | on |
| `LockOpenMinutes` | An unlocked lock stays open (minutes, 0 = ask every time) | REG_DWORD | 0–480 | 15 |
| `LockPolicyMode` | How locked categories open (uac / signin) | REG_SZ | `uac` / `signin` | `uac` |
| `LockedCategories` | Categories locked by your organisation (audit, credentials, hosts, …) | REG_MULTI_SZ | one entry per line (up to 200) | empty |

## Add-ons

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AddonAllowedIds` | Only these add-on ids may be installed (empty = any that passes the other checks) | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `AddonBlockedIds` | Add-on ids that may never be installed or run | REG_MULTI_SZ | one entry per line (up to 200) | empty |
| `AddonRequireSigned` | Only add-ons with a signed program | REG_DWORD | 1 = on, 0 = off | off |
| `AddonsNeedApproval` | Installing, updating and removing add-ons needs admin approval | REG_DWORD | 1 = on, 0 = off | on |
| `AllowAddonInstall` | Add-ons may be installed | REG_DWORD | 1 = on, 0 = off | on |

## What users may add and change

| Value name | Setting | Type | Accepted | App default |
|---|---|---|---|---|
| `AllowCloudAccountAdd` | Allow adding cloud accounts | REG_DWORD | 1 = on, 0 = off | on |
| `AllowCloudAccountEdit` | Allow editing and removing cloud accounts | REG_DWORD | 1 = on, 0 = off | on |
| `AllowCredentialAdd` | Allow adding credentials | REG_DWORD | 1 = on, 0 = off | on |
| `AllowCredentialDelete` | Allow deleting credentials | REG_DWORD | 1 = on, 0 = off | on |
| `AllowCredentialEdit` | Allow editing credentials | REG_DWORD | 1 = on, 0 = off | on |
| `AllowFolderEdit` | Allow creating, renaming, moving and deleting host folders | REG_DWORD | 1 = on, 0 = off | on |
| `AllowHostAdd` | Allow adding hosts | REG_DWORD | 1 = on, 0 = off | on |
| `AllowHostDelete` | Allow deleting hosts | REG_DWORD | 1 = on, 0 = off | on |
| `AllowHostEdit` | Allow editing hosts | REG_DWORD | 1 = on, 0 = off | on |
| `AllowInfraAdd` | Allow adding infrastructure providers | REG_DWORD | 1 = on, 0 = off | on |
| `AllowInfraDelete` | Allow removing infrastructure providers | REG_DWORD | 1 = on, 0 = off | on |
| `AllowInfraEdit` | Allow editing infrastructure providers | REG_DWORD | 1 = on, 0 = off | on |
| `AllowIntegrationEdit` | Allow changing integrations | REG_DWORD | 1 = on, 0 = off | on |
| `AllowLibraryCreate` | Allow creating shared libraries | REG_DWORD | 1 = on, 0 = off | on |
| `AllowLibraryJoin` | Allow joining shared libraries | REG_DWORD | 1 = on, 0 = off | on |
| `AllowNetworkScan` | Allow scanning the network (ping sweeps, port scans, nmap, the SSH finder and host import) | REG_DWORD | 1 = on, 0 = off | on |
| `AllowPortalAdd` | Allow adding portals | REG_DWORD | 1 = on, 0 = off | on |
| `AllowPortalEdit` | Allow editing and removing portals | REG_DWORD | 1 = on, 0 = off | on |
| `AllowSnippetEdit` | Allow adding, editing and deleting snippets | REG_DWORD | 1 = on, 0 = off | on |
| `AllowTunnelAdd` | Allow adding tunnels and VPN profiles | REG_DWORD | 1 = on, 0 = off | on |

## Identity and directory (computer)

Read before any profile opens, so they exist only under `HKLM` (computer policy).

| Value name | Setting | Type | Accepted | Not set |
|---|---|---|---|---|
| `RequireDirectorySignIn` | Only profiles that sign in with the organisation (no local profiles) | REG_DWORD | 0 / 1 | off |
| `AutoProfileForWindowsAccount` | Open the profile bound to the signed-in Windows account automatically | REG_DWORD | 0 / 1 | on |
