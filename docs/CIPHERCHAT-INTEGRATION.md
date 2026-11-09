# CipherChat in ServerManagement — design (ADR-117)

Status: **proposal**, for review before any code. Target: a release after 0.6.37.

## 1. Goal

CipherChat (post-quantum end-to-end encrypted chat and calls) built into ServerManagement as a native part of the app,
not a second program in a frame: chats in tabs and in a pinnable panel beside your terminals, dashboard widgets,
sharing from every screen, the house theme, the keyboard registry, the command palette, Settings and Group Policy.
A team works in ServerManagement and talks in it: a terminal open on the left, the team's room on the right, a
selection sent from one to the other in a keystroke.

It must interoperate fully with the CipherChat server and every CipherChat client (web, Windows, Android), stay as
secure as CipherChat itself, and take CipherChat's security updates without rework.

## 2. The one rule that makes it maintainable: the protocol is CipherChat's, the screens are ours

CipherChat's Windows client is already split into UI-free libraries:

| Library | What it is | Used by ServerManagement |
|---|---|---|
| `CipherChat.Crypto` | hybrid X25519 + ML-KEM-768 / Ed25519 + ML-DSA-65, room keys, sealing, safety numbers, call frame keys | as is |
| `CipherChat.Transport` | HTTPS over post-quantum-only TLS 1.3 (managed, BouncyCastle 2.7.0 — the same version ServerManagement pins) | as is |
| `CipherChat.Api` | the server API (sign-in, device sign-in, passkeys, poll, upload, download) | as is |
| `CipherChat.Engine` | rooms, messages, edits, reactions, burn-after-reading, files, outbox, key history, device prekeys (protocol 2), trust and certification, pairing, calls signalling | as is |
| `CipherChat.Vault`, `.App`, `.Ui`, `.Passkeys`, `.Addons` | the CipherChat app's own storage (DPAPI), window and controls | **not used** (replaced by ServerManagement's own) |

ServerManagement never re-implements protocol or cryptography. Everything security-relevant stays in CipherChat's code,
tested by CipherChat's suites and its interop fixtures; ServerManagement adds only screens, storage and policy.

**How the code comes in (recommended): a pinned, vendored copy of the four libraries.**
`tools/sync-cipherchat.ps1 -From ..\CipherChat -Tag v0.2.x` copies `windows/src/CipherChat.{Crypto,Transport,Api,Engine}`
into `src/Vendor/CipherChat/` with their own `Directory.Build.props` (CipherChat's compiler settings, not ours), and writes
`src/Vendor/CipherChat/VENDOR.json`: CipherChat version, protocol version, source commit, and the SHA-256 of every file.
The build and the self-test refuse a vendored file that doesn't match the manifest (no silent local edits). Updating
for a CipherChat security fix is: run the script, review the diff, build, run both suites. Public source builds offline
from the repo (both projects are MIT). Alternatives considered: a project reference into the sibling repo (breaks the
public source build), NuGet packages from CipherChat (binary blobs in the repo, harder to review).

## 3. Shape inside ServerManagement

A module, like the AI module (ADR-047 pattern), in its own project **`Dcx.Chat`**:

- **Off unless enabled.** `ChatFeatures` decides at start-up (machine policy, then the setting), exactly like
  `AiFeatures`: off means the module and the CipherChat libraries are never loaded — no sidebar item, no menu, no code.
- `ChatModule : IAppModule` contributes its sidebar section, settings cards, palette commands (`chat.*` in the
  keyboard registry), dashboard widgets, share targets and help articles through the existing module seams.
- `ChatSession` — ServerManagement's equivalent of CipherChat's `Session.cs`: one per account, owns `ChatApi`,
  `ChatEngine`, the device token and the key store; locks with the app lock.
- `ChatStore` — every chat secret in ServerManagement's encrypted store: the device token and identity in `SecureStore`
  (AES-256-GCM under the master key, Windows Hello when on), and CipherChat's `KeyStore` sealed through an
  `IStoreGuard` backed by `SecureStore` instead of DPAPI. Nothing in plain text, nothing in CipherChat's own folders.

## 4. Features (everything CipherChat has, as native screens)

- **Sign-in:** pair ServerManagement as a new device of your existing account (device code from another CipherChat
  device), or sign in with username + password + code, or a passkey. Several accounts / servers.
- **Chats as tabs:** rooms and DMs open as tabs (pin, split, pop out, tab groups, workspaces — everything a tab can do).
- **Chat panel:** a pinnable side panel (like the hosts panel and the AI panel) showing one room or the room list, beside
  any tab, in the main window and in pop-out windows.
- **Messages:** text with Markdown and code blocks (mono, copy button), replies, edits, reactions, redaction,
  burn-after-reading, colour emoji, GIFs (session-only, as in CipherChat), files and images with progress, unread and
  mention counts, typing-free (CipherChat has none), offline outbox with retry.
- **Rooms:** create, members, retention, history window and history sync (protocol 1.1), leave, delete, key rotation.
- **People and trust:** safety numbers, certify, revoke, device list, remove a device, approve history for a new device.
- **Calls:** ringing natively (toast + in-app banner), the call itself in a WebView2 call window running CipherChat's own
  call page, as the CipherChat Windows app does (frame-encrypted WebRTC; keys never leave the engine).
- **Notifications:** Windows toasts and the app's notification centre, quiet hours, per-room mute; never message text in a
  toast unless you allow it.

## 5. Working together (what makes it more than a chat window)

- **Send to chat** from a terminal (the selection, or the last command's output, as a code block), the file commander
  (files), Toolbox and reports (the report), screenshots, notes, snippets, and a host (its connection line without
  credentials, opened by others with one click if they have the host).
- **From chat to work:** a code block offers **Run in terminal…** (always shown first, never run without your confirm,
  paste-guard rules apply), **Save as snippet**, **Open host** for a shared host line, **Save file** for attachments.
- **Mentions** of `@host` names link to your saved hosts.
- **Incidents:** an incident or work session can have its room; its timeline links the room.
- The AI module (when on) can summarise a room you choose, on this PC only — never automatically.

## 6. Dashboard widgets

Unread and mentions, a room's latest messages, who's online (when the server says), pending trust or device
approvals, and a call indicator. They use the dashboard's existing tile framework and refresh from engine events.

## 7. Policy, security and audit

- Group Policy: `ChatFeatures` (off / on / user choice), `ChatServers` (allowed server origins; default the user's own
  choice), `ChatAllowFiles`, `ChatAllowCalls`, `ChatAllowShareFromTerminal`, `ChatToastPreviews`, `ChatMaxFileMB`, and
  the History rules (ADR-116) for local chat caches. A locked category for chat like the others.
- Messages are never stored by ServerManagement beyond what the engine keeps for the room's history window; any cache
  is in the encrypted store and follows the room's retention.
- Audit: sign-in, pairing, device changes, certify/revoke, files sent, shares from terminals (what kind and to which
  room, never the content).
- Pentests added to `SelfTestPentest`: a hostile server's state, poll and file answers (sizes, types, nesting), an
  oversized or malformed message, a code block with control sequences sent to "Run in terminal", a file name with path
  separators, a call signal flood.
- Interop: the self-test runs against a local CipherChat server (127.0.0.1) with CipherChat's own interop fixtures, plus
  a second client (the vendored engine) to exchange messages, files, edits and a burn message in both directions.

## 7a. Server administration (the same as CipherChat for Windows' Admin window)

For people whose account manages a CipherChat server, the CHAT section has an **Admin** pane with its own sign-in. It
matches CipherChat for Windows' Admin window: the same screens, the same server API, and the same rules.

- **Its own sign-in, by design.** A paired device is chat-only: the server refuses a device session on
  `/chat/api/admin/*` (403 with `admin_signin`). The pane therefore signs in separately, with password + authenticator
  code or a passkey, using `scope: "admin"`. That session works on the admin API only, never the chat, and it starts
  fresh (it counts as "confirm it's you").
  - It lives **in memory only**, in its own `ChatApi` with its own cookie. It is never stored and never resumed with the
    device token.
  - It is signed out when the pane closes, the app locks, the chat locks or you sign out. An idled-out session makes
    the pane ask again.
  - On a 0.2.0 server the scope is ignored (admin works from a normal sign-in).
- **Screens.** Each is shown only when you hold its permission; the server checks everything again anyway.
  - **Requests** (`admin.requests.review`): new / approved / declined / all; approve and invite (username, email the
    link); decline; delete.
  - **People** (`admin.users.view`):
    - invite (username, display name, email, role, email the link);
    - change role; per-permission *as the role / always / never*;
    - new invite link; suspend / re-activate; password reset link; authenticator reset link;
    - sign out everywhere; remove account.
    - The list shows status, locked, last sign-in, chat identity and whether it's certified, and live sessions.
  - **Roles** (`admin.users.view` / `admin.roles.manage`): create, edit label and permissions, delete. Permissions come
    from the server's own catalogue, so new ones appear without a code change.
  - **Settings** (`admin.settings.manage`): edited against the server's own schema (kinds and limits).
  - **Email & notifications** (`admin.mail.manage`): SMTP (STARTTLS or TLS only; the password is sent only when it's
    changed, and is never shown again), test email, per-event notifications, delivery log.
  - **Audit log** (`admin.audit.view`): the newest 300 events.
- **Rules the server enforces.** You can only change accounts with fewer permissions than you have, and only give
  permissions you hold. The owner is changed only by the owner. Every change needs a fresh authenticator code; the pane
  asks through `Ask` (or a passkey) when the server says so.
- **What stays on this PC: nothing.**
  - One-time invite and reset links are shown once to copy, and only when they point at that server's own
    `/chat/login/`. They are never logged or stored.
  - Requesters' addresses and messages are shown while the pane is open.
  - ServerManagement's audit log gets what was done to whom on which server, never a link, password, email address or
    message.
- **Code.** CipherChat is putting a UI-free `AdminApi` in `CipherChat.Api` (vendored with the next sync).
  `Dcx.Chat/Admin/ChatAdminClient.cs` is the one file that reaches it. Until that sync it calls the same endpoints
  through `ChatApi.CallAsync`, then it moves onto `AdminApi`. It adds only the typed records
  (`Admin/AdminModels.cs`), the shape checks, policy gating and ServerManagement's audit lines.
- **Policy:** `ChatAdmin` (on by default; 0 = no admin pane on this PC).

## 8. Phases

1. Vendoring + `Dcx.Chat` module skeleton, `ChatFeatures` + policy, `ChatStore`, sign-in and pairing, room list,
   chat tab (text, replies, edits, reactions), interop tests vs a local server.
2. Files and images, burn-after-reading, rooms and members, people and trust, device list, notifications.
3. Chat panel (pinnable), send-to-chat from every screen, chat-to-work actions, dashboard widgets.
4. Server administration pane (section 7a), on CipherChat's `AdminApi` once it is vendored.
5. Calls (call window), incidents link, AI summary, pentests, docs, release.

## 9. Decisions (DarkCipher0x, 2026-11-08)

| Question | Decision |
|---|---|
| Release | Hold: 0.6.36 (keyboard) and 0.6.37 (history) ship together with CipherChat as one release. |
| CipherChat code | A pinned vendored copy of its UI-free libraries (`tools\sync-cipherchat.ps1`, `VENDOR.json`). |
| Sign-in | Pair ServerManagement as a device (recommended); password + code and passkey are offered too. |
| Sharing | Yes, share into chats: terminals, commander, Toolbox, reports, notes, snippets, hosts, with actions on what arrives. |
| Administration | The same admin features as CipherChat for Windows, in their own pane behind a separate admin sign-in (section 7a). |
| Feel | Native: chats as tabs, pinnable chat panels, dashboard widgets, working beside open terminals with a dev team. |
