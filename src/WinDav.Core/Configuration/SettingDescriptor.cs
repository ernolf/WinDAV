// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Configuration;

/// <summary>
/// What one setting is, in the words a person needs to decide whether to change it.
/// </summary>
/// <remarks>
/// A setting nobody can understand without reading a manual elsewhere is a setting that
/// will be set wrong. Every entry carries its own explanation so the program can answer the
/// question rather than point at a document.
/// </remarks>
public sealed class SettingDescriptor
{
    /// <summary>
    /// Gets the setting's place in the file, written the way it appears there, with
    /// <c>[]</c> for an entry of a list — <c>mounts[].driveLetter</c>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets what the setting is, in one sentence.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Gets what changing it does.
    /// </summary>
    public required string Effect { get; init; }

    /// <summary>
    /// Gets the value that applies when the setting is absent, written as it would appear
    /// in the file.
    /// </summary>
    public required string DefaultValue { get; init; }

    /// <summary>
    /// Gets what the setting accepts, or <see langword="null"/> when the type says it all.
    /// </summary>
    public string? AllowedValues { get; init; }
}
