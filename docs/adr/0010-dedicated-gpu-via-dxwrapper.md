# Offer "Enable dedicated graphics card" through dxwrapper_go, instead of a test-environment installer

The earlier idea of installing Generals Online's test environment was a misunderstanding and is cancelled. What
players need is the dedicated graphics card on PCs with two of them, such as most laptops. Generals Online's client
loads `dxwrapper_go.dll` from its own folder when the file is there (`dx8wrapper.cpp:295-297`, inside
`GENERALS_ONLINE`). Choosing the graphics card in Windows or in the NVIDIA panel does not work without it.

How it works in Command Center:

- It detects two graphics cards through WMI. Only then does it show a Health check and an Options › Graphics switch.
- Turning it on writes `dxwrapper_go.dll` and `dxwrapper_go.ini` into the client's folder, while the game is closed.
  The files must match pinned SHA-256 hashes, only these two names are allowed, and the write goes through
  `BackupService`.
- Turning it off moves both files to quarantine.
- An `.ini` that the player edited is never replaced without a backup.

The files are a build of elishacloud/dxwrapper provided by the Generals Online team (confirmed by the user). Its zlib
licence allows redistribution, with attribution and with modified versions marked as such. The files ship as a
release asset together with `License.txt` and the credit "dxwrapper by elishacloud, build provided by the Generals
Online team". If Generals Online offers a stable download link later, it can replace the asset through the signed
`go-profile.json` (`dxwrapperUrl`, `dxwrapperSha256`).

The feature works with Generals Online clients only, not with the original `Generals.exe`.

Decided with the user on 2026-11-04 (plan appendix B, phase 1; plan section 4-B).
