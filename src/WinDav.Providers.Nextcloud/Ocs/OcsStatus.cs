// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace WinDav.Providers.Nextcloud.Ocs;

// The meta object of an OCS envelope: what the server made of the call, in its own words.
// Version 2 puts the HTTP status in here as well, so a failure that arrived as a 200 is
// still recognisable.
internal sealed class OcsStatus
{
    // Spelled without a capital in the middle, which the camel case policy would not arrive
    // at on its own.
    [JsonPropertyName("statuscode")]
    public int StatusCode { get; init; }

    public string? Message { get; init; }
}
