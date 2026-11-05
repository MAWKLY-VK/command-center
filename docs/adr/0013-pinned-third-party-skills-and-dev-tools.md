# Pin third-party agent skills inside the repository; trial Claude-Mem locally

The Claude Code tooling comes in four groups:

- **Process:** Matt Pocock's skills.
- **Docs and C#:** the Microsoft Learn MCP, and Microsoft's dotnet plugin as an experiment. The plugin is removed if
  its LSP tool never appears.
- **Security:** Cloudflare's security-audit and `/security-review`.
- **Design review:** `wpf-ui-review`, written for this project.

Third-party skills are copied into `.claude/skills/` at a pinned commit, with their licences, and listed in
`SOURCES.md`. They are not installed as live plugins, so an upstream change cannot alter how the agent works here
without a review.

Claude-Mem stays on trial with telemetry off. The user postponed the choice of its summarizing provider; it is
evaluated after phases 1 and 2.

Not used: Figma, Playwright, Chrome DevTools, the WinUI plugin, Context7, NuGet MCP and Security Guidance.

Decided with the user on 2026-11-04 (plan appendix B, phase 4; plan section 7).
