// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using WinDav.Providers.Nextcloud.Login;
using WinDav.Providers.Nextcloud.Ocs;

namespace WinDav.Providers.Nextcloud;

// Source-generated, as in the configuration: it keeps the door to NativeAOT and trimming
// open and turns a model that does not match the wire into a build error rather than a
// run-time surprise. Reading only - nothing here is ever sent as JSON.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OcsResponse))]
[JsonSerializable(typeof(OcsUser))]
[JsonSerializable(typeof(LoginFlowStart))]
[JsonSerializable(typeof(LoginFlowCredentials))]
internal sealed partial class NextcloudJson : JsonSerializerContext
{
}
