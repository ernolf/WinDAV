// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace WinDav.Providers.Nextcloud.Ocs;

// What is inside the envelope: what the server made of the call, and the answer to it. The
// answer stays unbound here on purpose. A call that failed carries an empty array where the
// object would have been, and binding that to a model would raise the wrong error before the
// status has been looked at.
internal sealed class OcsPayload
{
    public OcsStatus? Meta { get; init; }

    public JsonElement Data { get; init; }
}
