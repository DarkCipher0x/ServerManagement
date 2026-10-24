# For the organisation: deploying and controlling ServerManagement

This chapter is for the people who roll the app out to a team or a company and have to answer for it: where it stores
data, how users sign in, what an administrator can force, what leaves the PC and where it goes, and how to remove it
cleanly. Every control named here exists in the product and is covered by the self-test; the hand tests still open
against real systems are listed in the known-limitations chapter.

## 1. What an organisation controls, in one table

| Concern | Control | Where |
|---|---|---|
| Who can open a profile | Active Directory (the Windows session), LDAPS, Microsoft Entra ID; a required group; group → role (admin / operator / viewer) | Settings → Security & privacy → Profiles → *Sign in with your organisation*; policy `RequireDirectorySignIn` |
| Every setting | Group Policy (ADMX / ADML templates, 145 policies) | `HKLM`/`HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` |
| Security settings on an unmanaged PC | Admin protection (UAC): 20 settings in an HKLM record only administrators can write | Settings → Security & privacy → Admin protection |
| The audit trail | Syslog over TLS (RFC 5424 / 5425), log sinks (Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk, Graylog, webhooks), e-mail notifications | Settings → Integrations; all admin-protected and policies |
| Terminal session logs | Forced on, off or per profile; forwarded to syslog / log sinks | policy `SessionLogs`, `SyslogSendSessionLogs`, `ObserveSendSessionLogs` |
| AI | Off entirely (and locked), local-only, or with internet providers; what is masked | policies `AiFeatures`, `AiAllowCloud`, `AiRedactSecrets`; install switch `AIFEATURES=0` |
| Network | Offline / air-gapped mode with an approved-destination list; Telnet; the local API | policies `OfflineMode`, `OfflineAllowed`, `TelnetEnabled`, `LocalApiEnabled` |
| Team data | Shared libraries on your own file share, end-to-end encrypted per member and group | Settings → Security & privacy → Team features (`TeamFeatures`) |
| Branding | Your colours, logo, product name and tagline, by policy or a theme add-on | policies `BrandingTheme`, `BrandingLogoPath`, `ThemeAddon` |
| Dashboards | Grafana and SIEM links users may open | policies `GrafanaUrl`, `GrafanaOrgId`, `ObserveLinks` |
| Data at rest | AES-256-GCM for everything; DPAPI, master password or Windows Hello for the key; snapshots | Settings → Security & privacy, Data & backups |
| Removal | Silent uninstall switches that remove exactly what you choose, for one user or all | `msiexec /x … REMOVE_…=1` |

## 2. Installing at scale

- **Package:** the per-machine MSI (`DarkCipherServerManagement-{VERSION}-x64.msi`, WiX 5, UpgradeCode fixed since 0.1.0).
  It installs into `C:\Program Files\DarkCipher Systems\ServerManagement`, needs no .NET runtime (self-contained) and
  needs the WebView2 Runtime (Windows 11 has it; the installer can add it with `INSTALLWEBVIEW2=1`).
- **Silent install:** `msiexec /i DarkCipherServerManagement-{VERSION}-x64.msi /qn INSTALLDESKTOPSHORTCUT=0 AIFEATURES=0`.
  Deploy it with Intune (Win32 app, the same command line), Configuration Manager, Group Policy software installation
  or any tool that runs MSIs. Upgrades install over the top; downgrades are refused.
- **Checksums and SBOM:** every file in the release folder has a `.sha256`; `…-sbom.cdx.json` is the CycloneDX bill of
  materials (every package with its exact version and hash). Verify the MSI's hash before you distribute it.
- **Not code-signed yet:** SmartScreen shows "unknown publisher" when the MSI is run by hand; silent installs through a
  management tool don't see SmartScreen. The in-app updater is switched off until a signing certificate exists, so
  updates are your deployment tool's job.
- **Portable zip:** the same program without an installer; it uses the same data folders. Prefer the MSI in a company.

## 3. Identity: Active Directory, LDAPS and Microsoft Entra ID

A **profile** is one encrypted store. A profile that *signs in with your organisation* opens only after the directory
says yes; nothing typed is ever stored. All settings are in Settings → Security & privacy → Profiles → *Sign in with
your organisation* (the dialog signs in once before saving, so a binding that doesn't work is never kept).

| Kind | What it uses | What you configure |
|---|---|---|
| **Windows / Active Directory** | the signed-in Windows account (Kerberos / Negotiate — nothing typed); groups from the Windows token | the profile is bound to the account's SID; a required group; group → role |
| **LDAP over TLS** | LDAPS (636, TLS from the first byte) or StartTLS (389 → TLS); a SASL / Negotiate bind with the Windows session, or a simple bind inside TLS with a password asked each time and never stored; groups from `memberOf` | server, port, transport, bind, base DN (optional), the server certificate's SHA-256 pin for self-signed servers (optional), the account (UPN or `DOMAIN\name`) |
| **Microsoft Entra ID** | OAuth 2.0 authorization code with PKCE through the system browser — MFA and Conditional Access apply; the `id_token` is verified against the tenant's keys (RS256 only; issuer, audience, expiry, nonce, tenant) | tenant id or verified domain; the app registration's client id (a *public client* with the loopback redirect `http://127.0.0.1`); the account (UPN); the authority (optional) |

Common to all three:
- **Group → role:** one line per group, `Group name = admin | operator | viewer`; the first match wins, no match =
  operator. A **viewer** can't change anything. A **required group** gates the profile.
- **Offline grace:** a sealed proof (MAC'd with a key derived from the store key) lets the profile open without the
  directory for up to N days (0 = never offline). Tampering invalidates it.
- **Policy:** `RequireDirectorySignIn` (computer policy) forbids local profiles on the PC.
- **Entra app registration (once, in the tenant):** a *Mobile and desktop* / public-client registration, redirect URI
  `http://127.0.0.1`, no client secret, API permission `openid profile email` (delegated); optionally the `groups`
  claim in the token. The same tenant's **app registration with a certificate** is what the Azure cloud screens and the
  Azure Monitor log sink use (Settings → Cloud providers → Azure subscriptions) — a separate, confidential registration
  with roles on the subscription (Reader; Contributor where changes are wanted; *Monitoring Metrics Publisher* on a data
  collection rule for log shipping).
- **Hand tests still open:** a real domain, LDAPS server and Entra tenant (the self-test uses fakes and a substituted
  Windows identity).

## 4. Group Policy

Almost every setting is a policy (145 in {VERSION}; the complete reference with types, ranges and defaults is the next
chapter). The rules:
- **Keys:** `HKLM\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` (computer, wins) and
  `HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` (user). The value name is the setting's name.
- **Types:** `REG_DWORD` for on/off (0/1) and whole numbers, `REG_SZ` for text, decimals and JSON, `REG_EXPAND_SZ` for
  paths, `REG_MULTI_SZ` for lists. Out-of-range or wrong-type values are ignored and listed in Settings → Security &
  privacy → *Managed by your organisation*.
- **Templates:** `policy\DarkCipherServerManagement.admx` + `en-US\…adml` in every release folder. Copy them into the
  domain's Central Store (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`) or `C:\Windows\PolicyDefinitions`;
  in Intune, import the ADMX (Settings catalog → *Import ADMX*) and configure the same settings.
- **Order:** computer policy → user policy → the admin-protected record → the user's own settings. A managed setting
  can't be changed in the app (it shows why).
- **Never policies:** accounts, keys, tokens, passwords and per-PC state (listed at the top of the reference).

A **baseline for a company PC** (a `.reg` fragment; the same values as Group Policy):

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\DarkCipher Systems\ServerManagement]
"AiFeatures"=dword:00000000            ; no AI at all (or 1 with "AiAllowCloud"=0 for local-only AI)
"OfflineMode"=dword:00000001           ; nothing outside your networks …
"OfflineAllowed"=hex(7):…              ; … except these destinations (REG_MULTI_SZ: hosts, CIDRs)
"TelnetEnabled"=dword:00000000
"LocalApiEnabled"=dword:00000000
"TeamFeatures"=dword:00000001
"SessionLogs"="on"                      ; every terminal session logged, encrypted
"SyslogEnabled"=dword:00000001
"SyslogHost"="siem.example.com"
"SyslogPort"=dword:00001972             ; 6514
"SyslogSendSessionLogs"=dword:00000001
"ObserveEnabled"=dword:00000001
"ObserveSinks"="[{\"Id\":\"elastic01\",\"Name\":\"SOC Elastic\",\"Kind\":\"elastic\",\"Url\":\"https://es.example.com:9200\",\"Index\":\"servermanagement\",\"Auth\":\"apikey\"}]"
"GrafanaUrl"="https://grafana.example.com"
"ObserveLinks"=hex(7):…                 ; "Kibana=https://kibana.example.com", "Sentinel=https://portal.azure.com/#view/Microsoft_Azure_Security_Insights"
"BrandingTheme"="{ \"productName\": \"Contoso ServerManagement\", \"colors\": { \"Accent\": { \"dark\": \"#6FA8FF\", \"light\": \"#1F6FEB\" } } }"
"BrandingLogoPath"="\\\\files\\it\\servermanagement-logo.png"
"RequireDirectorySignIn"=dword:00000001
```

The sink **secrets** (API keys, tokens) are never policies: each user enters theirs once in Settings → Integrations →
Log sinks (or you deploy an *Azure Monitor* sink, which signs in with the Azure subscription's certificate and needs no
per-user secret).

## 5. Admin protection without a domain

On a PC without Group Policy, the security settings (AI, internet AI, offline mode, the local API, Telnet, the app lock,
Hello, team features, syslog, log sinks, notifications — 20 in all) can be **admin-protected**: their values live in
`HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Protected`, one record per Windows user, writable only by
administrators. Changing one asks for administrator approval (UAC; optionally Windows Hello first), through the
installed program only. A record others could write is ignored and reported. A policy outranks the record.

## 6. Logging, syslog and SIEM

What the app records and where it can go:

| Stream | Content | Local | Over the network |
|---|---|---|---|
| **Audit log** | every change by a person, the AI (`ai`), add-ons (`addon:<id>`) or the app: commands, power actions, cloud changes, sign-ins and refusals, exports, protected-setting changes | encrypted per line, Sidebar → Audit log, CSV export (formula-safe) | syslog, log sinks |
| **Application (error) log** | exceptions, masked | sealed per entry | syslog, log sinks |
| **Terminal session logs** | every line of a logged session, masked | encrypted per chunk | syslog (msgid `session`), log sinks (kind `session`) — only when switched on |
| **Notifications** | incidents, hosts down, failed backups, security events, failing syslog / sinks | — | e-mail (TLS / STARTTLS only) |

**Syslog over TLS** (Settings → Integrations → Syslog (TLS)): RFC 5424 messages framed per RFC 5425, TLS 1.2 or 1.3,
port 6514; the collector's certificate checked by Windows or against a SHA-256 pin; optional client certificate (mutual
TLS). Structured data `[dcx@32473 actor= target= result=]`, facility local0. No UDP or plain TCP exists. An encrypted
queue of up to 5,000 messages bridges outages; the number lost is reported.

**Log sinks** (0.4.0, Settings → Integrations → Log sinks; the Observability screen shows their state): the same events
as one JSON document each — `@timestamp` (UTC), `app`, `host`, `user`, `kind` (`audit` / `error` / `session`),
`severity`, `actor`, `target`, `result`, `session`, `message` — to:

| Sink | Protocol | Fields |
|---|---|---|
| Elasticsearch / OpenSearch | bulk API `POST /_bulk` (NDJSON, `create` into the index) | URL, index, API key or user + password |
| Grafana Loki | `POST /loki/api/v1/push`, one stream per event kind, labels `app`, `host`, `kind`, optional `job` | URL, optional tenant (`X-Scope-OrgID`), Grafana Cloud: instance id + token as user + password |
| Azure Monitor / Microsoft Sentinel | Logs Ingestion API `POST /dataCollectionRules/<dcr>/streams/<stream>?api-version=2023-01-01`, PascalCase columns with `TimeGenerated` | the data collection endpoint URL, the rule's immutable id, the stream (`Custom-…_CL`), the Azure subscription whose app registration signs in (*Monitoring Metrics Publisher* on the rule) |
| Splunk | HTTP Event Collector `POST /services/collector/event`, `sourcetype dcx:servermanagement` | URL, HEC token, optional index |
| Graylog | GELF over HTTP `POST /gelf`, one message per event | URL |
| Webhook | `POST` of a JSON array to any HTTPS endpoint | URL, bearer / API key / basic |

Rules: **HTTPS only** (TLS 1.2+, pin a self-signed certificate's SHA-256), no credentials in URLs, up to 12 sinks, an
encrypted queue of 5,000 events per outage with per-sink back-off (5 s → 5 min), offline mode enforced. The sink list is
admin-protected and the policy `ObserveSinks` (JSON without secrets, up to 2,048 characters); the secrets stay in each
user's encrypted settings. **Where to look for an event in your SIEM:** filter on `app:DarkCipherServerManagement`
(Elastic / Splunk / Loki label `app=dcx-servermanagement`), `host` for the PC, `user` for the Windows account, `kind`.

## 7. Observability for your users

Observability (sidebar) shows the sinks' state and activity, lists your **Grafana** dashboards (a service-account token
with the Viewer role, stored encrypted) and opens them — and any **links** you set (Kibana, OpenSearch Dashboards,
Microsoft Sentinel, Splunk…) — in the app's own browser window. `GrafanaUrl`, `GrafanaOrgId` and `ObserveLinks`
(`Label=https://…`) are policies, so every user gets the same entry points; the app only ever opens pages on the Grafana
server itself (a dashboard URL that points anywhere else is refused).

## 8. Team features

- **Shared libraries** need nothing but a folder every member can read and write (SMB share, NAS, a synced folder).
  Everything in it is ciphertext, signatures and public keys: the share's administrators can delete or roll back files
  (detected and flagged) but never read them.
- **Groups** give per-collection access on top of each member's own, enforced by the encryption (keys wrapped only for
  members with access; leaving a group re-keys).
- **Joining:** a member joins from the folder with the owner's **fingerprint** read out of band and a 6-digit code check.
  **Leavers:** remove them in Shared library → Members; the app re-keys and lists the credentials they could read —
  rotate those on the targets.
- **Owner key:** back it up (Shared library → Back up my keys…) somewhere other than the owner's PC. Hand over ownership
  before an owner leaves.
- **Switch:** `TeamFeatures` (admin-protected, policy) removes the TEAM section entirely.

## 9. Branding by policy

Settings → Appearance → **Branding & colours** recolours and renames the app for your organisation without an add-on:
product name, tagline, logo (PNG), the logo's plate, the UI font and every colour key in dark and light. Two ways to roll
it out:
- **Policy:** `BrandingTheme` = the theme JSON (the same `theme.json` a theme add-on carries; the card's *Export* gives
  you the text) and `BrandingLogoPath` = a PNG on a share (≤ 1 MB, ≤ 2048 px). Users see your look and can't change it.
- **Theme add-on:** the card's **Export as theme add-on** writes a `.dcxaddon` (theme-only: no program, no permissions);
  install it on each PC, or pin it with the `ThemeAddon` policy. `samples/addons/sample-theme` is a worked example.

Environment label colours (PRODUCTION, TEST, LAB, DEV, LOCAL) are never themable: they are a safety signal.

## 10. Data, encryption and backups

| What | Where (0.4.0) |
|---|---|
| Program | `C:\Program Files\DarkCipher Systems\ServerManagement` |
| Encrypted store (per Windows user, per profile) | `%APPDATA%\DarkCipher Systems\ServerManagement` (movable; profiles in `profiles\<id>\`) |
| Cache, WebView2 profile, error logs | `%LOCALAPPDATA%\DarkCipher Systems\ServerManagement` |
| Admin-protected records | `HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Protected` |
| Install information | `HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Install` |
| Policies | `HKLM` / `HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement` |

0.4.0 moved the data folders from `…\DarkCipher\ServerManagement` to `…\DarkCipher Systems\ServerManagement` on first
start (an audited one-time move; the old folders are removed by the uninstaller).

- **Everything the app writes is AES-256-GCM** under one data key per store; each file is bound to its own name. Settings
  → Security → *Verify everything is encrypted* walks the store and shows the result; the self-test fails any release
  with a plain file.
- **The key:** Windows DPAPI (the Windows account) by default; a **master password** (PBKDF2-SHA256, 600,000 rounds;
  *require on start* removes the DPAPI copy); **Windows Hello** (TPM-backed; the password stays as the fallback).
  There is no recovery key and no vendor back door: keep encrypted backups.
- **Backups:** rolling encrypted **snapshots** (verified before older ones are pruned) and `.dcxbackup` **exports** with
  their own password — including a pick-what-you-export tree (Import & export) that strips unticked credentials.
- **What the organisation's backup tool should include:** the user's `%APPDATA%\DarkCipher Systems\ServerManagement`
  folder (everything in it is ciphertext) and the shared library's folder on the share.

## 11. Network control

- **Offline / air-gapped mode** (`OfflineMode`, `OfflineAllowed`): every connection outside your networks is refused
  except approved destinations (hosts, CIDRs); DNS rebinding is closed by connecting only to the address that was
  checked; every block is audited. It covers HTTP, SSH, RDP, web windows, links and add-ons — not Windows itself or
  external programs after launch.
- **Destinations the app may contact** are listed in Sidebar → Privacy & network by class (local / on-premises /
  user-configured / external). Nothing is contacted silently: no telemetry, no update polling (updates are off), no
  crash upload. Dashboard checks of remote sources follow a rule shown on screen.
- **Listening:** nothing by default. The local API (off) listens on 127.0.0.1 only. Tunnels bound to 0.0.0.0 need a
  danger confirmation.
- **Proxies:** the app uses the Windows system proxy settings for HTTPS (WinHTTP / `SocketsHttpHandler` default) except
  the log sinks, which connect directly (`UseProxy = false`) so a proxy can't see the audit trail — put the sink hosts on
  the approved list when offline mode is on.

## 12. Cloud and hypervisor identities for a company

Give the app **least privilege**: a Reader / Viewer role first (every provider is read-only in the app until "Allow
changes"), then narrow roles where changes are wanted (Azure *Virtual Machine Contributor*, Google *Compute Instance
Admin (v1)*, a Hetzner Read token, a Proxmox `PVEAuditor` token, a vCenter read-only user, Hyper-V Administrators over
Kerberos). The app never reads keys, secrets, connection strings or object contents, and never manages roles. Credentials
are stored only in the user's encrypted settings; rotate them where the provider offers it ("Replace certificate / key…").

## 13. Uninstall and offboarding

- **Uninstall silently:** `msiexec /x DarkCipherServerManagement-{VERSION}-x64.msi /qn` keeps the users' encrypted data.
  Add `REMOVE_DATA=1` (the uninstalling user's store), `REMOVE_LIBRARY=1` (team membership and keys),
  `REMOVE_CACHE=1`, `REMOVE_SETTINGS=1` (protected records and policy values), `REMOVE_ALLUSERS=1` (every profile on the
  PC) or `REMOVE_EVERYTHING=1`. The uninstaller runs the program's own clean-up; never run `--cleanup` by hand.
- **A leaver:** remove them from every shared library (re-keys, lists credentials to rotate), rotate the credentials they
  could read on the targets, disable their directory account (profiles bound to it stop opening after the offline grace),
  then uninstall with `REMOVE_DATA=1` on their PC.
- **Domain Group Policy** is re-applied by the domain after an uninstall; remove the PC from the GPO's scope.

## 14. Compliance notes

- **No telemetry, no accounts with the vendor, no update service contacted.** The product has no server side.
- **Open source (MIT):** the published release includes its source archive, scanned for private data before publication,
  a CycloneDX SBOM, SHA-256 checksums, this guide, the security report with the penetration tests run against the exact
  version, and the project-structure document.
- **Self-test:** every release is built only after the full automated suite passes with no failures (hundreds of checks
  including crypto, fuzzing, loopback fakes of every provider, a real crash and recovery). Administrators can run it on
  any PC (`--selftest`, see Administration → Self-test).
- **Audit everything:** forward the audit log to your SIEM over syslog TLS or a log sink and keep it there for your
  retention period; the local copy is per month, encrypted, and never rolled back by a restore.

## 15. Rollout checklist

1. Decide the identity model (AD session / LDAPS / Entra), create the Entra public-client registration if needed.
2. Decide the AI posture (`AiFeatures`, `AiAllowCloud`) and offline mode with its approved destinations.
3. Stand up the collector(s): syslog TLS and / or a log sink; note certificates to pin.
4. Import the ADMX templates; create the baseline GPO (§4); scope it.
5. Prepare branding (`BrandingTheme`, a logo on a share) and the dashboard links.
6. Deploy the MSI silently; verify a PC: *Managed by your organisation* lists the policies, the Privacy & network view
   shows the expected destinations, a test audit line reaches the SIEM.
7. Create the team's shared library on the share; back up the owner key; onboard members by fingerprint.
8. Record the offboarding procedure (§13) in your runbook.

## 16. What 0.5.0 adds for an organisation

Every setting below is a Group Policy (the 0.5.0 templates in the release's `policy\` folder; `docs\POLICIES.md` lists
names, types and ranges). Settings marked 🛡 also need administrator approval (UAC) when a user changes them on a PC
without policy. Secrets (API keys, tokens, webhook addresses, the backup password) are never policies: users enter them
and they stay in each user's encrypted store.

### 16.1 Identity and single sign-on
- **Automatic profile for the Windows account** — computer policy `AutoProfileForWindowsAccount` (REG_DWORD, HKLM only):
  1 or not set = a profile bound to the signed-in domain account opens without the picker and signs in with the Windows
  session; Windows Hello unlocks the store when `HelloUnlock` is on. 0 = the picker always shows. Combine with
  `RequireDirectorySignIn` = 1 to forbid local profiles.
- **SSH with Kerberos** — `SsoKerberosSsh` 🛡 (allow it per host), `SsoKerberosDelegate` 🛡 (forward the ticket: leave off
  unless servers must reach further services as the user). Servers need sssd / realmd with a host keytab and
  `GSSAPIAuthentication yes`; the client is Windows' OpenSSH (Optional feature "OpenSSH Client").
- **Remote Desktop with the Windows account** — `SsoRdpAllowed` 🛡. Single sign-on (CredSSP) needs the Windows policy
  *Computer Configuration → Administrative Templates → System → Credentials Delegation → Allow delegating default
  credentials* with `TERMSRV/*.yourdomain` (otherwise Windows asks once). **Remote Credential Guard** needs Windows 10
  1607 / Server 2016 or later on both ends and `DisableRestrictedAdmin` = 0 on the server; **Restricted Admin** needs the
  account to be an administrator on the server. Prefer Remote Credential Guard for admin work: nothing reusable is left on
  the server.
- Settings → Identity & directory shows each user their account, the PC's join state and Kerberos tickets, and lets them
  test the profile's directory sign-in.

### 16.2 Backups and imports
- `BackupAllowExport` 🛡, `BackupAllowImport` 🛡, `BackupAllowSecrets` 🛡 (0 = every export and backup leaves out
  credentials, keys and the app's own tokens), `BackupScheduledSecrets` 🛡, `BackupMinPasswordLength` 🛡 (12–128),
  `BackupScheduleDays` 🛡 (0 = off, up to 30), `BackupFolder` 🛡 (an absolute or UNC path — a per-user share such as
  `\\backup01\ServerManagement$\%USERNAME%` works with REG_EXPAND_SZ), `BackupKeep`. Users set the backup password
  themselves; an organisation that needs recovery should record it in its own password manager.
- Imports from mRemoteNG, SecureCRT, Royal TS and the rest obey `BackupAllowImport`.

### 16.3 Automation, WSL and IDEs
- `PuppetAllowChanges` 🛡: Puppet stays read-only (listing, dry runs, lookups, PuppetDB) until allowed. Sudo for Puppet
  commands uses the hosts' vault credentials; give the operator's account the Puppet commands in sudoers.
- WSL management needs no policy of its own (it acts on the user's own distributions); `.wslconfig` edits stay per user.
- `IdeSshConfig` 🛡 decides whether the app may keep `~/.ssh/dcx_config` and its Include line; `IdePreferred` and
  `IdeAgentMinutes` (5–1440) pick the editor and how long keys stay in ssh-agent. The Windows "OpenSSH Authentication
  Agent" service must be running for keys to reach the IDE; set it to Manual or Automatic by policy if you allow this.

### 16.4 Microsoft Teams
- `TeamsEnabled` 🛡 (off by default), `TeamsAllowImages` 🛡, `TeamsTenant` 🛡 and `TeamsClientId` 🛡, `TeamsDestinations`
  (JSON; names and Teams ids only).
- Register an app in Entra ID: *public client*, redirect URI `http://127.0.0.1` (Mobile and desktop applications),
  delegated Microsoft Graph permissions **ChannelMessage.Send, ChatMessage.Send, Team.ReadBasic.All,
  Channel.ReadBasic.All, Chat.ReadBasic**, `offline_access`, `openid`, `profile`; grant admin consent. Users sign in as
  themselves; they can post only where they are members. Conditional Access applies.
- Workflows webhooks carry text only and must be on Microsoft's hosts (`*.logic.azure.com`, `*.powerplatform.com`).

### 16.5 Pingdom
- A read-only API token per user (Pingdom → Settings → Pingdom API → Read access). Only `api.pingdom.com` is contacted, on
  the dashboard's check schedule (`DashboardChecks`, `DashboardCheckMinutes`).

### 16.6 BeyondTrust
- `BeyondTrustEnabled` 🛡 (off by default), `BeyondTrustPraSite` 🛡, `BeyondTrustPraClientId`, `BeyondTrustPsHost` 🛡,
  `BeyondTrustPsRunAs`, `BeyondTrustAllowCheckout` 🛡, `BeyondTrustCheckoutMinutes` (1–480). Push the sites and client ids
  by policy; users enter the API secret / key, which stay in their encrypted store.
- **PRA / Remote Support:** create an API account (/login → Management → API Configuration) with *Configuration API* and
  *Reporting API* read permissions only; the app never changes the appliance. Jump Items open in the user's own access
  console through the client-scripting URL (`/api/client_script`), so the console's own policies, recording and approvals
  still apply.
- **Password Safe:** an API registration (BeyondInsight → Configuration → API Registrations) bound to a run-as user who
  may request the managed accounts the operator needs; restrict the source addresses to your admin workstations. A
  checkout is requested for the configured minutes with a reason ("Session to <host> from ServerManagement"),
  used only as an in-memory credential for that session and checked in when the tab closes, at expiry or at exit — so
  Password Safe's rotation-on-check-in and session reports keep working.

### 16.7 Microsoft Intune and other MDMs
- The ADMX ingests in Intune (Devices → Configuration → Import ADMX) like any other; computer-only policies sit under
  "Identity and directory (computer)".

## 17. What 0.5.1 adds for an organisation

### 17.1 Locks and restrictions (a baseline)
- **Lock categories for everyone:** `LockedCategories` = `credentials`, `knownhosts`, `team`, `settings` (one per line)
  and `LockPolicyMode = uac` — each opens only with an administrator's approval, closes again after `LockOpenMinutes`
  (15 by default; 0 = ask every time) and whenever Windows locks. `LockAuditLog` is on by default: operators see the audit
  log only after approval. Users can't remove a lock the policy sets; `AllowUserLocks = 0` stops them adding their own.
- **Turn off what operators may change:** e.g. `AllowHostAdd = 0`, `AllowHostDelete = 0`, `AllowFolderEdit = 0` for a
  fleet whose hosts come from a shared library, `AllowCredentialAdd = 0` where credentials come from Password Safe,
  `AllowIntegrationEdit = 0` once integrations are configured, `AllowNetworkScan = 0` where a sweep would trip the IDS.
  The screens say exactly which policy turned an action off; the add-on bridge refuses with the same reason.
- **Add-ons:** keep `AddonsNeedApproval = 1` (the default), set `AddonAllowedIds` to the add-ons you distribute, and
  `AddonRequireSigned = 1` once you sign them. A blocked add-on isn't run but stays installed, so lifting the policy
  restores it.
- **Dashboard:** `DashboardLocked = 1` with `DashboardCards` and `DashboardOrder` gives every operator the same overview
  (a host-folder card for the estate, the Sentinel sink, open Password Safe checkouts).

### 17.2 BeyondTrust certificate pinning
- Self-signed PRA and Password Safe certificates are refused until pinned. Pin centrally with `BeyondTrustPraPin` /
  `BeyondTrustPsPin` (the SHA-256 of the appliance's certificate, 64 hex digits), or let an administrator trust the
  shown fingerprint once per PC (Settings → Integrations → BeyondTrust → TLS, with UAC). `BeyondTrustAskReason = 1` makes
  every checkout carry the operator's reason into Password Safe.

### 17.3 Deployment
- The MSI's companions are ticked when they aren't installed yet (0.5.9) and can be switched off per deployment: `INSTALLWINGET=0`, `INSTALLNMAP=0`,
  `INSTALLFOUNDRY=0` (`INSTALLOLLAMA=1` adds Ollama). AI stays off unless you allow it; when it is on, Foundry Local with
  phi-4-mini is the engine everywhere, and `AiAllowEngineChange = 0` / `AiAllowModelDownload = 0` keep it that way.

### 17.4 VNC for Mac and Linux desktops
- VNC sessions go through SSH by default, so a Mac needs Remote Login (SSH) as well as Screen Sharing, and Linux desktops
  their SSH server; nothing listens on the network on the operator's PC. Direct VNC is possible but marked "not
  encrypted" unless the server offers VeNCrypt with a certificate (pinned on first trust).

## 18. What 0.6.14 adds for an organisation: Bitwarden

### 18.1 What it is
- Hosts sign in with a **Bitwarden Password Manager** vault item (a login or an SSH key) or a **Secrets Manager** secret.
  The password or key is read into memory each time a session connects (SSH, Remote Desktop, VNC) and never saved by the
  app; the host stores only the item's id and name. Bitwarden cloud (bitwarden.com or bitwarden.eu), self-hosted
  Bitwarden and Vaultwarden (vault only: Vaultwarden has no Secrets Manager) are supported.
- It runs through Bitwarden's own CLIs, `bw` (Password Manager) and `bws` (Secrets Manager), which do all of the
  end-to-end decryption, so the app never holds Bitwarden key material beyond the CLI's session key (in memory, until the
  vault locks). Deploy them with the app: `winget install Bitwarden.CLI` for `bw.exe`; `bws.exe` from Bitwarden's sdk-sm
  releases. The npm build of `bw` (a `.cmd` running Node) isn't used.

### 18.2 Policies
- `BitwardenEnabled` 🛡 (off by default), `BitwardenServer` 🛡 (empty = bitwarden.com, `eu`, or `https://your-server`),
  `BitwardenAllowSessions` 🛡, `BitwardenAllowCopy` 🛡, `BitwardenAllowRemember` 🛡, `BitwardenCliPins` 🛡, `BitwardenLockMinutes` (1–480),
  `BitwardenClipboardSeconds` (10–300), `BitwardenSyncOnUnlock`, `BitwardenCliPath`, `BitwardenSmCliPath`.
- Push the server and the CLI paths by policy; each user enters their own personal API key (web vault → Settings →
  Security → Keys) and, for Secrets Manager, a machine account's access token. Both stay in that user's encrypted store,
  never in a policy. The master password is asked when the vault is locked. It is stored only when the user ticks
  "Remember on this PC" (encrypted in their store, bound to the server, deleted when Bitwarden refuses it, on sign-out or
  a server change); it then unlocks without asking, also at start-up. Set `BitwardenAllowRemember` off to forbid it
  (a remembered one is then unused at once and deleted at the next unlock).

### 18.3 Trusting the CLI
- Only a CLI **signed by Bitwarden** (Authenticode, with the whole chain and revocation checked) is started. A build
  Bitwarden didn't sign (for example one you built yourself) runs only once its SHA-256 is in `BitwardenCliPins`: set it
  by policy, or let an administrator approve the fingerprint shown on the CLI card (UAC). The file is held open with
  writers locked out from the check until the process starts, and a changed file is checked again.
- Secrets reach the CLI only as environment variables of that one process (never on its command line, which process
  auditing and EDR record). Inherited `BW_*`, `BWS_*` and `NODE_*` variables are removed (`NODE_EXTRA_CA_CERTS` is kept
  for a self-hosted server with an internal CA), and `bw` keeps its encrypted cache in a folder of the app's own.

### 18.4 Recommended set-up
- Give Secrets Manager machine accounts **read access only**, to the projects operators need; rotate their tokens like any
  service credential. Turn `BitwardenAllowCopy` off where operators should only ever sign in, never see a secret.
- Allow remembering the master password only where the app's own lock (Windows Hello, PIN or master password) is on:
  whoever can open the app can then use the vault. Turn `BitwardenAllowRemember` off on shared or kiosk PCs.
- The vault locks after `BitwardenLockMinutes` without use, when the app locks and when it closes. Everything (unlock,
  sign-in, each session use and copy, approvals) is in the audit log; secrets never are. Offline mode refuses signing in,
  syncing and Secrets Manager, and still allows an unlocked vault's cached items.

### 18.5 Getting the CLI onto PCs (0.6.16)
- VAULT → Bitwarden starts with a set-up checklist. Its "Install Bitwarden CLI" button runs
  `winget install --id Bitwarden.CLI -e --source winget --scope user` for that Windows user (no administrator approval);
  offline mode refuses it. To deploy centrally instead, push `Bitwarden.CLI` with your software deployment and set
  `BitwardenCliPath` by policy.

## 19. What 0.6.16 adds for an organisation: UniFi

- **Two Infrastructure providers** on Ubiquiti's official API-key APIs: **UniFi Network** (the Network Integration API,
  Network 9.0+) on a console's address or through Ubiquiti's **Cloud Connector**, and **UniFi Site Manager**
  (api.ui.com), a read-only view of every console, site and device an account owns or administers.
- **Least privilege:** the API key carries its creator's role. Create keys from a dedicated UniFi account: a
  read-only administrator to look; a site administrator only where operators should restart devices, power-cycle PoE,
  manage guests and vouchers or switch WiFi, networks and firewall policies. Changes also need "Allow changes" on the
  provider in the app, and every one is audited and verified.
- **Network control:** local consoles are reached over https on their own address (self-signed certificates pinned on
  first use; a changed certificate is refused); the Cloud Connector and Site Manager go to `api.ui.com` only. Offline
  mode governs both, and the Infrastructure restrictions (`AllowInfraAdd`, `AllowInfraEdit`, …) apply as for every provider.
- **Not live-tested yet** in this beta (no UniFi estate on the maintainers' side): testers welcome.

## 20. What 0.6.17 adds for an organisation: Bitwarden and Password Safe credentials everywhere

- **One credential, every use.** A vault credential can point at a Bitwarden vault item, a Secrets Manager secret or a
  BeyondTrust Password Safe managed account (VAULT → Bitwarden / BeyondTrust → "＋ Add as a vault credential"). It is
  then used like any credential: host sign-in (cloud hosts included), sudo, infrastructure providers, portal sign-in,
  VPN keys, the quick-connect default key and add-on credential requests the user approves. The app stores only the
  item's id and a label; the password or key is read into memory for each use.
- **Password Safe:** the first use checks the account out for `BeyondTrustCheckoutMinutes`; every later use (any host,
  provider or portal) shares that checkout until it runs out, then it is checked in. App lock and exit check it in too,
  and it shows (and can be checked in) on the Password Safe screen. The reason prompt (`BeyondTrustAskReason`) applies.
- **Policies:** the existing ones govern it. `BitwardenEnabled` and `BitwardenAllowSessions` decide whether Bitwarden
  items may be used as credentials; `BeyondTrustEnabled` and `BeyondTrustAllowCheckout` whether Password Safe accounts
  may. Turning one off applies at once, also to a value read a moment before.
- **Unattended checks never prompt.** Dashboard and scheduled checks, the infrastructure screen's auto-refresh and the
  server dashboard's polling never ask for a master password or start a checkout: they report that the vault is locked
  (or no checkout is open) until an operator uses the credential. Plan service checks on Secrets Manager machine
  accounts (no unlock needed) where they must run unattended.
- **Team sharing:** shared libraries never carry these credentials or a host's direct Bitwarden / Password Safe link,
  and a library item that points into a vault is ignored, so one member can't make another's PC read their own vault.
- **Audit:** every use is logged with the credential's name and purpose (and every Password Safe checkout and check-in),
  never the value.

## 21. What 0.6.17 adds for an organisation: AI server deploys and the default model

- **AI server deploys (Vultr).** The AI Engineer and operator can order a Vultr server on request. Every order shows the
  plan, size, region, OS and monthly price and needs the user's approval; it is sent once (never retried); the root
  password goes into the user's vault and never to the model; the order is in the audit log. Turn it off with
  `AiAllowServerDeploy` (Group Policy). Use Vultr API keys with the least access the team needs, and Vultr's API access
  control (allowed IPs) as usual.
- **Default local model.** Foundry Local stays the default engine; the model now fits the PC: Qwen2.5 14B on a GPU with
  12 GB+, Qwen2.5 7B on a 6 GB+ GPU, phi-4-mini on an NPU or a smaller PC. It is applied once and only to untouched
  defaults. Pin a model for everyone with the existing AI policies (`AiEngine`, `FoundryModel`); downloads stay subject to
  `AiAllowModelDownload` (a GPU PC's first download is about 5–9 GB).
