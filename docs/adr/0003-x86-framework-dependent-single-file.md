# Ship a 32-bit, framework-dependent, single-file exe on Generals Online's runtime

Command Center targets x86 and runs on the .NET 10 Desktop Runtime (x86) that the Generals Online launcher already
installs. It is published as one framework-dependent `CommandCenter.exe` of a few megabytes.

Every installed copy updates itself by downloading that one asset, so its shape stays fixed: the same name, x86, and
framework-dependent. A 64-bit build would need a runtime that Generals Online does not install. A self-contained
build would be many times larger.

Decided with the user on 2026-11-04 (plan section 1, architecture; plan section 6).
