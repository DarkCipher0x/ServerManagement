# ServerManagement release pipeline (approved by DarkCipher0x 2026-10-21; implemented)

Follows the shared standard (`Projects\RELEASE-PIPELINE-PHASE1-2026-10-21.md`, memory "release-pipeline-standard").
This session owns ServerManagement's pipeline. Website deploys stay with the Website & Deployments session, and a
production deploy needs DarkCipher0x's go.

## Steps and order

One entry point, `.\build.ps1`. Local steps 1 to 3 always run. Public steps 4 to 8 run only when publishing is on.

| # | Step | Script | What it does | Done when (idempotency check) |
|---|------|--------|--------------|-------------------------------|
| 1 | Build | build.ps1 | As today: self-test gate, publish, MSI, zip, policy templates, SBOM, PDFs, checksums | `dist\<ver>\` bundle complete |
| 2 | Pre-publish checks | `tools\release-check.ps1` | Version agrees in the csproj, exe FileVersion, MSI ProductVersion, update.json, release notes file, README badge and history (both generated now, never by hand); every bundle file present; each `.sha256` and SHA256SUMS matches its file; self-test reports passed | Always re-run (cheap) |
| 3 | Sanitised source | `tools\write-public-source.ps1` | Rebuilt from scratch in a fresh staging folder, from an **allowlist** (`tools\public-allowlist.txt`) intersected with git-tracked files. A tracked file that is neither allowed nor on the private list **fails the build**, so every new file needs a decision. Keeps the privacy-term scan, the image hash gate and the forbidden-extension gate. | The zip in `dist\<ver>\` |
| 4 | Secret scan | `tools\secret-scan.ps1` | Pinned gitleaks (version and SHA-256 checked before each run, kept in `%LOCALAPPDATA%\DarkCipher\tools`, never installed system-wide) over the staged tree, with custom rules: Discord webhook URLs, GitHub tokens (ghp_, gho_, github_pat_), private keys and PFX, cloud API tokens, deploy keys. Any finding aborts. | Re-run per publish |
| 5 | Mirror push and tag | `tools\publish-public.ps1` | Checks `origin` is exactly `https://github.com/DarkCipher0x/ServerManagement.git` and the branch is `main`. Replaces the clone's tree, makes one commit and an annotated tag `v<ver>`, then pushes. Never force-pushes. | Tag exists and points at an identical tree: skip. Exists but different: stop and report. |
| 6 | GitHub Release | `tools\publish-github-release.ps1` (CaseKeeper's pattern) | Release "ServerManagement <ver>" on `v<ver>`. Notes come from `docs\release-notes\<ver>.md`. Assets: MSI, portable zip, source zip, SBOM, policy templates, the six PDFs and SHA256SUMS. GitHub's SHA-256 of each upload is checked. Auth is the token Git Credential Manager already holds, used in memory only. | Release exists with the same digests: skip. A published release is never edited. |
| 7 | Website hand-off and verify | `tools\website-handoff.ps1` and `tools\verify-website.ps1` | Writes `dist\<ver>\website\`: installer copies and source zip byte-identical to the Release assets, SHA256SUMS, the rendered release notes, and a manifest. The Website session deploys it with DarkCipher0x's go. `verify-website.ps1` then downloads the live files' SHA-256s and compares them with the Release assets and `-public` at the tag. Any mismatch is a failed publish and is reported. | The manifest exists; verification is re-run on demand |
| 8 | Discord | `tools\announce.ps1` | A card generated from the release notes (no hand-written JSON) to the ServerManagement channel. The webhook comes from Windows Credential Manager (`DarkCipher/discord/servermanagement`). Ledger: `%USERPROFILE%\.darkcipher\discord-posted.json` plus the posted message id. | Already in the ledger: skip |

## Kill switch, dry run, resume

- **Kill switch:** either the environment variable `DCX_PUBLISH_OFF=1`, or the file
  `%USERPROFILE%\.darkcipher\publish-off`, turns off steps 4 to 8 for every run. The local build still completes. The
  file form works across sessions and apps without editing anything. Each skipped step is recorded as
  "skipped: kill switch".
- **Dry run:** `.\build.ps1 -DryRun` (or a step script with `-DryRun`) does everything into a scratch folder and shows
  the commit, tag, release notes, assets, website manifest and Discord card it would publish (an HTML preview), then
  stops. Nothing leaves the PC.
- **Record and resume:** `dist\<ver>\release-state.json` records each step with its time, result and evidence (commit,
  tag, release URL, digests, message id). `.\tools\release.ps1 -Version <ver> -Resume` continues from the first step
  that didn't finish. A failed step never undoes earlier ones.

## Failure behaviour

| Step | On failure | State left | Resume |
|------|-----------|------------|--------|
| 1-3 | The build stops, nothing is published | `dist\<ver>` partial (the version is never rebuilt, so bump it) | Fix and bump |
| 4 | Abort, findings listed | Nothing pushed | Fix the source, rebuild under a new version |
| 5 | Push refused or failed | Local commit and tag waiting in the clone | `-Resume` pushes them |
| 6 | Upload failed or digest mismatch | Draft or partial release; it's reported and never auto-deleted | `-Resume` finishes it, or DarkCipher0x deletes it and resumes |
| 7 | Mismatch | Website not updated, or flagged as wrong | The Website session redeploys from the hand-off folder |
| 8 | Post failed | Not in the ledger | `-Resume` posts it |

## Rollback

- **Mirror:** a new commit that restores the previous tree (no force push without DarkCipher0x's go). Tags stay.
- **Release:** mark it as a pre-release, or delete it (DarkCipher0x's go), then release a fixed version.
- **Website:** the Website session's release snapshot and restore. The full rollback hasn't been exercised yet, so it
  should be tested once.
- **Discord:** delete the message using the stored message id.

## Trade-offs and risks

- Automatic publishing could push something private the moment a check misses it. The mitigations are layered: the
  allowlist (new files fail instead of shipping), privacy terms, the image gate, gitleaks, the origin check, and the
  kill switch. The repo is private until DarkCipher0x makes it public.
- The allowlist adds friction: every new file type or folder needs one line in `public-allowlist.txt`. That is
  deliberate.
- Relying on gitleaks means one more pinned binary to update. Its hash is checked on every run.
- The website stays a hand-off plus verification, not an automatic deploy, until a restricted deploy user exists.

## DarkCipher0x's decisions (2026-10-21)

1. **Design approved** ("this is how I want to publish").
2. **Website: off.** ServerManagement stays "coming soon" on darkciphersystems.com until DarkCipher0x launches it
   (`tools\release.json` "website.enabled": false). No download page, no update feed until then.
3. **Discord: off** until launch ("discord.enabled": false).
4. **Maintainers' notes:** public until 0.6.8; **private since 0.6.9**, the public beta (`docs\RESUME-*`, `PLAN-*`,
   `OUTSTANDING.md` and `STEAM-DISTRIBUTION.md` are on the private list, and the guides no longer embed the hand-test
   checklist).
5. **GitHub Releases back-filled for v0.5.3 only** (0.5.2 stays a tag).
6. **Builds keep pushing** (no `-NoPublish` default); the kill switch is there to stop it.

## As built

| Piece | File |
|-------|------|
| Entry point (build.ps1 step 7 calls it; run by hand to publish, resume or rehearse) | `tools\release.ps1 -Version <ver> [-DryRun] [-Resume] [-Backfill] [-NotLatest]` |
| Settings: repository, branch, author, release assets, website/Discord switches | `tools\release.json` |
| Shared helpers: kill switch, release record, origin check, GitHub sign-in, allowlist globs | `tools\release-common.ps1` |
| Pre-publish checks (csproj, MSI ProductVersion via msi.dll, exe in the portable zip, update.json, notes, README, assets, every checksum) | `tools\release-check.ps1` |
| README badge and history entry, from the release notes | `tools\write-readme-version.ps1` |
| Allowlist (unlisted files fail the build) | `tools\public-allowlist.txt`, used by `tools\write-public-source.ps1` |
| Secret scan (project rules; gitleaks once its pin exists) | `tools\secret-scan.ps1`, `tools\secret-patterns.txt` |
| Mirror push and annotated tag (idempotent by git tree hash) | `tools\publish-public.ps1` |
| GitHub Release (draft, verified uploads, then published; a published release is never edited) | `tools\publish-github-release.ps1` |
| Release record | `dist\<ver>\release-state.json` |
| Update manifest signatures (0.6.12): `update.json.sig` (ECDSA P-256) and `update.json.pq.sig` (ML-DSA-65), both release assets; `release-check.ps1` verifies both against the keys pinned in `Updater.cs` | `tools\sign-update.ps1`, `tools\release.json` "releaseAssets" |
| Website hand-off (0.6.9): `dist\<ver>\website\` with the downloads, SHA256SUMS, the release notes, `release-manifest.json` (the AgentControl / MaskedMail shape) and the Discord card; GitHub fields "pending" until the release is published, so run it again afterwards | `tools\website-handoff.ps1 -Version <ver> [-DryRun]` |
| Live-site check after the Website session's deploy (every site_path downloaded, SHA-256 compared; records "website" done) | `tools\verify-website.ps1 -Version <ver>` |
| Add-ons are never published (DarkCipher0x, 2026-10-24): `release-check.ps1` refuses an add-on among the release assets and any `.dcxaddon` / `dist/` entry in the source zip; the hand-off carries only the installers, the user guide and the source | `tools\release-check.ps1`, `tools\website-handoff.ps1` |

Test-fixture lines that must hold a pattern-shaped fake (the self-test's redaction tests) carry
`// secret-scan: example`; the scanner skips only those lines.

**Still open:** gitleaks. The scan runs the project rules now; gitleaks joins it once DarkCipher0x OKs downloading one pinned
release (`tools\gitleaks.pin` records its version and SHA-256; the binary lives in `%LOCALAPPDATA%\DarkCipher\tools`).
The website hand-off is implemented (0.6.9, for the public beta launch). Discord: the hand-off writes the card; it is
posted with the website's `announce-discord.ps1` (preview first) once the site is live and DarkCipher0x says go, never by
`release.ps1`. 0.6.9 is built with publishing held (`build.ps1 -NoPublish`) until DarkCipher0x's go; then
`tools\release.ps1 -Version 0.6.9 -Resume` publishes it.
