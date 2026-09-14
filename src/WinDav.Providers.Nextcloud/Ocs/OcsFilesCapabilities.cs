// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace WinDav.Providers.Nextcloud.Ocs;

// What the files app states about itself.
internal sealed class OcsFilesCapabilities
{
    [JsonPropertyName("chunked_upload")]
    public OcsChunkedUpload? ChunkedUpload { get; init; }
}
