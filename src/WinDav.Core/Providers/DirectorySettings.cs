// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Providers;

/// <summary>
/// How far ahead of a person a mount may list, how much of that it may do at a time, how
/// many listings it may hold, and when a name that is nowhere stops buying one.
/// </summary>
/// <remarks>
/// <para>
/// Four numbers, and every one of them a permission rather than a promise. Nothing here says
/// that a directory will have been listed before somebody opens it.
/// </para>
/// <para>
/// The numbers come from what was counted over a real account in
/// <see href="https://github.com/ernolf/WinDAV/issues/27">#27</see>, and each of them can be
/// set to the value that switches it off. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#76-listings-are-kept-an-etag-says-whether-they-still-hold-and-f5-throws-them-away">decision 76</see>.
/// </para>
/// </remarks>
public sealed class DirectorySettings
{
    /// <summary>
    /// How many levels below a listed directory are listed as well, by default.
    /// </summary>
    /// <remarks>
    /// One. Opening a directory should find its children already there, and the level below
    /// them is a level nobody is looking at yet. Every further level multiplies what a single
    /// listing may set off, and the gain from it stops at the window that is open.
    /// </remarks>
    public const int DefaultDepth = 1;

    /// <summary>
    /// How many requests one round of listing ahead may make, by default.
    /// </summary>
    /// <remarks>
    /// The most subdirectories in any one directory of the account measured for #27 is 28, so
    /// this covers everything found there and still catches the shape that was feared: a
    /// thousand subdirectories would otherwise be a thousand requests and some four minutes.
    /// It counts requests, never entries, because the width of a directory costs about
    /// 0.7 milliseconds an entry and a request costs about 160.
    /// </remarks>
    public const int DefaultRequests = 32;

    /// <summary>
    /// How many listings may be held at once, by default.
    /// </summary>
    /// <remarks>
    /// The whole account measured for #27 is 337 directories, so this holds one like it
    /// entire. What is held is a list of entries per directory and nothing else; it is in
    /// memory only and goes when the mount does.
    /// </remarks>
    public const int DefaultDirectories = 512;

    /// <summary>
    /// In how many directories a name must have been asked for and not found before a
    /// question about it stops buying a listing, by default.
    /// </summary>
    /// <remarks>
    /// One. Four runs at a live mount asked 2199 questions about a single name, and the 269
    /// of them that fell in a directory nothing had listed named nothing that was there. The
    /// names behind those were the same few, asked for in directory after directory and
    /// present in none: <c>.git</c>, <c>HEAD</c>, <c>Desktop.ini</c>, <c>AutoRun.inf</c>,
    /// <c>user32.dll</c>, <c>config</c>, <c>qtlogging.ini</c> and <c>vlc.mo</c>. A higher
    /// figure holds fewer of them and caught nothing more in the runs.
    /// </remarks>
    public const int DefaultProbes = 1;

    /// <summary>
    /// Gets how many levels below a listed directory are listed as well, or <c>0</c> for
    /// none, which leaves listings to be held but never fetched ahead of anybody.
    /// </summary>
    public int Depth { get; init; } = DefaultDepth;

    /// <summary>
    /// Gets how many requests one round of listing ahead may make, or <c>0</c> for none,
    /// which switches the listing ahead off as surely as a depth of nothing does.
    /// </summary>
    /// <remarks>
    /// A round is what one listing somebody waited for sets off. When the round is used up,
    /// what is left of it is dropped rather than carried over: the person has moved on by
    /// then, and the next thing they open starts a round of its own.
    /// </remarks>
    public int Requests { get; init; } = DefaultRequests;

    /// <summary>
    /// Gets how many listings may be held at once, or <c>0</c> for none at all, which takes
    /// the whole layer out and asks the store for every listing.
    /// </summary>
    /// <remarks>
    /// When there are more, the ones that have gone longest without being proven current are
    /// let go of first.
    /// </remarks>
    public int Directories { get; init; } = DefaultDirectories;

    /// <summary>
    /// Gets in how many directories a name must have been asked for and not found before a
    /// question about it in a directory nothing holds is answered as absent, or <c>0</c> for
    /// none at all, which lists the directory around every name as before.
    /// </summary>
    /// <remarks>
    /// A directory that is held answers out of its listing whatever this says. What is
    /// counted is the name, never the path, and it is held for the life of the mount and
    /// never written to disk.
    /// </remarks>
    public int Probes { get; init; } = DefaultProbes;
}
