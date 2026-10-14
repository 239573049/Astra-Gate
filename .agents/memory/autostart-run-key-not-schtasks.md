---
name: autostart-run-key-not-schtasks
description: Server autostart on Windows moved from a schtasks logon task to the HKCU Run key (2026-10-14), and the server never loads/unloads its own entry
type: project
updated: 2026-10-14
source: session
evidence: ["src/Astra.Server/Autostart/AutostartService.cs:AutostartService", "src/Astra.Server/Autostart/WindowsConsole.cs", "cli/src/lib/autostart.ts:enableAutostartWindows"]
---
On 2026-10-14 the settings page got a "Start at login › Background service" switch. The server writes the same entries
as `astra autostart`, and on Windows both moved from `schtasks /Create /SC ONLOGON` to the per-user Run key value
`Astra Server`. The old task is still detected, and it is deleted on enable and disable.
- `schtasks` logon triggers need an elevated prompt, so the switch could never work from the server or a normal terminal.
  A task created from an admin prompt cannot always be deleted without one. Disable then fails with the command to run.
- The Run key starts the console binary in a visible window. `serve --started-by autostart` on Windows calls
  `FreeConsole` and sends stdout/stderr to `logs/server-stdout.log`. A brief console flash at login remains.
- The server never runs `launchctl load/unload` or `systemctl --now` on its own entry. Loading would start a second
  server, and unloading would stop the running one when launchd or systemd started it. The CLI still loads on enable.

**Why:** one switch shared by the CLI and the UI has to work for a standard user on every OS.
**How to apply:** do not reintroduce schtasks or a load/unload step in the server path. Hiding the console without the
flash (a GUI-subsystem launcher, for example) is the open improvement.
