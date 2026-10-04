# Development Guide

## Setup

1. Windows 10/11 and the .NET SDK 8+.
2. Clone the repository, then import the toolkit once (see [BUILD.md](BUILD.md)).
3. Build and run:
   ```powershell
   dotnet build src\BBTech.LaptopTool -c Debug
   & "src\BBTech.LaptopTool\bin\Debug\net472\BBTech Laptop Tool.exe"
   dotnet test tests\BBTech.Core.Tests
   ```

## Where code goes

| Kind of change | Location |
|---|---|
| Parsing, identity, storage, crypto, settings, Wi-Fi XML, reports, test plans (no Windows UI) | `src/BBTech.Core/`, with unit tests in `tests/BBTech.Core.Tests/` |
| WMI / hardware access | `src/BBTech.LaptopTool/Detect/`, `Test/` |
| Services (paths, tool bundle, store, inspections) | `src/BBTech.LaptopTool/Services/` |
| Views and dialogs | `src/BBTech.LaptopTool/UI/` |

Core sources are linked into the app project, so the release stays a single EXE. The app project includes `..\BBTech.Core\**\*.cs` as linked files, so new Core files are picked up automatically.

## Adding a new hardware test

1. Add a value to the `HardwareTest` enum and its display name in `TestText`.
2. Add a panel derived from `TestPanelBase` in `UI/Tests/` and add a `case` for it in the panel factory in `UI/Tests/TestPanelBase.cs`.
3. The test automatically appears in **ALL TESTS** and in **+ Add Tests**, because `TestCatalog` is built from the enum. Add it to `TestCatalog.RequiredTests` only if BBTech makes it mandatory.
4. Add unit tests for any new parsing or decision logic.

## Conventions

- **Language:** C# `LangVersion latest` on .NET Framework 4.7.2. No new NuGet runtime dependencies (single-EXE requirement).
- **No automatic decisions:** a test never decides PASS by itself; the operator decides.
- **Unknown over guessed:** show **Unknown** rather than a guessed value (CPU generation, battery health, model).
- **Logging hygiene:** never log test results, operator names, serials, PINs, Wi-Fi passwords or SSID lists.
- **Text encoding:** keep source and docs as UTF-8. Do not round-trip files through Windows PowerShell 5 `Get-Content` / `Set-Content`, which corrupted characters once during development.
- **Placeholders:** use them in tests and docs (`SERIALEX01`, `SERIAL-EXAMPLE`, `UUID-EXAMPLE`, `BBTECH_EXAMPLE`, `[REDACTED]`), never real device serials or passwords.

## Repository settings

- **`.editorconfig`:** 4-space indentation (2 for project/XAML/YAML files), UTF-8, LF, `_camelCase` private instance fields and PascalCase static fields.
- **`.gitattributes`:** text files are stored with LF; images, video, archives and executables are binary.
- **CI:** `.github/workflows/ci.yml` runs the unit tests and builds the app on every push to `main` and every pull request.

## Writing tests

- One test class per area (for example `JournalTests`, `KeyVaultTests`, `WifiProvisionerTests`). Use temporary folders and delete them in `Dispose`.
- **Fakes, not real system state:** `FakeMachine` stands in for DPAPI and `ScriptedWlan` / `FakeWlan` for the Windows WLAN API, so tests never change the developer's Wi-Fi profiles or `ProgramData`.
- Tests that open the QC store are slow (PBKDF2 key derivation). Run a subset while iterating, e.g. `dotnet test --filter "FullyQualifiedName~JournalTests"`, and the whole suite before committing.
- Avoid locale-dependent expectations; when behaviour must not depend on the Windows region, test it under another culture (see `CsvExportTests`).

## Commits

- **Content:** one logical change per commit, with a clear message.
- **Before every commit or push:** run `git status` and `git diff --cached`. Never commit:
  - passwords, Wi-Fi passwords, API keys, tokens, private keys or `.env` files;
  - customer data or personal documents;
  - production or database credentials.
- **Never committed:** `third_party/`, `artifacts/` and `dist/` (ignored).
- **AI assistance:** commits produced with Claude Code carry a `Co-Authored-By` trailer.

## Development log

Daily notes go in `docs/development/DAY-NN.md`. Record only what actually happened that day.
