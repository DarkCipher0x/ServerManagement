# Stream manager for Twitch — design (ADR-118)

Status: **phases 1–2 built (ADR-118a), unreleased**; phase 3 (moderation, raids, polls, predictions, redemptions) and
phase 4 (OBS stats, live player) not built. Target: the CipherChat release train (held), or the one after it.

DarkCipher0x asked for "a full Twitch streamer management add-on for ServerManagement": everything a broadcaster's
stream manager has (stream feed, chat, activity feed, quick actions), keyboard shortcuts for clips and the like, and
Streamer.bot over the network. A typical setup is ServerManagement on the gaming PC and Streamer.bot (and OBS) on a
second streaming PC. It has to look native once its `.dcxaddon` is installed, refresh live so it can be used for chatting,
offer a clean at-a-glance dashboard with adjustable text size, and **not affect gaming performance**.

---

## 1. Shape: a data-only "stream" add-on that switches on a built-in module

Program add-ons can't draw UI, run for at most 600 s and live in a low-integrity sandbox (ADR-012, ADR-035), so a
live dashboard can't be one. The portal browser (ADR-032) and the shared library (ADR-033) already solve this the same
way, so this design follows them:

| Piece | Where | What |
|---|---|---|
| **Module add-on** (0.6.39, ADR-120) | `samples/addons/stream/addon.json` (`"module"`), packed and signed by `samples/addons/pack.ps1` | the package carries `Dcx.Stream.dll`, `stream.json`, `help.md` and the signed `module.json`; nothing of the stream manager is in the app |
| **`Dcx.Stream` project** | `samples/addons/stream/src` | the whole feature: Twitch sign-in, Helix, EventSub, Streamer.bot, OBS stats, the views, the module, its settings (`StreamSettings`), its policies and its self-test (`StreamSelfTest`) |
| **`StreamModule : IAddonModule, IModuleSelfTest`** | `samples/addons/stream/src/StreamModule.cs` | sidebar entry, screens, commands, settings cards, dashboard tiles, privacy rows, its help from the package |
| **Loader** | the app's `ModuleAddons.LoadAll()` | loads it only from the installed, enabled package whose module.json verifies with the release keys and whose files match; otherwise `Dcx.Stream.dll` doesn't exist on the PC at all |

> **Before 0.6.39** (ADR-118 / 118a) the stream manager was compiled into the app and a data-only `stream` add-on kind
> switched it on (`StreamFeatures`). ADR-120 moved all of it into the add-on; the rest of this document describes the
> feature, which is unchanged.

The add-on id is `com.darkcipher.stream`, name "Stream manager for Twitch". As with every add-on, it is packed into
`dist\addons\com.darkcipher.stream\` (plus the `.zip` copy) and never published.

### `stream.json` (≤ 16 KB, strict; unknown fields refused)

```json
{
  "name": "Stream manager for Twitch",
  "notes": "Optional text shown on the Add-ons screen.",
  "twitchClientId": "abcdefghijklmnopqrstuvwxyz0123",
  "streamerBot": { "host": "192.0.2.20", "port": 8080, "endpoint": "/" },
  "obs": { "host": "192.0.2.20", "port": 4455 }
}
```

- `twitchClientId`: the Client ID of a Twitch application registered as a **Public** client (dev.twitch.tv →
  Applications). It isn't a secret: public clients have no client secret. Settings and Group Policy can override it.
- `streamerBot` / `obs`: optional starting suggestions for the connection screens. Nothing connects until the user saves
  a connection and enters its password; passwords never live in the package.

---

## 2. What it does (feature map)

Everything uses Twitch's official Helix API and EventSub, as the signed-in broadcaster. Each row says where its data
comes from and which scope it needs; a scope is only requested when its feature is switched on (§6).

| Area | Feature | Source | Scope |
|---|---|---|---|
| **Status** | live / offline, uptime, viewers (sparkline), category, title | `GET /helix/streams` every 60 s while live, `stream.online` / `stream.offline` events | — |
| | followers (total, + since stream start), subscribers, latest follower / sub / cheer / raid | `GET /channels/followers`, `GET /subscriptions` once, then events | `moderator:read:followers`, `channel:read:subscriptions` |
| | next ad, pre-roll-free time, snoozes left | `GET /channels/ads`, `channel.ad_break.begin` | `channel:read:ads` |
| **Stream info** | edit title, category (search), tags, language, branded content, content labels | `PATCH /channels`, `GET /search/categories` | `channel:manage:broadcast` |
| **Chat** | live chat with badges, emotes, colours, replies, mentions highlighted, first-time chatters flagged | EventSub `channel.chat.message`, `channel.chat.notification`, `channel.chat.clear*`, `channel.chat.message_delete` | `user:read:chat` |
| | send messages and replies, announcements | `POST /chat/messages`, `POST /chat/announcements` | `user:write:chat`, `moderator:manage:announcements` |
| | moderation: delete, timeout, ban / unban, clear chat, chat modes (slow, followers, subs, emote-only, unique), shield mode, warn | `DELETE /moderation/chat`, `POST/DELETE /moderation/bans`, `PATCH /chat/settings`, `PUT /moderation/shield_mode`, `POST /moderation/warnings` | `moderator:manage:*` (each listed separately) |
| | who's here | `GET /chat/chatters` | `moderator:read:chatters` |
| **Activity feed** | follows, subs, resubs, gift subs, cheers, raids (in/out), channel-point redemptions, hype train, polls, predictions, ad breaks, shoutouts, goals, charity | EventSub (one subscription per type) | per type (`bits:read`, `channel:read:redemptions`, `channel:read:hype_train`, …) |
| **Quick actions** | clip, stream marker, run ad (30/60/90/120/180 s), snooze ad, start / cancel raid, shoutout, poll, prediction (create, lock, resolve, cancel), redemption queue (fulfil / refund) | `POST /clips`, `POST /streams/markers`, `POST /channels/commercial`, `POST /channels/ads/schedule/snooze`, `POST/DELETE /raids`, `POST /chat/shoutouts`, `/polls`, `/predictions`, `PATCH /channel_points/custom_rewards/redemptions` | `clips:edit`, `channel:manage:broadcast`, `channel:edit:commercial`, `channel:manage:ads`, `channel:manage:raids`, `moderator:manage:shoutouts`, `channel:manage:polls`, `channel:manage:predictions`, `channel:manage:redemptions` |
| **Stream feed** | preview (see §5): a thumbnail by default, the live player only when asked for | `thumbnail_url` from `GET /streams`; Twitch embed player | — |
| **Stream health** | bitrate, dropped / skipped frames, FPS, CPU on the streaming PC, congestion, recording state | OBS WebSocket v5 (`GetStreamStatus`, `GetStats`) on the streaming PC, directly or through Streamer.bot | — |
| **Streamer.bot** | see §4 | | |

Helix has no stream-health endpoint, so health comes from OBS on the streaming PC. Twitch's own "stream health"
panel isn't in the public API.

---

## 3. Screens: native, clean, at a glance

All screens use the app's own controls and theme keys (`Card`, `Hint`, `Label`, `SmallButton`, `PrimaryButton`,
`DangerButton`; brushes `Fg`, `Muted`, `Accent`, `Line`, `Surface`, `Surface2`, `Good`, `Warn`, `Bad`), so a theme
add-on recolours them like any other page. Nothing is a web page.

- **Sidebar:** a "STREAM" section with *Dashboard*, *Chat*, *Activity* and *Streamer.bot*, registered through
  `IAppHost.NavItem` (the module never edits `MainWindow.Sidebar.cs`).
- **Dashboard** (`OpenSingleton("stream", "📺 Stream", …)`):
  - **Top strip:** LIVE / OFFLINE pill, uptime, viewers with a 2-hour sparkline, followers +N, subs, next-ad countdown,
    health (bitrate · dropped %), and Twitch / Streamer.bot / OBS connection chips. Each chip goes `Good`/`Warn`/`Bad`.
  - **Left column:** preview, then stream info (title and category edited in place, Save).
  - **Centre:** chat, with the send box at the bottom.
  - **Right column:** activity feed (filter chips per type) above quick actions (a grid of large buttons) and
    favourite Streamer.bot actions.
  - Columns can be resized, hidden or swapped from the view's ⋯ menu; the layout is remembered.
- **Chat and Activity** also open on their own as tabs, or pinned in the right dock (`IAppHost.RightDock`) next to a
  terminal. Any of them can pop out (`DetachedWindow`), with an *Always on top* option and a **compact mode** for a
  small window on a second monitor or the gaming monitor's edge: viewers, uptime, the last 3 events, the last 15 chat lines.
- **Text size:** one slider per stream screen (80–250 %, Ctrl+Plus / Ctrl+Minus / Ctrl+0 while the screen has focus),
  plus separate chat and feed line spacing. It scales fonts, not the window, so layouts don't blur. Remembered per screen
  and per pop-out.
- **Main dashboard tiles** (`IAppHost.DashboardSource`): *Stream* (live, uptime, viewers) and *Latest activity*.

---

## 4. Streamer.bot over the network

Streamer.bot's WebSocket server is off-box by choice: by default it binds `127.0.0.1:8080`, endpoint `/`, with
authentication off. To use it from the gaming PC, the streaming PC sets *Address* to its LAN address (or `0.0.0.0`),
turns **Authentication** on and sets a password (Servers/Clients → WebSocket Server). The setup wizard on the
Streamer.bot screen shows these steps.

**Protocol** (docs.streamer.bot, API → WebSocket):
- On connect the server sends `Hello`. When authentication is on, it carries `authentication.salt` and
  `authentication.challenge`.
- The client answers `{"request":"Authenticate","id":"…","authentication":A}`, where
  `secret = base64(sha256(password + salt))` and `A = base64(sha256(secret + challenge))`.
- Requests are `{"request":…, "id":…}` with replies `{"status":"ok"|"error","id":…}`. Used: `GetInfo`, `GetActions`,
  `DoAction` (`action: {id, name}`, `args`), `Subscribe` / `UnSubscribe` (`events: {category: [names]}`), `GetEvents`,
  `GetActiveViewers`, `GetBroadcaster`, `GetCommands`, `GetCodeTriggers`, `ExecuteCodeTrigger`, `GetGlobals` /
  `GetGlobal`, `GetCredits`, `SendMessage`.
- Events arrive as `{"timeStamp", "event": {"source", "type"}, "data"}`.
- The docs disagree with themselves in places (`Twitch` vs `twitch` category keys; `GetGlobals`' example says `GetGlobal`;
  whether anything besides `SendMessage` needs authentication). The client therefore matches names case-insensitively,
  and the self-test's fake server exercises both spellings.

**What it offers:**
- **Actions:** every action, grouped as in Streamer.bot and searchable, with run, run-with-arguments and ★ favourite.
  Favourites appear on the dashboard and can each get a keyboard shortcut (§5). Recent runs and their results are listed.
- **Status:** connection, Streamer.bot version, connected platforms (`GetBroadcaster`), active viewers.
- **Events:** Streamer.bot's own events (OBS scene changes, custom events, other platforms) feed the activity feed,
  de-duplicated against Twitch's.
- **Commands and code triggers:** listed; a code trigger can be run like an action.
- **Globals:** read-only view of persisted globals. Changing them stays in Streamer.bot.
- Scene switching, OBS sources and anything else on the streaming PC are done through Streamer.bot actions, so
  ServerManagement never needs OBS's password for control. The direct OBS link (§2) is read-only stats.

**Security:**
- The link is plain `ws://`: Streamer.bot has no TLS. Anyone on the LAN who can reach the port can watch the traffic,
  and with authentication off, run actions.
- The client therefore **refuses to connect without authentication** unless the user ticks *Allow without a password
  (not recommended)* (policy `StreamerBotRequireAuth`, default 1, can remove the tick).
- By default only loopback, RFC 1918, link-local and `.local` / `.lan` hosts are accepted (policy `StreamerBotHosts`
  can list others).
- The password is kept in the app's encrypted store and is sent to Streamer.bot only as the challenge hash.
- The wizard suggests a Windows Firewall rule on the streaming PC that allows the port only from this PC's address. It
  shows the `New-NetFirewallRule` line to run there and doesn't run it itself.
- Every `DoAction`, `ExecuteCodeTrigger` and `SendMessage` is audited (`stream`, action name, never its arguments'
  values).
- The connection reconnects with backoff (1 s → 30 s) and shows its state on the chip. It never retries authentication
  on a wrong password.

The same rules cover the optional OBS WebSocket link (v5, `ws://`, port 4455, its own SHA-256 salt/challenge
authentication).

---

## 5. Keyboard: clips and every quick action, even while gaming

- Every quick action and every ★ Streamer.bot action is a command in the ADR-115 registry (`IAppHost.Command`):
  `stream.clip`, `stream.marker`, `stream.ad30` … `stream.ad180`, `stream.snooze-ad`, `stream.raid-cancel`,
  `stream.shield-toggle`, `stream.chat-focus`, `stream.compact-toggle`, `streamerbot.<action id>`. Each one is rebindable on
  Settings → Keyboard (its own "Stream" category) and in the command palette.
- **While gaming**, the game has the keyboard, so in-app keys don't fire. Each stream command therefore also has *Also
  system-wide* (`GlobalHotkeys`, `RegisterHotKey` on a message-only window: no hooks, no input polling, no cost while idle).
  - System-wide keys are **off until the user sets them**, so nothing steals a key a game uses. The capture box suggests
    Ctrl+Alt+F-keys and warns when a key is taken by another program.
  - Policy `StreamGlobalHotkeys` = 0 turns them all off.
- **Feedback** without leaving the game: a short sound (optional) and a toast through `Dcx.Notify`, e.g. "Clip created —
  link copied". The clip's link goes to the activity feed; *copy link on clip* is optional.
- A clip made from the gaming PC is Twitch's own clip (`POST /clips`), the last ~30 s before the press. Twitch processes
  it for a few seconds; the feed entry updates when its URL works.

---

## 6. Twitch sign-in and tokens

- **Device Code Grant flow**, which suits a desktop app and is a public client, so there's no secret to ship:
  1. `POST https://id.twitch.tv/oauth2/device` with `client_id`, `scopes`. The reply has `device_code`, `user_code`,
     `verification_uri`, `expires_in` (1800) and `interval` (5).
  2. The app shows the code and a QR code (QRCoder, already a dependency), and opens `verification_uri` in the user's
     browser.
  3. It polls `POST /oauth2/token` (`grant_type=urn:ietf:params:oauth:grant-type:device_code`) every `interval`
     seconds, treating `authorization_pending` as "wait".
- **Lifetimes:**
  - Access tokens last ~4 h and are refreshed (`grant_type=refresh_token`, no secret for public clients).
  - Refresh tokens are **one-time use**, and a public client's refresh token **expires after 30 days**.
  - Twitch's two pages differ on whether that is 30 days since it was made or 30 days unused. The app treats it as
    absolute: it shows "Twitch sign-in renews in N days" from day 25 and falls back to the device flow when a refresh is
    refused.
  - Each new refresh token is saved before the old one is dropped.
- **Validation:** `GET /oauth2/validate` at start and hourly (Twitch requires this). A 401 signs out cleanly.
- **Storage:** tokens live in the app's encrypted settings store (`ProtectedSettings`), never in a file of their own,
  never in an add-on process, never in logs (redacted like vault values). Sign out revokes (`POST /oauth2/revoke`) and
  wipes.
- **Scopes:** the minimum for the switched-on features (Settings → Stream → *Features*: read-only, chat, moderation,
  channel management, ads, raids, polls & predictions, redemptions). Turning a feature on asks for a new device sign-in
  with the larger scope list, and the screen says exactly which scopes and why.
- **Client ID:** taken from policy `StreamTwitchClientId`, else Settings, else `stream.json`. Registering it is one
  step the broadcaster does once at dev.twitch.tv (§9).

---

## 7. EventSub over WebSocket (push, not polling)

- One connection to `wss://eventsub.wss.twitch.tv/ws`. After `session_welcome`, the app creates its subscriptions with
  `POST /helix/eventsub/subscriptions` (`transport: {method: "websocket", session_id}`) inside the 10-second window.
- **Session messages:**
  - `session_keepalive` / `keepalive_timeout_seconds` drive the dead-link check.
  - `session_reconnect` moves to the new URL before closing the old one, so no events are lost.
  - `revocation` turns that feature's chip `Warn` with the reason.
- **Limits:** one socket, about 25–35 subscriptions for the full feature set, well within the per-connection limit
  (300). The exact connection and cost limits are re-checked against the docs at build time and kept in one constants
  file.
- **Duplicates and order:** messages carry `message_id`; the last 2 000 ids are kept to drop duplicates, which the docs
  allow. Timestamps order the feed.
- **Polling** is left only where Twitch has no event: viewer count (`GET /streams`, 60 s, live only), ad schedule (on
  ad events and every 5 min while live), chatters list (only while that panel is open, 60 s). Nothing polls while
  offline except the `stream.online` event, which is pushed.

---

## 8. Lightweight: a budget it is tested against

The module is built for running next to a game all evening. Targets, measured on the self-test's fake stream at 20 chat
messages/s:

| Budget | Target |
|---|---|
| CPU (whole app, dashboard visible, 20 msg/s) | < 1 % of one core on average |
| CPU (minimised or the stream screens hidden) | ~0 % (sockets idle, no timers but keepalive) |
| Extra memory with the module running | < 60 MB working set (no browser engine by default) |
| GPU | none by default (no video, no animations, no WebView2) |
| Network | chat + events only; the thumbnail is ~15 KB/min; no video unless the player is open |

How:
- **Push, coalesced.** Socket messages are parsed off the UI thread into a channel. The UI drains it at most 4×/s
  (`DispatcherPriority.Background`) and adds a batch at once. Viewer and uptime text ticks once a second only while
  visible.
- **Capped and virtualised.** Chat keeps the last 500 messages and the feed the last 300 events in memory (ring buffers;
  older ones are gone, nothing is written to disk). Both lists use a recycling `VirtualizingStackPanel`, so only visible
  rows exist.
- **Paused when unseen.** When the window is minimised, the screen isn't the visible tab, or the pop-out is hidden, the
  UI stops draining. Data keeps buffering, and the screen catches up from the buffers when shown.
- **Images are cheap.** Emotes and badges are fetched once from Twitch's CDN at 1× size, decoded at display size,
  frozen, and kept in a 300-entry memory LRU. Animated emotes show their first frame unless *Animate emotes* is on.
- **No video by default** (§9). The thumbnail refreshes every 60 s only while visible and live.
- **Stream mode** (on by default while live, Settings → Stream): lowers the app to `BelowNormal` priority and asks
  Windows for efficiency mode (EcoQoS via `SetProcessInformation(ProcessPowerThrottling)`), so the game always wins the
  CPU. It goes back when the stream ends. Policy `StreamEfficiencyMode` can force it on or off.
- **Nothing on the hot path:** no logging per message, no LINQ allocations per row, `System.Text.Json` source
  generators for the event models (like ADR-110), one `HttpClient`, and HTTP/2.

---

## 9. Decisions (defaults until DarkCipher0x says otherwise)

| Question | Default in this design | Why |
|---|---|---|
| Whose Twitch application (Client ID)? | DarkCipher0x registers one **Public** app at dev.twitch.tv ("DarkCipher ServerManagement", category *Broadcaster Suite*, OAuth redirect `http://localhost` — unused by the device flow but required by the form) and its Client ID goes in `stream.json`; Settings / policy can override | public clients need no secret, so nothing secret ships |
| Video feed | **Thumbnail** (refreshed every 60 s); *Open live player* is a button, and when used it opens at the lowest quality, muted, and stops when hidden | the player is a browser engine: tens of % of a core and 150–300 MB; the embed also needs a `parent` domain and the docs don't say whether a local virtual host is accepted, so it's phase 4 and tested before it's promised |
| System-wide shortcuts | none until set; suggested Ctrl+Alt+F-keys | never steal a game's key |
| Streamer.bot without a password | refused unless ticked | plain `ws://` on the LAN |
| Stream mode (priority + efficiency) | on while live | "not affecting gaming performance" |
| Chat sending / moderation | both on (broadcaster's own channel); moderation buttons confirm ban, not timeouts or deletes | normal stream-manager behaviour |

---

## 10. Group Policy (Software\Policies\DarkCipher\ServerManagement, regenerated into POLICIES.md)

| Value | Type | Meaning |
|---|---|---|
| (`AddonAllowModules` / `AddonBlockedIds`) | DWORD / MULTI_SZ | the app's add-on policies keep the module from loading (`StreamFeatures` was removed in 0.6.39) |
| `StreamTwitchClientId` | SZ | Client ID to use |
| `StreamAllowChatSend` | DWORD | 0 = read-only chat |
| `StreamAllowModeration` | DWORD | 0 = no moderation scopes or buttons |
| `StreamAllowChannelEdit` | DWORD | 0 = title / category / tags are read-only |
| `StreamerBotRequireAuth` | DWORD | 1 (default) = never connect without authentication |
| `StreamerBotHosts` | MULTI_SZ | extra allowed Streamer.bot / OBS hosts |
| `StreamGlobalHotkeys` | DWORD | 0 = no system-wide keys |
| `StreamLivePlayer` | DWORD | 0 = no embedded player |
| `StreamEfficiencyMode` | DWORD | 0 = never, 1 = while live, 2 = always while the module runs |

---

## 11. Testing (self-test, no windows, 127.0.0.1 only)

- **Fakes in the self-test:**
  - **Twitch:** id (device flow, token, refresh, validate, revoke), Helix (streams, channels, clips, markers, ads,
    chat, moderation) and an EventSub WebSocket server that replays a scripted evening (welcome, keepalives, a
    `session_reconnect`, 20 msg/s chat, follows, subs, a raid, a revocation).
  - **Streamer.bot:** Hello with salt/challenge, Authenticate (right and wrong passwords), GetActions, DoAction,
    Subscribe and events, both category spellings.
  - **OBS:** GetStats / GetStreamStatus.
- The base URLs are internal test seams (`InternalsVisibleTo` the self-test only), never settings, so a build can't be
  pointed at a fake Twitch.
- **Checks:**
  - sign-in, refresh rotation (the old refresh token is refused afterwards) and the 30-day warning;
  - every quick action's request body;
  - the chat cap and recycling;
  - pause-while-hidden (no UI work while minimised);
  - reconnect without lost events;
  - Streamer.bot refusing no-auth and non-LAN hosts;
  - global hotkeys registered and unregistered;
  - the CPU / memory budget at 20 msg/s, recorded in the run's report;
  - and that with the add-on removed, `Dcx.Stream.dll` isn't loaded.
- Every check runs through the same views and pickers the user uses (verify through the UI path).

---

## 11a. The stream board (ADR-118c): a dashboard you arrange like Twitch's advanced Stream Manager

The dashboard is one board of tiles on a 12-column grid. Everything is a tile, and every tile can be moved, resized,
added and removed:

- **Editing:** **✎ Edit layout** shows a frame, a title bar to drag and a ◢ grip on every tile. Moving or resizing pushes
  the tiles in the way downwards, then the board closes up the gaps (no overlaps, ever). From the keyboard: Tab to a tile,
  arrows move it, Shift+arrows resize it, Delete removes it, Esc cancels a drag. **✓ Done** leaves edit mode. Every change
  is saved at once.
- **Layouts:** five starting layouts (Default, Chat focus, Gaming (compact), Moderation, Production), your own saved with
  *Layout ⋯ → Save as*, switched from the combo box, reset or deleted. A layout is plain text, one tile per line
  (`kind col row width height`), so *Copy as text* / *Paste* share it and the `StreamBoardLayout` policy can set it.
- **Tiles (19):** stream stats (this stream's messages, chatters, follows, subs, bits, raids, clips, markers, peak), viewers
  (sparkline), chat, activity feed, quick actions, stream info (title presets), preview, stream health (Twitch + OBS:
  bitrate, dropped frames, CPU, FPS, history), OBS (scenes, mute, replay buffer, recording), Streamer.bot (searchable
  actions), moderation (chat modes, shield mode, clear chat), polls and predictions, the redemption queue, raid and
  shoutout (channel search, live first; announcements), hype train and goals, counters (any number; each can run a
  Streamer.bot action with `counter`/`value` arguments), notes (any number), timers (any number) and the AI assistant.
- **Live:** tiles follow EventSub as it arrives (poll and prediction progress, redemptions, hype trains, goals, chat
  settings, shield mode, shoutouts, incoming and outgoing raids). The board redraws at most once per change burst, ticks
  once a second while visible, asks OBS every 5 s and Twitch's chatters and goals every 60 s.
- **Chat moderation:** right-click a message: delete, time out (1 m / 10 m / 1 h / 1 d), warn, ban (asks first), unban,
  shout out, open the channel. Never offered on your own messages, and gone when moderation is off by policy.
- **OBS WebSocket 5:** on this PC or the local network only (LanHosts), the password in `stream-account.dcx`, a server
  without a password refused unless you allow it (never under `StreamObsRequireAuth`). The address and the board live in
  their own encrypted file, `stream-board.dcx`.
- **AI:** the AI tile asks the app's AI panel (local by default): summarise chat, questions in chat, title ideas, draft a
  poll, stream recap, mood check, or anything you type. The AI also gets 11 stream tools: 4 reads (status, recent chat,
  recent activity, Streamer.bot actions) and 7 changes (clip, marker, send chat, set title, start a poll, run a
  Streamer.bot action, switch an OBS scene), and every change waits for your approval.
- **Live player (ADR-118d):** the preview tile shows a picture every minute while live; **▶ Watch live** swaps it for
  Twitch's own player (muted, since your own stream's sound would echo) inside a script-free page served from memory on
  `dcx-stream-player.example` (Twitch's `parent`). The browser exists only while you watch: stopping, going offline,
  hiding the tile or closing the board disposes it. Its own profile folder, encrypted for this account; no dev tools,
  menus, dialogs or downloads; every permission denied; frames only to Twitch over https; links open in your browser
  (twitch.tv only). `StreamPlayer = 0` turns it off. WebView2 comes from the app at run time (compile-time reference
  only). Not yet tried against the real Twitch player.
- **Redemptions:** Twitch lets only the application that created a reward fulfil or refund it, so for rewards made
  elsewhere the queue says so and *Done* marks them locally.

**The module's own policies** (`…\ServerManagement\Stream`, machine then user): `StreamBoardLocked`,
`StreamBoardLayout` (multi-string), `StreamQuickActions` (`kind|value|label` lines: builtin, bot, scene, chat),
`StreamObs`, `StreamObsHost`, `StreamObsPort`, `StreamObsRequireAuth`, `StreamAi`, `StreamModerationTools`, `StreamPlayer`. They are read
by the module itself, so they ship with the add-on rather than with the app.

## 12. Phases

1. **Kind and module, read side:**
   - `stream` add-on kind, `Dcx.Stream` project, loader, policies and the add-on package;
   - device sign-in, token store, validation;
   - EventSub socket, chat (read and send), activity feed, status strip, stream info edit;
   - dashboard, text size, clip and marker commands (in-app keys), dashboard tiles.
2. **Streamer.bot and system-wide keys:**
   - client, wizard, actions, favourites, events in the feed;
   - `GlobalHotkeys`, toasts;
   - compact pop-out and *Always on top*.
3. **Full control** (built, ADR-118c):
   - moderation, chat modes and shield mode;
   - ads (run, snooze, schedule), raids, shoutouts, polls, predictions, redemption queue, announcements, chatters.
4. **Health and video** (OBS stats built in ADR-118c, the live player in ADR-118d): OBS stats (direct or via Streamer.bot), the live-player option (after the `parent` test), and
   the performance budget written into the release notes.

Each phase is one ADR entry in ARCHITECTURE.md (ADR-118 a/b/c/d), user guide pages, a help article, and its self-test
group. Nothing is released without DarkCipher0x's go.

---

## Sources checked (2026-11-08)

- Streamer.bot: API servers; WebSocket guide (authentication, configuration, events); WebSocket requests —
  docs.streamer.bot/api.
- Twitch:
  - dev.twitch.tv/docs/authentication (getting tokens — device code flow; refresh tokens);
  - EventSub (handling WebSocket events, managing subscriptions; WebSocket GA announcement on discuss.dev.twitch.com);
  - Embed (video and clips).
