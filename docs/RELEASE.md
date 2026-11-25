# Release Process

## Single release location

```text
dist\release\BBTech Laptop Tool.exe
dist\release\BBTech Laptop Tool.exe.sha256.txt   (SHA-256, version, build time)
```

There is no other EXE in `dist\`. The build script removes old duplicates (`dist\BBTech Laptop Tool.exe`,
`dist\release.sha256.txt`, `*.old.exe`).

## Build

Close any running copy of the tool, then:

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

The script: packs the embedded tools → runs all client unit tests → builds Release with the git commit stamped
into the EXE (`1.4.0+<commit>`; `-dirty` when built from uncommitted changes) → replaces the release EXE (**fails**
if the old EXE cannot be replaced, so an old binary is never left next to a new checksum) → verifies the released
file equals the build output → writes the checksum file.

At start the tool logs its version and its own SHA-256 (`BBTech Data\logs\app.log`): compare it with the checksum
file to prove which binary is running.

## Release checklist

1. Client tests (`build.ps1`) and server tests (`dotnet test server\BBTech.Server.sln`) pass.
2. Commit everything, then build (the version must **not** end in `-dirty`).
3. Real-laptop acceptance ([TESTING.md](TESTING.md)) recorded.
4. `CHANGELOG.md` entry dated; `git tag -a v1.4.0`.
5. Copy only `BBTech Laptop Tool.exe` to the USBs; keep each USB's `BBTech Data\` (settings, queue, audit).
6. Server: deploy the API container and let it migrate ([SERVER.md](SERVER.md)).

## Versioning

Semantic versioning. Store formats carry version markers (`BBQCJ001` journal, `BBTS0001` settings, `BBTM0001` master
configuration); a format change must keep existing data readable.

## Not implemented

- Code signing (no certificate): Windows SmartScreen may warn.
- Automated packaging in CI (the third-party tools are not in the repository).
