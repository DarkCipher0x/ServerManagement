# Building and releasing DarkCipher ServerManagement

This chapter is the complete build and release procedure: the tools, every script step by step, what lands in `dist\`,
and the rules every release follows. It was checked against `build.ps1`, `tools\*.ps1`, `samples\addons\pack.ps1`,
`installer\*` and `src\DcxManager\DcxManager.csproj` as of 0.1.18.

Related chapters: [SOURCE.md](SOURCE.md) (what the code does), [TESTING.md](TESTING.md) (what the self-test covers),
[ADDONS.md](ADDONS.md) (add-on packages and the SDK), `installer\msix\STORE-SUBMISSION.md` (Microsoft Store steps).

---

## 1. Prerequisites

| Tool | Version / how it is pinned | Needed for |
|---|---|---|
| Windows 10 1809+ or Windows 11, x64 | — | everything (the app targets `net10.0-windows10.0.19041.0`, minimum OS `10.0.17763.0`) |
| **.NET 10 SDK** | any 10.0 SDK (built with 10.0.402; .NET 10 is the LTS release, supported until November 2028) | `dotnet build` / `publish`, the guide tool, the WiX and MSIX tool restores |
| **WebView2 Runtime** | evergreen (Windows 11 has it) | running the app and its self-test (terminals, viewers and web windows render in WebView2) |
| **WiX Toolset 5** | `wix` **5.0.2** as a dotnet local tool in `.config\dotnet-tools.json` (`rollForward: false`); `build.ps1` adds the extension `WixToolset.UI.wixext/5.0.2` | the MSI |
| **Microsoft Edge** | installed Edge (either Program Files folder) | printing the PDF guide headless (`tools\make-guide.ps1`) |
| **Windows SDK build tools** | NuGet `Microsoft.Windows.SDK.BuildTools` **[10.0.28000.1839]** via `tools\msix\MsixTools.csproj` (restore only, `PackageDownload`) | `makeappx.exe` / `signtool.exe` for the MSIX (`tools\make-msix.ps1`) |
| Windows SDK `signtool.exe` | found under `%ProgramFiles(x86)%\Windows Kits\10\bin\…\x64\` | only for Authenticode signing in `build.ps1` (optional) |
| .NET Framework 4.x `csc.exe` | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (part of Windows) | only to rebuild the add-on SDK's `DcxBridge.dll` |
| Windows PowerShell 5.1 | part of Windows | all scripts; PowerShell add-ons run in it |
| **Git for Windows** | 2.55 (installed with `winget install Git.Git`, 2026-10-20) | the tree is a local repository on `main` since 0.2.0 phase 0; one commit per phase. `dist\`, `publish\`, `bin\`, `obj\` and signing material are ignored |

The solution file `DarkCipher.ServerManagement.sln` (repository root) lists the ten projects (`src\Dcx.*` and the app,
`src\DcxManager`; SOURCE.md section 1); `build.ps1` builds the solution for the self-test gate and publishes the exe
project, which bundles the libraries into its single file. Each project has its own `packages.lock.json`. After adding a
project or a package reference: `Remove-Item Env:\DCX_LOCKED_RESTORE; dotnet restore DarkCipher.ServerManagement.sln --force-evaluate`.

NuGet packages (each in the project that uses it; restored automatically):

| Package | Version | Used by |
|---|---|---|
| `Microsoft.Web.WebView2` | 1.0.2903.40 | terminal (xterm.js), document viewer, portal browser, web windows |
| `SSH.NET` | **2026.0.0** (never lower: older lines have advisories) | SSH, SFTP, SCP, tunnels |
| `PdfPig` | 0.1.9 | PDF text for the AI (`Documents\PdfText.cs`) |
| `System.IO.Ports` | 10.0.13 | serial consoles |
| `System.Security.Cryptography.ProtectedData` | 10.0.13 | DPAPI wrap of the data key |
| `System.Management` | 10.0.13 | hardware facts (WMI) |
| `System.DirectoryServices.Protocols` | 10.0.13 | LDAP sign-in |
| `BouncyCastle.Cryptography` | 2.7.0 | ML-KEM-768 / ML-DSA-65 (post-quantum, ADR-090) |
| `QRCoder` | 1.6.0 | the authenticator (2FA) QR code |

The guide tool (`tools\guide\Guide.csproj`) uses `Markdig` 0.37.0.

> **PowerShell 5.1 and UTF-8.** Never round-trip source or docs through `Get-Content`/`Set-Content` in Windows
> PowerShell 5.1: without a BOM it reads UTF-8 as the ANSI code page and writes mojibake. The scripts here read with
> `-Encoding UTF8` or `[IO.File]` and write with `UTF8Encoding($false)`.

---

## 2. The project file and what it controls

`src\DcxManager\DcxManager.csproj` is the single source of the version and the publish shape:

| Property | Value | Why |
|---|---|---|
| `Version` | e.g. `0.1.18` | **the one line to bump per build**; Assembly/File/Informational versions derive from it |
| `OutputType` / `UseWPF` | `WinExe` / true | WPF desktop app |
| `UseWindowsForms` | true | only for the embedded Remote Desktop ActiveX (`AxHost`, ADR-013); the implicit `System.Windows.Forms` and `System.Drawing` usings are removed so WinForms types never clash with WPF |
| `RuntimeIdentifier` / `PlatformTarget` | `win-x64` / `x64` | |
| `AssemblyName` / `RootNamespace` | `DcxServerManager` / `DcxManager` | the exe is `DcxServerManager.exe` |
| `SelfContained`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `EnableCompressionInSingleFile` | true | one self-contained exe; no .NET install on the target PC |
| `DebugType` | `none` | **no `.pdb`** in the output (build.ps1 also refuses one) |
| `PathMap` | `$(MSBuildProjectDirectory)=/_/` | keeps local build paths (the user name) out of the binary |
| `SatelliteResourceLanguages` | `en` | no localisation satellites |

Resources compiled into the exe:
- `Assets\app.ico`, `Assets\logo.png` (WPF resources; made by `tools\make-icon.ps1`).
- `Assets\web\**` as `web/<file>`: the terminal page (`terminal.html`, xterm.js, `addon-fit.js`, `xterm.css`, xterm licence). Unpacked to the local cache on first run and served from `https://dcx.term/*` (`Views\WebHost.cs`).
- `Assets\help\**` as `help/<file>`: the built-in help (`help.md`, searched offline by `Services\HelpKb.cs`; an article's optional `module: <id>` line after `keywords:` ties it
  to an app module, and the guide build drops both lines).
- `..\..\samples\addons\sdk\DcxBridge.dll`, `DcxAddon.ps1`, `DcxBridge.cs` as `sdk/<file>`: the add-on SDK (ADR-035), used by the self-test's add-on checks. **The app build therefore needs the `samples\addons\sdk` folder next to `src`.**

A `TrimPublishDocs` target deletes WebView2's three `.xml` doc files from the publish folder.

### Build hygiene for every project: `Directory.Build.props` (0.1.18)
The repository root's `Directory.Build.props` applies to the app, `tools\guide` and `tools\msix`.

| Setting | Effect |
|---|---|
| `RestorePackagesWithLockFile` | Each project has a `packages.lock.json` that pins every package, direct and transitive, by version and content hash. Keep these files with the source. |
| `RestoreLockedMode` when `DCX_LOCKED_RESTORE=1` | `build.ps1` sets this. A release build fails if a package, version or hash differs from the lock file. |
| `NuGetAudit` (mode `all`, level `low`) + `NU1903`/`NU1904` as errors | Each restore checks direct and transitive packages against the nuget.org advisory database. High and critical advisories stop the build; moderate and low ones are warnings. |
| `Deterministic` + `ContinuousIntegrationBuild` (release) | Same source gives the same binary. Release builds normalise paths. |

**Changing a package on purpose:**
1. Edit the `PackageReference`.
2. Run `dotnet restore <project> --force-evaluate` to rewrite the lock file.
3. Review the diff of `packages.lock.json`, then build.

**Security analyzers are not enabled yet.** The CA security rules as errors were tried in s40 and are kept for 0.1.19 (`tools\analyzers\security.globalconfig.txt`, kept inactive: to try it, copy it to the repository root as `.globalconfig` and add `EnableNETAnalyzers` / `AnalysisLevel latest` to `Directory.Build.props`). On 55k lines even the non-taint rules ran over 10 minutes; they need a separate pass with the findings fixed.

---

## 3. Versioning rules

- **Every build is a new version.** Bump `<Version>` in the csproj (0.1.1 was the first build under this rule) and add
  `docs\release-notes\<version>.md` before running `build.ps1`.
- **Security analyzers (0.2.0 audit):** `Directory.Build.props` turns on the .NET security analyzers (recommended set)
  and makes every code-analysis warning a build error. Don't enable `AnalysisModeSecurity=All`: its taint tracking ran
  the compiler server past 10 GB for over 20 minutes. A justified exception is a `#pragma warning disable` with the reason
  on the same line (one today: TOTP's HMAC-SHA1).
- **Release labels (0.2.0):** `<ReleaseLabel>` in the csproj names a release whose numeric version can't (a preview).
  `<Version>` stays numeric and always increasing (assembly, Windows Installer `ProductVersion`, update checks); the label
  is used for file names, `dist\<label>\`, the release notes file and what the app shows (`App.DisplayVersion`).
  0.2.0-preview.1 is built as 0.1.21 so the final 0.2.0 upgrades it; the next preview takes 0.1.22. Leave the label empty
  for a normal release.
- **Each release has its own folder** `dist\<version>\`; earlier releases stay in theirs and are never deleted.
  `build.ps1` refuses to run when `dist\<version>\…-x64.msi` (or a legacy `dist\…-<version>-x64.msi`) already exists.
- **The MSI UpgradeCode never changes:** `6b7ac824-8e15-4fe4-ba73-d3fc492b50e3` (also `Updater.UpgradeCode`). A newer
  MSI upgrades in place; a downgrade is refused by `MajorUpgrade`.
- **Add-on packages are versioned separately** in their `addon.json` and live in `dist\addons\<add-on id>\`, never in
  `dist\<version>\` (section 9).
- **Updates stay off** until a code-signing certificate's thumbprint is pinned in
  `Updater.TrustedPublisherThumbprints` (empty today, so `Updater.Enabled` is false); a Store install never uses the
  in-app updater (`Packaging.IsPackaged`).

---

## 4. Everyday build and the watched self-test

```powershell
cd <repository>\src\DcxManager
dotnet build -c Debug -nologo -v q          # expect 0 warnings
$s = "<an empty scratch folder>"
$exe = (Get-ChildItem -Recurse bin\Debug -Filter DcxServerManager.exe | Select-Object -First 1).FullName
..\..\tools\run-selftest-watch.ps1 -Exe $exe -Scratch $s            # -Minutes 10 by default
$t = [IO.File]::ReadAllLines("$s\selftest.txt", [Text.Encoding]::UTF8)
($t | ? { $_ -match '^ok' }).Count; $t | ? { $_ -match '^FAIL|failure' }
```

**Pass criteria:** `exit 0`, the line `no window appeared on screen`, 0 `FAIL` lines and the report line `no dialog was
reached`. Any `VISIBLE WINDOW:` line is a failure even when the report has 0 failures. The suite takes about 5–6
minutes (the 0.1.17 isolated run had 282 `ok` lines; the count grows with every unit).

**AI off (0.2.0):** add `-AiOff` for the second suite (`SelfTestAiOff.cs`, about 30 s): the app starts with
`DCX_AI_FEATURES=0`, the AI module is never registered, and the run checks that no AI exists anywhere. Its report
starts "self-test (AI off)"; the pass criteria are the same.

### What `tools\run-selftest-watch.ps1` does
1. `param(-Exe, -Scratch, -Minutes = 10, -AiOff)`. A fresh store is a new install, where AI features start off: the
   script sets `DCX_AI_FEATURES=1` for the normal run and `0` with `-AiOff`.
2. Deletes the run's own sub-folders of the scratch folder (`selftest-data`, `selftest-data-logs`, `selftest-data-addons`,
   `moved-store`, `commander-test`, `shots`, `addon-pkg`) and the old `selftest.txt`.
3. Waits until the exe has been unchanged for 3 s (a build may still be writing it).
4. Starts `<exe> --selftest <scratch>\selftest.txt` with `DCX_DATA_DIR=<scratch>\selftest-data`, so only a scratch store
   is used (logs go to `selftest-data-logs`, add-ons to `selftest-data-addons`, WebView2 to `selftest-data-webview2`).
5. Every 300 ms it lists the test process's **visible, on-screen** top-level windows (`EnumWindows`; windows parked at
   -10000 are ignored) and prints each new one as `VISIBLE WINDOW: …`. A `Microsoft .NET` or `#32770` (dialog) window
   stops the run at once.
6. Prints `exit <code>` and `no window appeared on screen` when none did. If no report was written it says why it knows
   nothing (exe write time, start time, running instances, the newest error log) — never just "no report" (ADR-025).

### The isolated-copy method (when several sessions share the tree)
Parallel sessions edit and build the same checkout. A watched run against the shared `bin\Debug` can pick up another
session's half-built exe, or lock it while that session builds. The method used since s37:

```powershell
$iso = "<scratch>\iso"                                    # a fresh folder per run
robocopy src "$iso\src" /MIR /XD bin obj /NFL /NDL /NJH /NJS
robocopy samples "$iso\samples" /MIR /NFL /NDL /NJH /NJS  # the csproj embeds samples\addons\sdk\*; tests look for samples\addons\<name>
dotnet build "$iso\src\DcxManager\DcxManager.csproj" -c Debug -nologo -v q
$exe = (Get-ChildItem -Recurse "$iso\src\DcxManager\bin\Debug" -Filter DcxServerManager.exe | Select-Object -First 1).FullName
.\tools\run-selftest-watch.ps1 -Exe $exe -Scratch "<scratch>\run"
```

Keep the `src` + `samples` layout: the add-on, theme, portal and library tests walk up from the exe's folder to find
`samples\addons\hello-hosts`, `sample-theme`, `portal-browser` and `shared-library` (without them some checks fall
back to inline packages and the report says "repo sample not found").

### Self-test switches
| Variable | Effect |
|---|---|
| `DCX_DATA_DIR` | the scratch store (required for `--selftest`; the real store is never touched) |
| `DCX_AI_FEATURES` | `1` = the full suite with the AI module; `0` = the AI-off suite (0.2.0; ignored while the AI features lock is set) |
| `DCX_FUZZ_SEED` | another fuzzing seed (default 20261018) |
| `DCX_FUZZ_ITER` | multiplies fuzz iterations (1–1000, default 1) |
| `DCX_ENDURANCE` | multiplies the endurance loops (default 1) |
| `DCX_HANDLE_DIAG` | `1` = handle census on every endurance line, and per-screen diagnostic loops |

---

## 5. `build.ps1`, step by step

Run from the repository root in Windows PowerShell: `.\build.ps1` (clear `DCX_DATA_DIR` first). It stops at the first
error (`$ErrorActionPreference = 'Stop'`).

0. **Version and guard.** Reads `<Version>` from the csproj. Refuses when `dist\<ver>\DarkCipherServerManagement-<ver>-x64.msi`
   or a legacy `dist\DarkCipherServerManagement-<ver>-x64.msi` exists ("bump <Version> first").
1. **Self-test gate.**
   - `dotnet build DarkCipher.ServerManagement.sln -c Debug`.
   - A throwaway folder `%TEMP%\dcx-selftest-<guid>` with `DCX_DATA_DIR=<it>\data`.
   - Starts the Debug exe with `--selftest <tmp>\report.txt` and `DCX_AI_FEATURES=1`, and waits up to **10 minutes**
     (it takes about 5); then the AI-off suite with `DCX_AI_FEATURES=0` against `<tmp>\data-ai-off` (`report-ai-off.txt`,
     up to 5 minutes). Both must pass.
   - Prints the report's first line. On a non-zero exit it prints the `FAIL` lines and stops: **nothing is packaged
     after a failing self-test**.
   - **Budgets (0.2.0 phase 7):** `tools\check-budgets.ps1` reads both reports' start-up lines (time to the main window,
     private memory) and stops the build when one is over `tools\budgets.json`. Raise a budget only with a reason in
     its `why`.
   - Deletes the scratch folder, retrying 10 times 2 s apart (WebView2 helper processes can hold a lockfile for a moment;
     since s36 this never fails the build, it only warns).
2. **Publish.**
   - Deletes `.\publish`, creates `dist\<ver>\`.
   - `dotnet publish <csproj> -c Release -o publish` (self-contained single file, no symbols).
   - Copies `LICENSE` → `publish\LICENSE.txt` and `THIRD-PARTY-NOTICES.md`.
   - **Refuses if any `.pdb` is in `publish`.**
   - **Optional Authenticode signing** of `publish\DcxServerManager.exe`: only when `DCX_SIGN_THUMBPRINT` names a
     code-signing certificate in `CurrentUser\My`. Uses the newest x64 `signtool.exe` from the Windows Kits,
     `/fd SHA256 /tr <DCX_SIGN_TIMESTAMP or http://timestamp.digicert.com> /td SHA256`, then requires
     `Get-AuthenticodeSignature` to report `Valid`.
   - The exe's size is checked against `exeMaxMb` in `tools\budgets.json`.
   - **The published exe is self-tested (0.2.0 phase 6g):** the AI-off suite runs against `publish\DcxServerManager.exe`
     (a new throwaway store, up to 5 minutes, `report-published.txt`); a failure stops the build.
3. **MSI** (WiX 5).
   - `dotnet tool restore` (wix 5.0.2) and `wix extension add -g WixToolset.UI.wixext/5.0.2`.
   - `wix build installer\DarkCipherServerManagement.wxs -arch x64 -ext WixToolset.UI.wixext -d Version=<ver> -d PublishDir=<repo>\publish -wx`
     (`-wx`: warnings are errors) → `dist\<ver>\DarkCipherServerManagement-<ver>-x64.msi`.
   - Deletes the `.wixpdb`; signs the MSI when signing is on. Unsigned builds print a yellow warning: the in-app updater
     refuses them by design. The MSI's size is checked against `msiMaxMb`.
3b. **The setup program (since 0.6.0, ADR-077, `installer\Setup\README.md`).** `dotnet build installer\Setup\DarkCipher.Setup.csproj -c Release`
   (locked restore); `DarkCipher.Setup.exe --check installer` must pass (setup-app.json parses, every option and
   detection is valid); `wix build installer\Setup\Bundle.wxs -d Version -d AppRoot -d MsiPath -d SetupBin -d AppInclude`
   writes `dist\<ver>\DarkCipherServerManagement-<ver>-Setup.exe` around this MSI. With a certificate the engine is
   detached, signed and reattached, then the bundle is signed. `--preview <folder> <out>` draws every page to PNG (no window).
4. **Update manifest and notes.**
   - `dist\<ver>\update.json` (UTF-8, no BOM): `product`, `version`, `released` (today), `notes` (the release-notes
     file's text), `schemaChange` (set `DCX_SCHEMA_CHANGE` to mark a store-format change), `signed`, and one `packages`
     entry (`kind msi`, `arch x64`, `file`, `url` = the file name, `bytes`, lowercase `sha256`). Publish it next to the MSI.
   - **Signed twice** by `tools\sign-update.ps1 -Path`: `update.json.sig` (ECDSA P-256, the CNG release key, ADR-079) and,
     since 0.6.12, `update.json.pq.sig` (ML-DSA-65, the post-quantum release key, ADR-090; its seed is DPAPI-protected in
     `%LOCALAPPDATA%\DarkCipher\keys`). The app pins both public keys and needs both signatures. Without either key the
     build stops rather than ship unsigned. Escrow both keys.
   - Copies `docs\release-notes\<ver>.md` to `dist\<ver>\DarkCipherServerManagement-<ver>-release-notes.md`.
   - **Portable zip:** `Compress-Archive publish\*` → `DarkCipherServerManagement-<ver>-x64-portable.zip`.
   - **SBOM (0.2.0 phase 7):** `tools\write-sbom.ps1` writes `DarkCipherServerManagement-<ver>-sbom.cdx.json`
     (CycloneDX 1.5): every package in the projects' lock files with its exact version and SHA-512 content hash
     (build-only packages and other platforms' native packages marked `excluded`), the bundled .NET and Windows Desktop
     runtimes, and xterm.js. No local paths.
5. **Checksums:** `tools\write-checksums.ps1 -Version <ver>`.
6. **The release bundle (0.4.0 rule, ADR-058).** `tools\make-guide.ps1 -All` prints six PDFs (one manifest each under
   `docs\guide\`), refreshing the generated tree table first (`tools\write-tree.ps1`); `tools\write-public-source.ps1`
   writes the sanitised source archive after its privacy scan; the build **fails when any bundle file is missing**; the
   checksums are written again so the PDFs and the archive have theirs; then a table of `dist\<ver>`.
7. **The release pipeline (since 0.5.4; full design in [RELEASE-PIPELINE.md](RELEASE-PIPELINE.md)).** `tools\release.ps1
   -Version <ver>` runs, in order, recording each step in `dist\<ver>\release-state.json`:
   - **check** (`tools\release-check.ps1`): the version agrees in the csproj, the MSI's ProductVersion, the exe in the
     portable zip, `update.json`, the release notes and the README; every asset is present and matches its checksums.
   - **public** (`tools\publish-public.ps1`): the sanitised archive (built from the **allowlist**
     `tools\public-allowlist.txt`; an unlisted file fails the build) goes into `..\ServerManagement-public`. Before any
     push: origin and branch must be those in `tools\release.json`; every image on `tools\approved-images.txt`; no key or
     symbol files; `tools\secret-scan.ps1` clean. One commit "ServerManagement <ver> - sanitised public source" as
     DarkCipher0x (no-reply), an annotated tag `v<ver>`, pushed; never a force push. A version already published with the
     same tree is "done"; a different tree is refused.
   - **github-release** (`tools\publish-github-release.ps1`): a draft "ServerManagement <ver>" with the release notes,
     the MSI, portable zip, source, SBOM, policy templates, six PDFs and `SHA256SUMS.txt`; each upload checked against
     GitHub's SHA-256, then published. A published release is never edited (a re-run only verifies it).
   - **website** (`tools\website-handoff.ps1`, 0.6.9): `dist\<ver>\website\` for the Website session (downloads
     byte-identical to the Release assets, SHA256SUMS, notes, `release-manifest.json`, the Discord card); after its
     deploy, `tools\verify-website.ps1` checks the live files. **discord**: the card is posted with the website's
     `announce-discord.ps1` on DarkCipher0x's go. Each has a switch in `tools\release.json`. Add-ons are never published.
   - `.\build.ps1 -NoPublish` skips step 7; `-DryRun` rehearses it (notes preview in `dist\<ver>\release-preview.md`);
     `.\tools\release.ps1 -Version <ver> -Resume` finishes a partly published version. **Kill switch:** `DCX_PUBLISH_OFF=1`
     or the file `%USERPROFILE%\.darkcipher\publish-off` turns every public step off. Edit the README and `.github\` here,
     never on GitHub: each release replaces the public tree.

### What a finished release folder holds
| File in `dist\<ver>\` | Made by |
|---|---|
| `DarkCipherServerManagement-<ver>-x64.msi` (+ `.sha256`) | build.ps1 step 3 |
| `DarkCipherServerManagement-<ver>-x64-portable.zip` (+ `.sha256`) | build.ps1 step 4 |
| `update.json`, `update.json.sig`, `update.json.pq.sig` | build.ps1 step 4 (`tools\sign-update.ps1`) |
| `DarkCipherServerManagement-<ver>-release-notes.md` | build.ps1 step 4 |
| `DarkCipherServerManagement-<ver>-sbom.cdx.json` (+ `.sha256`) | build.ps1 step 4 (`tools\write-sbom.ps1`) |
| `policy\` + `DarkCipherServerManagement-<ver>-policy-templates.zip` | build.ps1 step 4a (`--export-policy-templates`) |
| `DarkCipherServerManagement-<ver>-Guide.pdf` (+ `.sha256`) | build.ps1 step 6 (`tools\make-guide.ps1`, `docs\guide\guide.txt`) |
| `…-User-Guide.pdf`, `…-Developer-Guide.pdf`, `…-Business-Guide.pdf`, `…-Security-Report.pdf`, `…-Project-Structure.pdf` (+ `.sha256` each) | build.ps1 step 6 (`tools\make-guide.ps1 -All`, one manifest each) |
| `DarkCipherServerManagement-<ver>-source.zip` (+ `.sha256`) | build.ps1 step 6 (`tools\write-public-source.ps1`: tracked files minus `docs\history\`, privacy-scanned) |
| `DarkCipherServerManagement-<ver>-x64.msix` (+ `.sha256`) | `tools\make-msix.ps1` (Store; only once the identity is filled in) |
| `DarkCipherServerManagement-<ver>-SHA256SUMS.txt` | `tools\write-checksums.ps1` (rewritten by each script that adds a file) |

---

## 6. `tools\write-checksums.ps1`

`-Version <ver>` (mandatory). For every file in `dist\<ver>\` named `DarkCipherServerManagement-<ver>-*` with extension
`.msi`, `.zip`, `.pdf` or `.msix` (but never `*-test.msix`), and the `*-sbom.cdx.json`, it writes `<file>.sha256` containing
`"<lowercase sha256>  <file>\n"` (the `sha256sum` format the website's sync scripts check), then
`DarkCipherServerManagement-<ver>-SHA256SUMS.txt` with every line, for one upload or `sha256sum -c`. All UTF-8 without BOM.

---

## 7. The PDFs: `tools\make-guide.ps1`

`.\tools\make-guide.ps1 [-Version <ver>] [-Spec <manifest> -Suffix <name>] [-All] [-NoChecksums]` (default: the complete
guide from `docs\guide\guide.txt`; `-All` = every PDF of the release bundle; the version defaults to the csproj's). Needs
Microsoft Edge.

| Manifest (`docs\guide\`) | PDF (`dist\<ver>\DarkCipherServerManagement-<ver>-…`) | Audience |
|---|---|---|
| `guide.txt` | `Guide.pdf` | everything |
| `guide-user.txt` | `User-Guide.pdf` | daily users: the in-app help, AI, limitations, what changed |
| `guide-developer.txt` | `Developer-Guide.pdf` | layout, the project tree, source, build, ADRs, API coverage, add-ons, testing |
| `guide-business.txt` | `Business-Guide.pdf` | the organisation: identity (AD / LDAPS / Entra), Group Policy, SIEM, team, branding, data, offboarding |
| `guide-security-report.txt` | `Security-Report.pdf` | `SECURITY-REPORT.md` (the penetration tests and results) + the security model |
| `guide-project.txt` | `Project-Structure.pdf` | `PROJECT-TREE.md` + the generated folder table (`tools\write-tree.ps1`) |

1. `dotnet run --project tools\guide\Guide.csproj -c Release -- <repo> <manifest> <ver> <tmp>\guide.html`.
   `tools\guide\Program.cs` reads the manifest:
   - `TITLE <text>`, `SUBTITLE <text>`, `COVERS <text>`: the cover page (0.4.0).
   - `PART <title>`: a part divider (new page).
   - `DOC <path> [title="…"] [dropkeywords] [cut="<heading>"] [take="<heading>"] [top=N] [toc=N]`: a document whose H1
     becomes a chapter (all headings move down a level); `dropkeywords` removes help.md's search-keyword lines;
     `cut`/`take` remove or keep one section.
   - `{VERSION}` is replaced everywhere. Markdown is rendered with Markdig.
2. Edge prints it headless with a **throwaway profile** (`--user-data-dir=<tmp>\edge`, never the user's profile):
   `--headless=new --disable-gpu --no-first-run --no-pdf-header-footer --generate-pdf-document-outline --print-to-pdf=…`,
   3-minute limit → `dist\<ver>\DarkCipherServerManagement-<ver>-Guide.pdf`.
3. Runs `write-checksums.ps1` so the PDF gets its `.sha256` (unless `-NoChecksums`; build.ps1 writes them once at the
   end), and deletes the temp folder.

### Naming on covers and footers (house rule, DarkCipher0x 2026-10-21)
Every PDF uses the one template, `docs\guide\guide.html`. Keep it like this:
- **Cover:**
  - The company line stays the small teal spaced capitals "DARKCIPHER SYSTEMS". The source text is "DarkCipher
    Systems", and CSS sets the capitals.
  - Under it, the big white title is the application name only: **ServerManagement**. It is never "DarkCipher
    ServerManagement".
  - Then the document's `SUBTITLE`.
- **Footer** of every page: `ServerManagement <ver> – DarkCipher Systems` on the left and `Page N of M` on the right.
- **Browser/PDF title:** `ServerManagement <ver> – <TITLE> – DarkCipher Systems`.
- **Other title blocks:** the same two-part form, company line then application line. This covers the source archive's
  `SOURCE-ARCHIVE.txt` (`tools\write-public-source.ps1`) and any new generated report or cover sheet.
- **Never** "Dark Cipher".
- In running text the product is still "DarkCipher ServerManagement" (ADR-027).

When a new document should be in a guide, add a `DOC` line to its manifest (and a release-notes `DOC` line in `guide.txt`,
`guide-user.txt`, `guide-developer.txt` and `guide-business.txt` for each new version).

### The public source archive: `tools\write-public-source.ps1`

`-Version <ver>` (mandatory). Lists every file git tracks (committed or staged), drops `docs/history/`, `dist/`,
`publish/`, local tool caches and `*.local.txt`, then scans every text file for a local user path, a private e-mail
address, a private IPv4 address of this network, API keys / tokens, private keys, and every term in
`tools\privacy-terms.local.txt` (one per line, git-ignored, kept on the build PC). Any hit prints the file and line and
fails the build. The clean tree is zipped as `DarkCipherServerManagement-<ver>-source.zip` with a top-level folder and a
`SOURCE-ARCHIVE.txt` note.

---

## 8. Microsoft Store package: `tools\make-msix.ps1` (ADR-029)

Prepared, **not submitted** (DarkCipher0x: "dont deploy it yet just prepare it"). The Store signs MSIX packages for free after
certification, so the Store package is uploaded unsigned. Run **after** `build.ps1`: it reuses `.\publish`, so the
MSIX holds exactly the MSI's binaries.

| Mode | Output | Notes |
|---|---|---|
| `.\tools\make-msix.ps1` | `dist\<ver>\DarkCipherServerManagement-<ver>-x64.msix` + checksums | needs `installer\msix\store-identity.json` (`IdentityName`, `Publisher` = the full `CN=…`, `PublisherDisplayName`) from Partner Center; refuses while it is empty |
| `-TestSign` | `…-x64-test.msix` + `…-local-test.cer` | test identity `DarkCipherSystems.ServerManagement.Test` / `CN=DarkCipher Systems Local Test`; a self-signed code-signing certificate in `CurrentUser\My` (made once, 2 years). For THIS PC only: never publish the `-test.msix` or the `.cer` |
| `-Check` | nothing kept | packs with the test identity into `%TEMP%`, lets `makeappx` validate the manifest schema, prints the size, deletes the package. Signs nothing, writes nothing to `dist` or the certificate store |

Steps: refuses without `publish\DcxServerManager.exe`, without the version's MSI, or when the published exe's
ProductVersion isn't the csproj version; restores the pinned SDK build tools (`tools\msix\MsixTools.csproj`) and finds
x64 `makeappx.exe`/`signtool.exe` in `%USERPROFILE%\.nuget\packages\microsoft.windows.sdk.buildtools`; stages `publish`
plus `AppxManifest.xml` with `$(IdentityName)`, `$(Publisher)`, `$(PublisherDisplayName)` and `$(Version)` (`<ver>.0`)
filled in (XML-escaped; any unfilled token stops it); draws `StoreLogo` 50², `Square44x44Logo`, `Square150x150Logo` and
`Wide310x150Logo` from `Assets\logo.png`; `makeappx pack /d <stage> /p <pkg> /o`.

The manifest (`installer\msix\AppxManifest.xml`) declares `Windows.Desktop` 10.0.18362.0+ (tested 10.0.26100.0),
`Windows.FullTrustApplication` and the restricted capabilities `runFullTrust` and `unvirtualizedResources`. In a
packaged install `Services\Packaging.cs` turns the updater off, skips the AUMID override and restarts through the
package. DarkCipher0x's submission steps: `installer\msix\STORE-SUBMISSION.md`.

---

## 9. Add-on packages: `samples\addons\pack.ps1`

```powershell
cd samples\addons
.\pack.ps1 .\portal-browser
```

1. Reads `<folder>\addon.json`; `id` must match `^[A-Za-z0-9][A-Za-z0-9._-]*$` and `version` `^[0-9A-Za-z.+-]+$`
   (the app's own rules are stricter: see ADDONS.md).
2. Output folder `dist\addons\<id>\` (one per add-on). The package is `<id>-<version>.dcxaddon`, a zip of the folder
   with `addon.json` at its root. **An existing version is never overwritten:** bump `version` first. Every earlier
   version stays.
3. Writes `<package>.sha256` (`"<sha256>  <file>\n"`).
4. Re-creates `dist\addons\<id>.zip`, a zip of the whole `dist\addons\<id>\` folder (all versions and checksums, with
   the folder itself as the zip's top level).

The packages in `dist\addons\` today:

| Add-on id | Versions kept | API in the package |
|---|---|---|
| `com.darkcipher.sample.hello-hosts` | 1.1.0 | 2 |
| `com.darkcipher.portal-browser` | 1.0.0, 1.0.1 | 1, **2** |
| `com.darkcipher.shared-library` | 1.0.0, 1.0.1 | 1, **2** |
| `com.darkcipher.theme.sample` | 2.0.0 | **2** |

From 0.1.18 the app refuses API 1 packages at install, so only the newest version of each is installable; the older
ones are kept as history only. (Until 0.3.1 the sample theme carried an organisation's own branding under another id; it
was replaced by the neutral Ocean sample in 0.4.0 so the source tree can be published.)

### Rebuilding the SDK DLL
`samples\addons\sdk\DcxBridge.dll` is `DcxBridge.cs` compiled for .NET Framework so Windows PowerShell 5.1 can load it:

```powershell
cd samples\addons\sdk
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:library -r:System.Core.dll -out:DcxBridge.dll DcxBridge.cs
```

Copy the new `DcxBridge.dll` and `DcxAddon.ps1` into every program add-on that ships them (today `hello-hosts`), bump
that add-on's version, and pack it again. The app embeds the SDK files at build time, so rebuild the app too.

---

## 10. Icon and logo: `tools\make-icon.ps1`

`powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1` draws `src\DcxManager\Assets\app.ico` (16–256 px) and
`logo.png` (512 px) with System.Drawing: a teal server rack with status LEDs and a `>_` prompt inside the darkcipher0x
ring, on black. Run it only when the artwork changes; both files are committed.

---

## 11. The MSI (`installer\DarkCipherServerManagement.wxs`)

- Per-machine, x64, `UpgradeCode 6b7ac824-…`, `MajorUpgrade` with a downgrade error.
- Installs to `C:\Program Files\DarkCipher Systems\ServerManagement` from `-d PublishDir`.
- Start menu folder **DarkCipher Systems** with the app shortcut and a "Repair or uninstall" shortcut; an optional desktop
  shortcut (`INSTALLDESKTOPSHORTCUT`, on by default). The shortcuts carry the app's AUMID so the window groups with them.
- Options page: install the WebView2 Runtime via `winget` (`INSTALLWEBVIEW2`, ticked only when the runtime isn't found
  by registry search) and Ollama via `winget` (`INSTALLOLLAMA`, off). Both are deferred, impersonated, `Return="ignore"`.
- Uninstall page: **Remove your data too** (`REMOVE_DATA`) and the cache (`REMOVE_CACHE`). On a real uninstall (not an
  upgrade) with either ticked, the MSI runs `DcxServerManager.exe --cleanup data=[REMOVE_DATA] cache=[REMOVE_CACHE]`.
  `App.Cleanup` only ever deletes `%APPDATA%\DarkCipher Systems\ServerManagement` (and, when the store was moved, the store,
  snapshot and session-log *files* at the moved location) and `%LOCALAPPDATA%\DarkCipher Systems\ServerManagement`.
- Licence page from `installer\License.rtf` (MIT).

---

## 12. Release checklist

1. All work for the version is in; the watched self-test is green (section 4), ideally from an isolated copy.
2. Bump `<Version>`; write `docs\release-notes\<ver>.md`; add its `DOC` line to the top of the release-notes part of
   `docs\guide\guide.txt` (and the other guide manifests); update README's version badge and its Release history.
3. `.\build.ps1` (the self-test runs again inside it).
4. `.\tools\make-guide.ps1`. Open one PDF and check its cover and footer follow the naming rule in section 7:
   the teal "DARKCIPHER SYSTEMS" line above the white "ServerManagement" title, and the footer "ServerManagement <ver> – DarkCipher Systems".
5. Optional: `.\tools\make-msix.ps1 -Check` (and the Store package once the identity exists).
6. Changed add-ons: bump their `version`, `.\samples\addons\pack.ps1 <folder>`.
7. Record the build in `docs\RESUME-<ver>.md` (version, time, self-test count, what is untested).

Publishing to the website (`DarkCipherSystems-Website`) and the Discord release card are separate steps with their own
scripts and privacy gates. The only uploads from this repository are build.ps1 step 7 (`tools\release.ps1`): the sanitised source and the GitHub Release.
