// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Logging;

/// <summary>
/// Why a recording stopped.
/// </summary>
/// <remarks>
/// There is no fourth way. A recording is never switched off by hand and never starts again
/// on its own, so whichever of these three is written in the closing line is the whole answer
/// to what happened to it. See decisions.md 74.
/// </remarks>
public enum LogRecordingEnd
{
    /// <summary>
    /// It is still running.
    /// </summary>
    None,

    /// <summary>
    /// The time it was given was up.
    /// </summary>
    Duration,

    /// <summary>
    /// It had written as much as it is allowed to write.
    /// </summary>
    Size,

    /// <summary>
    /// The program ended while it was still running.
    /// </summary>
    Session,
}
