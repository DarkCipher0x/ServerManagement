# Testing and stability

Everything here runs offline, against a scratch store, with no visible window.

## How to run
```powershell
dotnet build DarkCipher.ServerManagement.sln -c Debug -nologo -v q; cd src\DcxManager
# watched run: fails loudly if any window of the test process appears on screen
..\..\tools\run-selftest-watch.ps1 -Exe (Get-ChildItem -Recurse bin\Debug -Filter DcxServerManager.exe | Select -First 1).FullName -Scratch <empty scratch folder>
```
`build.ps1` runs the same self-test (on the Debug build, against a throwaway store, before it publishes the Release build)
and refuses to package if anything fails. When several sessions share the tree, run it from an isolated copy of `src` +
`samples` ([BUILD.md](BUILD.md) section 4). Where each test unit lives and what it covers: [SOURCE.md](SOURCE.md) section 15.

| Variable | Effect |
|---|---|
| `DCX_DATA_DIR` | the scratch store (required; the real store is never touched) |
| `DCX_AI_FEATURES` | `1` = the full suite (the watch script sets it; a fresh store is a new install, where AI starts off); `0` = the AI-off suite (`-AiOff`) |
| `DCX_FUZZ_SEED` | another fuzzing seed (default 20261018; runs are deterministic) |
| `DCX_FUZZ_ITER` | multiplies fuzz iterations (default 1 ≈ 8,000 calls) |
| `DCX_ENDURANCE` | multiplies the endurance loops (default 1). A "long" run is e.g. 20; the 24-hour soak is still deferred |
| `DCX_HANDLE_DIAG` | `1` = every endurance line also carries the handle census (types, threads by start module, managed pool), and each of the five screens runs ×6 on its own before the combined loop (as extra `diag:` ok lines), to show which screen grows |

## 0.6.20: Bitwarden speed, a remembered master password, Fill sign-in in steps, TOTP, Qwen's calls (ADR-100)
- **`SelfTestBitwarden.cs` 4d** (scripted bw): the unlock returns before the list and sync (they follow in the
  background); 200 searches answered from memory without starting bw; a sync reads the list again; locking drops it; a
  ticked master password is remembered and unlocks without a prompt, also at start-up; one Bitwarden refuses is forgotten
  (asked for again, or silently at start-up); bound to its server; unused and deleted once `BitwardenAllowRemember` is
  off; a login's TOTP read with it; RFC 6238 vectors (SHA1, SHA256, SHA512), otpauth:// / Base32 / steam:// settings; the
  master password in no audit entry. Section 2's unlock order is now status, unlock, list, sync, list.
- **`SelfTestPortals.cs`** (live WebView2, loopback page): a sign-in in steps fills the user name, then the password when
  its box appears, then the TOTP code; a form in an open shadow root is filled; a page with no form offers the copies
  (user name, password, TOTP code; none without a secret).
- **`SelfTestAgent.cs`**: qwen2.5-14b's `terminal_run` + JSON-on-the-next-lines call read as a call (and the same call
  written twice over); prose before it isn't; `MentionsToolUse` counts it; repeated JSON keys don't throw anywhere.
- **Fuzzing:** `totp-settings` and `ai-written-calls` (42 parsers in all).

## 0.6.17–0.6.19: vault credentials everywhere and the credential picker, AI server deploys, the default model (ADR-096–099)
- **Credential picker (ADR-099, `SelfTestBitwarden.cs` 4c, `SelfTest050.cs`):** sources, Bitwarden listed once (one unlock,
  held), words in any order and case, keys only for key sign-in, search by address, no secret in any row, 5,000 items
  filtered in milliseconds and capped at 500, Secrets Manager and Password Safe lists, a picked item added once and then
  shown as in the vault, the window filtering as you type and choosing; the host editor's 🔍 Find… for sign-in and sudo
  (user name filled, a direct link replaced, an untouched link kept); infrastructure Save accepting external credentials.
- **`SelfTestVultrDeploy.cs`** (ADR-097, fake Vultr): the order from the lists (newest LTS, ARM only when asked, ISOs / backups
  aren't OSes, regions by id / city, exact size match, cheapest fallback without IPv6-only or GPU plans, Windows from 2 GB, an
  exact plan id) and the AI tool end to end (approval text with the price, declined → nothing, one POST, waited for, host and
  vault credential saved, the password never in the answer or audit log, SSH port checked, defaults for region and size,
  refusals before asking, off by policy).
- **Default model (ADR-098, SelfTestFoundry / SelfTestFoundrySetup):** revision 6 per hardware (14B / 7B on GPUs, phi-4-mini
  on an NPU or a small PC, 7B on a big CPU), the catalogue's pick per GPU size, revision 5's phi-4-mini upgraded, a user's
  model kept.
- **`SelfTestBitwarden.cs` 4b** (scripted bw / bws): a login, an SSH key and a Secrets Manager secret added as vault
  credentials once each (a second add finds the first); a background read of a locked vault refused without a prompt; a
  host's SSH sign-in resolved synchronously on the UI thread (the wait keeps pumping; the test prompt unlocks once);
  sudo reuses the value within the hold (no second `bw get`); a host linked straight to an item signs in outside its
  tabs; an infrastructure client signed when the request goes (no read when it is built); a portal offers the external
  credential; the store's JSON holds no secret; a vault-kept key isn't exported; the vault lock forgets every value; the
  policy refuses even a held value; the audit names, never shows.
- **`SelfTest050.cs` (fake Password Safe):** a background use never starts a checkout; the first use checks out once and
  later uses (after the hold, and in the background) share it; app-lock check-in, after which the password is gone.
- **Pentest:** 6 new probes (forged Bitwarden / Password Safe links, a shared library credential or host pointing into
  this PC's vaults, an unknown source, a source answering without a secret, exporting a vault-kept key). 199 in all.
- **Fuzzing:** `vault-credential-links` (stored links parsed strictly). 40 then; 42 since 0.6.20.
- **AI Engineer:** a reply that refuses ("As an AI … I am unable to execute commands") before any tool ran is corrected
  and the call runs; first-person refusals recognised, ordinary answers not (SelfTestAgent).
- Hand tests (OUTSTANDING "0.6.17 …"): a live Bitwarden vault and Password Safe through hosts, infrastructure, a portal
  and sudo; the AI fix with phi-4-mini.

## 0.6.16: UniFi, the pre-launch sweep (ADR-094, ADR-095)
- **`SelfTestUniFi.cs`:** a fake console speaking the Network Integration API (path discovery, paging with a cap, devices
  with details and statistics, every change verified: restart by a new uptime, PoE back up, adopt, unadopt, guest on / off,
  vouchers, WiFi / network PUT without read-only fields, firewall PATCH; tampered ids refused before any request, the key
  only in its header, a redirect refused) and a fake api.ui.com (Site Manager across `nextToken` pages, a 429 waited out,
  ISP metrics, a console added through the Cloud Connector and read through it).
- **Pentest:** 16 new probes (Bitwarden ids, labels, environment, hidden item types, server and approval lists; UniFi
  addresses, console ids, object ids, hostile errors; the AI tool-call scanner's worst cases). 193 in all.
- **Fuzzing:** 8 new parsers with a stricter rule (only a parse error or the parser's own refusal may escape):
  `bitwarden-items`, `bitwarden-secret`, `bws-secrets`, `bws-value`, `unifi-error`, `unifi-inventory` (a hostile
  console answering every request), `unifi-sitemanager`, `ai-tool-calls`. 39 in all; it found the duplicate-key crash
  fixed in UniFi (SECURITY-REPORT §4d).
- **Sidebar:** the TOOLS / DOCUMENTS sections (SelfTest051) and the AI-off sidebar without an AI section.
- **AI Engineer:** `<tool_call>` closed six ways (incl. DarkCipher0x's `}}>`), and the screenshot's exact reply running.
- Hand tests (OUTSTANDING "0.6.16 …"): a live UniFi console, the Cloud Connector and Site Manager; the AI fix with
  phi-4-mini; the Bitwarden install button.

## 0.6.14: Bitwarden (ADR-093)
- **`SelfTestBitwarden.cs`:** the CLI runner on a real process (a copy of `cmd.exe` named `bw.exe`: refused while neither
  signed by Bitwarden nor approved, run once its SHA-256 is approved, refused again after one changed byte; the
  environment it receives with inherited `BW_SESSION` / `NODE_OPTIONS` / `NODE_TLS_REJECT_UNAUTHORIZED` removed; the time
  limit; `.cmd` wrappers refused); the vault against a scripted `bw` (API-key sign-in, wrong master password, unlock and
  sync order, lists without secrets, one item read, refusals before the CLI starts, offline mode, lock, idle lock, a
  server change); Secrets Manager against a scripted `bws`; host sessions from a login, an SSH key and a secret; the
  clipboard formats; policies and admin-protected switches; the Bitwarden screen (no secret on screen, renders
  `bitwarden.png`) and its Settings page. No real CLI and no network.
- Hand test (OUTSTANDING "0.6.14 Bitwarden"): a real `bw` / `bws` against a live account.

## 0.6.13: .NET 10 (ADR-092)
- No new suite: the whole self-test (full and AI-off) is the migration test and passes unchanged on .NET 10. The
  certificate fixtures load through `X509CertificateLoader`; the test web servers read at least one byte explicitly
  (CA2022).

## 0.6.12 suites and additions (post-quantum, Docs)
- **`SelfTestPqc.cs` (ADR-090):** the primitives (ML-KEM-768 and ML-DSA-65 sizes, round trips, a changed ciphertext /
  signature / message refused, the hybrid combiner binding both secrets and the context); the shared library's hybrid
  keys (export / import, hybrid wraps only under their context, classical wraps, changed or missing ML-KEM parts,
  ECDSA-only and mixed signatures all refused for a hybrid key; classical members and pre-0.6.12 audit keys still work;
  fingerprints cover the post-quantum halves); SSH key exchange order and the "require" mode (admin-protected, policy).
- **`SelfTest.UpdatesAndServices.cs`:** update.json needs both release signatures; with test keys (`Updater.TestKeys`) a
  fully signed release is offered, one without or with a foreign ML-DSA signature is refused.
- **`SelfTestProtected.cs`:** the Windows Hello wrap is the hybrid (signature + DPAPI secret), unlocking takes exactly
  one Hello prompt, and a pre-0.6.12 wrap opens and is upgraded on unlock.
- **`SelfTestDocs.cs` (ADR-091):** the document store (folders, pins with folder inheritance, rename / move / remove,
  search, revisions, trash, encryption on disk, damaged files set aside), the renderer (all blocks, only https links live,
  no image elements, raw HTML as text, a 4,000-line document within budget, the outline) and the Docs screen (smart
  lists, autosave, split preview, a host's pinned documents; renders docs.png).
- **`SelfTestAgent.cs`:** small models' loose and in-prose tool calls (ADR-088), the default target and the typed
  confirmation (ADR-089).

## 0.5.1 suites and additions
- `SelfTestVnc.cs` (`VncTests`): the VNC client against scripted fake RFB servers on 127.0.0.1 — RFB 3.8 with the VNC
  password (the fake checks the DES response with its own bit reversal), every encoding checked pixel by pixel (Raw,
  CopyRect, RRE, Hextile, ZRLE over one zlib stream), key / pointer / clipboard messages, a resize, the server clipboard,
  a rectangle outside the screen; Apple 003.889 with Apple Remote Desktop (a real 1024-bit DH; the fake decrypts the
  credentials), a wrong macOS password, RFB 3.3 without a password, a Mac without a user name, VeNCrypt Plain (refused
  direct, used in a tunnel), VeNCrypt X.509 (pinned on first trust, a changed certificate refused), hostile sizes.
- `SelfTest051.cs` (`Release051Tests`): item and folder locks with the clock moved, password locks and their throttle,
  the audit log locked by default and its prompt, policy-locked categories, add-ons and the local API refused locked
  items (Strict), padlocked cloud accounts, every `Allow*` policy and `AllowNetworkScan`, the add-on policy lists, host
  import against loopback SSH and Apple-VNC banners (rows, AI suggestions, import, duplicates), Hyper-V editions (Server,
  Pro, Home, rights notes, the elevated install faked), BeyondTrust pinning against a loopback TLS appliance, the
  Integrations pages, the dashboard catalogue (one card of each kind, a locked host, the lock policy, reset), Commander's
  none pane, the LOGS section, a sweep of every dropdown in Settings and the host editor, the VNC tab drawing, and the
  help coverage of the new features.
- `SelfTestPentest.cs` section J: 29 probes for the 0.5.1 surfaces (docs/SECURITY-REPORT.md §3 J).
- `SelfTestProfiles.cs`: Quick Start's fifteen pages each build; none asks for a protected change by being shown.
- The run starts with `LockAuditLog` off so the screen walks can open the audit log; the 0.5.1 suite checks the lock.

## 0.5.0 suites and additions
- `SelfTest050.cs` (`Release050Tests`): live screens (a portal added while the cached Portals screen is shown), dropdown
  names on every Settings tab, policy locks and 🛡 marks (a fake policy), drag and drop (folder moves, dashboard drop),
  Pingdom (fake API), Microsoft Teams (fake Graph and webhook), BeyondTrust (fake PRA and Password Safe, linked-host
  checkouts), the help's coverage of every new feature, the About title.
- `SelfTest050Data.cs` (`Release050DataTests`): GCM for any nonce length against .NET's AesGcm, every import format on
  made-up files (mRemoteNG default / custom master password / whole-file / pre-1.76, XXE refused), import apply, backup
  rules (passwords, exports off, no secrets) and scheduled backups, identity parsing and automatic profile choice,
  Kerberos SSH command lines.
- `SelfTest050Automation.cs` (`Release050AutomationTests`): Puppet command builders and parsers, the Puppet screen against
  a fake SSH executor (read-only refusals, dry runs, signing, runs, last-run summaries), WSL parsing and `.wslconfig`
  editing, IDE links and the SSH config text.
- `SelfTestPentest.cs`: a new section for the 0.5.0 surfaces (see `SECURITY-REPORT.md`).
- Additions: `SelfTestPolicies.cs` counts the computer-only ADMX policies; `SelfTestArchitecture.cs` the Identity &
  directory tab; `SelfTest.UpdatesAndServices.cs` uses a backup password of the new minimum length.

## 0.4.0 suites and additions
- `SelfTestObserve.cs` (`ObserveTests`): the event document (masked, one line, round trip, Azure columns); the sink rules
  (https only, kinds, pins, Azure Monitor ids, the list without secrets, duplicates / damage / more than 12 refused);
  every kind's payload shape and where the secret goes; live delivery to a TLS fake on 127.0.0.1 with a pinned
  self-signed certificate (Elastic ApiKey, Loki Basic + tenant, Azure Monitor bearer), a wrong pin refused and queued
  then delivered, no secret in statuses / messages / audit; the Grafana list through a fake (Bearer, org header, URLs
  kept on the server, 401 without the token); the Observability screen off screen; the policies and protected items.
- `SelfTestPentest.cs` (`PentestTests`): the penetration-test suite — see `SECURITY-REPORT.md` for every probe family and
  the results. Runs on every build; one `ok` line names the count.
- Additions in existing suites: `SelfTestAzureGcp.cs` checks every new Azure / Google catalog path (api-version, the
  subscription, allowed hosts, no secret-reading paths) and the Azure Monitor token for a second scope; `SelfTestAnsible.cs`
  the new commands; `SelfTestPolicies.cs` the folder migration and the renamed keys; `SelfTestThemes.cs` the Ocean sample
  theme and branding restore; `SelfTestHouseTheme.cs` the Branding & colours card; `SelfTestToolbox.cs` draws a loopback
  port pair that binds for both UDP and TCP; `SelfTest.cs` cancels keyboard-focus tooltips of the off-screen window
  before they open and counts them (a tooltip of an on-screen window still fails the run); the terminal fuzz failure
  message now says what the page showed.

## 0.3.0 suites (one file per area, all offline, 127.0.0.1 only, fakes for anything external)
- `SelfTestToolbox.cs` (`ToolboxTests`): SPF tree + evaluation, DMARC / DKIM key strength, blacklists (listed / refused /
  clean), MTA-STS / TLS-RPT / FCrDNS, the header analyser, NetTests against loopback servers (TCP banner, refused port,
  HTTP 301 → 200 with security headers, a self-signed TLS service, traceroute to loopback), the DNS client on the wire
  (fake resolver: compressed names, TC → TCP, propagation, a self-pointing name refused), WHOIS referral chain and ASN
  with fakes, the toolbox screen (8 tabs, header analysis, a TCP run into History).
- `SelfTestAnsible.cs` (`AnsibleTests`): inventory generation (safe unique names, groups), command building and
  validation, the job's stdin layout (passwords never in the script), the screen with a fake runner and a PRODUCTION
  confirmation answered by a test seam.
- `SelfTestProfiles.cs` (`ProfileTests`): the whole-app encryption audit over the live test store, profiles (index,
  folders, policy), bindings, JWT (RS256 only), the Entra flow against a fake identity provider (handler + loopback
  "browser"), the Windows-account sign-in with a substituted identity, LDAP certificate rules (pin), the cached proof,
  the setup window off screen.
- `SelfTestChecks.cs` (`ChecksTests`): dashboard tiles validated, a TCP tile OK → FAILING with audit and history, an HTTP
  tile, HostPerf parsing, the due rule, the Pinned checks card.
- `SelfTestVpn.cs` (`VpnTests`): WireGuard / OpenVPN import (secrets split out, commands refused), render (routes,
  management), routes and the routing-table read-back, status parsing, the manager with a fake engine (connect hook).
- `SelfTestSatellite.cs`, `SelfTestCommanderMc.cs` (+ brief mode, quick search), `SelfTestEditors.cs` (+ gutter and
  syntax colours), `SelfTestLibrary.cs` (+ groups), `SelfTestLan.cs`, `SelfTestNotify.cs`, `SelfTestMail.cs`.
- The architecture scan (`SelfTestArchitecture.cs`) also checks that `docs/POLICIES.md` matches the catalog and that no
  file below the screens calls the UI directly; the encryption audit (`EncryptionAudit`) fails on any plain file.

## Group Policy and uninstall (0.3.0-preview.2)
`SelfTestPolicies.cs` (`PolicyTests`, ADR-040): the catalog accounts for every setting (no secret is a policy); value rules;
enforcement from a made-up policy source (computer beats user, invalid values listed, load / save put managed values back,
the Settings card, render `settings-policy.png`); the ADMX / ADML names match and `docs/POLICIES.md` is current; the
uninstall clean-up on scratch folders and a fake registry (each option alone, moved store, Hello key, other users).

## Admin protection and Windows Hello (0.3.0-preview.1)
`SelfTestProtected.cs` (`ProtectedSettingsTests`, ADR-039). UAC and the elevated window are replaced by `AdminGate.FakeElevate`,
which does what the elevated copy does (reads and checks the request, writes the record) against a scratch file store;
Windows Hello by `WindowsHello.FakeHello` (deterministic signatures). The real HKLM record is never read in a test run.
- **ACL rule:** only administrators / SYSTEM / TrustedInstaller may own or write the store.
- **Requests:** the first carries every protected setting; expired, garbage, oversized, unknown-setting,
  out-of-range, bad-SID and other-format requests are refused.
- **Gate:** protect, change, decline at UAC (nothing applies), stricter values for the session only, an edited settings
  file losing to the record, no prompt for unchanged values, history.
- **Trust:** a loose ACL or an invalid value in a record is ignored and reported.
- **Settings:** Admin protection first in Security & privacy; 🛡 switches; render `settings-admin-protection.png`.
- **Hello:** sign-in wrap, right / wrong signature, cancel, the password as fallback, Hello off restoring the previous
  protection, the Hello AI lock, sensitive-action confirmation.

## The house theme (0.2.1)
`SelfTestHouseTheme.cs` (`HouseThemeTests`, ADR-038), in the full suite:
- **Theme packs and contrast (0.6.9):** every pack in dark and light (a pack without a light palette as DarkCipher
  Light) keeps body, strong and header text at 7:1, secondary text at 4.5:1 on the background and cards, status
  colours, its own accent and every accent swatch (as painted) at 3:1, and leaves the environment labels unchanged;
  each pack paints in both modes; a pack's own accent is painted as its light accent in light mode; DarkCipher stays
  the default; Settings shows one tile per pack with the chosen one outlined, and the everyday packs are rendered in
  both modes (`theme-<pack>-<mode>.png`); each pack paints the extra keys;
  status intensity changes the status colours; `CheckAccent` refuses a dim custom accent; `Normalize` repairs bad
  stored values.
- **Tokens:** corners reach `Radius`, `IconFont` is Segoe Fluent Icons, the new styles exist, reduced animation stops the
  header cursor's blink.
- **Settings layout:** the Appearance and Navigation tabs hold the right cards, every drop-down is 340 px, the terminal
  card's on/off settings are switches, captions counted, a Reset on each resettable card.
- **Import & export:** the export holds no key / token / password / host / path field, reading a file changes nothing,
  the preview lists each change, out-of-range values and unknown pages are skipped and listed, non-export / newer /
  oversized files refused, applying repaints and is audited.
- **Sidebar:** Fluent icons, pinning stops at 6 with a PINNED section first, icons only is a 52 px strip where every page
  keeps its name, compact rows, a hidden page (0.2.2) leaves the sidebar and its pin and comes back, Settings can't be
  hidden, bad landing page / style / pins / hidden pages corrected.
- **Menus:** short menus stay flat, long ones fold their groups; then every right-click menu on nine main screens is
  built as when it opens (through `Menus.BuilderOf`, never shown, a row selected), plus the terminal's (0.2.2, through
  `ShowMenuForTest`, recorded by `Menus.ShowAt`), and none has more than 12 top-level rows.
- **Theme add-ons** (`SelfTestThemes.cs`, 0.2.2): with the sample theme on, the house theme's extra keys exist,
  `FgStrong` is derived from the add-on's colours (not DarkCipher's) and headings reach 4.5:1 on its background.
- **Renders** (no window shown): `settings-*-tab.png`, `settings-light.png`, `settings-highcontrast.png`,
  `settings-about.png`, `settings-import-export.png`, `sidebar-icons.png`, `sidebar-pinned.png`.
- **No popups:** `RunFullAsync` records every `ContextMenu` and `ToolTip` that opens during the run (with the app code
  that opened it) and fails the run if there is any: a popup is a window on the screen.

## Three runs in every build (0.2.0)
`build.ps1` runs the full suite (AI on) and the AI-off suite on the Debug build, then the AI-off suite again on the
**published single-file exe** (what the MSI and zip ship) before packaging. A full suite against a published exe
(0.2.0-preview.4: 328 ok) skips only the three source scans that need the source tree next to the exe.

0.2.0 additions to the full suite (331 checks at the Azure / Google step):
- **Projects** (`SelfTestArchitecture.cs`): every library references only the projects below it (checked on the built
  assemblies), none references `Dcx.Ai`, and the embedded resources keep their names (`help/help.md` in Core, `web/*` and
  `sdk/*` in the app). **Settings-writer scan** (phase 7): every file besides AppSettings / FeatureLock that writes
  `settings.dcx` goes through a lock guard, and the scan still finds the three known writers.
- **Lock recovery** (`SelfTestAiOff.cs`, phase 7): a damaged settings file recovered from a snapshot comes back with AI
  and internet providers off while locked.
- **Azure and Google Cloud** (`SelfTestAzureGcp.cs`, against `FakeAzure` / `FakeGcp`; the certificate and key are made in
  memory for the run): catalog rules (every path in the subscription with an api-version / on an allowed Google host,
  none reading secrets); the sign-in verified by the fakes (Azure: PS256 assertion with the thumbprint, audience,
  issuer, lifetime and signature; Google: RS256 grant with key id and the read-only or full scope); token caching;
  `nextLink` and aggregated / `pageToken` pages; URL guards (other hosts, subscriptions, projects, plain http, a foreign
  project number) refused before sending; operations followed to success, failures reported with the cloud's reason;
  read-only refused locally; sign-in and API errors explained; no token, certificate, password or key in messages or the
  audit log; the VM screens in the Cloud providers workspace with state-aware buttons (renders `azure-vms.png`,
  `gcp-vms.png`); the AI cloud tools. Fuzzing adds six targets (both clouds' error and sign-in-error parsers, list-item
  flattening, Google key files, resource-path escaping).

## Two runs since 0.2.0-preview.1
- **AI on** (the full suite, 294 checks at 0.2.0-preview.1). Among them the architecture checks
  (`SelfTestArchitecture.cs`: seams, capability registry, app modules, the assistant and AI-namespace source scans) and
  `AiSwitchTests` (`SelfTestAiOff.cs`: the switch's rule and first-start migration, the lock with an injected clock —
  12+ characters, verifier only, 3 wrong → 30 s doubling to 10 min, change/remove, deletion outside the app audited,
  nothing typed in the audit — the module capability filter, the Settings card and the privacy row).
- **AI off** (`-AiOff`, `AiOffTests`, 6 checks): the app starts with `DCX_AI_FEATURES=0` and never registers the AI module.
  Nothing of it exists (contributions, hooks, assistant, analyst, dock, help articles); the sidebar's AI section reads
  no AI section (Reports in DOCUMENTS, Help in SYSTEM); 38 capabilities (the 6 terminal-target ones unavailable, every other 0.1.18 name kept); privacy row
  "OFF — module not loaded"; every sidebar screen plus a server view and a terminal are opened and their texts, buttons,
  tabs and tooltips searched for AI wording (the AI features card excepted); help search and log digest still work; no
  Foundry Local / Ollama process started. Since the audit the sweep also builds every right-click and "More ▾" menu
  on those screens (87) without opening a popup (`Menus.BuilderOf`) and checks every item.
- Both reports start with a **start-up line**: milliseconds from process start to the main window, private memory,
  handles and threads (preview tree: AI off 755 ms / 108 MB, AI on 773 ms / 111 MB). Since phase 7 `build.ps1` checks them against `tools\budgets.json` (with headroom for machine noise; the AI-on run is the first after a build and starts cold).

## What the self-test covers (282 checks in the 0.1.17 watched run; the count grows with each unit)
- **Add-on bridge, sandbox and full-control API** (`SelfTest.cs`, ADR-012/035/036, 0.1.17–0.1.18). A probe add-on runs in
  the real Low-integrity sandbox and Job object over the encrypted bridge:
  1. The process is at RID 0x1000 and no other process can open it for memory reads, handle duplication or injection;
     `app.info`, `hosts.list` (no secrets) and `incidents.addEvent` work; an ungranted call gets -32001, an unknown one -32601;
     writes to its install folder and Documents are denied.
  2. Misbehaviour: credentials only through the per-request prompt (denied → -32003 and audited; approved → that one
     secret, sent encrypted); a crash, a hang (killed at the timeout), a flood and a plain-text line on the encrypted
     bridge each end the run cleanly.
  3. API 1 program add-ons are refused at install and blocked if already installed; an add-on that skips the handshake
     gets nothing and is stopped.
  4. **ADR-036:** `app.capabilities` lists the bridge methods and app capabilities with risk and permission; `app.call`
     runs a read with `app.read`; `run_command` is refused without `app.control` and with it reaches the user's approval
     (declined → -32003, nothing ran); `hosts.add/update/remove` are each approved and validated; `ui.open` refuses
     unknown screens; every call is audited as the add-on. An API 1 theme is refused at install (`SelfTestThemes.cs`).
  5. Life cycle: revoke → -32001 on the next call, disabled → refused, a tampered file → blocked and disabled, updates
     keep grants with new permissions off, downgrades refused; the repository's hello-hosts sample is installed and run.
- **AI speed, agents and terminal control** (`SelfTestAgent.cs`, `AgentTests` + `FakeAi`, ADR-034, 0.1.17). Against a
  loopback fake engine (Ollama, OpenAI-compatible, Anthropic and Foundry Local endpoints, streamed or whole, with tool
  calls) and a real local PowerShell session: streaming on every engine, parallel read-only tools, the agent loop and its
  step limit, the text tool protocol for models without tool calling, the per-chat model, the memory guard, interrupt and
  target switch, the AI Engineer commanding its session (auto-run off → every command reviewed), and terminal typing
  latency. Timings are written to `ai-timings.txt` next to the report. No real model.
- **Terminal bridge and command risk** (`SelfTestRelease.cs`, 0.1.11, extended in 0.1.18): the PowerShell `__dcx`
  helper typed once per session, readable `. __dcx <id> '<command>'` lines, Base64 for multi-line or non-ASCII
  commands, re-typing a missing helper, nested `powershell -Command` judged by its inner command, reading cmdlets
  read-only, method calls on objects counted as changes.
- **Shared library** (`SelfTestLibrary.cs`, `LibraryTests`, ADR-033, 0.1.15–0.1.17). Three members (owner, editor,
  reader) against a scratch folder that plays the share, a fourth never approved: nothing readable on the share,
  non-members read nothing, readers can't write (even with a forged file), tamper / roll-back / vanishing detected,
  concurrent saves become a conflict resolved explicitly, the offline cache and outbox, the signed hash-chained audit,
  removal re-keys and leaves a rotation checklist, local hosts and credentials never changed, ownership hand-over, key
  backup and restore, the general and team add-on packages, the screens render.
- **Provider API coverage** (`SelfTestInfraCoverage.cs`, `InfraCoverageTests`, ADR-031, 0.1.14). Each against a fake of
  that API: Proxmox (per-action privileges, snapshots, pause, clone, migrate, vzdump, template, read views), vCenter
  (vCPU/memory verified, delete, guest info, host disconnect), Hyper-V (through the PowerShell seam), Docker (pause, kill,
  inspect, top, stats, image/network/volume removal), Unraid's extra mutations, and the Hetzner / Contabo / Vultr catalog
  additions checked against each official spec's endpoint list.
- **The s37 freeze** (`SelfTestFreeze.cs`, `FreezeTests`, 0.1.14). The UI error guard's decisions (logged once, then
  counted, then a loop); a screen that throws on every layout pass is closed while the app stays responsive (never a
  terminal); 20 "Dispatcher processing has been suspended" notices never open a box; SSH auto-reconnect stops on a
  refused sign-in; the real path (live terminals, then every Cloud providers screen) with the UI-thread hold measured;
  terminals never paint WebView2's white default background.
- **UI seams and layering** (`SelfTestArchitecture.cs`, `ArchitectureTests`, 0.2.0 phase 1). The WPF side of
  `Ui.Dialogs` / `Ui.Window` / `Ui.Thread` is registered; `CheckAccess`, `Invoke`, `Post`, `Every` and `TryInvoke` are
  checked from the UI thread and a worker thread; a dialog asked through the seam reaches `Views.Dialogs`' self-test
  refusal (the record is removed again, it was expected); `Ui.Window.Status` reaches the status line; `App.Log` forwards
  to `Log.Write`. Then a source scan (Debug builds, source next to the exe): no file below the screens may contain
  `MainWindow.Instance`, `Dialogs.*(`, `App.Log(`, `Application.Current`, `DispatcherTimer` or `Dispatcher.Invoke/…`,
  except the listed leftovers that move to the App project in phase 6. Skipped with an `ok` note when the source isn't there.
  **Capability registry (phase 2):** the 17 operator built-ins are the first registry entries, in their 0.1.18 order,
  each gating itself; `Capabilities.Schema()` = the registry = `Assistant.AllTools`; every name in
  `docs/baseline/capabilities-0.1.18.txt` is present; `AddonApi.RiskOf` / `PermissionFor` give the 0.1.18 risks
  (`terminal_read_screen` Private, unknown → null); a built-in runs through `Capabilities.RunAsync` (an unknown host is
  reported, nothing approved); `Redaction.Mask` equals `AiClient.Redact` and counts two secrets in a two-secret line.
  **App modules (phase 3):** the one registered module is "ai"; it contributes `aipanel` (open action + 4-item dock
  menu) and `assistant` (screen, header "✦ AI operator") to the AI section; both are in the sidebar; `Navigate("assistant")`
  opens the module's screen as a singleton tab and closes it again. Settings: the merged category list is the 0.1.18
  order with "AI & models" third; the module's three AI cards are the contributions in that category, `CategoryOf`
  places them, and revealing the category in a fresh Settings screen shows exactly those three. Menus and the error
  box: one "tab" menu contribution ("✦ Ask AI about this screen…", enabled with the Helper feature; unknown surfaces
  give nothing) and one error-box action ("✦ Explain this error with AI", or none while AI is blocked). Dock, shortcut
  and hooks: the right dock is the module's `AiDock`; Ctrl+Shift+A is registered and toggles it (Ctrl+A runs nothing);
  one window-ready hook and one start-up task are registered (the task never runs in the self-test).
- **Portal browser** (`SelfTestPortals.cs`, `PortalTests`, ADR-032, 0.1.15). Eight checks against a loopback test web server (`TestWeb`: plain HTTP and a self-signed HTTPS server on 127.0.0.1; nothing external, the one TEST-NET address is refused by offline mode before any connection); no window or dialog (confirmations through `Dialogs.ConfirmForTests`, pop-out windows off-screen):
  1. The sample package installs as data only; Portals follows install / disable / enable; 13 malformed packages refused; starter bookmarks added once.
  2. Rules: URL validation, address redaction, navigation schemes, safe download names, risky types, profile names per environment / host / override / named, Open web UI in the host menu.
  3. Bookmarks encrypted in data.dcx and read back.
  4. A live tab: `dcx-lab` profile, password saving / autofill / SmartScreen off, Not secure chip, LAB label, title; token masked; file: refused; `window.open` → a tab in the same profile closed by `window.close()`; download declined (nothing written) and accepted (saved to the chosen folder); Fill sign-in with a quote/script-laden password on its own origin (after the http warning) and refused on another origin; window mode pops out.
  5. Self-signed HTTPS: refused with the fingerprint, trusted → pinned and loaded, another certificate on the same port → CERTIFICATE CHANGED (replace asks for the typed host:port), Forget → asked again; pins encrypted.
  6. Offline mode refuses an external portal and a navigation to one before connecting.
  7. Privacy rows; renders `portals.png`, `portal-tab.png`, `portal-untrusted.png`, `addons-portal-browser.png`.
  8. A changed add-on file disables it and removes Portals / Open web UI.
- **Theme add-ons** (`SelfTestThemes.cs`, `ThemeAddonTests`, ADR-030, 0.1.12–0.1.13). Seven checks (header branding added in 0.1.13: name, logo, plate, title, restored afterwards; enabling = in use; 7 more malformed cases for logos and names, 19 in all) against the scratch store; no window or dialog:
  1. The repo's `samples/addons/sample-theme` is packed, inspected (theme-only: no program, permissions or commands), installed, and **Use** paints every colour it names in dark AND light; the environment label colours don't move; installing alone doesn't switch the theme.
  2. The UI font rule: Roboto is used only when installed (otherwise the app's own font stays).
  3. **Back to the default theme** and **Disable** restore the built-in palette and font exactly (all 25 colour resources compared); Enable brings the chosen theme back.
  4. A theme file changed after install is not applied; the add-on is disabled with the reason.
  5. 12 malformed themes refused at inspection (Env* keys, unknown keys, named / ARGB colours, unknown fields, wrong types, bad JSON, no colours, font injection, permissions, commands, missing file).
  6. **Remove** deletes the files, clears the choice and leaves the default palette in both modes. Renders: `shots\theme-sample-light.png`, `theme-sample-dark.png`.
- **0.1.11 release** (`SelfTestRelease.cs`, `ReleaseTests`, ADR-028). Nothing contacts the network, and no window or dialog is shown. Twelve blocks:
  1. **Hetzner provider:** registration and capabilities; **every catalog operation checked against Hetzner's official spec** (embedded endpoint list); normalisation.
  2. **Hetzner client** against `FakeHetzner`:
     - Bearer header and 3-page pagination;
     - RateLimit headers, and a 429 waited out with RateLimit-Reset;
     - 401 / invalid_input / token_readonly / protected messages;
     - a read-only token refused before sending, and an order not repeated after a 429;
     - no token in messages or the audit log.
  3. **Hetzner actions:** followed via `/actions/{id}` with progress; a failed action is an error; create waits for next_actions and returns the one-time password.
  4. **Hetzner UI in the workspace:** rows, state-aware buttons, power on watched to running (the list redraws and a late progress report can't overwrite the result). AI `cloud_list` and a declined action. Render `hetzner-servers.png`.
  5. **Cloud workspace:** tree collapsed; search; recent pruning; old links; picker. Render `cloud-workspace.png`.
  6. **Explorer defaults:**
     - `MigrateNavSettings` (and idempotent);
     - sidebar collapsed at start;
     - session-only expansion versus the opt-in, saved encrypted;
     - Hosts tree root collapsed, and stale folders dropped.
  7. **Settings:** every card has a category; one category shown at a time; search (telnet, ctrl+shift+s, nonsense); RevealCard; migration; reset of one category only, after a confirmation. Render `settings-categories.png`.
  8. **vCenter** against `FakeVCenter`:
     - endpoint rules;
     - one Basic sign-in, then the session id only;
     - hostile ids skipped;
     - guest shutdown verified;
     - 401 renewal, clone, 403 reason;
     - unsafe ids refused;
     - no password in any message.
  9. **Hyper-V:**
     - **real `powershell.exe` plumbing:** an injection-shaped value comes back unchanged and runs nothing;
     - **scripted seam:** inventory (bad ids skipped); a hostile checkpoint name passed only as JSON; shutdown verified; invalid inputs refused; missing module and access denied explained; the password only on stdin.
  10. **Command risk:** 28 commands across the four levels; multi-line and interactive flags.
  11. **AI tools:**
      - the registry and the operator's tool list;
      - current context;
      - notes off by default and asked per read;
      - a secret masked and an injected instruction fenced, with a citation;
      - bounded untrusted data;
      - the AI Engineer prompt rule.
  12. **Terminal bridge** in a **real local Windows PowerShell session:**
      - wrap/parse with the POSIX shell;
      - code blocks;
      - explicit target; exit status 3, working directory and output;
      - destructive review typed and declined;
      - Stop (Ctrl+C);
      - disconnected and reconnected targets refused;
      - output for the AI masked, bounded and fenced;
      - audit; Stop hidden afterwards.

      Render `ai-terminal-bridge.png`.
- **Updated for 0.1.11:**
  - the sidebar order tests (six sections);
  - the provider registry now includes Hetzner;
  - Contabo's UI test runs inside the workspace;
  - the Infrastructure "＋ Add ▾" menu now has six providers;
  - "every screen" includes `cloud`, `hetzner:reference` and Hetzner's screens.
- **Bug found by the renders:** ConPTY draws new lines with cursor moves, so the bridge treats row moves as line breaks. A cloud list's rows weren't redrawn after a status watch (Vultr, Contabo and Hetzner).
- **Foundry Local hands-off set-up** (`SelfTestFoundrySetup.cs`, ADR-026). The CLI, winget, the health probe and "end a process" are fakes; the self-test never touches the real Foundry Local or installs anything (SelfTest.cs disables winget and process-ending for the whole run).
  - **Parsing:** real Foundry Local 0.11 JSON from this PC: catalogue builds per device, sizes, downloaded, tools, loaded. Errors show message + code + hint; text output keeps its error lines.
  - **Model choice:**
    - an x64 CPU id from another PC resolves to the NPU build on a synthetic Copilot+ catalogue; an exact CPU pick is kept;
    - recommendations: NPU / 16 GB GPU / 8 GB GPU / CPU 16-8-4 GB; never a model without tools;
    - defaults are aliases, and revision 2's ids move to them.
  - **Worst-case pipeline:**
    - not installed → winget;
    - started, but the service doesn't answer → restart doesn't help → stop + end daemon (pid checked) + start;
    - x64 id → NPU build → downloaded (2.6 GB) → load fails with exit 4 → repaired → loaded and verified.
    - Every install, download and repair is audited.
  - **Gates:** offline mode never installs or downloads; on battery the automatic run defers a big download (a button press doesn't); a model not offered here is replaced by the recommendation and the status says so.
  - **Chat path:** a question asked while the service is down triggers one repair and is answered by a real loopback service; with the setting off, the error comes back untouched and nothing starts.
  - **Model guide:** rendered to `shots\foundry-models.png` (recommendation, NPU/GPU/CPU groups, size, downloaded, tools, "good for").
- **Robustness** (`SelfTestRobustness.cs`, ADR-025). Root causes of the s26 flakes, now tested deterministically.
  - **Hanging background check:** the AI engine is pointed at a loopback server that accepts and never answers. The AI operator screen is opened, then closed while its engine check is still waiting. It must be collectable within about a second (seen: ~0.4–0.6 s). Before the fix, the awaiting method held it for 4–8 s.
  - **Timer audit:** every app screen in the sidebar (20) is opened fresh and closed. Any copy left open by an earlier test is closed first. No `DispatcherTimer` may still be running for an element that isn't loaded.
    - The handler owner is resolved through lambda closures.
    - The dispatcher's private timer list is read by reflection, read-only, in the self-test only.
    - Excluded: the Vultr portal (a real website), cloud screens (provider APIs), local shells.
  - **Add-on flood:** the test add-on now floods with one pre-built line at full speed. Before, building each message with `ConvertTo-Json` was slower than the 5 s run timeout under load, which caused the "timed out" flake.
  - **No silent exits:** every early start-up exit in self-test mode writes a one-line FAIL report with the reason (another instance on the test store, store can't be opened, store wants a password, start-up recovery stopped) and never shows a box. `tools\run-selftest-watch.ps1` waits until the exe has been quiet for 3 s, keeps the process handle, and prints diagnostics if there's still no report.
- **Foundry Local / NPU engine** (`SelfTestFoundry.cs`, ADR-023). A fake `foundry` CLI behind `AiClient.FoundryCli` and a REAL loopback HTTP server (`/v1/models`, `/foundry/list`, `/v1/chat/completions`); nothing on screen.
  - **Parsing:** the current `foundry server status` output (colour codes, IPv4 + IPv6 URLs) and the old `service status` line; port 0 ignored; wildcard bindings mean loopback; "Unknown command … Did you mean 'server'?" detected.
  - **Model IDs:** an alias resolves to the NPU build the server has (else GPU, else CPU); an exact ID (the lightweight `qwen2.5-0.5b-instruct-generic-cpu`) is sent as set; no catalogue → the instruct build that starts with the alias.
  - **Discovery + chat:** the new CLI is asked `server status` only, and the chat request carries the resolved ID; an old CLI gets `server status`, then `service status`; not installed / not running give the `winget` / `foundry server start` texts; the start button runs `server start --idle-timeout 0`, falling back to `service start`.
  - **CLI runner:** a real `cmd` that leaves a `ping` child holding the output pipe returns in seconds, not when the child ends.
  - **AI default (ADR-024):** Foundry Local with the best model per hardware case (NPU → phi-4-mini; no NPU at 32 / 8 / 4 GB → Phi-4-mini CPU / Qwen2.5 1.5B / 0.5B); once per revision; a 0.1.5 default is re-chosen, a user's engine or model is kept; "Detect hardware" recommends the same and mentions Ollama only for a strong GPU.
  - **Set-up steps:** Settings opened with the fake CLI shows 1 Install / 2 Start / 3 Load / 4 Test, ticked ✓ installed (0.11.0), running at the loopback address, the selected model ready; rendered `shots\foundry-steps.png`.
  - **Isolation:** the whole self-test runs with a "not installed" stand-in for the `foundry` CLI, so a real Foundry Local on the test PC is never touched.
- **Infrastructure "＋ Add ▾"** (in the infrastructure check): a left click opens the four provider choices (recorded, not shown).
- **Crash recovery** (`SelfTestRecovery.cs`, ADR-022). No window is ever shown (editors and pop-outs are off-screen, the recovery screen is rendered to `shots\recovery.png`).
  - **Journal:** drafts sealed on disk (no plaintext), round trip + `StoreSchema` check, over-size drafts left out by name, file deleted when empty; no write while idle; a burst of 5 changes = exactly 1 write.
  - **Editor drafts:** typing journals source/path/original hash; Save and Discard clear it; a recovered draft is marked "not saved yet", journaled while open, and a changed file is not overwritten without the (recorded) confirmation.
  - **Transfers:** a paused job is journaled, rebuilt as the same job; a mirror's deletions are dropped with a warning; finished jobs leave the journal.
  - **Workspace v2:** three cmd tabs in a group, one split, one popped out, one active → closed → restored in two panes + a pop-out, same group and active tab, none connected.
  - **Recovery screen:** a missing host's transfer can't be ticked; only ticked items come back (draft reopened unsaved, transfer resumed and finished, unticked tabs not opened).
  - **REAL crash:** a child process (`--selftest-crash write`, its own scratch store) journals a draft, a paused transfer and a workspace, then is killed with `Process.Kill`; a second child finds the unclean exit and all three, encrypted; a third (clean) run still has them ("Later"); a fourth settles them and the journal is gone.
- **Stability** (`SelfTestStability.cs`, ADR-021). No window is ever shown.
  - **Single instance:** this store's guard is held; an abandoned holder counts as free; a REAL second process (`--selftest-second`, same `DCX_DATA_DIR`) exits 0 and the running instance counts an activation; a probe for another store gets through (exit 9, before any UI); the self-test's WebView2 profile is `<store>-webview2`, never the installed app's.
  - **Renderer recovery:** a live cmd tab; the REAL renderer process (found by the tab's page id through `GetProcessExtendedInfosAsync`, name-checked `msedgewebview2`) is killed; the display is recreated, earlier output replayed, the status says "display restarted after", the session is still connected and takes new input; recoveries 2–3 allowed, the 4th within 10 min refused with a clear message; a stale environment's failure doesn't reset the current one.
- **Cloud layer and Contabo** (`SelfTestContabo.cs`, ADR-020). Runs against `FakeContabo`, an in-process HTTP handler implementing the OAuth2 token endpoint and the API routes from Contabo's spec. There is no network and no real credentials.
  - **Provider layer:**
    - registry; capabilities (Contabo: no delete / resize / console);
    - catalog paths; state mapping; instance normalisation.
  - **Sign-in:**
    - the token form;
    - one sign-in reused; renewed once after a 401;
    - per-account tokens; UUIDv4 `x-request-id` plus `x-trace-id`;
    - wrong password / client / missing values give clear 401s that contain no secret.
  - **Resilience:**
    - 250 instances over 3 pages with one trace id;
    - 429 retried after Retry-After; 503 reads retried; a persistent 500 reported;
    - a rate-limited order is not repeated;
    - read timeouts retried ×4, but a timed-out order is uncertain after 1 attempt;
    - an unreachable API; malformed or unexpected answers; 400/403/404 parsing.
  - **Actions:**
    - power start/restart/stop paths;
    - order, rename, cancel contract;
    - an audit trail with status, time and request id.
  - **UI:**
    - the CONTABO sidebar; the screen loads through the shared engine;
    - no account picker in the header (removed in 0.3.0); no Delete button;
    - Start/Stop state-aware with the reason; busy marker; status watch;
    - copy-IP menu; account switch (tenant isolation); disabled accounts;
    - every Contabo screen opens; render `contabo-instances.png`.
  - **Hosts, dashboard, AI:**
    - import and reconciliation (changed IP, removed instance);
    - server-view buttons by capability;
    - AI cloud tools (declined = nothing sent);
    - the dashboard count, and a failing provider contained to its card.
  - **Security:**
    - offline mode refuses before connecting;
    - accounts encrypted on disk and restored after a reload;
    - no secret, token or `Bearer` in the error log or audit log.
- **Regression:** Vultr screens now run on the same shared engine. The existing every-screen, form-building and Vultr-offline checks still pass.
- **Terminal features** (`SelfTestTerminal.cs`, ADR-019). Telnet talks to a loopback fake device; local sessions are real cmd.exe shells.
  - **Profiles:** key-chord normalisation and the plain-typing guard, escapes, key-map line errors, scheme completeness, four encodings, host → profile → default resolution, encrypted settings.
  - **Telnet:**
    - refused while off; quick-connect forms;
    - negotiation byte by byte: NAWS (and again on resize), TTYPE, ECHO/SGA, unknown options refused, no re-answering, IAC both ways, NUL padding, CR LF;
    - a Telnet tab with a Latin-1 profile (output and input), the unencrypted status, host tools refused, audit.
  - **Page:**
    - the profile reaches xterm (probe);
    - a key mapping and Backspace ^H through the page's own key handler;
    - scrollback search: count, order, previous/next, case, regex, wrapped line, bad regex, no match; the find bar count.
  - **Session logging:**
    - escape stripping, CR overwrite, split sequences, backspace, row moves as line breaks, timestamps;
    - encrypted on disk with no host name in the file name;
    - a real logged cmd session; the Session logs screen; delete limited to log files.
  - **Broadcast input and tab groups:**
    - refusal starts nothing;
    - two real shells both receive typed input; strips; stop;
    - PRODUCTION typed; a closed member ends it;
    - groups: names, the label on the tab, broadcast to a group.
  - **Renders:** `terminal-features.png`, `session-logs.png`.
- **Engineering dashboard** (`SelfTestDashboard.cs`, ADR-018). Infrastructure uses a fake provider (nothing contacted); terminals are created without connecting.
  - **Model:** card order, hide and columns; sessions counted by kind; every local warning with its level and fix target; the check rule (manual / on open / interval, per-source opt-in, offline skip); defaults.
  - **Remote checks:** a contact log proves nothing is contacted unless included, manual mode contacts nothing, and an offline-refused source is skipped without a blocked attempt. Also checked: last-known state with its source and time, Check now, the interval timer only in interval mode and only while on screen, and the Privacy row.
  - **Local facts by events:** sessions, PRODUCTION, notes, incidents and warnings update the cards. 100 events while hidden → no redraw; 500 events → at most 3 redraws; closing a tab updates the card.
  - **Customise and lifetime:** move / hide / reset saved in settings; a disposed dashboard leaves no handler and is collected; render `shots\dashboard.png`.
- **Environment labels** (`SelfTestEnv.cs`, ADR-017): vocabulary, production confirmations (recorded, never shown), tabs and strips, live updates, no handler left.
- **Offline mode** (`SelfTestOffline.cs`, ADR-016). Uses a fake resolver, so there is no real DNS, and only TEST-NET addresses, which are never contacted.
  - **The rule:**
    - the allow / block matrix;
    - approved name / suffix / IP / CIDR entries;
    - validation;
    - fail-closed behaviour.
  - **Loopback HTTP:**
    - it connects to the checked address;
    - redirects are not followed;
    - DNS rebinding is caught at connect;
    - no socket is opened for a blocked request.
  - **Real clients:** AI, Vultr, SSH, web window, mstsc, add-ons.
  - **UI:** the badge, the Privacy states, encrypted persistence, the audit log, a render.
- **Interactive prompts:** any `Dialogs.Prompt` reached during the self-test is refused, and the report fails.
- **Functional:** every screen, store crypto, every roadmap unit (sessions 6–22). See the `ok` lines in the report.
- **UI font size and window sizing** (`SelfTestUi.cs`, ADR-015):
  - the type ramp at 100 % and 125 %;
  - the sizing rule;
  - each formerly fixed window;
  - a source scan that fails on any raw interface `FontSize` number.
- **Parser fuzzing** (`SelfTestStress.cs`): 25 parsers that see untrusted input.
  - **Server output:** LinuxProbe, Docker CLI JSON and multiplexed logs, Proxmox resources and permissions, Unraid schema and inventory, the update manifest, the SCP `stat` lines.
  - **Our own front door:** local API HTTP requests.
  - **Files:** store files, snapshots, add-on zips, PDFs.
  - **Typed text:** terminal and paste text (PasteGuard), secret masking, broadcast redaction, quick connect, note queries, serial quick connect, Vultr templates, colours, URLs, help search.
  - **Inputs:** mutations of valid samples (bit flips, cuts, duplication, hostile tokens, 50–2,000× repetition) plus pure random bytes.
  - **Bug classes:** NullReference / IndexOutOfRange / ArgumentOutOfRange / KeyNotFound / InvalidCast / regex timeout; any exception at all from a parser that promises not to throw; and any call above its time budget (250 ms for cheap parsers).
  - **Repro:** each failure writes its input to `<report dir>\fuzz\<parser>-<n>.bin`.
- **Terminal host bridge:** about 640 KB of hostile output through the real WebView2/xterm page:
  - OSC 8 `javascript:`/`file:` links, 100 KB titles, 5,000-parameter SGR, DCS, an OSC 52 clipboard grab, invalid UTF-8;
  - huge cursor / insert / delete / scroll / repeat parameters, including split across two writes;
  - random binary.

  The page must answer after every named input and render normal text afterwards, and nothing may reach the error log.
- **Endurance loops**, measured after two warm-up rounds with three GC passes. Each has limits on the growth of handles, GDI and USER objects and private memory:
  - terminal tabs (WebView2) open/close;
  - AI sidebar detach/reattach;
  - UI font/size changes;
  - five screens open/close, against a fake **hung Ollama** (a loopback listener that accepts connections and never answers; the Ollama URL points at it during the loop and is restored afterwards). The loop first checks that Settings really contacts it (s36c);
  - note create/save/delete;
  - 150 local API requests;
  - RDP ActiveX create/destroy.

  **Handle census (s36c, `SelfTestHandles.cs`):** a failing loop names what grew, not just a total. It reports kernel handles by object type (Event, File, Key, Section, … from the system handle table, filtered to the app's PID; only type names are queried, never object names, which can hang on pipes), threads by the module their start address is in (managed pool threads start in the app/coreclr, native pool/RPC/COM workers in ntdll), and the managed thread-pool size before → after. Event growth together with managed pool growth means pool threads were blocked (starvation), not handles leaked.
- **Closed views are released:** 20 screens (every closable one, plus a terminal) are opened, closed and must be collected (WeakReference, polled up to 15 s). A static event or field holding a view fails the test.

## Found and fixed by this (session 23)
| Finding | Fix |
|---|---|
| `ESC[99999999L` from a server froze the terminal tab (xterm inserts lines one at a time) | CSI numbers above 9999 are clamped at the host bridge, including sequences split across writes (`TerminalView.ClampCsi`) |
| Secret masking took 750 ms on 60 KB of repeated `-----BEGIN … KEY-----` / dashes (quadratic regex) | PEM pattern on the linear-time engine; bounded key-name prefixes; 500 ms limit per pattern that fails closed (the text is withheld, never sent unmasked) |
| Docker CLI parser threw on duplicate JSON keys (whole inventory lost) | Lines are parsed into flat maps inside the try; a bad line is skipped |
| Update-manifest parser threw on duplicate keys / wrong types | Answers "malformed" like invalid JSON |
| Unraid schema reader: NullReference on missing names / nulls | Missing entries are skipped; malformed answers become a clear error |
| SCP `stat` parser threw on a mode string with digits 8/9 | Octal digits validated first |
| Settings and Add-ons screens stayed in memory after closing (static events / `Current`) | Subscribed only while on screen; `Current` cleared on close |
| Self-test showed windows on the user's screen (pop-out / detached AI at 0,0; a maximised AI window for 150 ms) | Detached windows stay off-screen during the self-test and can't maximise; the test minimises instead |

## Not covered (stated honestly)
- **Sleep/resume and VPN changes:** they need real power and network events (by hand: OUTSTANDING.md).
- **The 24-hour soak:** deferred by DarkCipher0x. `DCX_ENDURANCE=20` gives a longer endurance run in the meantime.
- **Remote servers:** SSH, SFTP, SCP, RDP, Docker, Proxmox and Unraid are tested against fakes or refusals only.
