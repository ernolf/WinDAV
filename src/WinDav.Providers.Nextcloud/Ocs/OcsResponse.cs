// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace WinDav.Providers.Nextcloud.Ocs;

// The envelope every OCS answer comes in: one object named ocs, and everything the server
// has to say inside it.
internal sealed class OcsResponse
{
    [JsonPropertyName("ocs")]
    public OcsPayload? Ocs { get; init; }
}
