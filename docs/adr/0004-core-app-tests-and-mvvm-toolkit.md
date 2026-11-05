# Split into Core, App and Tests; move to MVVM page by page with CommunityToolkit.Mvvm as the only package

- The services move into `CommandCenter.Core` (net10.0-windows, no WPF).
- The WPF shell moves into `CommandCenter.App`.
- The tests move into `tests/`.

Pages move to MVVM one at a time behind characterization tests. A hand-written composition root wires them, not a
DI container. Today the pages are code-behind files of up to about 1,200 lines that do I/O on the UI thread. They
cannot be tested or redesigned safely as they are.

CommunityToolkit.Mvvm 8.4.2 is the only NuGet package in the product. Any other package needs a discussion with the
user and a new ADR.

Decided with the user on 2026-11-04 (plan appendix B, phase 5; plan section 1).
