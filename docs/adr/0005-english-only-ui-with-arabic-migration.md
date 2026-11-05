# English-only interface; remove the Arabic UI and the Arabic game text, with a mandatory migration

The interface becomes English only. These are removed:

- `Loc.T`, `LocText`, `{l:T}` and `ar.json`
- Arabic shaping, right-to-left layout and `--lang`
- the Arabic game text feature

Players who turned the Arabic game text on must not be left with it. 0.5.0 puts the game's English text back,
keeps their saved hotkeys, and removes `CommandCenterArabic.ini`. It does this with a backup and Undo, and it is
tested first on real files in an isolated copy.

The translation layer runs through every page that the MVVM and design work rewrite. The Arabic game text could
only ever cover part of the game: Generals Online's own screens stay in English, and Arabic player names show
reversed.

Decided with the user on 2026-11-04 (plan appendix B, phases 3 and 5).
