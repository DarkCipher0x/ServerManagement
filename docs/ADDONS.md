# Add-ons for DarkCipher ServerManagement (API 2)

This is the complete add-on reference: the package format, every manifest rule, the install and run life cycle, the
sandbox, the encrypted bridge, every method and permission, the SDK, the four kinds of add-on, and each add-on that ships
with the project. It was checked against `src/DcxManager/Addons/*.cs`, `Views/AddonsView.cs` and `samples/addons/**` for
0.1.18.

Design records: ARCHITECTURE.md ADR-012 (add-ons), ADR-030 (themes), ADR-032 (portal browser), ADR-033 (shared library),
ADR-035 (encrypted bridge, sealed data-only add-ons), ADR-036 (full-control API, API 2 for every add-on). Where the code
lives: [SOURCE.md](SOURCE.md) section 9. Packing: [BUILD.md](BUILD.md) section 9.

> **Every add-on is API 2 (since 0.1.18).** A package must declare `"api": 2` to install, whatever its kind. API 1 was
> the old plain-text bridge. An API 1 *program* add-on that is already installed is blocked when run; an API 1
> *data-only* add-on installed earlier keeps working (it has no channel to the app and is sealed at rest), but it can't
> be reinstalled or updated as API 1.

---

## 1. Kinds of add-on

| Kind | Manifest has | Runs a program? | What it does |
|---|---|---|---|
| **Program** | `entry`, `commands`, optional `permissions` | yes, sandboxed, per command | commands on the Add-ons screen, the host menu or the terminal menu; talks to the app over the encrypted bridge |
| **Theme** | `theme` (and no `entry`) | no | recolours the app and can rebrand its header (ADR-030) |
| **Portal browser** | `browser` | no | switches on Portals and brings starter bookmarks (ADR-032) |
| **Shared library** | `library` | no | switches on the Shared library, optionally naming one team library (ADR-033) |

Data-only kinds can't have permissions or commands. A portal browser or shared library add-on can't also carry an entry,
theme or anything else; a program add-on may carry a `theme` file too.

---

## 2. Package format

A `.dcxaddon` file is a zip archive with `addon.json` at its root, plus your files. Limits checked at inspection:
- 1–500 files; the package and its unpacked size each ≤ 50 MB.
- Every path is relative, ≤ 200 characters, with no `:`, no leading `/`, no empty, `.` or `..` segment and no invalid
  file-name characters (no zip-slip).
- `addon.json` ≤ 64 KB, read case-insensitively, depth ≤ 8.

### `addon.json` fields

```json
{
  "api": 2,
  "id": "com.example.disk-check",
  "name": "Disk check",
  "version": "1.0.0",
  "publisher": "Example Ltd",
  "description": "Adds a disk-usage event to the active incident.",
  "entry": "run.ps1",
  "timeoutSeconds": 60,
  "permissions": ["hosts.read", "incidents.write"],
  "commands": [
    { "id": "check", "title": "Disk check → incident", "context": "host" }
  ]
}
```

| Field | Rules (from `AddonPackages.Validate`) |
|---|---|
| `api` | **must be `2`.** `1` is refused with "the old unencrypted standard"; anything else with "this app provides API 2" |
| `id` | 3–64 characters, `^[a-z0-9][a-z0-9.-]{2,63}$`; never changes between versions |
| `version` | `major.minor.patch`, each 1–5 digits; an update must be strictly newer (equal or older is refused) |
| `name` | 1–60 characters |
| `publisher` | 1–80 characters (shown as "as stated by the package": it is not verified) |
| `description` | ≤ 1000 characters |
| `entry` | program add-ons: a `.exe` or `.ps1` in the package. `.ps1` runs with `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File` |
| `timeoutSeconds` | 5–600 (default 60); the process is killed after it |
| `permissions` | names from section 6; unknown names are refused; duplicates removed |
| `commands` | 1–30; each `id` `^[a-z0-9][a-z0-9.-]{0,40}$` and unique, `title` 1–60, `context` `global` (Add-ons screen), `host` (host right-click menu) or `terminal` (terminal right-click menu) |
| `theme` / `browser` / `library` | a `.json` file in the package (sections 9–11) |

---

## 3. Life cycle

1. **Inspect** (`AddonPackages.Inspect`): hashes the package (SHA-256), checks the limits and the manifest, parses the
   kind's data file strictly (theme, browser, library) and refuses downgrades and same-version installs. For an `.exe`
   entry it checks the Authenticode signature on a temporary copy ("Signed by …", "NOT signed" or "Signature INVALID");
   a `.ps1` shows "script (no signature check)"; data-only kinds show "theme only / portal browser / shared library (no
   program)". Nothing is installed yet.
2. **Approve:** the Add-ons screen shows publisher, description, program and signature, package SHA-256, file count, size,
   timeout, commands, and each requested permission with its risk; on an update, new permissions are marked.
3. **Install** (`AddonPackages.Install`):
   - The package is hashed again and must match what was approved.
   - Files are unpacked into `…\addons\<id>\<version>.installing`, then moved to `…\addons\<id>\<version>`
     (`%LOCALAPPDATA%\DarkCipher Systems\ServerManagement\addons`, or `<DCX_DATA_DIR>-addons` in the self-test). The SHA-256 of
     every file is recorded.
   - **Data-only add-ons are sealed:** each file is written encrypted with the store key (AES-256-GCM, AAD
     `addon|<id>|<version>|<path in lower case>`), `Sealed = true`. Program files stay plain so Windows can run them.
   - Grants: a new install gets what the user ticked. An update keeps the old grants that the new manifest still asks
     for, and adds only the newly requested permissions the user ticked.
   - The previous version's folder is deleted; the writable data folder is created; the install is audited.
4. **Verify** (`VerifyFiles`): before every run, and whenever a data-only file is read, every file must match its hash and
   no file may have been added. A mismatch disables the add-on with the reason ("files changed after install").
   `ReadInstalled` is the only way the app reads a data-only add-on's file: it decrypts and checks the hash.
5. **Migrate** (`SealExisting`, at start-up): data-only add-ons installed before 0.1.17 are sealed in place (two phases:
   all sealed copies first, then swapped in) — only when every file still matches.
6. **Manage:** enable/disable, grant/revoke each permission (a HIGH one asks again), view the run log (last 50 runs),
   remove (deletes the install folder and, by default, the data folder). Everything is audited.

---

## 4. The sandbox (program add-ons)

`Addons/SandboxProcess.cs` starts the entry with `CreateProcessAsUser`:
- **Low integrity** token (`S-1-16-4096`): no writes to Medium-labelled places — your profile, the app, other add-ons.
- **Job object:** killed when the job closes or the run ends, ≤ 512 MB per process, ≤ 8 processes, dies on an unhandled
  exception. There is **no unsandboxed fallback**: if the sandbox can't be created, the add-on doesn't run.
- **Only three handles inherited** (stdin, stdout, stderr pipes), by an explicit `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`.
- **Nobody else can open it:** protected process and thread DACLs. SYSTEM has full access; everyone else, and the owner
  through OWNER RIGHTS, gets only synchronize and query information (`0x101400` for the process, `0x100840` for the thread),
  which .NET inside the add-on needs on itself (with less it fails at start with 0x80131623). No memory reads or writes,
  no handle duplication, no injected threads.
- **Environment:** a whitelist (`SystemRoot`, `SystemDrive`, `windir`, `ComSpec`, `PATHEXT`, `PATH`, `OS`,
  `PROCESSOR_ARCHITECTURE`, `NUMBER_OF_PROCESSORS`, `USERNAME`, `USERPROFILE`, `HOMEDRIVE`, `HOMEPATH`, the Program Files
  and CommonProgramFiles variables, `ProgramData`, `PSModulePath`, `LOCALAPPDATA`, `APPDATA`) plus `TEMP` = `TMP` =
  `DCX_ADDON_DATA`, `DCX_ADDON_ID`, `DCX_ADDON_VERSION` and `DCX_ADDON_API` (`2`).
- **Writable folder:** `DCX_ADDON_DATA` = `%USERPROFILE%\AppData\LocalLow\DarkCipher Systems\ServerManagement\addons\<id>`
  (`…\DarkCipher\selftest\…` in the self-test).
- **Run limits:** the manifest timeout, 2,000 messages, 1 MB of messages and 256 KB per line. Crashes, hangs, floods and
  protocol errors end the run, never the app. Runs are refused while offline mode blocks add-ons.
- **Stated limitation:** a Low-integrity process can still read files your account can read and use the network.

---

## 5. The encrypted bridge (API 2)

**Handshake** — the only plain-text lines:
- app → add-on: `{"dcx":"hello","api":2,"epk":"<base64 X‖Y>","salt":"<base64, 32 bytes>"}`
- add-on → app: `{"dcx":"hello","epk":"<base64 X‖Y>"}`

Both `epk` values are ephemeral P-256 public keys (64 bytes: X then Y), new for every run. The app writes its hello by
hand so base64's `+` is never JSON-escaped (the SDK's parser is small).

**Keys:**
- `ikm = SHA-256(ECDH shared secret)`.
- `okm = HKDF-SHA256(ikm, salt, info = "dcxaddon-bridge-2|" + appEpk + "|" + addonEpk, 64 bytes)`.
- App → add-on uses `okm[0..32]`; add-on → app uses `okm[32..64]`. Both are wiped from memory after the AES objects are made.

**Frames:**
- Each line is `base64(ciphertext ‖ 16-byte tag)`, AES-256-GCM, AAD `dcxaddon-bridge-2`.
- The nonce is never sent: 12 bytes, byte 3 = direction (0 app → add-on, 1 add-on → app), bytes 4–11 a big-endian
  64-bit counter from 0.
- A changed, dropped, replayed or reordered frame, or a plain-text line, fails to decrypt and ends the run.

**Messages** (JSON inside the frames):
1. The app sends `{"jsonrpc":"2.0","method":"invoke","params":{"api":2,"command":"check","context":{"hostId":"…","hostName":"web-01","terminal":"…"}}}`.
2. You call methods with `{"id":1,"method":"hosts.list","params":{}}`. The app answers `{"jsonrpc":"2.0","id":1,"result":…}`
   or `{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"permission denied: hosts.read"}}`.
3. Progress: `{"method":"log","params":{"text":"checking…"}}` (shown in the run log).
4. Finish with `{"method":"done","params":{"text":"Result shown to the user","show":true}}`, then exit. Without `done`
   the run is "ended without a result" (exit 0) or "crashed (exit code N)".

**What the encryption covers:** no other process can read or inject messages on the pipes, even one that duplicated a
pipe handle; a secret handed over by `credentials.request` crosses the pipe encrypted. It does not hide from the add-on
what the app gives it: that is what permissions and per-request prompts are for.

---

## 6. Methods and permissions

### Fixed bridge methods (`AddonHost.Call`)
| Method | Permission | Params → result |
|---|---|---|
| `app.info` | — | → `{version, addon, granted[]}` |
| `app.capabilities` | — | → `{api: 2, encrypted: true, methods[], tools[]}`: every bridge method and app capability with its permission, risk and whether it is granted |
| `app.call` | `app.read` / `app.private` / `app.control` (by the tool's risk) | `{tool, args}` → `{tool, result}`: runs an app capability (below) |
| `ui.notify` | — | `{text}` (≤ 300) → a status-bar line `◇ <name>: <text>` |
| `hosts.list` | `hosts.read` | → `[{id, name, address, port, user, folder, environment, shared, library}]` (never credentials; shared-library hosts only with `library.read`, `shared: true`) |
| `incidents.active` | `incidents.read` | → `{id, title, status, severity}` or null |
| `incidents.list` | `incidents.read` | → `[{id, title, status, severity, entries}]` |
| `incidents.addEvent` | `incidents.write` | `{title, text?, kind?: "event"\|"log", incident?: id}` (default: the active one) → `{id, incident}`; the title gets "(add-on: <name>)" |
| `notes.search` | `notes.read` | `{query}` → `[{id, title, snippet}]` (≤ 50) |
| `notes.create` | `notes.write` | `{title, body?, hostId?}` → `{id}`; tagged `addon` and the add-on id |
| `terminal.screen` | `terminal.read` | → `{lines[], hidden}`: the invoking terminal's visible rows, secrets masked, nothing at a password prompt (terminal commands only) |
| `files.pick` | `filesystem.read` | `{title?}` → the USER picks a file: `{name, text}` (≤ 2 MB) or `{cancelled: true}`; audited |
| `credentials.request` | `credentials.request` | `{reason}` (≤ 300) → the USER sees your name and reason, then picks one credential (local or shared) or denies: `{name, username, secret}` or error -32003. Never remembered; audited (also in the library for a shared credential) |

### `app.call`: every app capability (ADR-036)
`app.call` runs a capability through the AI operator's own code (`Assistant.RunToolForAsync`) — the same checks and
approvals the AI gets, with the add-on named in each approval and every call audited as `addon:<id>`. The capabilities
are the operator's built-in tools (`list_hosts`, `host_overview`, `read_logs`, `list_services`, `run_command`,
`service_action`, `vultr_*`, `cloud_list_instances`, `cloud_instance_action`, `list_files`, `read_file`,
`transfer_files`, `make_directory`, `rename_path`, `delete_path`) and every tool in the capability registry (`Services/Capabilities.cs`)
(infrastructure, cloud screens, terminal target, notes, incidents, PDFs, transfers, portals, libraries — see SOURCE.md
section 8). Ask `app.capabilities` for the live list with parameters.
- A **read** needs `app.read`; a **private** read (notes, incidents, documents) needs `app.private` and asks each time;
  a **change** needs `app.control` **and** the user's approval of the exact action (PRODUCTION hosts need the name typed).
- `args` must be an object ≤ 200 KB. A decline is -32003; an unknown tool -32601.
- **AI features off (0.2.0):** the six capabilities that act on the AI Engineer's target terminal (`terminal_run`,
  `terminal_read_screen`, `terminal_send_keys`, `terminal_interrupt`, `terminal_switch_target`, `terminal_open_session`)
  belong to the AI module. With AI off they are not in `app.capabilities`, and calling one returns -32601 with the
  message "capability unavailable (AI off): <name>". Every other capability is unchanged; check `app.capabilities`
  instead of assuming the list.

### Registered methods (`AddonApi.Register`)
| Method | Permission | Params |
|---|---|---|
| `hosts.add` | `hosts.write` | `{name, address, port?, user?, folder?, protocol?: "ssh"\|"rdp", environment?, tags?, notes?, credentialId?}` → `{id}`; approved with the exact fields |
| `hosts.update` | `hosts.write` | `{hostId, …any field of hosts.add}` → `{changed}`; validated as a dry run, then the exact changes are approved |
| `hosts.remove` | `hosts.write` | `{hostId}` → `{removed}`; credentials stay in the vault. Only local saved hosts (shared ones are managed in their library) |
| `ui.open` | `ui.control` | `{screen}`: dashboard, hosts, commander, notes, sessionlogs, incidents, infra, cloud, portals, snippets, tunnels, serial, lan, credentials, knownhosts, library, apiexplorer, aipanel, assistant, reports, help, privacy, addons, audit, settings |
| `ui.openHost` | `ui.control` | `{hostId, view?: "server"\|"terminal"\|"files"\|"commander"\|"notes"}` (a terminal signs in with the host's own credential) |

App features can add their own `AddonMethod`s; the fixed names can't be replaced.

### Permissions (what the user sees)
| Permission | Risk | Grants |
|---|---|---|
| `hosts.read` | low | names, addresses, ports, users, folders of saved hosts — never passwords or keys |
| `library.read` | low | also the hosts of your shared libraries, marked shared |
| `ui.control` | low | opening screens and a host's server, terminal, files or notes view |
| `incidents.read` / `incidents.write` | medium | read incidents / add entries (marked with the add-on's name) |
| `notes.read` / `notes.write` | medium | search and read notes / create notes (tagged) |
| `terminal.read` | medium | the visible screen of the terminal you ran it from (masked) |
| `filesystem.read` | medium | a file you pick, each time |
| `app.read` | medium | any read-only app capability (the AI operator's reads) — never passwords or keys |
| `app.private` | medium | notes, incidents and documents through app capabilities; each read asks |
| `hosts.write` | medium | add, change or remove saved hosts; each change shows the exact fields |
| `credentials.request` | **HIGH** | ask you for a password or key; you choose or deny, every time |
| `app.control` | **HIGH** | ask to make ANY change the app can make; each change is shown and approved |

Updates keep old grants; new permissions stay off until approved; any permission can be revoked at any time (calls then
fail with -32001 "not granted by the user", or "not requested in addon.json").

### Error codes
`-32601` unknown method or capability, `-32602` bad params, `-32001` permission denied, `-32003` the user declined,
`-32000` other (including "internal error").

---

## 7. The SDK (`samples/addons/sdk`)

| File | What it is |
|---|---|
| `DcxBridge.cs` | The bridge for any .NET add-on: `DcxBridge.Connect()` runs the handshake on stdin/stdout and returns the `invoke` JSON; `DcxBridge.Read()` returns the next message (null when the app closed the bridge); `DcxBridge.Write(json)` sends one. Compiles for .NET Framework 4.x and .NET 6+. Includes RFC 5869 HKDF and AES-256-GCM through Windows CNG (`bcrypt.dll`), because .NET Framework has no `AesGcm`. |
| `DcxBridge.dll` | `DcxBridge.cs` compiled for .NET Framework so Windows PowerShell 5.1 can `Add-Type` it. Rebuild: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:library -r:System.Core.dll -out:DcxBridge.dll DcxBridge.cs` |
| `DcxAddon.ps1` | PowerShell wrapper: `Connect-Dcx` (handshake → the invoke object), `Invoke-Dcx <method> [params]` (call → result; throws on an error reply), `Get-DcxCapabilities` (`app.capabilities`), `Invoke-DcxTool <tool> [args]` (`app.call` → its `result`), `Write-DcxLog <text>`, `Complete-Dcx <text> [-Show]`, and the low-level `Send-Dcx`. |

Put `DcxAddon.ps1` and `DcxBridge.dll` next to your script in the package. The app checks the hash of every package file
before each run, so the DLL can't be swapped after install. The three SDK files are also embedded in the app
(`sdk/*` resources) for the self-test.

### Minimal PowerShell add-on
```powershell
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DcxAddon.ps1"
$invoke = Connect-Dcx
$hosts = @(Invoke-Dcx 'hosts.list')
Complete-Dcx "You have $($hosts.Count) saved hosts." -Show
```

### Calling anything the app can do
```powershell
# addon.json: "permissions": ["app.read", "app.control"]
$caps = Get-DcxCapabilities                       # what exists and what you are granted
$overview = Invoke-DcxTool 'host_overview' @{ host = 'web-1' }
Invoke-DcxTool 'service_action' @{ host = 'web-1'; service = 'nginx'; action = 'restart' }   # the user approves it first
```
Check parameter names in `app.capabilities` (`tools[].parameters`) before relying on them.

### A .NET add-on
Add `DcxBridge.cs` to your project (or reference the DLL), build an `.exe`, and make it the `entry`:
`var invoke = DcxBridge.Connect(); DcxBridge.Write("{\"id\":1,\"method\":\"app.info\"}"); var reply = DcxBridge.Read(); …`.
Sign the `.exe` if you can: the approval screen shows the signer.

---

## 8. Data-only add-ons: sealed at rest
- Theme, portal browser and shared library add-ons have no program and no bridge. Their files are encrypted with the
  store key at install (section 3) and read only through `AddonPackages.ReadInstalled`, which decrypts them and compares
  the SHA-256 approved at install. A changed file disables the add-on.
- They still declare `"api": 2`: one standard for every package.

---

## 9. Theme add-ons (ADR-030)

A theme add-on recolours the app and can rebrand its header (logo, product name, tagline). It has a `theme` file and no
`entry`, `commands` or `permissions`. Installing or enabling it puts it in use, overriding the app's light and dark
themes; **Back to the default theme**, Disable or Remove restores the app's own palette and branding. One theme is in use
at a time (`AppSettings.ThemeAddon`).

```json
{
  "api": 2,
  "id": "com.example.theme.ocean",
  "name": "Ocean theme",
  "version": "1.0.0",
  "publisher": "Example Ltd",
  "description": "Blue and sand.",
  "theme": "theme.json"
}
```

`theme.json` (≤ 64 KB, UTF-8):

```json
{
  "name": "Ocean",
  "notes": "free text, ≤ 1000 characters",
  "productName": "Ocean ServerManagement",
  "tagline": "",
  "logo": "logo.png",
  "logoBackground": "#FFFFFF",
  "uiFont": "Roboto",
  "colors": {
    "Bg":     { "dark": "#071733", "light": "#FFFFFF" },
    "Accent": { "dark": "#7BA6DE", "light": "#243C8E" }
  }
}
```

| Field | Rules |
|---|---|
| `colors` | required, at least one key (table below) |
| `productName` | ≤ 60 characters of plain text; replaces "DarkCipher ServerManagement" in the header and the window title |
| `tagline` | ≤ 60 characters; replaces the `darkcipher0x@servers:~$` line under the name; `""` hides it |
| `logo` | a `.png` in the package, ≤ 1 MB, ≤ 2048 × 2048 px, one frame (decoded with the PNG decoder whatever the bytes claim); shown 36 px high (wide logos up to 200 px) |
| `logoBackground` | `#RRGGBB`: a rounded plate behind the logo, for logos drawn for a white background |
| `uiFont` | a family list (≤ 120 characters), used only when installed and the user has no UI font of their own |
| `name`, `notes` | free text for people, ≤ 1000 characters |

| Colour key | What it paints |
|---|---|
| `Bg`, `Surface`, `Input` | window background; sidebar, status bar, cards and panels; text boxes and lists |
| `Fg`, `Muted` | normal text; secondary text and hints |
| `Line`, `Hover`, `Track`, `ScrollThumb` | borders; hovered rows; scroll tracks and thumbs |
| `HeaderBg`, `HeaderFg` | the top bar and the product name on it |
| `Good`, `Warn`, `Bad` | status colours |
| `Accent`, `AccentHover`, `OnAccent` | primary buttons, focus, highlights, sidebar headings; their hover; text on them |
| `Brand` | the prompt or tagline under the product name |

Rules: every colour is exactly `#RRGGBB`; a key may give `dark`, `light` or both, and whatever is left out keeps the
app's own colour; `Selection` is derived from `Bg` and `Accent`. **Not themable:** the environment label colours (a safety
signal, ADR-017), terminal colours (terminal profiles), remote desktops, dialog titles, the installer and the taskbar icon.
Font files in the package are not loaded. Unknown fields or keys, other colour formats, wrong types, no colours, and logos
that aren't a small PNG in the package are refused at inspection.

---

## 10. Portal browser add-ons (ADR-032)

The portal browser is part of the app and has been **on by default since 0.1.17** (Settings → Integrations → built-in
portal browser). The add-on still switches it on when the built-in switch is off, and brings starter bookmarks. The
package never sees cookies, pages or the vault.

`browser.json` (≤ 64 KB, UTF-8): `name` (required, ≤ 60), `notes` (≤ 1000), `bookmarks` (≤ 50 starter bookmarks, each
only `title` ≤ 80, `url`, `folder` ≤ 120). Addresses must be absolute `https://` or `http://` without `user:password@`.
Starter bookmarks are offered on the Portals screen ("Add N starter bookmarks"); nothing is added without a click.
Unknown fields, wrong types and other schemes are refused at inspection.

```json
{
  "name": "Lab portals",
  "notes": "Optional text shown on the Add-ons screen.",
  "bookmarks": [
    { "title": "Proxmox VE", "url": "https://192.0.2.10:8006/", "folder": "Lab" }
  ]
}
```

---

## 11. Shared library add-ons (ADR-033)

A shared library add-on connects the app to the Shared library feature. Encryption, sync and merging run in the app,
never in an add-on process, so decrypted team secrets never reach add-on code.
- **The general add-on** (`com.darkcipher.shared-library`) names no library: it only switches the Shared library screen on.
- **A team add-on** names one library. You don't write it by hand: **Shared library → Create a library…** or **Export
  team add-on…** produces it (`SharedLibraries.WritePackage`): id `com.darkcipher.library.<first 12 hex of the library id>`,
  name `Shared library: <name>`, `"api": 2`. It holds no secret; an admin approves each member after checking their
  fingerprint.

`library.json` (≤ 16 KB): `name` (1–60), `notes` (≤ 1000), and for a team add-on `libraryId` (32 lowercase hex), `path`
(an absolute folder `C:\…` or `\\server\share\…`, only a suggestion when joining) and `ownerFingerprint` (the app refuses a
share whose owner key doesn't match). Any other field is refused.

Program add-ons see shared hosts only with `library.read`; `credentials.request` may be answered with a shared
credential, which is also audited in the library.

---

## 12. The add-ons in this repository

All four live in `samples/addons/<folder>` and are packed with `samples\addons\pack.ps1` into `dist\addons\<id>\`
(BUILD.md section 9). Older packed versions that declare API 1 are kept as history and can't be installed by 0.1.18.

### Hello hosts (sample) — `samples/addons/hello-hosts`
| | |
|---|---|
| id / version | `com.darkcipher.sample.hello-hosts` / **1.2.0** (packed so far: 1.1.0) |
| kind | program, API 2, entry `run.ps1`, timeout 30 s |
| permissions | `hosts.read`, `incidents.read`, `incidents.write` |
| commands | `summary` "Host summary" (global): counts hosts by folder and, if an incident is active, adds the summary to its timeline; `note-host` "Add host to active incident" (host menu); `capabilities` "What can add-ons call?" (global, new in 1.2.0): calls `app.capabilities` and lists the bridge methods and app capabilities with what this add-on may use |
| files | `addon.json`, `run.ps1`, `DcxAddon.ps1`, `DcxBridge.dll` (identical to the SDK copies) |
| at rest | plain (a program), hash-checked before every run |
| tests | the self-test packs this folder, installs it, runs `summary` in the real sandbox over the encrypted bridge and checks the incident entry |

### Portal browser — `samples/addons/portal-browser`
| | |
|---|---|
| id / version | `com.darkcipher.portal-browser` / **1.0.1** (API 2; 1.0.0 was API 1) |
| kind | data only: `browser.json` (name "Portal browser", a note pointing at the Portals screen and the host menu, no starter bookmarks) |
| permissions / commands | none |
| at rest | sealed with the store key |

### Shared library — `samples/addons/shared-library`
| | |
|---|---|
| id / version | `com.darkcipher.shared-library` / **1.0.1** (API 2; 1.0.0 was API 1) |
| kind | data only: the general add-on; `library.json` has only `name` and `notes` |
| permissions / commands | none |
| at rest | sealed with the store key |

### Sample theme (Ocean) — `samples/addons/sample-theme`
| | |
|---|---|
| id / version | `com.darkcipher.theme.sample` / **2.0.0** (API 2) |
| kind | data only: `theme.json` + `logo.png` |
| what it sets | `productName` "Ocean ServerManagement", empty tagline, a sample logo on a white plate, `uiFont` Roboto, and all 18 colour keys for dark and light (blue palette; white header and pages in light mode, deep navy in dark mode) |
| at rest | sealed with the store key |
| note | the starting point for an organisation's own theme: copy the folder, change the values, pack it. Settings → Appearance → Branding & colours → **Export as theme add-on** writes the same package from the live settings (0.4.0). |

---

## 13. When something is refused
| Message | Meaning |
|---|---|
| "This add-on is made for API 1, the old unencrypted standard…" | rebuild/repack with `"api": 2` (and, for a program, the SDK) |
| "This add-on uses the old, unencrypted bridge (API 1)…" (when run) | an API 1 program installed before 0.1.17: install an API 2 version |
| "the add-on did not answer the encrypted handshake (API 2)" | the program wrote something other than the hello line: use the SDK's `Connect` first |
| "a frame that doesn't decrypt…" / "a plain-text line on the encrypted bridge" | something was written to stdout outside the SDK (e.g. `Write-Host` output or a stray `Write-Output`) |
| "The add-on's files no longer match what you approved…" | a file changed after install: the add-on was disabled; reinstall it from a trusted package |
| "Version X is already installed" / "Downgrades are refused" | bump `version` |
| "permission denied: X (not requested in addon.json)" / "(not granted by the user)" | add it to `permissions` / grant it on the Add-ons screen |
