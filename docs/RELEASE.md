# Release Process

## Current status

**v1.0.0 is NOT released.**

- **Code:** `Directory.Build.props` already says `1.0.0` as the target version.
- **Not yet done:** no git tag, no GitHub release and no signed build exist.
- **Planned date:** 2026-10-09, after the real-hardware acceptance in [TESTING.md](TESTING.md) §3.

## Release checklist

1. **Acceptance:** all automated tests pass, and the real-hardware acceptance checklist is complete and recorded.
2. **CHANGELOG:** move `[1.0.0] — Unreleased` to `[1.0.0] — YYYY-MM-DD` with the actual date.
3. **Clean build:** close any running copy of the app, then run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File build\build.ps1
   ```
4. **Record the hash:** keep the SHA-256 from `dist\release.sha256.txt`.
5. **Secret review:** `git status` and `git diff` must show no secrets or customer data.
6. **Tag:**
   ```powershell
   git tag -a v1.0.0 -m "BBTech Laptop Tool 1.0.0"
   git push origin v1.0.0
   ```
7. **GitHub release** (optional): attach `BBTech Laptop Tool.exe` and the SHA-256. The repository is private; do not publish the EXE publicly, because it embeds third-party tools under their own licenses.
8. **Distribution:** copy the EXE to the BBTech USBs ([INSTALLATION.md](INSTALLATION.md)).

## Versioning

Semantic versioning:
- **MAJOR:** incompatible store or settings format changes.
- **MINOR:** new features or tests.
- **PATCH:** fixes.

The journal (`BBQCJ001`) and settings (`BBTS0001`) formats carry version markers; a format change must keep existing laptop histories readable.

## Not implemented

- **Code signing:** no certificate is configured, so Windows SmartScreen may warn.
- **Automated CI builds.**
