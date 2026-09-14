// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Providers.Nextcloud.Ocs;

// What is read out of the capabilities the server states. The answer holds the version and
// a section for every app that has something to say, and only what the files app says about
// uploads has a consumer here. What is not modelled is skipped on the way in.
internal sealed class OcsCapabilities
{
    public OcsServerCapabilities? Capabilities { get; init; }
}
