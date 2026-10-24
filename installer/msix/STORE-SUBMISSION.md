# Microsoft Store: free code signing (ADR-029)

Microsoft's [code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
list one free option that gives no SmartScreen warnings: publish an **MSIX package** through the Microsoft Store. After
certification, the Store signs the package with Microsoft's certificate, so we never buy or manage one.

The other options don't fit:
- **Azure Artifact Signing** is not open to individual developers in the UK.
- **OV/EV certificates** cost £150+ a year.
- **Self-signed certificates** are for testing only.

The MSI and zip on the website stay unsigned, and the in-app updater stays off. A Store install gets its updates from
the Store, and the app turns its own updater off whenever it runs from a package.

**Status:** everything is prepared but nothing has been submitted. Nothing below has been done yet.

## What is in the repo

| File | Purpose |
|---|---|
| `installer\msix\AppxManifest.xml` | MSIX manifest. Full-trust desktop app with file and registry write virtualization off, so the Store install uses the same `%APPDATA%\DarkCipher Systems\ServerManagement` store as the MSI. |
| `installer\msix\store-identity.json` | The three identity values from Partner Center. Empty until you reserve the name. |
| `tools\make-msix.ps1` | Packages `.\publish` (built by `build.ps1`) as `dist\<ver>\DarkCipherServerManagement-<ver>-x64.msix`. |
| `tools\msix\MsixTools.csproj` | Pins Microsoft's `makeappx` and `signtool` (NuGet package `Microsoft.Windows.SDK.BuildTools` 10.0.28000.1839). |
| `Services\Packaging.cs` | Detects a Store or MSIX install. When it finds one, the app turns its updater off, keeps the manifest's taskbar identity, and restarts through the package. |

## One-time setup (you)

1. **Register as a developer.** Go to <https://storedeveloper.microsoft.com> and register as an **Individual**. This is
   free. Microsoft verifies your identity, which can take a few days.
2. **Reserve the name.** In [Partner Center](https://partner.microsoft.com/dashboard), go to Apps and games → New
   product → **MSIX or PWA app**, and reserve **DarkCipher ServerManagement**.
   - Don't pick "EXE or MSI app": that path makes us sign the installer ourselves.
3. **Copy the identity.** Open the product → Product management → **Product identity**. Copy these into
   `installer\msix\store-identity.json`:
   - `Package/Identity/Name` → `IdentityName`
   - `Package/Identity/Publisher` → `Publisher` (the whole `CN=…` value)
   - `Package/Properties/PublisherDisplayName` → `PublisherDisplayName`

## Each release

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```
```bash
powershell -ExecutionPolicy Bypass -File tools\make-msix.ps1
```
```bash
powershell -ExecutionPolicy Bypass -File tools\make-guide.ps1
```

- `make-msix.ps1` writes the unsigned Store package and adds it to `SHA256SUMS`.
- The MSIX version is the app version with `.0` added (for example 0.1.12 becomes 0.1.12.0). The Store requires the last
  part to be 0.
- Each submission needs a higher version than the last one, which the version bump on every build already gives.

Upload `DarkCipherServerManagement-<ver>-x64.msix` under the submission's **Packages**. Don't sign it: the Store does that.

## Test on this PC before submitting (optional)

```bash
powershell -ExecutionPolicy Bypass -File tools\make-msix.ps1 -TestSign
```

1. The command makes `…-x64-test.msix` and `…-local-test.cer`. They use a local test identity and a self-signed
   certificate that is created in your own `CurrentUser\My` store.
2. Trust that certificate once, from an **elevated** PowerShell. The script prints the exact `Import-Certificate` line.
3. Double-click the `.msix` to install it.
4. Uninstall it afterwards, and remove the certificate from certlm.msc → Trusted People.
5. Never upload the test files.

To check the manifest without signing anything:

```bash
powershell -ExecutionPolicy Bypass -File tools\make-msix.ps1 -Check
```

## Submission answers

- **Pricing:** free.
- **Category:** Developer tools (or Utilities & tools).
- **Privacy policy URL:** required, because the app makes network connections. Use the website's privacy page, which
  must state no telemetry, local encrypted storage, and connections made only to the user's own servers and chosen
  services.
- **Age rating:** complete the IARC questionnaire. There is no user-generated content shared between users.

**Restricted capabilities:** Certification asks why the package needs each one. Suggested text:

- **runFullTrust:** "Win32 desktop application (WPF, .NET 10). Provides SSH and RDP terminals (ConPTY), local
  PowerShell, Hyper-V management via Windows PowerShell, and an embedded WebView2. These require a full-trust process."
- **unvirtualizedResources:** "The app keeps an encrypted data store under %APPDATA%\DarkCipher Systems\ServerManagement. It
  is shared with the same product's MSI edition, so users can move between editions without losing data, and an
  uninstall never silently deletes the user's encrypted vault. The app writes only under its own DarkCipher folders."

**Notes for certification:** "No account needed to start. On first run the app creates a local encrypted store
protected by a password the user chooses. Infrastructure features need the user's own servers. A demo is not
possible without them, but the UI can be explored offline."

## Known differences in a Store install

- **No data clean-up on uninstall.** The MSI's "also delete my data" option (`--cleanup`) doesn't exist in MSIX. The
  data folder stays, which is deliberate. Delete it from Settings → Data before uninstalling, if you want it gone.
- **Windows 10 1903 or later.** The MSI supports 1809 or later; the MSIX needs 1903 because the virtualization opt-out
  requires it.
- **Fallback if certification refuses `unvirtualizedResources`.** Remove the two `desktop6:` lines and the capability.
  The store then lives in the package's private AppData, is separate from the MSI install, and **is deleted when the
  app is uninstalled**. Tell users to make a backup first.
- **The website MSI stays unsigned.** SmartScreen keeps warning on it. Only the Store copy is signed.
