<!--
  SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
  SPDX-License-Identifier: GPL-3.0-or-later
-->

# WinDAV

> **In development, and not released.** The repository is public from the first commit so that the tooling can run in the open, not because any of it is ready to use.

A WebDAV client for Windows that mounts a remote share as a real drive letter.

Microsoft deprecated the WebClient service — the WebDAV mini-redirector — in November 2023. It no longer starts by default, Windows Server 2025 needs the redirector installed as a separate feature, and where it does run it is limited to two credential sets per host, has no RFC 4918 locking worth the name, and offers four registry values as its configuration surface. WinDAV is the replacement: a user-mode file system built on WinFsp, with as many mounts as you have drive letters, full Class 2 locking, and a cache that is invalidated rather than guessed.

The protocol layer is generic. Server-specific behaviour lives behind a provider seam, and the first provider is Nextcloud — chunked upload, OCS, Login Flow v2, live invalidation through `notify_push`.

## Where the project stands

The [wiki](https://github.com/ernolf/WinDAV/wiki) carries what WinDAV is and is not, and every decision the code rests on. What is built and what is not is in the [issues](https://github.com/ernolf/WinDAV/issues?q=is%3Aissue), open and closed, and in the [pull requests](https://github.com/ernolf/WinDAV/pulls?q=is%3Apr+is%3Aclosed) that closed them.

## Layout

| Project | |
| --- | --- |
| `src/WinDav.Abstractions` | the provider seam |
| `src/WinDav.Dav` | RFC 4918 on the wire |
| `src/WinDav.Core` | mounts, cache, configuration |
| `src/WinDav.Providers.WebDav` | plain WebDAV provider |
| `src/WinDav.Providers.Nextcloud` | Nextcloud provider |
| `src/WinDav.Fs` | WinFsp host |
| `src/WinDav.Cli` | the `windav` command |

## Building

Requires the .NET SDK pinned in `global.json`.

```
dotnet build WinDAV.slnx
dotnet test --solution WinDAV.slnx
```

## Licence

GPL-3.0-or-later. The repository follows the [REUSE](https://reuse.software/) specification; licence texts are in `LICENSES/`.
