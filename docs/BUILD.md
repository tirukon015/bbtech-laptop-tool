# Build

## Build machine

- **Windows** with the **.NET SDK 8 or newer.** `build.ps1` uses `dotnet` from PATH, or else `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.
- **Not needed:** Visual Studio is optional; Node.js and Python are not used.
- **Toolkit ZIP:** a copy of BBTech's `TEST-Laptop.zip` (third-party tools). The ZIP is only read, never modified.

## 1. Import the third-party tools (one time)

```powershell
powershell -ExecutionPolicy Bypass -File build\import-toolkit.ps1 -Zip "C:\path\to\TEST-Laptop.zip"
```

- **Copies into `third_party\`:** only the approved tools (BatteryInfoView, Hard Disk Sentinel, Free PC Audit) and the speaker test media.
- **Never copied:** `Activation.bat` and other unrelated files.
- **Kept out of git:** `third_party\` is in `.gitignore`, because the binaries belong to their vendors.

## 2. Build

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1            # tests + EXE
powershell -ExecutionPolicy Bypass -File build\build.ps1 -NoMedia   # without the 18 MB speaker video
powershell -ExecutionPolicy Bypass -File build\build.ps1 -SkipTests
```

What `build.ps1` does:

1. **Packs the tools** into `artifacts\bundle\*.zip`. Hard Disk Sentinel is trimmed (help files, non-English languages and alert sounds are removed). It also copies `SpeakerTest.mp4` unless `-NoMedia` is given.
2. **Runs the unit tests:** `dotnet test tests\BBTech.Core.Tests -c Release`, with results in `dist\test-results\`. The build stops on any failure.
3. **Builds the WPF app** in Release. The bundle is embedded as `bundle.*` resources.
4. **Produces the release:** copies only `BBTech Laptop Tool.exe` to `dist\release\` and writes `dist\release.sha256.txt`.

**Output:** a single EXE of about 48 MB:

| Component | Size |
|---|---|
| HD Sentinel zip | ~28.3 MB |
| Speaker media | ~18.2 MB |
| Free PC Audit | ~0.5 MB |
| BatteryInfoView | ~0.1 MB |

**"File in use" error:** the build cannot replace `dist\release\BBTech Laptop Tool.exe` while that EXE is running. Close the app first.

## Continuous integration

GitHub Actions (`.github/workflows/ci.yml`, `windows-latest`, .NET SDK 8):

1. `dotnet test tests/BBTech.Core.Tests -c Release`, with results uploaded as the `test-results` artifact.
2. `dotnet build src/BBTech.LaptopTool -c Release`.

The CI build has **no embedded tools**: `third_party/` and `artifacts/` are not in the repository, and the project only embeds the bundle when `artifacts\bundle` exists. CI therefore checks that the code compiles and the tests pass. Release EXEs are still produced locally with `build.ps1`.

**Cost:** for a private repository, Actions minutes on Windows runners count against the account's monthly allowance.

## Branding assets

`build\make-branding.ps1` derives `Assets\bbtech.ico`, `Assets\Branding\logo-tile-*.png` and `splash.png` from `logo-master.png`. The master file is never modified. Run it only when the logo changes.

## Project layout

```
BBTechLaptopTool.sln
Directory.Build.props        Version (1.0.0), LangVersion
src/BBTech.Core/             Platform-independent logic (parsing, identity, storage, crypto, settings, Wi-Fi XML, reports)
src/BBTech.LaptopTool/       WPF application (WinExe "BBTech Laptop Tool"); links the Core sources into one EXE
tests/BBTech.Core.Tests/     xUnit tests for BBTech.Core
build/                       import-toolkit.ps1, build.ps1, make-branding.ps1
```

## Versioning

The version is set in `Directory.Build.props` (`<Version>`). Release steps are in [RELEASE.md](RELEASE.md).
