// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Providers.Nextcloud.Ocs;

// The capabilities themselves, one section per app.
internal sealed class OcsServerCapabilities
{
    public OcsFilesCapabilities? Files { get; init; }
}
