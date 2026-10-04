# Security Policy

## Supported versions

| Version | Status |
|---|---|
| 1.0.0 | In development (not yet released) |

## Reporting a problem

This is a private BBTech repository. Report suspected security problems directly to the lead developer, Touhidul Islam Rukon (tirukon015@gmail.com). Do not open public issues.

Include the version, the Windows build, the steps to reproduce, and whether QC data, Wi-Fi credentials or the admin PIN could be exposed. **Never include real Wi-Fi passwords, PINs, serial numbers or customer data in a report.**

## What must never be committed

Wi-Fi passwords, admin PINs, settings files (`BBTech Data\config\settings.dat`), QC store files (`qc.journal`, `qc.key`), exported reports with real devices, logs from customer laptops, API keys or tokens.

The `.gitignore` excludes build outputs (`bin/`, `obj/`, `dist/`, `artifacts/`) and the imported third-party binaries (`third_party/`). Review `git status` and `git diff` before every commit and push.

## Security design

See [docs/SECURITY.md](docs/SECURITY.md) for the implemented mechanisms (encryption, key handling, admin PIN, Wi-Fi credential handling), the accepted trade-offs and the known limits.
