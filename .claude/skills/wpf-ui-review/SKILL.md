---
name: wpf-ui-review
description: Review Command Center's --capture screenshots against the approved design rules (plan section 3-c, the 43-item list, the blur test and the mirror test) and report what fails. Use when a page's captures are ready, before the user sees them. It reports only and changes nothing.
---

# WPF UI review

Command Center's pages are approved by the user from real screenshots. This review runs first, by a fresh agent that
did not build the page, so that generic or broken design is caught before the user spends time on it. It never edits
code or files: it reports.

## Inputs

- A folder of captures made by `tools\Capture-Pages.ps1` (or `CommandCenter.exe --capture`), and the pages to review.
  Review only the pages the change touched; images are expensive to read.
- Blurred copies for the blur test:
  `dotnet run --file tools/Blur-Captures.cs -- <captures folder> [<out folder>]` (default `<captures>\blur`).
- For colour values, read the Theme resource dictionaries (`src/CommandCenter.App/Theme/`), not the pixels: the token
  tests already prove their contrast, so the review checks that the right token is used in the right place.

## How to review

1. Look at each capture at full size, then at its blurred copy.
2. Walk the checklist below, item by item. Note only real failures you can point to on the screen.
3. Do the blur test and the mirror test.
4. Write the report (format below). Rank the findings: **blocker** (breaks a rule the user approved, such as contrast,
   the one-gold-element rule, colour as the only signal, or a fake number), **major** (clearly against an item),
   **minor** (polish).

## The checklist (plan 3-c, 43 items)

**Hierarchy**
1. Blurred, the screen shows one focal point (PLAY on Home, the main action elsewhere).
2. At most three text sizes on the screen.
3. At most two large elements.
4. One distinctive object per screen, and no "number cards" (big numbers in boxes).
5. Highlight colour covers at most 5% of the window.

**Layout**
6. Everything sits on a 4 px grid.
7. The space between groups is larger than the space inside a group.
8. No card inside a card.
9. Similar items (maps, replays) are lists or tables, not grids of cards.
10. Not every section is separated the same way (variety in grouping, not boxes everywhere).

**Type**
11. At most two font families (Barlow with its condensed cut counts as one family; JetBrains Mono is for data).
12. Sizes step by a ratio of 1.125 to 1.2 (PLAY excepted).
13. No text under 12 px.
14. No spaced-out capital labels above headings (eyebrows).
15. The monospaced face shows only real data, with tabular figures.

**Colour**
16. Text has at least 4.5:1 contrast with what is behind it, art included (the scrim must make this true).
17. Controls, icons and the focus ring have at least 3:1.
18. Greys carry a slight tint (the gunmetal of the tokens), not pure grey.
19. No purple or blue gradients, and no coloured glow (the soft gold glow behind PLAY is the one allowed glow).
20. Not the known pattern "near-black background with one sharp green or crimson accent".
21. A state is never shown by colour alone: it has an icon and words.

**Icons**
22. One family, and no emoji.
23. The line weight matches the text next to it.
24. The same perspective, detail and corners throughout.
25. Drawn at their real size (16, 20 or 24) and crisp, not blurry from scaling.
26. A live area of 20 inside 24.
27. Filled means selected or active; outline means navigation.
28. A visible label next to every icon that is not universal.
29. One concept, one icon, everywhere.
30. No icon inside a coloured circle.
31. Every click target is at least 32 px.

**Images**
32. Real game art from the player's game, not illustrations, stock shapes or empty gradients.
33. A steady scrim keeps the text readable over the art.

**Motion** (judge from the page's code or a short run when the capture cannot show it)
34. Motion only shows a change of state, within 150 to 250 ms.
35. No fade-up of every panel, no lift of every item on hover, no bounce. The one continuous motion is Home's slow zoom.

**Words**
36. Specific verbs ("Launch Zero Hour", "Install map"), and the same name for the same action everywhere.
37. No chains of middle dots, and no arrow glued to a word.
38. No invented numbers: every number is real data from the game, the server or the player's files.
39. Every error says what happened and what to do.

**States**
40. Every area that shows data has a loading, an empty, an error and a full state (check the captures that show them,
    or ask for them).
41. Every control has default, hover, focus, pressed and disabled looks.

**Command Center's own rules** (plan 3-c decisions)
42. One element filled with gold per screen: PLAY or the main action. Other gold is light and edges only (the active
    tab line, links, the focus ring, the glow behind PLAY).
43. **Mirror test:** imagine another game's logo and art in place of Zero Hour's. If the screen would still fit as it
    is, it is generic: report it, and name at least one decision that would tie it to Zero Hour.

## The blur test

Open the blurred copy. Say what the eye goes to first, second and third. It must be the page's main action first
(PLAY on Home). Two things competing for first place, or a decorative element winning, is a finding.

## The mirror test

Item 43 above. Be concrete: name what is generic (for example a plain dark panel layout any launcher could have) and
what would make it Zero Hour's (its art as material, the briefing language, the faction details the game itself uses).

## Report format

```
# wpf-ui-review: <pages>, <captures folder>

Blockers: N, majors: N, minors: N

## Findings
1. [blocker] <page>, <where on the screen>, item <n>: <what fails and why>
2. ...

## Blur test
<page>: first <...>, then <...>, then <...>. <pass, or the finding>

## Mirror test
<page>: <pass, or what is generic and one decision that would fix it>
```

Report what fails, with enough detail to fix it; do not restate passing items one by one, and do not propose new
features. If a capture cannot show something (motion, hover), say so instead of guessing.
