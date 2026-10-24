# Developer reference

This chapter is the short version. The detail is in the chapters that follow it: the **Source code reference**
(`docs/SOURCE.md`: every folder and file, start-up, the store, sessions, AI, add-ons, the shared library, the portal
browser, the updater, and where the tests live), **Building and releasing** (`docs/BUILD.md`: pinned tools, `build.ps1`
step by step, the PDF guide, MSIX, add-on packing, the watched and isolated self-test), the architecture decisions, and
**Add-ons and the SDK** (`docs/ADDONS.md`).

## Technology

- **App:** WPF on .NET 10 (`net10.0-windows10.0.19041.0`, x64), published self-contained single-file.
- **Terminal:** WebView2 + xterm.js 5.5. Pages are served from memory (`https://dcx.term/*` via `WebResourceRequested`), not from a folder mapping.
- **Libraries:** SSH.NET **2026.0.0** (pinned: older lines have advisories; never go below), PdfPig 0.1.9, System.IO.Ports 8, System.Management 8, ProtectedData 8, QRCoder.
- **WinForms** is enabled only for the Remote Desktop ActiveX host; the implicit WinForms/Drawing usings are removed.
- **Installer:** WiX 5 (dotnet local tool, pinned in `.config\dotnet-tools.json`, with `WixToolset.UI.wixext` 5.0.2).

## Repository layout

Since 0.2.0 the code is ten projects under `src\` with references pointing down only (ADR-037; the full map is in the
source-code chapter, section 1, and each project's `README.md`):

| Path | What |
|---|---|
| `src\Dcx.Core\` | the encrypted store, settings and records, audit, offline mode and network policy, capabilities, masking, the AI switch and lock, snapshots, schema, updater (off), help search, notes, incidents, documents, and the `Ui` seams; no WPF |
| `src\Dcx.Connectivity\` | `Ssh\`, `Sessions\` (SSH, ConPTY shells and WSL, serial, Telnet; profiles; session logs; the AI terminal bridge and command risk), `Files\` (local / SFTP / SCP, transfers, sync), `Reports\` |
| `src\Dcx.Ui\` | the WPF kit (Dialogs, Menus, NetUi, theme and typography, the module contracts) |
| `src\Dcx.Infra\` | Docker, Proxmox, Unraid, VMware vCenter, Hyper-V, TLS pinning, last-known status |
| `src\Dcx.Cloud\` | the provider-neutral cloud layer and its providers: Vultr, Contabo, Hetzner, Azure, Google Cloud |
| `src\Dcx.Addons\`, `Dcx.Library\`, `Dcx.Browser\` | add-ons (sandbox, encrypted bridge, full-control API), the shared library engine, the portal browser engine |
| `src\Dcx.Ai\` | everything AI as one module (engines, agent loop, AI views, `AiModule`); never loaded when AI features are off |
| `src\DcxManager\` (the exe) | `App.xaml.cs` (start-up, `WireLayers()`), `MainWindow` + `Shell\`, `Views\` (every screen; `Settings\`, `Terminal\`), app-level `Services\`, `Api\`, `Rdp\`, `Themes\`, `Assets\web` |
| `src\DcxManager\SelfTest\` | the self-test: the full suite by area, the AI-off suite, one file per unit |
| `src\Dcx.Core\Assets\help\help.md` | the in-app help (Part 2 of this guide) |
| `installer\DarkCipherServerManagement.wxs` | the MSI definition |
| `build.ps1` | release build: self-test → publish → MSI + zip + update.json |
| `tools\run-selftest-watch.ps1` | self-test plus an on-screen-window watchdog |
| `tools\make-guide.ps1` (+ `tools\guide\`) | builds this PDF guide from the docs (`docs\guide\guide.txt` lists the chapters) |
| `tools\make-msix.ps1` (+ `tools\msix\`), `installer\msix\` | the Microsoft Store package (prepared, not submitted) |
| `tools\write-checksums.ps1`, `tools\make-icon.ps1` | release checksums; the app icon and logo |
| `samples\addons\` | the shipped add-ons (hello-hosts, portal-browser, shared-library, sample-theme), the SDK (`sdk\`) and `pack.ps1` |
| `docs\` | architecture (ADRs), security, testing, roadmap, release notes, this guide's chapters |

## Building

```powershell
cd src\DcxManager
dotnet build -c Debug -nologo -v q          # expect 0 warnings
```

Release (from the repository root):

```powershell
$env:DCX_DATA_DIR = $null
.\build.ps1
```

`build.ps1`:
1. builds Debug and runs the full self-test against a temporary store (allowed 10 minutes; it takes about 5);
2. refuses to package on any failure (the report is left in `%TEMP%\dcx-selftest-<guid>\report.txt`);
3. publishes Release (no `.pdb`) and builds the WiX MSI, the portable zip, `update.json`, a copy of the release notes and the checksums into `dist\<version>\`.

It always prints an "Unsigned build" warning until a signing certificate exists. Signing will use `$env:DCX_SIGN_THUMBPRINT`.

## Release rules

- **Every build is a new version.** Bump the single `<Version>` in `src\DcxManager\DcxManager.csproj` (0.1.1 was the first under this rule) and write `docs\release-notes\<version>.md`.
- **Old releases stay**, each in its own `dist\<version>\` folder. `build.ps1` refuses to rebuild a version that already exists.
- **Add-on packages** go to `dist\addons\<add-on id>\` (all versions kept, plus `dist\addons\<id>.zip`), never into a release folder; every add-on declares `"api": 2`.
- **The MSI UpgradeCode `6b7ac824-8e15-4fe4-ba73-d3fc492b50e3` never changes.**
- **Updates stay off** until a code-signing certificate is pinned in `Updater.TrustedPublisherThumbprints`.
- **Generate this guide** for the version: `tools\make-guide.ps1` (needs Microsoft Edge).

## Coding rules

- **Encrypt everything stored.** New stores use `SecureStore`. There is no plaintext mode.
- **One network gate.** Every HttpClient is built on `Offline.Handler()`; SSH, RDP, web windows and launchers call `Offline.EnforceAsync` / `Run`. Every destination is classified by `NetworkPolicy`; external ones get the ⚠ treatment.
- **Typography.** Use the `Fs*` type ramp / `.Fs(FontStep.X)` and `Typography.Size(window, w, h)`. Raw `FontSize` numbers fail the self-test.
- **Production safety.** Disruptive host actions go through `Envs.Confirm(host, …)`.
- **Dialogs.** Only through `Views\Dialogs.cs`. During the self-test every dialog is refused and fails the report; tests answer confirmations with `Dialogs.ConfirmForTests`.
- **Settings** can be replaced by a restore or import: read `AppSettings.Current` live, never a captured copy.
- **Cloud providers** implement `ICloudApi` + a catalog of `ResourceDef`s + a `CloudProvider` subclass registered in `CloudProviders.All`. No provider-specific branches elsewhere. Orders are never retried.
- **Honesty.** Never fake a feature: say "NOT IMPLEMENTED". Nothing counts as working without a test.
- **Style.** Match the surrounding code (comment density, small helper lambdas); 0 warnings.

## Adding a feature (the per-unit routine)

1. Write an ADR in `docs\ARCHITECTURE.md` first (problem, decision, alternatives, not done).
2. Implement in the surrounding style.
3. Add a `SelfTest<Unit>.cs` with real I/O where possible (loopback servers, real processes, fakes), renders through off-screen windows, and never a visible window. Register it in `SelfTest.cs`.
4. Build with 0 warnings and run the watched self-test (`tools\run-selftest-watch.ps1`): 0 failures, "no window appeared on screen", "no dialog was reached".
5. Update the docs: `help.md`, SECURITY, TESTING, ROADMAP, OUTSTANDING, README, and the release notes.
6. Bump the version and run `build.ps1`.

## 0.5.1: where the new pieces live

| Concern | Where | Notes |
|---|---|---|
| Locks (rules, open state, `Demand`, `Strict`, `Hidden`) | `Dcx.Core\Services\Locks.cs` | ADR-067; no WPF. Prompts: `DcxManager\Views\LockGate.cs`, menus: `LockUi.cs`, elevated side: `AdminApproveWindow.cs` |
| Restrictions (`Allow*` → actions) | `Dcx.Core\Services\Restrictions.cs` | `Check` in screens, `Demand` below them; a new `Allow*` setting is picked up by the policy catalogue's "Allow" prefix |
| Add-on policy | `Dcx.Addons\Addons\AddonPackages.cs` (`PolicyRefusal`, `UseRefusal`) | ADR-068; the UAC step is in `AddonsView.cs` (`AddonUi.AdminApprovedAsync`) |
| VNC | `Dcx.Connectivity\Vnc\` (`RfbClient`, `RfbAuth`, `RfbDecoders`, `RfbKeys`, `VncConnector`), `DcxManager\Views\VncView.cs` | ADR-069; tests `SelfTestVnc.cs` (loopback fakes) |
| Host import | `Dcx.Connectivity\Net\HostDiscovery.cs`, `DcxManager\Views\HostImportWindow.cs`, AI: `Dcx.Ai\Ai\HostImportAi.cs` | ADR-070; the AI is reached only through `Services.HostIdentify` |
| Dashboard catalogue | `DcxManager\Services\DashboardCatalog.cs`, `Views\DashboardView.Cards.cs`, `Views\DashboardEditor.cs` | ADR-071 |
| BeyondTrust page and pinning | `Views\Settings\SettingsView.BeyondTrust.cs`, `Dcx.Connectivity\BeyondTrust\BtTls.cs`, `Dcx.Core\Services\TlsPin.cs` | `TlsPin` moved to Core (namespace `DcxManager.Infra` kept) |
| Bitwarden | `Dcx.Connectivity\Bitwarden\` (`BitwardenCli`, `BitwardenVault`, `BitwardenSecrets`), `DcxManager\Services\BitwardenSessions.cs`, `Views\BitwardenView.cs`, `Views\SecretClipboard.cs`, `Views\Settings\SettingsView.Bitwarden.cs` | ADR-093; test seams `BitwardenCli.Fake` / `ExeForTest`, `BitwardenSessions.MasterPasswordForTest`; tests `SelfTestBitwarden.cs` |
| Vault credentials kept in Bitwarden / Password Safe | `Dcx.Core\Services\ExternalCredentials.cs` (the seam: `Use`, `Resolve`, `Background`, `LinkedTo`, `CredentialAuthHandler`), `DcxManager\Services\CredentialSources.cs` (the resolver), `PasswordSafeSessions.LeaseAsync` | ADR-096; every credential use goes through the seam (never `Credential.Secret` straight from the vault); providers sign requests at send time; tests `SelfTestBitwarden.cs` 4b, `SelfTest050.cs`, pentest probes |
| UniFi | `Dcx.Infra\Infra\UniFi.cs`, `UniFiCloud.cs`; the add form in `Views\InfraView.cs` | ADR-094; `ProviderConfig.ConsoleId` for the Cloud Connector; tests `SelfTestUniFi.cs` (fake console and api.ui.com) |
| Hyper-V editions | `Dcx.Infra\Infra\HyperVSetup.cs` | the elevated install has a test seam (`RunElevated`) |
| Seams for the AI module | `Services.HostIdentify`, `Services.LocalAiSetup` (Core) | registered in `Dcx.Ai\Views\AiModule.cs`; screens outside `Dcx.Ai` must not name `DcxManager.Ai` (the layering scan checks) |
| Self-test for 0.5.1 | `DcxManager\SelfTest\SelfTest051.cs` (`Release051Tests`), pentest section J in `SelfTestPentest.cs` | |

Two rules that 0.5.1 made explicit:
- **No prompt below the screens.** Code in `Services\`, `Ai\`, `Ssh\` and the other lower folders never calls a dialog or
  a WPF timer; prompts (UAC, Windows sign-in, a lock's password) live with the screens. The self-test refuses any UAC or
  Windows sign-in prompt and reports it.
- **A locked item is refused where it is used**, not only hidden on screen: call `Locks.Demand(target, name)` before
  anything that connects, reads or changes an item, and `Locks.Hidden(target)` before listing its details.
