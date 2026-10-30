# Security report — ServerManagement 0.6.31 – DarkCipher Systems

**Date:** 2026-10-30. **Version under test:** 0.6.31 (the build this report ships with), on .NET 10 (LTS, security
fixes until November 2028; ADR-092). The first public beta was 0.6.22 (2026-10-24). The 0.6.30 sweep of everything
added since then is in §4g (0.6.31 changes only the window layout and tab groups: no new inputs or connections); the earlier 0.6.x surfaces and the pre-launch audit are in §4b–§4f and in "Live attacks".
The probe-by-probe list (§3) covers 0.4.0–0.5.1; later probes are described with their sweeps. **Prepared by:** the maintainers,
from the automated suites that run on every build. **Scope:** the Windows application, its data store, every input it
takes from outside (servers, files, policies, add-ons, web and API answers) and every connection it makes.

This report says what was tested, how, and what was found. It makes no claim beyond its scope: the product has no server
side, so there was nothing to scan over a network; the tests are in-process attacks against the real code paths, plus
live loopback counterparts (TLS servers, fake identity providers and APIs) where a protocol is involved. Real external
systems (a domain controller, an Elasticsearch cluster, an Azure tenant…) are hand tests and are listed as open.

## 1. Summary

| | |
|---|---|
| Automated checks in the release run | 516 `ok` lines, 0 failures (full suite), plus the AI-off suite and the published-exe suite |
| Penetration-test suite (`SelfTestPentest.cs`) | **199 attack probes, all stopped** (92 from 0.4.0, 56 for the 0.5.0 surfaces, 29 for 0.5.1, 22 new for the 0.6.14–0.6.17 surfaces: Bitwarden, Password Safe links, vault credentials, UniFi, the AI tool-call scanner) |
| Attacks with a live counterpart (other suites, same run) | TLS pins (syslog, log sinks, LDAP, portals, Proxmox), plaintext refusal (syslog, SMTP), JWT (alg=none / HS256 refused), the local API (auth, Host / Origin, limits, lockout), the add-on sandbox and bridge, offline mode (rebinding), snapshot / backup tampering, the AI lock — all passed |
| Fuzzing | 40 untrusted-input parsers (deterministic seed 20261018; 8 new in 0.6.16 for bw / bws, UniFi and Site Manager answers and AI replies, 1 in 0.6.17 for stored vault-credential links), 13,060 calls, no crash or hang; 636 KB of hostile terminal output |
| Pre-launch audit (0.6.9) | dependencies (NuGet advisories, transitive), TLS validation overrides, token handling (update token, Microsoft Graph), shell construction (tmux), secret scan, privacy scan, add-on exclusion — 0 findings; see §4b |
| Post-quantum (0.6.12) | everything saved is quantum-safe (AES-256-GCM; Windows Hello now a hybrid with a DPAPI secret); SSH prefers ML-KEM / NTRU Prime hybrids; shared library and updates use ML-KEM-768 / ML-DSA-65 hybrids; see §4c |
| Bitwarden (0.6.14) | vault items and Secrets Manager secrets sign sessions in from memory through Bitwarden's own signed CLIs; secrets never on a command line, never stored, never listed; the master password stored only when the user ticks "Remember on this PC" (encrypted, bound to the server, §4f) |
| UniFi (0.6.16) | the Network Integration API and Site Manager with Ubiquiti's API keys: https only, the key only in its header, self-signed consoles pinned, ids checked, changes verified and only with "Allow changes"; Site Manager read-only (§4d) |
| Vault credentials from Bitwarden / Password Safe (0.6.17) | a vault credential can point at a Bitwarden item or secret or a Password Safe account (ids and a label only); every use reads it into memory, unattended checks never prompt, shared libraries never carry or follow such links (§4e) |
| A remembered Bitwarden master password (0.6.20) | opt-in at the unlock prompt; encrypted with the app's other secrets, bound to its server, forgotten when refused, on sign-out or by policy (`BitwardenAllowRemember`); start-up unlocks in the background without a prompt (§4f) |
| The 0.6.30 sweep (2026-10-30) | 248 attack probes, all stopped (49 new in section K for the 0.6.23–0.6.30 surfaces); 48 parsers fuzzed, 16,300 calls, no crash or hang; full suite 544 `ok` lines, 0 failures; 7 findings fixed (three Medium, four Low), see §4g |
| Findings fixed while building 0.5.1 | 5 — see §4a (one Medium, four Low) |
| Findings fixed while building 0.5.0 | 5 — see §4 (all Low; one found by a new pentest probe) |
| Findings fixed in 0.4.0 (second sweep) | 3 — see §5 |
| Open findings | 0 in scope; known gaps and open hand tests in §6 |

## 2. Method

1. **Threat model** (`SECURITY.md`): someone with a copy of the disk or a backup; another local user; a malicious or
   compromised remote server or provider API; prompt injection through content shown to the AI; network observers.
   Malware running as the same Windows user is out of scope (it reads memory and keystrokes).
2. **Attack surface inventory**, from the code: every parser of untrusted input (server output, file names, JSON / XML /
   CSV / zip / PDF / PNG, registry policy values, add-on packages, theme files, HTTP answers), every outgoing connection
   (TLS settings, pins, redirects, proxies), every place a secret could leak (messages, logs, audit, URLs, bodies, command
   lines), every privilege boundary (admin-protected settings, the add-on sandbox, the local API, the AI's approvals).
3. **Probes**: for each surface, inputs built to break it — traversal, injection, oversize, nesting bombs, wrong types,
   control characters, hostile schemes, header injection, XXE, entity expansion, forged answers, wrong certificates — and
   an assertion that the attack is **refused, neutralised or bounded** (never "handled by luck"). Probes live in the
   self-test so they run on every build; a failing probe fails the release.
4. **Review** of the diff of every change in the release for the classes of mistakes the probes can't see (missing
   `using`, a seam that bypasses a gate, a plain write): the second security sweep (§4).
5. **Verification of encryption at rest**: `EncryptionAudit` walks the test store and the logs after the whole suite ran
   and fails on any plain file that isn't on the plain-by-design list.

## 3. The penetration-test suite, probe by probe

All 177 probes passed in the release run. Grouped by surface:

### A. Path traversal through server-sent names (16 probes)
A hostile SFTP / SCP server names a file `..\..\x`, `../../etc/passwd`, `C:\Windows\win.ini`, `\\server\share\x`,
`a/b`, `a\b`, `..`, `.`, an empty name, a space, `x.`, `x `, `con`, `a\0b`, `a:b`, a 300-character name, a
right-to-left-override name. **Result:** every name is reduced by `LocalFs.SafeChild` to a plain file name inside the
download folder (no separators, drive letters, `..`, trailing dots or spaces, invalid characters); `Path.GetFullPath`
of the result stays under the folder. Download, Share and whole-tree download use it.

### B. Injection into spreadsheets and shells (17 probes)
CSV cells `=1+1`, `+cmd|' /C calc'!A0`, `-2+3`, `@SUM(A1)`, tab- and CR-led cells, `=HYPERLINK(…)`: every one is
quoted as text with a leading apostrophe; quotes, commas and line breaks are escaped. Shell arguments `'; rm -rf / #`,
`$(reboot)`, `` `id` ``, `$HOME`, `*`, quotes and newlines: `RemoteExec.Quote` single-quotes with `'\''` for every
quote, so nothing can end the quoting. Event-log names with `"`, `&`, `..`: refused by `WindowsEvents.CheckLog`.

### C. Hostile Group Policy and admin-protected values (17 probes)
REG_SZ with control characters or 3,000 characters, a choice outside its set, a DWORD out of range or negative, the
wrong registry type, REG_MULTI_SZ with 300 entries: every one is **ignored and listed** by `PolicySettings.Convert`
(the app keeps its default). The protected sink list: `http://`, `javascript:`, `file:` URLs, credentials in the URL,
9 KB of JSON, a 5,000-deep nesting bomb, a number or an object instead of a list — all refused by the sinks' own
rules through `ProtectedSettings.SinksCheck`; a good list passes. A syslog host with `;`, a recipient list with CRLF:
refused.

### D. Hostile answers from log sinks and hostile event text (17 probes)
A 1 MB sink answer is read at most 64 KB and reported in under 300 characters; ANSI escapes, control characters,
line breaks and a token in an answer are removed or masked; an Elasticsearch per-item error carrying a password is
reported masked; a non-JSON "200" is accepted (nothing to parse). An event of 1 MB is bounded to 4,000 characters;
terminal escapes and a bearer token in a session line are stripped and masked at creation (before the spool); a
kind with control characters is cleaned; damaged spooled lines are skipped. A 10,000-deep sink list, an object as a
list: empty. URLs with user info, fragments or any non-https scheme (`http`, `ftp`, `javascript`, `data`, `file`,
UNC): refused. Header injection: a tenant with CRLF is refused by validation; a secret with CRLF is refused by the
HTTP layer (the request never leaves).

### E. Hostile XML from the Event Viewer path (7 probes)
XXE (`<!ENTITY xxe SYSTEM "file:///…/win.ini">`): refused, no file content appears. The "billion laughs" entity
expansion and an external DTD: refused (`DtdProcessing.Prohibit`, no resolver). A 2 MB event message is bounded to
2,000 characters; malformed XML is an exception the report shows, not a crash; a 50,000-event digest completes in
under 5 s with a bounded result; the events CSV is formula-safe.

### F. Hostile answers from Grafana (8 probes)
Dashboard URLs on another host, `//evil`, UNC, `javascript:`, with a fragment, or with traversal: refused or kept
on the Grafana server. A 50,000-dashboard answer is capped at 500; a non-list answer and truncated JSON are
explained errors; dashboards whose URL points elsewhere are dropped and titles are cleaned of control characters;
a 401 never echoes the token (neither the app's nor the server's text).

### G. Hostile theme and branding files (10 probes)
Unknown keys, named colours, environment-label colours, a 1,000-character product name, a font name with CSS
separators, non-JSON, a 10,000-deep nesting bomb: refused by `ThemePack.Parse` (the same rules for a theme add-on, a
policy value and the Settings card). Logos that aren't PNG, a 2 MB "PNG", and a PNG header with garbage: refused by
the decoder (WIC, size- and dimension-limited).

### H. Hostile report data (1 probe)
Event records with a 5,000-character provider, `int.MaxValue` ids, escape sequences, empty fields and
`DateTime.MinValue` produce findings without control characters and with bounded titles.

### I. The 0.5.0 surfaces (56 probes)
- **Imported files (12):** an mRemoteNG file with an external entity (XXE) and one with a nested-entity bomb are refused
  before any entity is read; hostile host names (`x;reboot`, spaces, an embedded newline + `ProxyCommand`, `$(id)`,
  backticks, empty) are skipped with a note; a folder path cannot climb with `..` (`..\..\Windows/../x` → `Windows/x`);
  control characters are removed from names; an out-of-range port falls back to the protocol's default; an mRemoteNG
  value sealed with another key fails the GCM tag check before any plaintext is produced.
- **Backups (2):** a "no secrets" file keeps no credential, no credential reference on a host, and none of the app's own
  tokens (AI keys, SMTP password, sink tokens); an 11-character file password is refused.
- **Microsoft Teams (7):** webhook addresses over http, on a look-alike host (`evil.test/x.logic.azure.com`,
  `logic.azure.com.evil.test`, `x.logic.azure.com@evil.test`), with user info or a `javascript:` scheme are refused; HTML
  in a title or text is escaped.
- **BeyondTrust (10):** a `;`, `[`, `]` or CRLF in the Password Safe key, run-as user or password (PS-Auth header
  injection) is refused; a PRA site over http, with a path, user info, a query or a `file:` scheme is refused; a hostile
  Jump Item hostname is URL-escaped in the access-console link (it can't add `action=…`).
- **IDE links (3):** only the registered IDE schemes can be opened (`https:` and `file:` links refused); an address with
  a newline can't inject `ProxyCommand` into `dcx_config`; a path with a newline is refused.
- **Kerberos SSH (4):** user names carrying an option (`-oProxyCommand=…`), a `;`, a leading `-` or a quote never reach
  OpenSSH's command line.
- **Puppet (9):** certnames with `;`, `../`, a leading `-`, a space, `$(id)` or a NUL are refused; a disable reason with a
  newline, a Bolt plan name with `;` and a Bolt parameter with a newline are refused.
- **WSL (7):** distribution names with spaces, `;`, a leading `--`, `..` or empty are refused; a file outside the three
  editable ones (`/etc/shadow`) can't be read; a `.wslconfig` value carrying a new section (`8GB\n[boot]\ncommand=…`) is
  refused.
- **Pingdom (2):** an answer whose id isn't a number, or that isn't an object, is ignored; escape sequences in a check's
  name are removed.

### J. The 0.5.1 surfaces (29 probes)
- **Locks (6):** an add-on (the `Strict` caller) is refused a locked host even while the lock is open, and the host is
  hidden from it; a closed lock refuses the host; a password lock refuses a password under the minimum; a 1 MB password
  is refused cleanly; a forged rule with a garbage salt and hash never verifies; an unknown category target (`cat:../..`)
  matches nothing.
- **Restrictions (1):** an unknown action name is an error, never "allowed".
- **Dashboard (4):** forged card ids (`x:<script>`, `../x`) and an unknown card type are dropped; a 10 KB title falls back
  to the type's title; pipes and percent signs in a target or title round-trip without splitting the line.
- **Certificate pins (6):** BeyondTrust pins that aren't 64 hex digits (`../etc`, 63 digits, non-hex, a trailing newline,
  a `SHA256:` prefix) are refused by the protected-setting check; a different certificate is a mismatch even when its
  chain is valid.
- **Add-on policy (2):** a blocked id in other letter case is still blocked; a script can't pass "signed only".
- **Host import (2):** a public address and a public range are refused before anything is scanned.
- **VNC (8):** VeNCrypt Plain on a direct connection and anonymous-TLS-only servers are refused; hostile servers — RFB 4.0,
  a refusal with a 1 GB reason, 200 VeNCrypt sub-types, a 5,000-byte Diffie-Hellman key, a degenerate public key (1),
  a 0×0 desktop — end in a refusal, never a crash or a large allocation.

### Live attacks in the other suites (same run)
- **VNC (0.5.1):** eight loopback fake servers: the VNC password and Apple Remote Desktop sign-ins are checked by the fake
  (ARD decrypted with the negotiated key), a wrong macOS password is refused with the server's reason, VeNCrypt X.509
  is pinned on first trust and a changed certificate is refused, a server drawing outside the screen is cut off, and
  hostile desktop sizes and names are refused before any allocation.
- **BeyondTrust TLS (0.5.1):** a self-signed loopback PRA appliance is refused until pinned, then used; a swapped
  certificate is refused as changed.
- **Locks and restrictions (0.5.1):** every `Allow*` policy refuses its action with the reason, and `AllowNetworkScan`
  stops the scanners; the add-on bridge's `list_hosts` returns a locked host as `{name, locked}` only.
- **TLS:** a wrong SHA-256 pin is refused by the syslog forwarder and by every log sink (the messages wait in the
  encrypted queue and go when the pin is right); a plain TCP listener posing as a syslog collector only ever sees a
  TLS handshake byte; a self-signed LDAP server is refused without a pin; portal and Proxmox certificates are pinned
  on first use and a change is refused.
- **Identity:** the Entra id_token verifier refuses `alg=none` and HS256, wrong issuer / audience / nonce / tenant,
  expired tokens; the Windows sign-in refuses another account's SID and a missing required group; a tampered offline
  proof is ignored and grace 0 never opens offline.
- **Local API:** no token / wrong token → 401, 20 failures → 60 s lockout (423), an `Origin` header → refused, a wrong
  `Host` → refused, oversize bodies and headers → refused, nothing answers while locked.
- **Add-ons:** a modified file → disabled before the run; an API 1 package → refused at install; a probe add-on can't
  change anything without `app.control` and the user's approval; the bridge is encrypted (ECDH P-256 + AES-256-GCM)
  and a bad frame ends the run; the process runs at Low integrity in a Job with a restrictive DACL.
- **Store:** a snapshot restore or a backup import can't remove the AI lock or replace the key ring; damaged files are
  recovered from the newest good snapshot and kept aside; every file is bound to its name (no swapping).
- **Offline mode:** DNS rebinding is closed (connect only to the checked address); blocked calls open no socket.
- **Encryption at rest:** the audit over the test store after the full run finds **0 plain files** (the key ring,
  pointers, the profile index, the crash marker and add-on program files are plain by design and listed as such).

## 4b. The pre-launch audit (0.6.9–0.6.12, 2026-10-24)

Before the first public beta, every surface added since 0.5.1 was reviewed by hand against the code, and the release
gates were re-checked. No finding; nothing needed fixing.

| Area | What was checked | Result |
|---|---|---|
| Dependencies | `dotnet list package --vulnerable --include-transitive` over the whole solution; the build restores only what `packages.lock.json` pins and fails on high / critical advisories | no known-vulnerable package |
| TLS | every `RemoteCertificateValidationCallback` and `SslPolicyErrors` use: pins (`TlsPin`, syslog, log sinks, BeyondTrust, VNC, Proxmox, portals) refuse a changed certificate; the network toolbox's "inspect only" accepts any certificate by the user's explicit choice and labels the session UNTRUSTED | as designed |
| In-app updates (ADR-079/080, 0.6.12 ADR-090) | `update.json` is signed with ECDSA P-256 by the release key and, since 0.6.12, with ML-DSA-65 by a second release key (both required) and verified against the public key pinned in `Updater.cs`; the optional GitHub token is stored encrypted, sent only to `api.github.com`, and redirects are followed by hand so it never reaches the download host | as designed |
| Microsoft Graph (Cloud PCs, Microsoft 365 admin) | authorization code + PKCE + a random state, loopback redirect on 127.0.0.1 only, public client (no secret); refresh tokens sealed in the encrypted store with a separate cache per feature; access tokens in memory only; Cloud PC power actions behind admin approval and their own sign-in | as designed |
| tmux (ADR-085) | the custom config travels as base64 over the exec channel (never interpreted), into its own file (700 / 600), never `~/.tmux.conf`; the session name is checked against `[A-Za-z0-9_.-]{1,32}`; installing tmux happens only after the user agrees, with sudo prompting in the terminal | as designed |
| Secrets in the source | `tools\secret-scan.ps1` (13 project rules) over docs, tools, installer and samples, and over the release's source zip in the build | clean |
| Privacy | the public source is built from an allowlist and scanned for the maintainer's private terms; a hit fails the build | clean |
| Add-ons | packed add-ons (`dist\addons\`) are never published: `release-check.ps1` refuses an add-on among the release assets and any `.dcxaddon` or `dist/` entry in the source zip; the website hand-off carries only the installers, the user guide and the source | enforced |
| AI Engineer written tool calls (0.6.10, ADR-088) | calls a small model writes as text run only when the reply is nothing but calls to offered tools, through the same tools, approvals and read-only auto-run rules; a session id after "-- session" can't become the command; a reply that only describes calls is flagged "nothing ran" | as designed |
| Typed confirmations (0.6.11) | Enter in the typed box confirms only once the text matches and never cancels; matching ignores letter case and surrounding spaces; the confirmation is still required wherever it was | as designed |
| AI Engineer default target (0.6.11) | follows only the terminal the user brings to the front, until the user picks one (or none); held for the whole of an AI run; the AI can't switch it without approval; PRODUCTION still reviews every command | as designed |
| Theme packs (0.6.9) | colours only; environment labels (a safety signal, ADR-017) can't change; every pack meets the contrast rules in both modes | as designed |
| Docs (0.6.12, ADR-091) | each document its own sealed file (AES-256-GCM, file name as AAD), folder paths cleaned of `..`, pins validated; the preview is our own renderer: no HTML engine, no network (images never fetched), only https / mailto links, opened after a confirmation and through offline mode; a damaged file is set aside, never overwritten | as designed |
| Bitwarden (0.6.14, ADR-093) | only a CLI signed by Bitwarden (Authenticode with revocation) or an admin-approved SHA-256 starts, held open against writers until it runs; a copy of cmd.exe standing in for bw was refused until approved and again after one changed byte; secrets only as environment variables of that process (the self-test saw an inherited `BW_SESSION`, `NODE_OPTIONS` and `NODE_TLS_REJECT_UNAUTHORIZED` removed); no secret on any command line (checked on every scripted call); lists keep no password, key or value; option-shaped ids and searches refused before the CLI starts; the session key in memory only, dropped on idle, app lock and exit; offline mode governs sign-in, sync and Secrets Manager |

## 4g. The 0.6.30 sweep of the new surfaces (0.6.23–0.6.30, 2026-10-30, ADR-108)

DarkCipher0x asked for "a full pentest and debugging sweep". Every surface added since the 0.6.22 report was reviewed by
hand, probed (pentest section K, 49 new probes) and fuzzed (7 new parsers). Dependencies: no known-vulnerable package in
any of the 11 projects, transitive ones included. Secret scan: clean.

| Surface | What is in place | Checked by |
|---|---|---|
| Restored shell places (0.6.28–0.6.29) | the folder comes only from OSC 7 or a prompt line; a folder with a control or Unicode format character, or over 1,024 characters, is never restored; the `cd` line is one quoted word, POSIX lines start with a space to stay out of history; cmd and network gear get no `cd` | probes (curly quotes, line breaks, escapes, bidi, zero-width, `$(…)`, backticks); fuzzing (`shell-place`) |
| Snippets and the button bar (0.6.29, ADR-104) | host values and answers quoted per shell; control and format characters dropped; values that can't be quoted for cmd or network gear refused before anything is sent; risky commands and PRODUCTION targets confirmed | probes (PowerShell quotes, `@`, `,`, leading `-`, unquotable answers and host names); fuzzing (`snippet-variables`) |
| RDP / VNC through SSH (shared libraries) | a shared host tunnels only through an SSH host of its own library; your own hosts never fall back to a shared one | probes (by id, by address, another library, a look-alike library id) |
| Host-to-host copy and folder sync (0.6.26–0.6.27) | listings keep only plain names; SCP lines must name a file directly in the folder; local targets were already safe through `LocalFs.SafeChild` | probes (`..`, `/`, NUL, out-of-folder lines, impossible times); fuzzing (`scp-stat`) |
| This PC's SSH keys (0.6.30, ADR-107) | read-only; at most 64 KB read through the handle; names and comments shown without control or format characters; over 1,000 bcrypt rounds listed, never tried; the self-test never reads the real `.ssh` | probes (4 billion rounds, 70 KB under a default name, bidi names, lying headers); fuzzing (`local-key-header`); `LocalKeysTests` |
| Ctrl+click words and highlighting (0.6.29) | only http / https URLs, IPv4 addresses and absolute paths are offered, nothing runs without a click; highlight rules are checked for empty matches, inline options and backtracking (150 ms timeout) | probes; fuzzing (`highlighting`) |
| Palette search, AI sampling retry (0.6.29) | search is bounded by the item list; a refused sampling setting is removed and the call retried once, never in a loop | fuzzing (`palette-search`, `ai-refused-sampling`); review |
| Fill sign-in in frames (0.6.22) | same-origin frames only (`contentDocument`), the origin checked again before filling | review, no finding |

**Findings, all fixed in this release:**

| # | Severity | Finding | Fix |
|---|---|---|---|
| F1 | Medium | PowerShell ends a single-quoted string at any of ' ‘ ’ ‚ ‛, but only the ASCII quote was doubled. A server could print a PowerShell prompt into a local pwsh tab naming a folder with a curly quote; a restored saved session would then type the rest as a command. Snippet values had the same flaw. | `SnippetVariables.PowerShellQuoted` doubles all five, used by the restored `cd` line and by snippet values. |
| F2 | Medium | A shared library's RDP or VNC host could tunnel through one of your own SSH hosts: by the same address, or because the rebuild didn't remap `SshHostId`. A library owner could reach your bastion's network with your credentials. | `HostEntry.TunnelHost`: same library only, no fallback for shared hosts; the rebuild remaps the ids. |
| F3 | Medium | A hostile SFTP or SCP server could list names with `/` or `..`. In a host-to-host copy or sync, the name was joined onto the target folder on the other host, writing outside it. | `SftpFs.RemoteNameOk` filters SFTP and SCP listings; SCP lines must also name a file in the folder. |
| F4 | Low | A `{pick:}` answer listed by one host went unquoted into a cmd or network device terminal the same snippet was sent to, where it could add a command. | `Expand` refuses a value that isn't safe unquoted there; nothing is sent to any terminal. |
| F5 | Low | `.ssh` key import: a symlinked file's size check could be bypassed, bidi characters could disguise a name, and a key asking for billions of bcrypt rounds would freeze the app on a passphrase check. | Bounded read, cleaned names and comments, a 1,000-round cap. |
| F6 | Low | PowerShell plain words included `@` (splatting) and `,` (an array), and a value starting with `-` became a parameter name. | Those are now quoted. |
| F7 | Low | An SCP `stat` time outside the date range threw and failed the whole listing (found while writing the probes). | The time is treated as unknown. |

## 4f. Bitwarden speed, a remembered master password, sign-in in steps and TOTP (0.6.20, ADR-100)

DarkCipher0x asked for faster Bitwarden searches, for the master password to be remembered "encrypted, so that it
remembers the next time you use it and launch ServerManager", and for Fill sign-in to work on more pages, with copies of
the user name, password and TOTP code as a fallback.

| Property | How | Checked by |
|---|---|---|
| The held list has nothing secret | the item list read once per unlock keeps names, user names, addresses and ids (passwords dropped while reading bw's answer); it is in memory only and dropped when the vault locks (idle, app lock, exit, sign-out) | SelfTestBitwarden 2 and 4d (no secret in any listed field; lock → not held) |
| A remembered master password is opt-in, encrypted and bound | only when "Remember on this PC" is ticked at the prompt; kept in the encrypted settings with the app's other integration secrets (AES-256-GCM; never in the registry, a policy, an export or a backup); used only for the server it was typed for | SelfTestBitwarden 4d (unticked → not kept; another server → unused) |
| It can't go stale or outlive a decision | a password Bitwarden refuses is deleted and asked for again; signing out, a server change and "Forget" delete it; policy `BitwardenAllowRemember` (admin-protected) off: unused at once, deleted at the next unlock | SelfTestBitwarden 4d |
| Start-up never prompts | the background unlock at start-up and after the app unlocks runs only with a remembered password, a trusted bw and the integration on; any failure is logged, never shown as a dialog | SelfTestBitwarden 4d (no prompt; a refused password forgotten without one) |
| The password only goes where a password box is | Fill sign-in probes the page with a script that carries no secret; the password script runs only when a visible password box is there, on the bookmark's own origin (checked again at every later step of a sign-in in steps, for 90 s at most); a cross-origin frame is never reached | SelfTestPortals (two-step page, shadow root, page with no form, another origin refused) |
| Copies follow the policies | ⧉ Password / ⧉ TOTP code follow the Bitwarden copy policy for Bitwarden credentials and the item lock for vault ones (Fill sign-in now checks the item lock too); copied the password-manager way (out of clipboard history and cloud sync, cleared after the set seconds) | SelfTestPortals, SelfTestBitwarden |
| TOTP secrets stay with their credential | a vault credential's TOTP secret is stored encrypted with it; a Bitwarden login's is read with its password for one use and never listed or stored; the code, not the secret, is filled or copied | SelfTestBitwarden 4d (RFC 6238 SHA1/SHA256/SHA512 vectors), fuzzing (`totp-settings`) |
| Nothing secret in the audit log | unlocks, remembered/forgotten, fills and copies are audited by name | SelfTestBitwarden 4d (the master password is in no entry) |

**Trade-off, by design:** with a remembered master password, whoever can open the app can use the Bitwarden vault
without typing that password. That is the app's own lock (Windows Hello, PIN, master password; Settings → Security), or
the Windows account itself while no master password is required at start, since the store's key is then also wrapped
by DPAPI. The prompt and Help say so; organisations can forbid remembering with `BitwardenAllowRemember`.

**Finding, fixed in this release:** (Low, availability) the new fuzz target `ai-written-calls` found that a model's
written tool call whose JSON repeats a key (`{"command": "a", "command": "b"}`) threw an ArgumentException after parsing
(System.Text.Json reads objects lazily, so the duplicate surfaced on first use, outside the parser's catch). A model
reply could have ended the AI Engineer's run with an error. Every written-call path now reads the JSON whole inside the
catch (`AiAgent.SafeParse`), and such a call is simply not a call.

## 4e. Vault credentials kept in Bitwarden or Password Safe (0.6.17, ADR-096)

DarkCipher0x asked for Password Safe and Bitwarden credentials to work "in all portions of the software". A vault
credential can now point at a Bitwarden item, a Secrets Manager secret or a Password Safe managed account, and every
place that uses a credential reads it through one seam (`ExternalCredentials`). What was designed in, and how it is checked:

| Property | How | Checked by |
|---|---|---|
| Nothing secret stored | the credential keeps ids and a label (`Credential.External`); `Secret` stays empty; the copy a use gets is in memory only, marked not external, never saved | SelfTestBitwarden 4b (the store's JSON holds no secret), SelfTest050 (Password Safe) |
| Short-lived in memory | a value read from Bitwarden is reused for 2 minutes at most, Password Safe's for 30 s (then the lease answers); both are forgotten when the vault or the app locks; app lock checks the leases in | SelfTestBitwarden 4b (lock → a background read fails again), SelfTest050 (check-in, then refused) |
| No surprise prompts | dashboard and scheduled checks, the infrastructure screen's auto-refresh and the server dashboard's polling run in an unattended scope: a locked vault or a missing checkout fails the check instead of asking; building a provider never reads the credential (it is read when a request is sent) | SelfTestBitwarden 4b (no prompt, no read at construction), SelfTest050 (no background checkout) |
| Policies win at once | Bitwarden off, using items as credentials off or Password Safe checkouts off refuse before any held value is used | SelfTestBitwarden 4b |
| Shared libraries can't redirect your vault | publishing leaves external credentials and hosts' direct links behind; a library's credentials and hosts are loaded without them; the resolver refuses a shared external credential and shared hosts' links are never followed (without this, a member could publish a host pointing at a Bitwarden item id, and this PC would read its own item and sign in to the member's host with it) | pentest probes (shared credential refused, shared host links ignored) |
| Forged links refused | links are parsed strictly (a GUID, or two numbers of at most 18 digits, and a label without control characters) before anything runs | pentest probes (option-shaped, traversal, injection, negative and oversized ids), fuzzing (`vault-credential-links`) |
| No key files | a key kept in Bitwarden is never exported to a file by the app | pentest probe, SelfTestBitwarden 4b |
| Audited by name | every read is in the audit log with the credential's name and what it was for, never the value | SelfTestBitwarden 4b |
| The search lists nothing secret (0.6.19, ADR-099) | 🔍 Find… lists names, user names, addresses and ids only; each source's list is held 5 minutes and dropped when the vault or the app locks; picking an item stores a link, not the secret | SelfTestBitwarden 4c (no secret in any listed row), SelfTest050 |

**Finding, fixed in this release:** (Medium, design) since 0.6.14 a shared library host carried its Bitwarden or Password
Safe link like any other field. A member could have published a host whose link names a Bitwarden item id (ids are not
secret), and a member who opened that host would have read their own item and signed in to the publisher's server with
it. Fixed as above; found while designing 0.6.17 before any public release.

**Findings of the pre-release check (0.6.19), fixed:** (Low, availability) infrastructure Save refused credentials kept in
Bitwarden or Password Safe; (Low) deleting a VPN profile also deleted the sign-in credential picked for it from the vault;
(Low) ■ Stop during the AI's server deploy could lose Vultr's root password (it is now saved before the wait); (Low) a
background sudo lookup with a locked vault aborted the command instead of running without a password. None exposes a
secret.

**Finding, fixed in this release:** (Low) the new fuzz target `vault-credential-links` found that a stored Bitwarden or
Password Safe link accepted control characters in its label (`.` matches everything but a newline). The app's own links
are cleaned when they are made, but a hand-edited or restored value could have put a carriage return or other control
character into the screens and the audit log. Both link patterns now refuse control characters in the label.

### AI server deploys (0.6.17, ADR-097)

The AI can order a Vultr server (`vultr_deploy_server`). It costs money and creates a machine on the internet, so:

| Property | How | Checked by |
|---|---|---|
| Nothing is ordered without the user | the approval shows name, OS, region, plan, size and monthly price; declined orders nothing; Group Policy `AiAllowServerDeploy` turns the tool off | SelfTestVultrDeploy (declined → no request; off → refused) |
| Never two servers | the create request is sent once and never retried; readiness is read back with GETs | SelfTestVultrDeploy (one POST per approval) |
| Only what Vultr offers | OS, region, plan and SSH keys come from Vultr's own lists; anything unknown is refused before the approval; IPv6-only and GPU plans are never picked by the fallback | SelfTestVultrDeploy (pure order logic, refusals before asking) |
| The root password never reaches the model | Vultr's generated password (only in the create answer) goes straight into a vault credential; the tool's answer and the audit log name it only | SelfTestVultrDeploy (answer and audit checked for the password) |

## 4d. The pre-launch sweep of the new surfaces (0.6.13–0.6.16, 2026-10-24)

DarkCipher0x asked for "a full pentest and debug sweep" before the launch. Every surface added since the 0.6.12 audit was
probed (16 new pentest probes), fuzzed (8 new parsers with a stricter rule: only a parse error or the parser's own refusal
may escape, so a shape error counts as a bug), and the code reviewed by hand. Dependencies: no known-vulnerable package
in any of the 11 projects, transitive ones included (`dotnet list package --vulnerable --include-transitive`).

| Surface | What is in place | Checked by |
|---|---|---|
| UniFi Network (ADR-094) | https only; the API key only in `X-API-KEY`; a console's self-signed certificate pinned on first use, a changed one refused; no redirects; every id from the API a GUID before it is used in a path; answers parsed strictly (no duplicate keys, depth 64) and read through objects only; lists paged with a cap; changes only with "Allow changes", each verified; system-defined items and the default network refused | SelfTestUniFi; probes (http / javascript / file / credentials / path addresses, traversal and encoded console ids, traversal object ids, 1 MB markup-and-secret errors, bearer tokens in errors); fuzzing (`unifi-inventory`: a hostile console answering every request, `unifi-error`) |
| UniFi Site Manager | read-only by design; the key only in its header; public certificate trust (never pinned); `nextToken` paging bounded; one 429 waited out | SelfTestUniFi; fuzzing (`unifi-sitemanager`) |
| AI tool-call scanner (ADR-095) | `<tool_call>` blocks read by brace matching in linear time; a tagged call to a tool that wasn't offered never runs; approvals unchanged | probes (1 MB unterminated tag, 10,000 tags without JSON, nesting 10,000 deep, an unoffered tool), fuzzing (`ai-tool-calls`) |
| Bitwarden set-up (ADR-095) | Unlock refuses before asking for the master password when bw or the API key is missing; "Install Bitwarden CLI" runs winget with fixed arguments (`--id Bitwarden.CLI --scope user`, no shell), refused in offline mode; the installed CLI still goes through the signature / approval check | SelfTestBitwarden 1b; probes (item ids, link labels, environment stripping, card / identity / note items never listed, hostile item lists, server and approval-list checks); fuzzing (`bitwarden-items`, `bitwarden-secret`, `bws-secrets`, `bws-value`) |
| Setup wizard (0.6.15) | the same Burn engine, MSI and silent installs; the preview mode replaces the PC's name with a fictional one so renders can be published | `--preview` renders checked by eye; the shipped Setup.exe's setup program compared by hash with the build |

**Findings, all fixed in this release:**

| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | Low | UniFi: a device's `interfaces` is an object in the detail answer but a list in the list answer; the inventory read it as an object and stopped on the list form (found by the self-test). | Reads go through `UniFiProvider.At` (objects only, otherwise empty). |
| 2 | Low | UniFi: a console answering with duplicate JSON keys made .NET throw `ArgumentException` later, outside the provider's error handling (found by the new fuzzer). | Answers are parsed with `AllowDuplicateProperties = false`, so the duplicate is invalid JSON at once. |
| 3 | Low | UniFi: `totalCount` sent as text would have thrown while paging. | Read with `TryGetValue`; anything else means "unknown" and paging stops at the cap. |
| 4 | Low | Bitwarden: `bw`'s answers were read with `TryGetProperty` / `TryGetInt32` without checking the element's kind; an answer of the wrong shape would have thrown instead of being refused. | `TypeOf` and `Str` check the kind first; hostile item lists now give an empty list. |
| 5 | Info (usability) | Bitwarden: without `bw` installed, Unlock asked for the master password and then failed with no hint (DarkCipher0x's report). | Checks before the prompt, the set-up checklist and the install button. |

**Open (documented, Low):** other providers' JSON parsing (Docker, vSphere, Satellite, the cloud clients) still lets a
duplicate key surface as `ArgumentException`. Their callers catch it and show an error, so it can't crash the app, and the
existing fuzzers treat it as a refusal. It will move to the strict parse with the next provider round.

## 4c. Post-quantum (0.6.12, ADR-090)

The goal: nothing recorded or copied today (network traffic, a disk image, a file share, a backup) can be opened by a
future quantum computer, and nothing the app trusts can be forged by one. Everything is a **hybrid** with the classical
algorithm it already used, so the result is never weaker than before, and hybrid keys refuse a classical-only downgrade.

| What | Before | Now | Checked by |
|---|---|---|---|
| Everything saved (store, notes, docs, logs, snapshots, backups) | AES-256-GCM | unchanged: symmetric, quantum-safe (Grover leaves 128-bit security) | encryption audit (0 plain files) |
| Data key protector: Windows account | DPAPI (AES) | unchanged (symmetric) | — |
| Data key protector: master password | PBKDF2-SHA256 600,000 + AES | unchanged (symmetric) | — |
| Data key protector: Windows Hello | key from a TPM RSA / ECC signature (its public key is on the disk) | key from the signature **and** a 32-byte secret sealed with DPAPI for the Windows account; one Hello prompt as before; older wraps upgrade at the next Hello unlock | SelfTestProtected |
| SSH key exchange | library default order | `mlkem768x25519-sha256` (ML-KEM-768 + X25519), `sntrup761x25519-sha512` first; optional requirement (admin-protected, policy `PqSshRequired`); negotiated exchange shown per session | SelfTestPqc |
| Shared library key sharing | P-256 ECDH-ES + HKDF + AES-GCM | P-256 + ML-KEM-768, both secrets into HKDF, bound to the ML-KEM ciphertext; a classical wrap for a hybrid key is refused | SelfTestPqc, LibraryTests (end to end) |
| Shared library signatures | ECDSA P-256 | ECDSA P-256 + ML-DSA-65, both must verify; an ECDSA-only or mixed signature for a hybrid key is refused | SelfTestPqc |
| Update manifest | ECDSA P-256 (`update.json.sig`) | plus ML-DSA-65 (`update.json.pq.sig`) by a second pinned release key; offered only when both verify | SelfTest.UpdatesAndServices (test keys) |
| Add-on bridge (local pipe) | ECDH P-256 + AES-GCM | unchanged: recording it means being on the PC already; the add-on SDK would need BouncyCastle | — |
| HTTPS to vendors, Remote Desktop, Entra tokens, SSH host / user keys | vendors' cryptography | unchanged: outside the app's control (listed in Settings, not claimed) | — |

Implementation: ML-KEM-768 (FIPS 203) and ML-DSA-65 (FIPS 204) from BouncyCastle 2.7.0, the exact version SSH.NET already
pinned (no new dependency; lock-file hash unchanged). The new ML-DSA release key's seed is DPAPI-protected on the
release PC and needs escrow like the ECDSA key.

## 4a. Findings while building 0.5.1, and their fixes

| # | Finding | Severity | Fix |
|---|---|---|---|
| 1 | The dashboard's Recent activity card read the audit log while the audit log was locked (its new default), showing the latest entries without the unlock | Medium | The card shows a padlock and an "Open the audit log" button until it is open |
| 2 | Settings → Data → "View error log" opened the decrypted log in a text box, outside the error log's category lock | Low | It opens LOGS → Error log, which asks first; clearing the logs asks too |
| 3 | A padlocked cloud account made the dashboard's "Check now" throw (the new lock in `ApiFor`) instead of marking that provider | Low | The dashboard and the host import show "the account is locked" |
| 4 | BeyondTrust → Add Jump Items as hosts didn't honour the new "adding hosts" policy | Low | `Restrictions.Check("host.add")` first |
| 5 | The lock prompts (`LockGate`) sat in the services layer with direct UI calls; the layering scan caught it | Low (structure) | Moved to the screens' layer |

## 4. Findings while building 0.5.0, and their fixes

| # | Finding | Severity | Fix |
|---|---|---|---|
| 1 | Settings showed policy-set and admin-protected values as editable: a user could change a field, press Apply, and see it quietly put back (no security impact — the policy always won — but misleading for administrators) | Low | Every bound control is greyed out with who set it; 🛡 marks and card lines for protected settings (ADR-059) |
| 2 | The ADMX templates lacked the computer-only policy `RequireDirectorySignIn` (it worked only as a hand-made registry value) | Low | Computer-only policies are generated into the ADMX / ADML and POLICIES.md |
| 3 | Saving a host in the editor reset its Remote Desktop fit mode, smart-card redirection and keyboard-hook mode to the defaults | Low | The editor starts from the host's own settings |
| 4 | A static reference to the File commander screen (added for WSL → Open files) kept closed Commander tabs in memory; the leak test caught it before release | Low | A weak reference |
| 5 | The new Pingdom reader threw on an answer whose check id wasn't a number (a hostile or broken API answer could stop the dashboard's check with an exception instead of being ignored); the same pattern was in the Password Safe and token readers — found by the new pentest probe | Low | Every number is read only when the JSON value is a number; malformed token answers are refused cleanly |

## 5. Findings of the 0.4.0 security sweep, and their fixes

| # | Finding | Severity | Fix |
|---|---|---|---|
| 1 | A server-sent file name was used as given when building the local download path; `..\` or a drive letter could place a downloaded file outside the chosen folder (SFTP download, Share, whole-tree download) | High | `LocalFs.SafeChild` neutralises separators, drive letters, `..`, trailing dots / spaces and invalid characters before any local path is built; `Path.Combine` on the result only; 16 traversal probes |
| 2 | CSV exports (audit log, tables) wrote cells as given; a cell starting with `=`, `+`, `-`, `@`, tab or CR is read as a formula by spreadsheets (CSV injection from server names, log lines) | Medium | `Menus.CsvCell` quotes such cells as text with a leading apostrophe and escapes quotes, commas and line breaks; the Event Viewer CSV uses it too |
| 3 | A process id and a mount point reached a remote shell command unquoted (`/proc/{pid}/…`, `du … {mount}`); the values come from the server's own output, so a hostile server could inject into a later command | Medium | Both quoted with `RemoteExec.Quote` |

Also tightened in 0.4.0 while building the new features (not findings against 0.3.1): log sinks accept https only and
refuse credentials and fragments in URLs; sink answers are bounded and masked; the Azure Monitor token is cached per
scope so a Monitor token can never be sent to Resource Manager or the other way round; Grafana dashboard URLs are
constrained to the Grafana server; the self-test cancels keyboard-focus tooltips of its off-screen window so nothing
can appear on the user's screen during a run; the DNS self-test picks its loopback port in an order Windows' reserved
TCP ranges can't break.

## 6. Known gaps and open hand tests

- **0.6.20 Fill sign-in in steps and the remembered master password** were tested against loopback pages and a scripted
  `bw`; a real Synology DSM / Microsoft sign-in and a live vault with a remembered password are on the maintainers'
  hand-test checklist. A sign-in inside a frame from another site is never filled (by design): the copies are offered.
- **0.6.16 UniFi** hasn't been run against a live UniFi console, the Cloud Connector or Site Manager yet; it follows
  Ubiquiti's published API references (Network Integration API 10.x, Site Manager v1) and is tested against fakes. The
  Cloud Connector's exact path is discovered from two documented forms.
- **0.6.14 Bitwarden** hasn't been run against a live Bitwarden account or a real `bw` / `bws` build yet (the protocol is
  tested against scripted CLIs and the process plumbing against a real executable). Whether a given `bw.exe` release is
  Authenticode-signed by Bitwarden decides whether it runs without an approval; an unsigned build needs an administrator
  to approve its SHA-256. `bws` may keep its own login state in its configuration folder, outside the app.

- **0.5.1 real systems:** VNC against a real Mac (Screen Sharing, macOS 13–15) and real Linux servers (TigerVNC with
  X.509, x11vnc, GNOME / KDE); Apple's own Screen Sharing types (33 / 35 / 36, "High Performance") are undocumented and
  not supported. The Hyper-V module install on Windows Server and Pro; the MSI's winget options; UAC for locks and
  add-on installs on the installed copy; BeyondTrust pinning against a real appliance — all on the maintainers' hand-test checklist.
- **VNC sign-in cryptography** is what the protocols define (DES for the VNC password, MD5 and AES-ECB for Apple Remote
  Desktop); it protects the sign-in only. The stream is encrypted by the SSH tunnel (the default) or VeNCrypt TLS; a
  direct connection to a server offering neither is shown as "NOT encrypted on the network".
- **0.5.0 real systems:** real mRemoteNG / SecureCRT / Royal TS / RDCMan / RDM files, a domain PC for automatic sign-in,
  Kerberos SSH and RDP single sign-on (Remote Credential Guard, Restricted Admin), a Puppet server with PuppetDB, WSL
  Containers (wslc's commands are read from its own help — no published reference yet), VS Code Remote-SSH with
  ssh-agent, a Teams app registration, Pingdom, a BeyondTrust PRA appliance and Password Safe — all tested against fakes,
  listed on the maintainers' hand-test checklist.
- **Settings locks:** every admin-protected setting and about half of all policies have a control that greys out when a
  policy sets it; the rest are enforced but not yet greyed out on their card.
- **Real systems:** Elasticsearch / OpenSearch, Loki, Azure Monitor, Splunk, Graylog, Grafana, a real AD domain, LDAPS
  server and Entra tenant, real VPN tunnels, real Azure / Google subscriptions — every one tested against fakes or
  loopback servers only. Beta testers with these systems are welcome (README).
- **0.6.x real systems:** Microsoft 365 admin (Intune, Defender, Entra) and Windows 365 Cloud PCs have only run against
  a fake Graph (no test tenant yet); tmux has been checked by its unit tests, not yet on every distribution's package
  manager. Beta testers are invited to try them (README).
- **Code signing:** the installer and the exe are not Authenticode-signed yet, so SmartScreen warns; check the SHA-256
  against the release's. In-app updates don't depend on it: `update.json` is signed with both release keys (ECDSA P-256, ADR-079, and
  ML-DSA-65, ADR-090) and an update whose signatures don't both verify is refused.
- **Malware as the same user** is out of scope (it can read memory). The master password / Hello protect the store at
  rest; the admin-protected record and Group Policy protect settings against a non-administrator.
- **A reachable sink receives the masked events in clear inside TLS**, as syslog does; from there the SIEM's own access
  control applies.
- **Command risk assessment for the AI** is rule-based and errs towards "change"; the person approving reads the full
  text.
- Everything else in `SECURITY.md` → *Known gaps*.

## 7. How to reproduce

```powershell
dotnet build DarkCipher.ServerManagement.sln -c Debug -nologo -v q
$env:DCX_DATA_DIR = 'C:\Temp\dcx-scratch'      # an EMPTY folder, never the real store
$env:DCX_AI_FEATURES = '1'
& (Get-ChildItem src\DcxManager\bin\Debug -Recurse -Filter DcxServerManager.exe | Select-Object -First 1).FullName --selftest C:\Temp\dcx-report.txt
Select-String -Path C:\Temp\dcx-report.txt -Pattern '^FAIL|pentest:|encrypt'
```

The first line of the report is the verdict; the `pentest:` line names the probe count; the encryption audit line
names the number of sealed files and the plain-by-design ones. `build.ps1` runs the same before it packages anything.
