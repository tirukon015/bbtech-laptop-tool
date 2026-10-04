# Project Ownership and Development Evidence

> This document records development evidence. It is **not** a legal ownership or copyright determination. Questions of legal ownership (for example, employer vs. developer rights) are governed by agreements between BBTech and the developer, which are not part of this repository.

## Project

| | |
|---|---|
| Project | BBTech Laptop Tool |
| Organization | BBTech |
| Lead Developer / System Architect | Touhidul Islam Rukon |
| GitHub | [tirukon015](https://github.com/tirukon015) |
| Contact | tirukon015@gmail.com |
| Planned development period | 2026-10-04 – 2026-10-09 |
| License | None selected (private repository) |

## How the software was produced

- **Direction:** Touhidul Islam Rukon defined the requirements, made the design decisions and accepted the results. Decisions recorded during development include:
  - local-first encrypted QC history stored on each laptop;
  - no pre-sale deletion of history or Wi-Fi profiles;
  - the dual-locked key;
  - admin PIN and one-click Wi-Fi provisioning;
  - the REQUIRED / ALL TESTS modes;
  - the report naming format.
- **AI assistance:** the source code, tests, build scripts and documentation were generated with **Claude Code** (an AI coding assistant by Anthropic) working under that direction. AI-assisted commits carry a `Co-Authored-By: Claude …` trailer. The AI tool is not an author or owner.
- **Third-party components:** BatteryInfoView (NirSoft), Hard Disk Sentinel and Free PC Audit are embedded unmodified from BBTech's toolkit and remain the property of their vendors. They are not committed to this repository. See [CONTRIBUTORS.md](../CONTRIBUTORS.md).

## Evidence available in this repository

| Evidence | Status |
|---|---|
| Git history with author `Touhidul Islam Rukon <tirukon015@gmail.com>` | Starts 2026-10-04 |
| Development log (`docs/development/DAY-NN.md`) | DAY-01 and DAY-02 written; later days are added only when they happen |
| Design documentation (`docs/`) | Present |
| Automated test results (224 passing on 2026-10-04, 587 on 2026-10-05) | Reproducible with `dotnet test` and in GitHub Actions |
| Release builds with SHA-256 | Built locally; no published release yet |
| Signed commits (GPG/SSH) | **Not configured** |
| Git tags / GitHub releases | **None yet** |
| Remote repository | Private GitHub repository `tirukon015/bbtech-laptop-tool`, created 2026-10-05; the first push follows the day-2 commits |
| Continuous integration | GitHub Actions workflow added 2026-10-05 |

## Git history: honest note

- **Before git:** the code was developed in the working directory **before** the git repository was created. The first commits on 2026-10-04 therefore import already written code in a few logical groups. They do not show the step-by-step evolution.
- **No fabricated history:** no commits were back-dated, generated to reach a target count, or attributed to anyone who did not make them.
- **Commit schedule:** the lead developer set a six-day plan ending at 1,500 commits on `main` (241 by 2026-10-05, then 527, 724, 1,042, 1,265 and 1,500). The 6 commits of 2026-10-04 count toward it, so day 2 added 235 commits.
- **How the count is reached:** with small, granular commits, each a real change (a fix with its test, a single new test, a configuration or documentation change). There are no empty commits and no changes made only to raise the number. Test batches were written and run together first, then committed one test per commit, so every intermediate commit contains only tests that pass.
