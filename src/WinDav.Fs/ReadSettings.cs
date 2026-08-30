// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Fs;

/// <summary>
/// How much a mount may fetch at once, and how much of it at the same time.
/// </summary>
/// <remarks>
/// <para>
/// Three numbers, and every one of them is a permission rather than a promise: how far ahead
/// of a read the mount <em>may</em> fetch, how much of that may be held across all open
/// handles together, and how many requests may be on the wire at the same time. Nothing here
/// says that a byte will be fetched before it is asked for.
/// </para>
/// <para>
/// The defaults come from the measurement in
/// <see href="https://github.com/ernolf/WinDAV/issues/26">#26</see>, and each of them can be
/// set to the value that switches it off, which is how a report about a wrong byte is
/// narrowed down to the layer that produced it. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#75-the-read-path-read-ahead-keep-attributes-briefly-and-let-the-server-set-the-width">decision 75</see>.
/// </para>
/// </remarks>
public sealed class ReadSettings
{
    /// <summary>
    /// How far ahead of a read a mount may fetch, by default.
    /// </summary>
    /// <remarks>
    /// A request costs about a quarter of a second before it costs a byte, whatever it asks
    /// for. Eight mebibytes at a time moved 15.86 MB/s against the server measured where one
    /// mebibyte at a time moved 4.18, and the server's own cost per megabyte fell from 0.173
    /// seconds of processor time to 0.027. Larger pieces still buy a little; eight is where
    /// the curve flattens and is small enough that a handle costs little to hold.
    /// </remarks>
    public const long DefaultWindow = 8L * 1024 * 1024;

    /// <summary>
    /// How much may be held in all windows together, by default.
    /// </summary>
    /// <remarks>
    /// Eight windows of the default size. A window is taken when a handle is first read from
    /// and given back when it is closed, so this is what a mount costs while several files
    /// are being read at once, not what it costs while it sits idle.
    /// </remarks>
    public const long DefaultTotal = 8 * DefaultWindow;

    /// <summary>
    /// How many requests one mount may have on the wire at the same time, by default.
    /// </summary>
    /// <remarks>
    /// The knee measured on a two-core server: from one to two the throughput doubles, from
    /// two to four a little is left, past four there is nothing, and the latency of every
    /// single request climbs the whole way. Measuring it against the server instead of taking
    /// this number is <see href="https://github.com/ernolf/WinDAV/issues/43">#43</see>.
    /// </remarks>
    public const int DefaultRequests = 2;

    /// <summary>
    /// Gets how far ahead of a read the mount may fetch, in bytes, or <c>0</c> for a request
    /// per read.
    /// </summary>
    /// <remarks>
    /// The window belongs to an open handle: it is filled by a read that continues where the
    /// last one ended, and a read that lands anywhere else is served by a request of its own.
    /// A file shorter than this is never held in more than the room it needs.
    /// </remarks>
    public long Window { get; init; } = DefaultWindow;

    /// <summary>
    /// Gets how much all windows of the mount may hold together, in bytes, or <c>0</c> for no
    /// ceiling at all.
    /// </summary>
    /// <remarks>
    /// Once this is used up, a handle that is read from gets no window and reads the way a
    /// mount with no window at all reads. Nothing fails and nothing waits; there is simply no
    /// room to read ahead into.
    /// </remarks>
    public long Total { get; init; } = DefaultTotal;

    /// <summary>
    /// Gets how many requests the mount may have on the wire at the same time. Never below
    /// one, and one is the value that switches the whole idea off.
    /// </summary>
    /// <remarks>
    /// This is a ceiling, not a target: nothing here starts a request in order to fill it.
    /// A server that answers <c>423</c> or <c>503</c> lowers what is in flight at once, and
    /// it is raised again slowly, never past this number.
    /// </remarks>
    public int Requests { get; init; } = DefaultRequests;
}
