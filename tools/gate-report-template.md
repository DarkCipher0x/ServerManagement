# ServerManagement <ver>: gate report

Every release produces `reports/<ver>-gate.md` from this template (guard 8, ADR-123); the reviewer checks it against the
guards. The guard table comes from `dist\<ver>\guard-results.md`, which `tools\release-check.ps1` writes on every run.

## 1. Summary
<one paragraph: what ships, what changed, verdict at the publish gate>

## 2. HEAD
- Branch, commit, tree; `main..HEAD` commit list (every commit, one line each)
- The clean clone it was built from (path, `git remote -v` gateway origin)

## 3. SHA256SUMS
<table of every line of dist\<ver>\DarkCipherServerManagement-<ver>-SHA256SUMS.txt>

## 4. Guard results
<paste dist\<ver>\guard-results.md: one row per guard - result and evidence>
- Negative tests: `tools\test-release-guards.ps1 -Version <ver>` summary (pass / fail per test)
- Paste regression: `tools\test-terminal-paste.ps1` (run by build.ps1, step paste-test)

## 5. Pipeline
- build.ps1 run id, steps (release-state.json "pipeline"), toolchain ("toolchain")
- Warm-up launch (step warm-up, its time) before the measured start-up; the start-up budget lines (AI on / AI off, against tools\budgets.json)
- release-check, `release.ps1 -DryRun` (DCX_PUBLISH_OFF=1) output summary; gitleaks result

## 6. Real-UI verification (guard 7)
<dist\<ver>\real-ui-test.md: tester, exe SHA-256, result> or "UNVERIFIED in the real UI"

## 7. Still unverified / blockers
