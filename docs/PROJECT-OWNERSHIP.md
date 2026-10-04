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
| Development log (`docs/development/DAY-NN.md`) | DAY-01 written; later days are added only when they happen |
| Design documentation (`docs/`) | Present |
| Automated test results (224 passing on 2026-10-04) | Reproducible with `dotnet test` |
| Release builds with SHA-256 | Built locally; no published release yet |
| Signed commits (GPG/SSH) | **Not configured** |
| Git tags / GitHub releases | **None yet** |
| Remote repository push | **Not done yet** |

## Git history: honest note

- **Before git:** the code was developed in the working directory **before** the git repository was created. The first commits on 2026-10-04 therefore import already written code in a few logical groups. They do not show the step-by-step evolution.
- **No fabricated history:** no commits were back-dated, generated to reach a target count, or attributed to anyone who did not make them.
- **Commit count:** a target of "1,500 commits" was mentioned in the project brief. It was **not** pursued by artificial means. The count will be whatever real work produces.
