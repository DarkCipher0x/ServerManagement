# Product spec — "infrastructure command centre" (DarkCipher0x, 2026-10-18)

Condensed from DarkCipher0x's unified specification. It **extends** every earlier requirement (SECURECRT-COMPARISON.md,
history/RESUME-session3/5/6) — nothing earlier is dropped. Status per item lives in `history/SPEC-GAP-ANALYSIS.md` (2026-10-18; superseded by the release notes).

**Core priorities, in order:** stability · security & privacy · performance · reliability · intuitive UX · deep
customisation · consistent design · AI integration · error handling · maintainability · long-running stability ·
local-first data ownership. Real functionality only — no placeholders, never fake what the platform/API/runtime can't do
(explain the limit, build the strongest real alternative, leave the seam, mark it incomplete).

1. **Audit first**; reuse what works, fix what's broken, extend cleanly; never remove capability while redesigning.
2. **Layout/navigation:** frequent actions visible; secondary in dropdowns/overflow/context menus/expanders; consistent
   hierarchy across top bar, toolbars, ad-hoc connect, explorers, sidebars, tabs, menus, settings, AI, Vultr, terminal,
   transfers, documents. Clean, not hidden.
3. **Ad-hoc connection area:** obviously ad-hoc; never looks like "connecting to Vultr"; distinguish ad-hoc / saved /
   providers / Vultr resources / protocols / quick actions; say what happens before the click.
4. **RDP** exposed and integrated with saved hosts, explorer and menus; shared connection abstractions (SSH, RDP, SFTP, SCP).
5. **Explorer customisation (extremely important):** Linux/Windows/Network/Cloud/Local/Remote/Providers/Servers/Services/
   Storage + custom categories, groups, folders; collapse, rearrange, resize, hide/show, rename, sort, filter, search;
   persistent organisation and layout everywhere.
6. **Context menus:** context-aware, reusable framework, only valid actions (open, connect, terminal, SSH, RDP, SFTP, SCP,
   files, edit, rename, duplicate, move, groups, refresh, reconnect, disconnect, copy info, screenshot, open externally,
   AI, properties, settings, delete).
7. **Terminal right-click** fixed properly with a real terminal menu.
8. **Screenshots:** visible button near terminal/tab bar + context menus; optional AI analysis only when enabled, user
   controlled, never auto-sent to remote providers.
9. **Readability:** larger defaults; configurable UI scale, UI font size, terminal font family/size (independent),
   explorer text size, density (compact/comfortable).
10. **Commander SFTP/SCP:** dual pane, local⇄remote and remote⇄remote; browse, upload, download, copy, move, rename,
    delete, mkdir, refresh, history, sort, filter, search, properties, menus, progress, status, errors, cancel; integrated
    with saved servers; AI operator can use it with permissions; destructive ops confirmed.
11. **Vultr:** clear categories, separate from ad-hoc connect; documents (invoices, billing, account) viewable where the
    API exposes them; don't invent provider features.
12. **Reusable document viewer:** in-app/popup, zoom/navigation, open externally, save, AI summarise/explain/extract/
    answer/compare; follows storage, encryption and AI-privacy rules.
13. **AI application-wide** (optional operator layer, not a side chatbot): terminals, servers, connections, explorer,
    SFTP, screenshots, providers, Vultr, invoices, documents, logs, settings, diagnostics, monitoring, help, errors,
    navigation — context given deliberately, respecting settings/permissions/privacy/confirmation.
14. **AI help/FAQ:** contextual questions grounded in the app's real features/settings/docs; static help works without AI.
15. **AI backends:** provider abstraction — Ollama/local, CPU, GPU, NPU (only where runtime+hardware genuinely support it,
    detected properly), remote APIs when configured; Settings shows provider, model, local/remote, acceleration.
16. **AI operator = tool/action architecture:** well-defined actions with permissions, confirmations, sensitivity,
    destructive safeguards, connection state, local/remote boundaries.
17. **Local-only + encryption:** all data local by default; never silently synced/backed up/transmitted. 17A established
    crypto only, secrets in OS secure storage where appropriate, keys never beside data in clear; migrate plaintext.
    17B temp/cache/screenshots/logs/AI context/clipboard/transfers/crash data in scope; no leftover plaintext.
    17C detect OneDrive + other sync providers reliably (not by path text), extensible; clear warning with Review /
    Change location / Continue / **Don't show again (permanent, per location; re-arm when the location changes)** —
    warn, never block. 17D storage settings: location, encryption status, local vs synced, usage, safe transactional
    migration that leaves old data intact on failure. 17E backups/exports are explicit, encrypted where sensitive, warned
    (not blocked) for synced/network targets. 17F local AI keeps context local; remote AI only gets what the user
    deliberately sends; no secrets in AI context; LOCAL STORAGE + LOCAL AI must be a complete workflow. 17G security
    audit of credentials, tokens, keys, configs, logs, caches, temp files, hard-coded secrets, UI exposure, AI requests.
18. **Customisation first-class:** fonts, scale, organisation, collapsed state, pane sizes, columns, layouts, default
    connection behaviour, terminal profiles, AI provider/model/toggles/context privacy, themes, data location — coherent
    settings architecture.
19. **Stability/performance for 24/7:** leaks, listeners, subscriptions, renders, polling, leaked sessions, handles,
    timers, freezes, blocking, races, reconnect, unhandled exceptions, error isolation, watchers, background work, AI
    processes that don't exit.
20. **UX consistency:** shared patterns for menus, dropdowns, toolbars, tabs, trees, dialogs, confirmations,
    notifications, settings, document viewers, connection/loading/error states, AI actions, transfers.
21. **Keep every earlier requirement.**

**Implementation order:** P1 audit + storage/secrets/sync warning + terminal right-click + readability + top bar + RDP +
menus framework · P2 explorer customisation + context menus + screenshots · P3 Commander + transfers + AI file ops ·
P4 Vultr cleanup + documents + AI review · P5 app-wide AI, help AI, tool architecture, backend abstraction, privacy
controls · P6 stability/performance audit, soak tests, consistency, security review, polish.
After each phase: build, tests, check the change, fix regressions, security/stability check, report honestly; keep going
without asking.
