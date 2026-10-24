# The project tree and how it is built out

This document explains the shape of DarkCipher ServerManagement as a tree — what each branch is for, how a feature flows
through it, how it is built, tested and released — so a reader can find anything in the repository from this page alone.
The generated companion `PROJECT-TREE.generated.md` (from `tools\write-tree.ps1`) lists every folder with its files and
line counts for the exact version. The file-by-file reference is `SOURCE.md`; the decisions behind the shape are the
ADRs in `ARCHITECTURE.md`.

## 1. The repository at a glance

```
DarkCipher-ServerManagement\
├─ build.ps1                       the release build: self-test → publish → MSI/zip → policy templates → SBOM → bundle (PDFs, source) → checksums
├─ DarkCipher.ServerManagement.sln ten projects (nine libraries + the app)
├─ Directory.Build.props           build hygiene for every project: analyzers as errors, locked restore, deterministic output
├─ README.md · LICENSE (MIT) · THIRD-PARTY-NOTICES.md
├─ src\                            the code (section 2)
├─ samples\addons\                 the shipped add-ons, the SDK and pack.ps1 (section 6)
├─ installer\                      the WiX 5 MSI definition (+ msix\ for the Store package)
├─ tools\                          release tooling: guide PDFs, tree table, public source + privacy scan, SBOM, checksums, budgets, MSIX, icon, watched self-test
└─ docs\                           architecture (ADRs), security + security report, source and build references, testing, policies, release notes, the guides' chapters, history\
```

## 2. The code: ten projects, references point down only

```
                 ┌──────────────────────────────────────────────┐
                 │  DcxManager (the exe)                         │  App start-up and WireLayers(), MainWindow + Shell\, every screen (Views\),
                 │  Views\ Shell\ Services\ Api\ Rdp\ SelfTest\  │  app services, the local API, RDP, the self-test
                 └───────────────┬──────────────────────────────┘
          ┌──────────────────────┼───────────────────────────────────────────┐
   ┌──────┴──────┐    ┌──────────┴─────────┐    ┌────────────┐    ┌───────────┴────────┐
   │  Dcx.Ai     │    │ Dcx.Browser        │    │ Dcx.Notify │    │ Dcx.Library        │   Ai: every AI feature as a module that can be off
   │ (module)    │    │ portal browser     │    │ syslog TLS │    │ shared library     │   Notify: syslog, log sinks, Grafana, SMTP, notifications
   └──────┬──────┘    └──────────┬─────────┘    │ log sinks  │    └───────────┬────────┘
          │                      │              │ Grafana    │                │
   ┌──────┴──────┐    ┌──────────┴─────────┐    │ SMTP       │    ┌───────────┴────────┐
   │  Dcx.Cloud  │    │ Dcx.Addons         │    └─────┬──────┘    │ Dcx.Infra          │   Cloud: Vultr, Contabo, Hetzner, Azure, Google on one engine
   │ 5 providers │    │ sandbox, bridge,   │          │           │ Docker, Proxmox,   │   Addons: packages, Low-IL sandbox, encrypted bridge, themes
   └──────┬──────┘    │ themes, API        │          │           │ Unraid, vCenter,   │   Infra: hypervisors and containers, TLS pinning
          │           └──────────┬─────────┘          │           │ Hyper-V, Satellite,│
          │                      │                    │           │ Synology, DCX-CTL  │
          │           ┌──────────┴─────────┐          │           └───────────┬────────┘
          └───────────┤  Dcx.Ui            ├──────────┼───────────────────────┘
                      │ WPF kit: Dialogs,  │          │
                      │ Menus, theme,      │          │
                      │ typography, seams, │          │
                      │ Branding           │          │
                      └──────────┬─────────┘          │
                      ┌──────────┴─────────┐          │
                      │ Dcx.Connectivity   │          │   SSH, sessions (ConPTY, serial, Telnet), files (SFTP/SCP, transfers, sync),
                      │ Ssh\ Sessions\     │          │   reports (Linux + This PC + Event Viewer), network and mail tools, Ansible, VPN
                      │ Files\ Reports\    │          │
                      │ Net\ Mail\ Ansible\│          │
                      │ Vpn\               │          │
                      └──────────┬─────────┘          │
                      ┌──────────┴─────────┐          │
                      │  Dcx.Core          ├──────────┘   the encrypted store (SecureStore), settings, records, audit, offline mode,
                      │ Services\ SignIn\  │              policies and protected settings, profiles, directory sign-in, masking, help,
                      │ Notes\ Incidents\  │              notes, incidents, documents, AppInfo (folders, migration) — no WPF
                      │ Documents\ Assets\ │
                      └────────────────────┘
```

Rules the compiler and the self-test enforce: a project references only projects below it; nothing below the screens
calls the UI except through the `Ui` seams in Core; `Dcx.Ai` is loaded only when AI features are on; every `HttpClient`
is built on the one network gate (`Offline.Handler()`); every stored file goes through `SecureStore`.

### What lives where in the app project

```
src\DcxManager\
├─ App.xaml(.cs)        start-up order (profiles → store → policies → migration → modules → window), WireLayers() = the composition root
├─ MainWindow.xaml(.cs) + Shell\   the window: header, sidebar (sections, pins, icon rail), tabs and panes, navigation keys, quick connect, start-up flow
├─ Views\               one file per screen or window; Settings\ (one partial file per card group), Terminal\ (the terminal in parts),
│                       Toolbox\, Lan\, Editors\; ObservabilityView, ReportView, DashboardView, CredentialsView, …
├─ Services\            app-level services: dashboard overview and checks, VPN manager, LAN monitors, privacy report, recovery, screenshots, app lock
├─ Api\                 the loopback local API and OBS broadcasts
├─ Rdp\                 the embedded Remote Desktop control
├─ Themes\Controls.xaml every control style; colours and fonts only through DynamicResource
├─ Assets\              app.ico, logo.png, web\ (terminal.html + xterm.js)
└─ SelfTest\            the whole test suite (section 5)
```

## 3. How a feature flows through the layers

Take **log sinks** (0.4.0) as the worked example; every feature follows the same path.

```
AppSettings (Core)          ObserveEnabled, ObserveSinks (JSON, no secrets), ObserveSecrets (excluded from policy), ObserveSend*
   │  PolicySettings         every simple-typed setting is a Group Policy unless listed in Excluded with a reason (self-test: none unaccounted)
   │  ProtectedSettings      the admin-protected items observe.* with their own value checks (the sink JSON is checked by Dcx.Notify's rules)
   ▼
Dcx.Notify\Sinks            LogEvent (masked at creation), LogSink (model, rules, payload per kind), LogShipper (queue, spool, TLS pin, back-off)
Dcx.Notify\Grafana          GrafanaClient (dashboard list, URL rules)
   │  Offline.Handler()      the one network gate; SecureStore for the spool
   ▼
App.WireLayers()            SessionLog.Forward → shipper; ProtectedSettings.SinksCheck = LogSink.Check; LogShipper.AzureToken = AzureClient.MonitorTokenAsync
App.OnStartup / OnExit      LogShipper.Start() after the store is open; Stop() writes the spool
   ▼
Views\Settings\SettingsView.Observe.cs    the Log sinks and Dashboards cards (admin gate, secrets straight to the encrypted settings)
Views\ObservabilityView.cs                 the screen: state per sink, activity bars, Grafana list, links → WebWindow
Shell\MainWindow.Navigation / Sidebar      the nav key "observe"
   ▼
SelfTest\SelfTestObserve.cs  rules, payload shapes, a TLS fake on 127.0.0.1 with a pinned certificate, a Grafana fake, the screen, the policies
docs\                        help.md articles, SECURITY.md row, ADR-056, release notes, the business guide, POLICIES.md (generated)
```

### What 0.5.0 added, by folder

```
Dcx.Core\Services          BackupPolicy.cs (export / import rules, ScheduledBackup), AppSettings (Backup*, Sso*, Ide*, Teams*, BeyondTrust*, Puppet*)
Dcx.Core\SignIn            WindowsAccount.cs (identity, dsregcmd, klist), EntraDelegated.cs (delegated Graph tokens, PKCE loopback)
Dcx.Connectivity\Import    ConnectionImport (model, cleaning, safe XML), one file per source (mRemoteNG + GcmAnyIv, SecureCRT, Royal TS,
                           MobaXterm, PuTTY / KiTTY, WinSCP, RDCMan, .rdp, Devolutions), ConnectionImportApply
Dcx.Connectivity\Puppet    PuppetCommands (server, agents, code, PuppetDB PQL, Bolt; builders and parsers)
Dcx.Connectivity\Wsl       WslManager (distributions, files, .wslconfig, wslc)
Dcx.Connectivity\Ide       IdeIntegration (editors by URL scheme, dcx_config, ssh-agent)
Dcx.Connectivity\BeyondTrust  PraClient (PRA / RS config + reporting APIs, jump links), PasswordSafeClient (check-out / check-in)
Dcx.Connectivity\Ssh       KerberosSsh (GSSAPI through Windows OpenSSH)
Dcx.Notify\Teams           TeamsShare (Graph channel / chat messages with hosted images, Workflows webhooks)
Dcx.Notify\Pingdom         PingdomClient (read-only checks)
Dcx.Ui\Views               Live (subscribe while shown), DragGhost (drag picture, drop outline, slide animation), SettingTag
DcxManager\Views           PuppetView, WslView, BeyondTrustView, ConnectionImportDialog, TeamsUi, IdeUi;
                           Settings\SettingsView.Locks / Backup / Identity / Automation (Automation, IDE, Pingdom, Teams, BeyondTrust cards)
DcxManager\Services        PasswordSafeSessions (credentials piped into sessions in memory, checked in on close / expiry / exit)
DcxManager\SelfTest        SelfTest050.cs, SelfTest050Data.cs, SelfTest050Automation.cs; SelfTestPentest.cs section I
```

### What 0.5.1 added, by folder

```
Dcx.Core\Services          Locks.cs (category / item locks, open state, Strict for add-ons), Restrictions.cs (Allow* → actions),
                           HostIdentify.cs (AI seams: IHostIdentifier, LocalAiSetup), TlsPin.cs (moved from Dcx.Infra)
Dcx.Connectivity\Vnc       RfbClient (handshake, reader), RfbAuth (VNC, Apple Remote Desktop, VeNCrypt), RfbDecoders (Raw,
                           CopyRect, RRE, Hextile, ZRLE), RfbKeys, VncConnector (SSH tunnel or direct)
Dcx.Connectivity\Net       HostDiscovery (scan, identify, candidates)
Dcx.Connectivity\BeyondTrust  BtTls (trust-on-first-use pins for PRA and Password Safe)
Dcx.Infra\Infra            HyperVSetup (editions, module fix, rights, elevated install)
Dcx.Ai\Ai                  HostImportAi (✦ Identify with AI)
Dcx.Ui\Views               AdminApproval ("Apply (admin approval)…"); Dcx.Ui\Services\WindowsSignIn (Hello, then CredUI + LogonUser)
DcxManager\Views           VncView, HostImportWindow, DashboardEditor, DashboardView.Cards, ErrorLogView, LockUi, LockGate,
                           AdminApproveWindow; Settings\SettingsView.Padlocks (Locks & restrictions), SettingsView.BeyondTrust
DcxManager\Services        DashboardCatalog (added dashboard cards)
DcxManager\SelfTest        SelfTest051.cs (Release051Tests), SelfTestVnc.cs (loopback RFB fakes), SelfTestPentest.cs section J
```

## 4. The build pipeline (`build.ps1`)

```
0  version guard            <Version> from the csproj; refuses to rebuild an existing dist\<ver>
1  self-test gate           dotnet build (Debug) → --selftest full suite (AI on) → AI-off suite → budgets (start-up time, memory)
2  publish                  dotnet publish (Release, self-contained single file, no .pdb) → optional Authenticode → the published exe self-tested
3  MSI                      WiX 5: dist\<ver>\DarkCipherServerManagement-<ver>-x64.msi (UpgradeCode fixed)
4  manifest, notes, zip     update.json · release notes copy · portable zip · policy templates (ADMX/ADML + POLICIES.md) · CycloneDX SBOM
5  checksums                <file>.sha256 + SHA256SUMS
6  release bundle (0.4.0)   PDFs: Complete guide, User guide, Developer guide, Business guide, Security report, Project structure (tools\make-guide.ps1 -All,
                            tools\write-tree.ps1 for the table) · the sanitised source archive with its privacy scan (tools\write-public-source.ps1) · checksums again
```

Everything the build needs is pinned: the .NET SDK version (`global.json`), NuGet packages with hashes
(`packages.lock.json`), the WiX tool (`.config\dotnet-tools.json`). Builds are deterministic and strip local paths.

## 5. The self-test

One exe, one switch: `DcxServerManager.exe --selftest <report>` against a scratch store (`DCX_DATA_DIR`). The suite is a
tree of its own, one file per area under `src\DcxManager\SelfTest\`:

```
SelfTest.cs (+ SelfTest.*.cs)   the core suite by area and the final guards: no dialog reached, no popup or tooltip on screen
SelfTest<Unit>.cs               one unit each: Offline, Env, Dashboard, Terminal, Contabo, Recovery, Stability, Foundry, Robustness, Release,
                                AzureGcp, HouseTheme, Protected, Policies, Export, SshKeys, QuickConnect, Notify, Observe, Lan, Mail, LibraryAccess,
                                DcxCtl, Synology, Satellite, Editors, CommanderMc, Toolbox, Ansible, Profiles, Checks, Vpn, InfraCoverage, Freeze,
                                Architecture, AiOff, Themes, Portals, Library, Agent, Pentest, Stress (fuzzing, endurance, leaks)
Fakes                           in-process HTTP fakes (Azure, Google, Hetzner, vCenter, Contabo, Grafana…), TLS servers on 127.0.0.1 (syslog, log sinks,
                                SMTP, web), a fake resolver, fake engines (VPN, Ansible, Foundry), a substituted Windows identity
```

Rules: no visible window (everything off-screen, never activated), no dialog (refused and recorded), no real network
(loopback and TEST-NET only), never the real store. The AI-off run proves the AI assembly is never loaded when AI is off.

## 6. Add-ons and samples

```
samples\addons\
├─ sdk\             DcxBridge.cs / .dll + DcxAddon.ps1: the SDK for program add-ons (API 2, encrypted bridge)
├─ hello-hosts\     a program add-on (PowerShell) using the SDK
├─ portal-browser\  a data-only add-on that switches the portal browser on
├─ shared-library\  a data-only add-on that joins a team library
├─ sample-theme\    the Ocean theme: theme.json + logo.png (every key a theme can set)
└─ pack.ps1         packs a folder into dist\addons\<id>\<id>-<version>.dcxaddon (+ .sha256, + dist\addons\<id>.zip)
```

## 7. The release bundle (`dist\<ver>\`)

```
dist\0.4.0\
├─ DarkCipherServerManagement-0.4.0-x64.msi                 the installer (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-x64-portable.zip        unzip-and-run (+ .sha256)
├─ update.json                                              the update manifest (the updater stays off until releases are signed)
├─ DarkCipherServerManagement-0.4.0-release-notes.md
├─ policy\ + DarkCipherServerManagement-0.4.0-policy-templates.zip   ADMX / ADML + POLICIES.md
├─ DarkCipherServerManagement-0.4.0-sbom.cdx.json           CycloneDX SBOM (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-Guide.pdf               the complete guide (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-User-Guide.pdf          (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-Developer-Guide.pdf     (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-Business-Guide.pdf      (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-Security-Report.pdf     (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-Project-Structure.pdf   this document and the generated tree (+ .sha256)
├─ DarkCipherServerManagement-0.4.0-source.zip              the sanitised source archive (+ .sha256)
└─ DarkCipherServerManagement-0.4.0-SHA256SUMS.txt
dist\addons\<id>\                                           add-on packages, every version kept (never inside a release folder)
```

Earlier releases keep their own folders; `build.ps1` refuses to overwrite one.

## 8. The documentation tree

```
docs\
├─ ARCHITECTURE.md        the decision records ADR-001 … ADR-058 (why things are the way they are)
├─ SECURITY.md            the security model, control by control, with how each is tested
├─ SECURITY-REPORT.md     the penetration tests run against this version and their results
├─ SOURCE.md              every folder and file, the main flows
├─ BUILD.md               tools, build.ps1 step by step, the PDFs, MSIX, add-on packing, the watched self-test
├─ TESTING.md             what the self-test covers and what it doesn't
├─ POLICIES.md            the Group Policy reference (generated by the build from the exe's catalog)
├─ PROVIDER-API-COVERAGE.md   what each provider API offers vs. what the app exposes
├─ ADDONS.md              the add-on format, bridge, permissions, SDK and the shipped add-ons
├─ PROJECT-TREE.md (+ .generated.md)   this document
├─ ROADMAP.md · OUTSTANDING.md (hand-test checklist) · PRODUCT-SPEC.md · SECURECRT-COMPARISON.md · SHARED-LIBRARY-DESIGN.md
├─ RESUME-0.5.0.md        where a new contributor (or the next session) starts
├─ release-notes\<ver>.md one per release
├─ guide\                 the guides' own chapters (00-about, 10-admin, 15-ai, 20-developer, 30-limitations, 40-business), guide.html (the PDF template)
│                         and one manifest per PDF: guide.txt, guide-user.txt, guide-developer.txt, guide-business.txt, guide-security-report.txt, guide-project.txt
└─ history\               the maintainers' session notes and old plans (kept in git, left out of the public source archive)
```
