# Vendored CipherChat engine

CipherChat's four UI-free libraries — `CipherChat.Crypto`, `CipherChat.Transport`, `CipherChat.Api` and
`CipherChat.Engine` — copied from the CipherChat commit recorded in `VENDOR.json`, with the SHA-256 of every file
(ADR-117). ServerManagement's chat (`src/Dcx.Chat`) is built on them and draws its own screens.

**Do not edit these files here.** A protocol or security change is made in CipherChat, tested there, and brought in with:

```
.\tools\sync-cipherchat.ps1 -From ..\CipherChat -Ref <commit or tag>
```

then reviewed (`git diff -- src/Vendor/CipherChat`), built and tested with both self-test suites. `build.ps1` runs
`tools\sync-cipherchat.ps1 -Check` and stops if any file differs from `VENDOR.json`.

They build with CipherChat's own settings (`CipherChat.Build.props`, verbatim from that commit). License: MIT
(`LICENSE`), the same author as ServerManagement.
