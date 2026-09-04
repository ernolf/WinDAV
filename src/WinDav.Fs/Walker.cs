// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Fs;

/// <summary>
/// A program that opened something on this mount, and what it opened.
/// </summary>
/// <param name="ProcessId">The process WinFsp named as the one that asked.</param>
/// <param name="Program">
/// What that process is called, taken the first time it was seen. One that was already gone
/// by then has <see cref="OpenTally.Unnamed"/> here.
/// </param>
/// <param name="Opened">How many entries it opened.</param>
/// <param name="Directories">How many of those were directories.</param>
/// <param name="Waited">How long those opens took together.</param>
public readonly record struct Walker(
    int ProcessId,
    string Program,
    int Opened,
    int Directories,
    TimeSpan Waited);
