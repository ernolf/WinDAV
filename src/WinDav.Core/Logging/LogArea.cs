// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Logging;

/// <summary>
/// Where a record came from.
/// </summary>
/// <remarks>
/// The level says how loud, the area says where, and both are needed: the interesting
/// question is almost always what <see cref="Http"/> did while <see cref="Fs"/> was asking.
/// Four of them, not one per class, because an area is what a person switches on when they
/// are looking for something. See decisions.md 74.
/// </remarks>
public enum LogArea
{
    /// <summary>
    /// What a command did, and what it read or wrote in order to do it. The first value, so
    /// that a record that belongs to none of the others belongs here.
    /// </summary>
    Cli,

    /// <summary>What WinFsp asked of us, and what we answered.</summary>
    Fs,

    /// <summary>What went out on the wire, and what came back.</summary>
    Http,

    /// <summary>The seam between the two: what a provider was asked for, and what it made of it.</summary>
    Provider,
}
