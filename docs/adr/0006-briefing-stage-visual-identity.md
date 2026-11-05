---
status: accepted
---

# Visual identity: Briefing stage, General's dossier, Generals Online gold as the only accent

The home page is a "Briefing stage":

- full-screen art read at run time from the player's own game files
- a scrim and a vignette over the art
- one large split PLAY button whose label follows the state: Play, Update, Fix or Locate game
- a status line under the button, and two quiet panels

The art changes on each launch between the title screen and the three faction loading screens, and the player can
pin one. The statistics page is a "General's dossier".

- **Colour.** Generals Online gold (`#FECD03`) is the only accent. It fills one element per screen.
- **Type.** Barlow Condensed, Barlow and JetBrains Mono, embedded under the OFL. No Fluent theme.
- **Motion.** Only for state changes. The one exception is a very slow zoom of the Home art (1.00 to 1.04 over
  about 25 s). It stops when the window is minimized or animation effects are off.
- **Sound.** Interface sounds come from the game and are off by default.

This replaces an earlier draft (adaptive faction colour, no images) that the user found dry and flat. Research into
single-game launchers and anti-slop guidance pointed to a hero stage around real game art. Near-black with one
sharp colour is a known generic pattern. The art is never shipped: Command Center reads it from the player's own
installation. The full tokens and rules are in the plan, section 3-C.

Decided with the user on 2026-11-04 (plan appendix B, phase 3).
