# Inno Setup installer into the game folder, next to a portable exe

Like Generals Online, Command Center installs into the game's root folder. The installer is built with Inno Setup
6.7.3:

- The folder page lists the copies it detects, plus Browse. It accepts only a folder that has `WindowZH.big` and
  `INIZH.big`.
- It asks for no admin rights when the folder is writable (Steam gives Users full control; this was checked).
  Otherwise it uses UAC and warns first.
- It checks for the x86 .NET 10 runtime and offers only Microsoft's download.
- Uninstalling removes Command Center's own files only.

The portable `CommandCenter.exe` stays a release asset, because it is the only way existing copies update. After the
automatic update to 0.5.0, those copies keep running where they are and offer "Install into your game folder" once.

Decided with the user on 2026-11-04 (plan appendix B, phase 6; plan section 6).
