# Real-UI test record (guard 7, ADR-123)

Copy this file to `dist\<ver>\real-ui-test.md` once you have tested the release's changed feature in the real app,
fill it in, and run `tools\release-check.ps1 -Version <ver>`. Publishing (`tools\release.ps1` without `-DryRun`) refuses
a version without a passing record for this exact exe. A harness test alone is "unverified in the real UI".

Version: <ver>
Exe SHA-256: <SHA-256 of DcxServerManager.exe from this version's portable zip (or the installed copy from its MSI)>
Tester: <name>
Date: <yyyy-mm-dd>
Installed from: <MSI | Setup.exe | portable zip>, data folder: <scratch DCX_DATA_DIR | normal>

## Cases (the changed feature; replace with the release's own)
- [ ] Ctrl+Shift+V, one line: pasted once, no confirmation
- [ ] Ctrl+Shift+V, several lines: one confirmation, pasted once
- [ ] Ctrl+Shift+V held down: one paste (and at most one confirmation)
- [ ] Shift+Insert, Ctrl+V, right-click / PuTTY-style paste: each pastes once
- [ ] Notes: <anything odd>

Result: <PASS | FAIL>
