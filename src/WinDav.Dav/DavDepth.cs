// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Dav;

/// <summary>
/// How far into a collection a request reaches, sent as the <c>Depth</c> header of
/// RFC 4918 section 10.2.
/// </summary>
public enum DavDepth
{
    /// <summary>The resource itself and nothing below it.</summary>
    Zero,

    /// <summary>The resource and the members directly in it.</summary>
    One,

    /// <summary>
    /// The resource and everything below it, however deep. Servers are free to refuse
    /// this with 403, and many do, because the answer can be arbitrarily large.
    /// </summary>
    Infinity,
}
