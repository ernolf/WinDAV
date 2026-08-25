// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace WinDav.Core.Configuration;

// Source-generated rather than reflection-based. It costs nothing here, keeps the door to
// NativeAOT and trimming open, and moves a mistyped model from a run-time surprise to a
// build error.
//
// Comments are not allowed, on purpose. Reading them would mean losing them the next time
// the program writes the file, and a setting that explains itself through `windav config`
// is worth more than one explained in a comment that disappears.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ClientConfiguration))]
internal sealed partial class ConfigurationJson : JsonSerializerContext
{
}
