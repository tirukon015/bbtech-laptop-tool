# Day 2 — 2026-10-05

**Developer:** Touhidul Islam Rukon (with Claude Code)

## Objective

Make the codebase sturdier before the 1.0.0 acceptance tests:
- fix real defects found by reviewing the Core library;
- widen automated test coverage;
- add repository tooling (line endings, code style, CI);
- bring the documentation in line with the code;
- create the private GitHub repository and push the history.

## Work Completed

- Reviewed every file in `src/BBTech.Core` and the relevant app code (settings, export, logging, history views).
- Fixed 27 defects and made 1 performance improvement and 1 security improvement. Core library fixes were committed together with a regression test; the three WPF-side locale fixes (export file names, log timestamps, UI dates) were verified by building the app, because the UI project has no unit tests.
- Added 363 test cases (224 → 587), committed in small steps.
- Added `.gitattributes`, `.editorconfig` and a GitHub Actions workflow.
- Updated 17 documentation files.
- Created the private repository `tirukon015/bbtech-laptop-tool`.

## Features Added

No new user-facing features. The new pieces are infrastructure:
- the CI workflow (`.github/workflows/ci.yml`);
- the `DisplayFormat` helper for region-independent UI dates.

## Bugs Fixed

| Area | Fix |
|---|---|
| Operators | Renaming or reactivating can no longer produce two active operators with the same name |
| Admin PIN | Only ASCII digits (Bengali, Arabic-Indic and full-width digits were accepted by `char.IsDigit`); a damaged salt or hash fails verification instead of throwing |
| Locale | DETECT CSV timestamp, DETECT export file names, log timestamps and UI dates no longer depend on the Windows region (Thai calendar gave year 2569) |
| Wi-Fi | WPA3 rejects 64-hex keys; malformed SSID hex no longer hides a profile; unchanged saves keep the version; native API exceptions and a stopped WLAN service give friendly failures |
| QC store | Validation of status labels, overall results, Wi-Fi records and null ids; duplicate device records refused; unknown devices reported clearly; journal readable when the tail cannot be repaired |
| Detection | 1st-gen Intel "Q 720" / "X 920" names get "1st Gen" |
| Reports | History rows tolerate missing results and duplicate selections; `DateTimeOffset` written as ISO 8601 |
| Other | No `.tmp` left after a failed atomic write; clear exception for `Encrypt(null)`; `CompletedAt` notifies only on change |

## Refactoring

- `TestCatalog.All` is built once (performance).
- UI dates go through `DisplayFormat`.
- An outdated XML doc comment on the overall result was corrected, and a misplaced doc comment in the exporters was moved to the right class.

## Documentation Updated

- **Guides:** TESTING, SECURITY, OPERATOR-MANAGEMENT, WIFI-MANAGER, REPORTS, DATA-STORAGE, DEVICE-IDENTITY, DETECT, DEVELOPMENT, BUILD, TROUBLESHOOTING, PERFORMANCE, RELEASE.
- **Root and logs:** README, PROJECT-OWNERSHIP, CHANGELOG and this log.

## Tests

- **Unit tests:** `dotnet test tests\BBTech.Core.Tests -c Release` → **587 passed, 0 failed** (358 test methods).
- **New suites:** JournalTests, KeyVaultTests, CryptoTests, SettingsStoreTests, OperatorRulesTests, WifiSettingsTests, WlanProfileXmlTests, WifiProvisionerTests, ReportFormatTests, CsvExportTests, HistoryReportTests, IdentityRulesTests, ParsingEdgeTests, DetectNamingTests, TestModelTests, TestCatalogTests, RepositoryRulesTests.
- **Assumptions corrected while writing tests:**
  - An all-caps 4-letter vendor name is kept as an acronym by design, so the test data was adjusted rather than the code.
  - One test string had been mangled by an encoding problem in a helper script; it was fixed in its own commit.

## Build

- `dotnet build src/BBTech.LaptopTool` (Debug and Release, with the embedded tool bundle): **succeeded, 0 warnings**. The Release EXE is 50.8 MB.
- **Smoke test:** the Release build was launched once; the main window opened and the app closed cleanly.
- **Not rebuilt:** `build\build.ps1` (copy to `dist\release`). A running copy of the app (started by the user) holds `dist\release\BBTech Laptop Tool.exe`, and it was not closed. `dist\release` therefore still contains the 2026-10-04 build.
- **CI:** the first GitHub Actions run starts with the push. Its result was not known when this log was written.

## Security Changes

- CSV formula neutralisation.
- ASCII-only admin PIN; damaged PIN data never unlocks.
- Wi-Fi error messages never include the profile XML.
- **Secret review before pushing:**
  - `git grep` for keys, tokens, private keys and the real development serial found only dummy test passwords (`example-pass`, `changed-pass`, `Password123`).
  - No `.env`, key, journal, settings, report, EXE or media files are tracked.

## Git Statistics

| | |
|---|---|
| Starting commit count | 6 |
| New commits created | 235 |
| Ending commit count | 241 |

The plan expected 0 at the start of day 2, but 6 commits from 2026-10-04 already existed. The lead developer decided to keep them and end the day at 241. The first 3 commits of today were made before that decision and are included in the 235.

## Important Decisions

- Keep the existing history; no rewriting.
- The repository is **private**; CI minutes on private repositories are billed against the account allowance.
- Each commit is a real change. Test batches were run together first and then committed one test per commit, so every commit contains only passing tests.
- Running user processes are never closed by the tool session.

## Known Issues

- Real-hardware acceptance is still pending: a real pendrive, a second laptop, Windows 10, Dell/HP/AMD, real DPI scaling, Wi-Fi auto-reconnect ([TESTING.md](../TESTING.md) §3).
- `dist\release` holds the 2026-10-04 build until `build\build.ps1` is rerun with the app closed.
- Test data from development remains in `C:\ProgramData\BBTech\LaptopTool` on the development laptop.
- The EXE is not code-signed.

## Next Development Phase

- Check the first CI run and fix anything specific to the CI environment.
- Rebuild `dist\release` with the app closed.
- Review the WPF views and services (DETECT, TEST, Wi-Fi, Settings) for error handling, accessibility and DPI behaviour.
- Start the real-hardware acceptance checklist.
