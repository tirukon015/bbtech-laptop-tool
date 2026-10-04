# Performance

## Design

- **Lazy start-up:** no hardware scan, WMI query or tool extraction at start-up. Work begins only when DETECT, TEST or Wi-Fi is chosen.
- **Splash screen:** a native WPF splash screen is shown while the runtime loads.
- **Background threads:** detection, history lookup and provisioning run off the UI thread.
- **On-demand tools:** embedded tools are extracted only when needed, into `%TEMP%`, and deleted on exit.
- **Idle:** no background timers when idle; live panels (meters, previews) stop when their window closes.
- **Audio:** a single audio owner (`AudioArbiter`) prevents overlapping playback.

## Measurements

Measured on the development laptop (ThinkPad T14 Gen 2i, Windows 11) on 2026-10-04. These are single-machine figures, not benchmarks.

| Metric | Value |
|---|---|
| Start-up to usable window | 250–510 ms (about 1.05 s on the first launch after copying the EXE) |
| Detection (DETECT) | ~1 s |
| CPU while idle | 0 % |
| Working set | ~100 MB |
| EXE size | 48.4 MB (≈ 30 MB without the speaker media) |

Not measured: slower laptops, HDD-based systems, or launching from a slow USB 2.0 pendrive, where the first launch of a 48 MB EXE takes longer.
