# 0.2.0 baseline (phase 0 of docs/history/PLAN-0.2.0.md)

Measured 2026-10-20 on the dev PC from the tree as committed in the initial git commit (0.1.18 as shipped plus the
0.1.19 work in progress; csproj version 0.1.19). Phase 7 compares the finished 0.2.0 against these numbers.

| Measure | Value | Source |
|---|---|---|
| Watched self-test | 283 ok, 0 FAIL, no window, no dialog (after the BrowserView address fix below; the first run had 282 ok + 1 timing failure) | `tools\run-selftest-watch.ps1`, Debug build |
| Self-test duration | about 6 minutes | the watch script |
| Working set after start | 227 MB private, 2406 handles, 39 threads (start of the stress unit, AI on) | the "resources over the whole stress run" line |
| Debug exe size | 9,659,392 bytes (`DcxServerManager.exe`, framework-dependent) | `bin\Debug` |
| Release single-file exe | ~69 MB in the portable zip / MSI of 0.1.18 | `dist\0.1.18` |
| `app.capabilities` | 44 names | `capabilities-0.1.18.txt` (the add-on compatibility fixture for phase 2) |
| Cold start time | not measured: the self-test does not record it and the app is never started with a window by a session. Phase 7 adds a start-timing line to the self-test report. | — |
| Source | 55k lines in one project, 311 tracked files | `git ls-files` |

**Test made robust by the baseline runs:** `SelfTestStress.cs` terminal-bridge fuzzing now gives xterm.js 10 s (was 5)
to render a marker after 60 random chunks; the second run tripped the 5 s limit while the page was still parsing them.

**Fix found by the baseline run:** `Views\BrowserView.cs` `CurrentUrl` read the WPF control's `Source`, which lags
`CoreWebView2.Source` at `NavigationCompleted`; the address bar could show the previous page for a moment (the portal
tab test caught it). It now reads the core's source first.
