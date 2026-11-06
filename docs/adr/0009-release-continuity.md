# Keep the forced-update path intact across every release

Older copies must update before they run, so every release reaches every user, and a broken release breaks
everyone. These never change:

- the repository
- the asset names: `CommandCenter.exe`, `update.json`, `CommandCenter-Setup-<ver>.exe` and `dxwrapper_go.zip`
- the shape of the portable exe: x86 and framework-dependent

Each version reads and migrates the data of the versions before it.

Every release goes out first as a pre-release. It is tested with `--update-url`, updating from 0.4.0 and from the
previous release. The replaced program is kept as `.old.exe` until the new one has started successfully, so it can
be rolled back. ~~0.5.0 carries the smallest change that makes the next updates safe.~~ (Replaced on 2026-11-06, see
Release order.)

Decided with the user on 2026-11-04 (plan sections 6 and 10).

## Release order

Changed with the user on 2026-11-06: no version is released before the complete one. The design phases (5 and 6) come
before the installer and the release (phase 4), so the first release after 0.4.0 carries the new design together with
all of phase 3's protections, and reaches every player in one forced update.

## Rollback

Decided with the user on 2026-11-05 (phase 3, P3-D1), the simpler form, without a watchdog:

- `.old.exe` is removed once the new version has stayed open for 30 seconds or was closed normally.
- If Windows cannot start the new program at all (missing, blocked by an antivirus, not a valid program), the
  previous one is put back at once and that version is not offered again in that run.
- A new version that starts and then fails is put back by hand, as README and the release notes explain.
- In a folder that needs administrator rights, a copy of Command Center started through Windows' prompt does the
  replace or the put-back and nothing else. It is the only elevation in Command Center.
