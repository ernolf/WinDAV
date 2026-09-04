// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Providers;

/// <summary>
/// A name that was asked for on this mount and was in no directory it was asked for in.
/// </summary>
/// <param name="Name">The name, without the directory it was asked for in.</param>
/// <param name="Asked">How often the answer was that there is nothing there.</param>
/// <param name="Listings">How many listings those answers bought.</param>
/// <remarks>
/// Counted per name and never per path, because the same name is what arrives in directory
/// after directory: that is what makes it a probe rather than somebody looking for a file.
/// See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#84-the-mount-says-who-walked-it-and-what-the-shell-has-registered">decision 84</see>.
/// </remarks>
public readonly record struct AbsentName(string Name, int Asked, int Listings);
