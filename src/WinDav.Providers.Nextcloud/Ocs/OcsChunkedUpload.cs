// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace WinDav.Providers.Nextcloud.Ocs;

// How a file may be sent in chunks, as the administrator set it up. A server states this
// from Nextcloud 31 on; before that there is no such section.
internal sealed class OcsChunkedUpload
{
    // The size of one chunk. The web interface reads zero or less as no chunks at all.
    [JsonPropertyName("max_size")]
    public long? MaxSize { get; init; }

    // How many chunks of one upload may be out at once.
    [JsonPropertyName("max_parallel_count")]
    public int? MaxParallelCount { get; init; }
}
