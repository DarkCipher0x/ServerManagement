# Administration

## Requirements

| Item | Requirement |
|---|---|
| Operating system | Windows 10 1809 or later, or Windows 11, 64-bit (x64) |
| Runtime | none to install: the app is a self-contained .NET 10 build |
| Browser engine | Microsoft Edge WebView2 Runtime (included in Windows 11; the installer can add it through winget) |
| Optional | Microsoft Foundry Local for the default AI engine (installed, started and repaired automatically since 0.1.8; Settings → AI engine has the buttons; see the AI chapter); Ollama for GPU models (installer option); OpenSSH client for "open in Windows Terminal"; WSL for WSL shells |
| Rights | installing the MSI needs administrator rights (per-machine); running the app does not |

## Install, upgrade, repair, uninstall

- **Install:** run `DarkCipherServerManagement-{VERSION}-x64.msi`. Wizard: Welcome → Licence (MIT) → Folder → Options → Install. Options: desktop shortcut; WebView2 Runtime via winget (ticked only when it's missing); Ollama via winget.
  - Default folder: `C:\Program Files\DarkCipher Systems\ServerManagement`.
  - Start menu: *DarkCipher Systems → DarkCipher ServerManagement*, plus *DarkCipher ServerManagement - Repair or uninstall*.
  - **SmartScreen** shows "unknown publisher" because the MSI isn't signed: *More info → Run anyway*.
- **Portable:** `DarkCipherServerManagement-{VERSION}-x64-portable.zip` runs without installing. It uses the same data folders as the installed app.
- **Upgrade:** install the newer MSI over the top. It replaces the old version in place (same UpgradeCode) and keeps all data. Downgrades are refused. Close the app first. Taking a snapshot first (*Settings → Snapshots → Snapshot now*) is good practice.
  - **0.1.6 moved the program folder** from `…\DarkCipher Systems\Server Management` to `…\DarkCipher Systems\ServerManagement`. Upgrading removes the old folder and installs into the new one; your data (in `%APPDATA%`) doesn't move. Shortcuts are recreated; a taskbar pin made by hand may need re-pinning.
  - **0.1.9 renamed the product** from "DarkCipher Server Management" to **"DarkCipher ServerManagement"**: the window, the installer, the shortcuts and the *Installed apps* entry. The upgrade replaces the old shortcuts with renamed ones. The program folder, data folders, registry key and executable name (`DcxServerManager.exe`) don't change, so your data, settings and sign-in are untouched. New screenshots go to `Pictures\DarkCipher ServerManagement`; earlier ones stay where they are.
- **Version check:** the version shows at the top left of the window and in *Settings → Updates*.
- **Repair:** Start menu → *Repair or uninstall*, or Windows *Installed apps*.
- **Uninstall:** the program, shortcuts, the app's registry keys and its temp files always go. Your encrypted data is **kept** unless you tick it on the "Remove your data too?" page (0.3.0 adds the shared library membership, settings enforced on this PC, and "every Windows user"). Every choice is also a silent switch: see *Deployment switches* below.
  - The uninstaller runs `DcxServerManager.exe --cleanup …` as you and `--cleanup-machine …` as SYSTEM. **Never run either by hand**: they act on the real store and registry regardless of anything else.

## Deployment switches (install and uninstall)

Pass them on the `msiexec` command line; all are optional. Uninstall with the MSI file or its product code:
`msiexec /x DarkCipherServerManagement-{VERSION}-x64.msi /qn REMOVE_EVERYTHING=1`.

| Switch | When | Effect |
|---|---|---|
| `INSTALLDESKTOPSHORTCUT=0` | install | no desktop shortcut (default 1) |
| `INSTALLWEBVIEW2=1` | install | install the WebView2 Runtime with winget (ticked by default only when it is missing) |
| `INSTALLWINGET=0` | install | don't register winget (App Installer) for the installing user (default: on when winget isn't set up yet, off when it is; nothing is downloaded by the MSI) |
| `INSTALLNMAP=0` | install | don't install Nmap with winget (default: on when Nmap isn't installed, off when it is; Nmap's own Npcap may ask separately) |
| `INSTALLFOUNDRY=0` | install | don't install Microsoft Foundry Local with winget (default: on when Foundry Local isn't installed, off when it is; the default AI engine) |
| `INSTALLOLLAMA=1` | install | install Ollama with winget (default 0 since 0.5.1) |
| `AIFEATURES=0` | install / upgrade | writes the computer policy `AiFeatures = 0` (AI off for every user); pass it on every upgrade, or use Group Policy |
| `REMOVE_DATA=1` | uninstall | the uninstalling user's encrypted store (a moved store's files too, never the folder it was moved into) and their Windows Hello key |
| `REMOVE_LIBRARY=1` | uninstall | shared library membership: local copies and member keys (`library-*.dcx`); the team's share is never touched |
| `REMOVE_CACHE=1` | uninstall | cache, browser profile and error logs (`%LOCALAPPDATA%\DarkCipher Systems\ServerManagement`, and the pre-0.4.0 `…\DarkCipher\ServerManagement` if still present) |
| `REMOVE_SETTINGS=1` | uninstall | settings enforced on this PC: admin-protected records (`HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Protected`) and policy values (`HKLM` and signed-in users' `HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement`) |
| `REMOVE_ALLUSERS=1` | uninstall | do the data, library and cache removals for every Windows user profile on the PC, not just the uninstalling user (their Windows Hello keys can only be removed in their own sessions) |
| `REMOVE_EVERYTHING=1` | uninstall | all of the above |

Never removed: servers, cloud accounts, `%USERPROFILE%\.ssh`, backups and exported keys saved elsewhere, a shared library's share, Ollama and the WebView2 Runtime (separate programs). Domain Group Policy is re-applied by the domain after an uninstall; remove it from the GPO.

## Group Policy (0.3.0)

Almost every setting can be set by policy (214 in 0.5.1; 0.5.1 added the lock, restriction, add-on, dashboard, BeyondTrust and AI policies — see *Locks, restrictions and add-ons* below): `HKLM\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` (computer, wins) or `HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` (user). The business chapter has a baseline GPO and the identity, SIEM and branding set-up in full. The value name is the setting's name; DWORD for on/off and numbers, REG_SZ for text and decimals (REG_EXPAND_SZ for paths), REG_MULTI_SZ for lists. Accounts, keys, tokens and per-PC state are not policies.
- **Templates:** each release has `policy\DarkCipherServerManagement.admx` and `policy\en-US\DarkCipherServerManagement.adml` (also zipped as `…-policy-templates.zip`). Copy them into `C:\Windows\PolicyDefinitions` (or the domain's Central Store) for the Group Policy editor, or import them into Intune. Settings appear under *Administrative Templates → DarkCipher Systems → DarkCipher ServerManagement*, in both Computer and User Configuration.
- **Reference:** [POLICIES.md](../POLICIES.md) lists every policy with its type, accepted values and the app's default.
- **In the app:** a managed setting can't be changed (a change is put back and the status bar says why); *Settings → Security & privacy → Managed by your organisation* lists every managed setting, its value and source, and any value it ignored as invalid.
- **Order:** computer policy → user policy → admin-protected settings → the user's own settings.
- **Locks, restrictions and add-ons (0.5.1):** `LockedCategories` (one category key per line: audit, sessionlogs, errorlog, observe, credentials, team, knownhosts, hosts, infra, cloud, portals, beyondtrust, automation, commander, notes, incidents, snippets, tunnels, addons, privacy, settings) with `LockPolicyMode` (`uac` or `signin`) locks categories for everyone; `LockAuditLog` (default on), `AllowUserLocks`, `AllowItemLocks` and `LockOpenMinutes` (0–480) set the rest. Twenty-one `Allow*` values (`AllowHostAdd`, `AllowHostEdit`, `AllowHostDelete`, `AllowFolderEdit`, `AllowCredentialAdd/Edit/Delete`, `AllowCloudAccountAdd/Edit`, `AllowInfraAdd/Edit/Delete`, `AllowPortalAdd/Edit`, `AllowTunnelAdd`, `AllowSnippetEdit`, `AllowLibraryCreate/Join`, `AllowIntegrationEdit`, `AllowAddonInstall`, `AllowNetworkScan`) turn actions off: the screens say why, and add-ons get -32001 with the reason. Add-ons: `AddonsNeedApproval` (default on: install, update, enable, grant and remove need UAC), `AddonAllowedIds`, `AddonBlockedIds`, `AddonRequireSigned`. Dashboard: `DashboardLocked`, `DashboardCards` (lines `x:<id>|<type>|<target>|<title>|<normal or wide>`), with `DashboardOrder` / `DashboardHidden`. AI: `AiAllowEngineChange`, `AiAllowModelDownload`. BeyondTrust: the pins `BeyondTrustPraPin` / `BeyondTrustPsPin` (SHA-256 hex), `BeyondTrustAskReason`, `BeyondTrustJumpClient` (`console` / `web`), `BeyondTrustJumpGroupFilter`, `BeyondTrustJumpointFilter`.
- **In-app updates are switched off** in this build (no signing certificate yet). The app never contacts an update service. Install newer MSIs by hand.

## Where everything lives

| What | Where |
|---|---|
| Program | `C:\Program Files\DarkCipher Systems\ServerManagement\DcxServerManager.exe` |
| Encrypted store (default) | `%APPDATA%\DarkCipher Systems\ServerManagement` (movable: *Settings → Data & storage*; profiles in `profiles\<id>\`) |
| Cache, WebView2 browser profile, error logs | `%LOCALAPPDATA%\DarkCipher Systems\ServerManagement` (`WebView2\`, `logs\`) |
| Admin-protected records, install information | `HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Protected`, `…\Install` |

**0.4.0 moved the data folders** from `…\DarkCipher\ServerManagement` to `…\DarkCipher Systems\ServerManagement`: the
first start after the upgrade moves each folder once (audited: "moved to the DarkCipher Systems folder"); a store moved
elsewhere with *Settings → Data & storage* stays where it is. The uninstaller removes both generations.
| Snapshots | `snapshots\` inside the store, or a folder you choose (*Settings → Snapshots*) |
| Session logs | `sessionlogs\` inside the store (move with it) |

**Files in the store** (all encrypted with AES-256-GCM except where noted):

| File | Contents |
|---|---|
| `keyring.json` | the wrapped data key (DPAPI and/or master-password copies); never the key in clear |
| `data.dcx` | hosts, folders, credentials, keys, known hosts, snippets, tunnels, serial profiles |
| `settings.dcx` | settings, cloud accounts and their API credentials, terminal profiles |
| `reports.dcx`, `transfers.dcx`, `workspace.dcx` | AI/health reports; transfer history; open tabs for crash restore |
| `note-*.dcx`, `incident-*.dcx`, `incfile-*.dcx` | one file per note, incident and incident attachment |
| `store.dcx` | data-format versions and upgrade history |
| `audit-YYYY-MM.dcxlog` | the audit log, one encrypted entry per line |
| `snapshots\snap-*.dcxsnap` | sealed snapshots of the whole store |
| `sessionlogs\slog-*` | terminal session logs, encrypted per chunk |
| `running.marker` | plain timestamp: present while the app runs (crash detection) |
| `location.json` | plain: where a moved store now lives (only in the default folder) |

Each encrypted file is bound to its own file name, so files can't be swapped or renamed into each other.

## One copy per data store

From 0.1.3 only one copy of the app runs per data store. Starting it again (Start menu, shortcut) brings the running window, or its unlock screen, to the front and exits. Two processes can therefore never write the same encrypted files. A crashed instance doesn't block the next start.

For testing, a separate store can be used by setting the environment variable `DCX_DATA_DIR` to an empty folder before starting the exe. That copy is a separate instance with its own browser profile (`<folder>-webview2`) and logs (`<folder>-logs`), and it never touches the real store. This is how the self-test runs.

## Keys, sign-in and access

- **Default:** the data key is protected by **Windows DPAPI**, tied to your Windows account. No password is needed; another Windows user, or a copy of the disk, can't open the store.
- **Master password** (*Settings → Security*): PBKDF2-SHA256, 600,000 iterations. With **require on start** the DPAPI copy is removed, so only the password opens the store, even for an administrator.
- **Two-factor sign-in:** TOTP (RFC 6238) with any authenticator app; 10 one-time recovery codes. It gates opening the app. It doesn't strengthen the file encryption against someone holding a copy of the files.
- **App lock:** auto-lock after N idle minutes, lock with Windows, or Ctrl+Shift+L. Sessions keep running while locked; every window is hidden.
- **Sandbox mode:** password required at start, auto-lock 15 minutes, temp and share files wiped on exit.
- **Forgotten password:** there is no vendor recovery or backdoor. Keep an encrypted backup (next section) with a password you can find.

## Backup and recovery

- **Snapshots** (*Settings → Snapshots*): rolling encrypted copies of the whole store, daily by default with the newest 10 kept. They are also taken before every restore, import, data upgrade and app update. A snapshot only counts after it has been read back and checked, and old ones are removed only after a newer one has verified.
  - **Verify** is a restore drill: it decrypts and checks every file without changing anything.
  - **Restore** shows what changes, snapshots the current data first (so a restore can be undone), then restarts the app. The master password / 2FA and the audit log are never rolled back.
  - Snapshots use the store's own key, so they only work with this store on this account.
- **Encrypted backup / export** (*Settings → Security*): one `.dcxbackup` file with its own password (PBKDF2 600k + AES-256-GCM), restorable on another PC or account. Import can **Merge** or **Replace everything** (you type REPLACE).
- **Start-up checks:** every start finishes any restore or upgrade a crash interrupted and checks every data file. A damaged file is offered back from the newest good snapshot; the damaged copy is kept beside it (`.damaged-…`), never deleted. Data written by a newer version is not opened.
- **Crash restore:** after an unclean exit the app offers to reopen the host tabs that were open. Terminals reopen disconnected; press Enter to reconnect. Turn it off with *Settings → RestoreWorkspace*.
- **Moving the store** (*Settings → Data & storage*): the store, snapshots and session logs move together. A synchronised folder (OneDrive, Dropbox, Google Drive…) triggers a warning, because the store is meant to stay local.

## Network control

- **Privacy & network view:** every destination the app can contact, by class (Local / On-premises / User-configured / External ⚠), and what the app is listening on.
- **Offline / air-gapped mode:** one switch (status bar lock text, or *Settings → Offline mode*) blocks every connection outside your networks except destinations you approve.
  - It covers HTTP clients, SSH, RDP, web windows, links and add-ons.
  - It checks the resolved address and connects only to that address, so DNS rebinding can't get round it.
  - Every block is audited.
  - **Not covered:** Windows and WebView2 runtime services, and traffic made by external programs after launch.
- **No telemetry:** the app sends nothing about you anywhere. Internet AI providers are off by default and confirmed once when enabled. Dashboard checks of remote sources follow a rule you can see (*Checks ▾*).
- **Server trust:** SSH host keys are trusted on first use, with a loud warning on change. Proxmox, Unraid and vCenter certificates are pinned on first use (compare the fingerprint, e.g. `pvenode cert info`).

## Cloud providers and hypervisors (0.1.11)

### Hetzner Cloud
- **Token:** one API token per project (Hetzner Console → project → Security → API tokens). Use a *Read* token for look-only use and mark it read-only in the app; the app then refuses every change before sending.
- **Destination:** `api.hetzner.cloud` (External ⚠), contacted only when Hetzner screens, tests, the AI or a dashboard check you enabled ask.
- **Limits:** 3600 requests per hour per project. The robot and storage-box APIs are not supported.

### Microsoft Azure (0.2.0)
- **Identity:** an Entra app registration (single tenant) that signs in with a **certificate**. Create or obtain an RSA
  certificate, keep the .pfx (with its private key) for the app, and upload the public .cer to the registration under
  Certificates & secrets. Client secrets aren't accepted.
- **Roles:** on the subscription (Access control (IAM)): *Reader* to only look; *Contributor* or narrower roles (e.g.
  *Virtual Machine Contributor*, *DNS Zone Contributor*) for changes. The app doesn't manage roles.
- **In the app:** a new subscription is read-only until "Allow changes / read-only" in its Settings card; every change
  still asks first. Right-click a subscription to copy the certificate's SHA-256 thumbprint and compare it with the
  portal.
- **Destinations:** `login.microsoftonline.com` (sign-in) and `management.azure.com` (External ⚠), only when Azure
  screens, tests, the AI or a dashboard check you enabled ask; an Azure Monitor log sink adds its data collection
  endpoint (`*.ingest.monitor.azure.com`), contacted as events are shipped.
- **0.4.0 screens:** VM scale sets, availability sets, VM extensions, network interfaces, private DNS zones, container
  instances, PostgreSQL / MySQL flexible servers, Resource Health, Advisor. Roles for changes: *Virtual Machine
  Contributor*, *Private DNS Zone Contributor*, the database services' contributor roles; *Monitoring Metrics Publisher*
  on a data collection rule for the log sink.
- **Rotating the certificate:** upload the new .cer to the registration, then "Replace certificate…" in the card (the
  old one is kept until the new one signs in), then remove the old .cer from the registration.

### Google Cloud (0.2.0)
- **Identity:** a service account in the project and a JSON key for it (IAM & Admin → Service accounts → Keys → Add key
  → JSON). Delete the downloaded file after adding it; the app keeps an encrypted copy. Consider an organisation policy
  that limits key lifetime, and rotate with "Replace key…".
- **Roles:** *Viewer* to only look; for changes, roles for the services you use (e.g. *Compute Instance Admin (v1)*,
  *DNS Administrator*, *Cloud SQL Admin*). The APIs must be enabled in the project (Compute, Kubernetes Engine, Cloud
  Run, Cloud SQL Admin, Cloud DNS, Cloud Storage, Cloud Resource Manager); a disabled one shows "This API isn't enabled".
- **In the app:** a new project is read-only until allowed; while read-only the app only asks Google for read-only
  access.
- **Destinations:** `oauth2.googleapis.com` (sign-in) and `compute`, `container`, `run`, `sqladmin`, `dns`, `storage`,
  `cloudresourcemanager`, and since 0.4.0 `cloudfunctions`, `artifactregistry`, `pubsub` `.googleapis.com` (External ⚠),
  only on demand. Enable the matching APIs (Cloud Functions, Artifact Registry, Pub/Sub) for the new screens.

### VMware vCenter
- **Version:** vCenter 7.0+ (vSphere Automation API); standalone ESXi hosts aren't supported.
- **Account:** a dedicated user, with the Read-only role to look. To make changes, add *Virtual machine › Interaction › Power on/off* and *› Provisioning › Clone* where needed.
- **Certificate:** pinned on first use.

### Hyper-V
- **On this PC:** needs the Hyper-V PowerShell module.
- **Remote hosts:** need WinRM (`Enable-PSRemoting`) and an account in *Hyper-V Administrators*.
- **Sign-in:** Kerberos (your Windows sign-in) is the default. The app never enables Basic authentication, unencrypted WinRM or TrustedHosts.
- **Outside a domain:** use HTTPS WinRM with a trusted certificate, or run the app on the host.

### Where they appear
Cloud providers share one sidebar entry, **☁ Cloud providers**. vCenter and Hyper-V are under **Infrastructure**, with the other on-premises providers.

## Local API and OBS broadcast

- **Off by default.** *Settings → Local API & broadcast*. When on, it listens on **127.0.0.1:47321 only**; never on the network.
- **Tokens:** `dcx_…`, with scopes (`status.read`, `hosts.read`, `incidents.read`, `incidents.write`, `notes.write`) and an expiry. A token is shown once and only its hash is stored. Revoke one or all at any time.
- **Limits:**
  - browsers are refused (any Origin header), and the Host header must be 127.0.0.1 or localhost;
  - 64 KB bodies, 10 requests/s per token;
  - 20 bad tokens in a minute lock the API for 60 s;
  - nothing answers while the app is locked.
  - No endpoint returns credentials, keys, settings, terminals or files.
- **Example:** `Invoke-RestMethod -Headers @{Authorization="Bearer dcx_…"} http://127.0.0.1:47321/api/v1/status`
- **OBS broadcast** streams a terminal's screen as redacted text (secrets masked, `[sudo]` input hidden) to an OBS Browser source over the same loopback listener.

## Add-ons

- Installed from `.dcxaddon` packages (*Sidebar → SYSTEM → Add-ons*) after an approval screen showing the publisher, SHA-256, signature state, commands and every permission in plain words. Only ticked permissions are granted; high-risk ones start unticked.
- Each run is a separate process at **Low integrity** in a Windows Job (memory, process and time limits), with a clean environment. Files are hash-checked before every run; a modified add-on is disabled. Updates can't gain permissions silently.
- Add-ons can still read files and use the network on their own, and offline mode blocks them unless allowed. Install only add-ons you trust. The SDK is in Part 5.
- **Encrypted only (API 2).** Every message between the app and an add-on is AES-256-GCM with new keys for every run, and nothing else can open the running add-on. Since 0.1.18 every package must be built for API 2 to install, themes and other data-only add-ons included; data-only add-ons are stored encrypted and checked on every read.
- **Full control, if you grant it (0.1.18).** An add-on can ask to use anything the app can do: `app.read` (reads), `app.private` (notes, incidents, documents; asks each time), `app.control` (HIGH: any change, and you approve each exact action, typing the name for PRODUCTION), `hosts.write` (add, change or remove saved hosts, each change shown first) and `ui.control` (open screens). Every call is in the audit log under the add-on's id. Grant `app.control` only to add-ons you wrote or fully trust.

## Shared libraries (0.1.15)
- **Where:** the library folder can be any path every member can read and write: an SMB share, a NAS folder, or a folder synced by OneDrive or Syncthing. Give members modify rights on the folder; the app needs nothing else on the server. Back up the folder like any other file share. Every file in it is ciphertext, a signature or a public key.
- **Owner key:** the owner's keys live only in the owner's encrypted store (`library-<id>.dcx`). Keep a password-sealed copy (Shared library → ⚿ Back up my keys…) somewhere other than that PC, and include the store in your backups. If both are lost, the other members can still use the library, but nobody can appoint admins. To change owner while the key still exists, use Hand over ownership… (0.1.16).
- **Joining:** hand out the team add-on (`.dcxaddon`, no secret inside). Approve each request only after the member has read their fingerprint to you over a channel you trust.
- **Leavers:** remove them in Shared library → Members. Collections are re-keyed at once (the share must be reachable), and a rotation checklist lists every credential they could read. Change those on the targets: re-keying protects future changes only.
- **What the share can and can't do:** anyone with write access to the folder can delete or roll back files. Members' apps detect this, flag it and keep their newer cached copy, but cannot prevent it. Limit write access to the members.
- **Network:** a UNC share is a network destination and obeys Offline mode. Syncing happens at start-up, on Sync, after member changes, and on an optional interval per library, which is off by default.

## Logs and audit

| Log | Where | Notes |
|---|---|---|
| Audit log | *Sidebar → Audit log* | every change by you or the AI (actor `ai`): commands, service and power actions, cloud changes, exports, recovery. Encrypted per line; filter; export CSV |
| Error log | *Settings → Data & storage → View error log* | sealed per entry once the store is unlocked; lines written before unlock contain exception text only and are plain (known limitation) |
| Session logs | *Sidebar → Session logs* | per terminal profile, or forced on / off for every session (*Settings → Terminal & connections*, policy `SessionLogs`), encrypted; plaintext only through a warned export; forwarded over syslog TLS / to the log sinks when those switches are on |
| Syslog and log sinks | *Settings → Integrations*; *Sidebar → Observability* | the audit log, the application log and (optionally) session logs to a syslog collector over TLS and / or to Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk, Graylog or a webhook (0.4.0) — HTTPS only, masked, queued while unreachable; the business chapter has the field reference |
| Transfer history | *Commander → More → Transfer history* | last 300 transfers, encrypted |

## Self-test (for administrators)

The app tests itself without showing any window, against a scratch store:

```powershell
$env:DCX_DATA_DIR = 'C:\Temp\dcx-scratch'   # an EMPTY folder, never the real store
& 'C:\Program Files\DarkCipher Systems\ServerManagement\DcxServerManager.exe' --selftest C:\Temp\dcx-report.txt
```

The first line of the report gives the number of failures (0 expected); renders go to a `shots` folder next to the report. Remove `DCX_DATA_DIR` afterwards. Never combine it with `--cleanup`.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Can't connect (timeout) | address and port, firewalls, *◉ Check reachability*; raise the connect timeout in Settings; offline mode blocks external addresses |
| Permission denied / auth failed | credential and key on the host (*Edit*), keyboard-interactive for 2FA prompts, the user's shell |
| Host key changed warning | expected after a server rebuild; otherwise treat it as an incident. Compare the fingerprint before accepting |
| A second copy won't open | by design (0.1.3): the running window comes forward instead. Use *Pop out* for more windows |
| A terminal says "display restarted after …" | the browser process that draws it crashed and was replaced; the session was never interrupted. After 3 restarts in 10 minutes, reopen the tab |
| Blank web console / document viewer | close and reopen that window (no automatic recovery there yet) |
| "Data written by a newer version" | you installed an older MSI; install the newer one again (downgrades aren't supported) |
| Store in a synced folder warning | move it to a local drive (*Settings → Data & storage*) |
| SmartScreen "unknown publisher" | expected: the build isn't code-signed yet |
