# Build in feature layers, and keep an explicit list of what is out

Scope creep is one of the plan's named risks, so features come in layers:

1. **Layer 1, the foundation, comes before any new feature:** the Core/App split, the theme, MVVM, shared controls,
   removing Arabic with its migration, signed updates, single instance and the operation log.
2. **Layer 2:** personal statistics and the mismatch guard were moved up into this layer.
3. **Layer 3:** linking a replay to its client version, settings import and export, and crash data for testers.
4. **Layer 4, structure only:** several installations, mods through hard links, per-mod profiles and the
   TheSuperHackers client.

Out of scope:

- GenTool (its status is unresolved)
- downloading game files
- the camera exe
- website scraping
- a developer mode
- an isolated test copy for players. It stays only as an internal development tool: a temporary hard-link copy for
  integration tests.

Decided with the user on 2026-11-04 (plan appendix B, phases 1, 5 and 6; plan section 4).
