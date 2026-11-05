# Keep the forced-update path intact across every release

Older copies must update before they run, so every release reaches every user, and a broken release breaks
everyone. These never change:

- the repository
- the asset names: `CommandCenter.exe`, `update.json`, `CommandCenter-Setup-<ver>.exe` and `dxwrapper_go.zip`
- the shape of the portable exe: x86 and framework-dependent

Each version reads and migrates the data of the versions before it.

Every release goes out first as a pre-release. It is tested with `--update-url`, updating from 0.4.0 and from the
previous release. The replaced program is kept as `.old.exe` until the new one has started successfully, so it can
be rolled back. 0.5.0 carries the smallest change that makes the next updates safe.

Decided with the user on 2026-11-04 (plan sections 6 and 10).
