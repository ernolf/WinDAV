// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Fs;

/// <summary>
/// An icon overlay handler as the registry has it.
/// </summary>
/// <param name="Name">
/// The name the key has, leading spaces and all. They are there to win the sort, which is why
/// they are kept rather than trimmed away.
/// </param>
/// <param name="Clsid">The class the name points at.</param>
/// <param name="Module">
/// The library behind that class, as the registry names it, or <see langword="null"/> where the
/// class has no server registered.
/// </param>
/// <param name="Vendor">Who the module says it is from, where it says so.</param>
/// <param name="Present">
/// Whether the module is where the registry says it is, and <see langword="null"/> where the
/// registry names no place: a server registered by its bare name is one Windows finds through
/// the search path, and that says nothing about whether it is there.
/// </param>
/// <param name="Loaded">Whether Windows still has room for it.</param>
public readonly record struct ShellOverlay(
    string Name,
    string Clsid,
    string? Module,
    string? Vendor,
    bool? Present,
    bool Loaded);
