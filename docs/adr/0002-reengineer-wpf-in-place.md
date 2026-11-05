# Re-engineer the existing WPF code in place instead of rewriting it

The logic of 0.4.0 is sound: backups and Undo, write guards, the BIG archive reader and strict profile validation.
That logic sits in very large code-behind files. We keep WPF, C# and .NET 10 LTS on Windows only, and improve the
existing code step by step behind characterization tests. Every release is forced on all users, so a rewrite would
put all of that proven behaviour at risk at once.

## Considered Options

- WinUI 3, Avalonia, Tauri or Rust. Rejected: each means a rewrite, a second language or a new UI stack, and this
  decision rules those out.

Decided with the user on 2026-11-04 (plan appendix B, phase 2; plan section 1).
