# Third-party skills

These skills are copied unchanged from the upstream repositories below, each pinned to one commit.
Every skill folder also holds a copy of its upstream `LICENSE` (all three upstreams are MIT).
On 2026-11-04 every file was checked against the git blob hash recorded at the pinned commit, and the
scripts were reviewed: the `security-audit` validators (`validate-*.cjs`) only read local files.

| Skill | Upstream repository | Pinned commit | Upstream path |
|---|---|---|---|
| `grilling` | [mattpocock/skills](https://github.com/mattpocock/skills) | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/productivity/grilling` |
| `grill-with-docs` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/grill-with-docs` |
| `domain-modeling` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/domain-modeling` |
| `tdd` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/tdd` |
| `diagnosing-bugs` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/diagnosing-bugs` |
| `codebase-design` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/codebase-design` |
| `improve-codebase-architecture` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/improve-codebase-architecture` |
| `to-spec` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/to-spec` |
| `to-tickets` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/to-tickets` |
| `setup-matt-pocock-skills` | mattpocock/skills | `4ae2317ddb22b046e26455e4f9c9b3aaaf62212d` | `skills/engineering/setup-matt-pocock-skills` |
| `systematic-debugging` | [obra/superpowers](https://github.com/obra/superpowers) | `22f0b463e7a5e579904dd6211a520321035526f7` | `skills/systematic-debugging` |
| `verification-before-completion` | obra/superpowers | `22f0b463e7a5e579904dd6211a520321035526f7` | `skills/verification-before-completion` |
| `security-audit` | [cloudflare/security-audit-skill](https://github.com/cloudflare/security-audit-skill) | `c1c8a8c1471069fb0e188eeaff69b8e8db6564a8` | `skills/security-audit` |

## Notes

- `grill-with-docs`, `improve-codebase-architecture`, `setup-matt-pocock-skills`, `to-spec` and `to-tickets`
  set `disable-model-invocation: true` upstream. Claude does not start them on its own. They run when the user
  types their slash command (for example `/grill-with-docs`), or when Claude reads and follows their `SKILL.md`
  as `CLAUDE.md` directs.
- The Matt Pocock engineering skills expect `setup-matt-pocock-skills` to run once before first use.
- `wpf-ui-review` is not third-party. It will be written for this project before the first visual change (phase 5).

## Updating a skill

1. Download the folder at a newer upstream commit into a scratch folder outside the repository.
2. Verify each file against the upstream git tree, and review the diff, especially scripts.
3. Replace the folder here, keep its `LICENSE`, and update the commit in the table above.
