// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace WinDav.Fs;

/// <summary>
/// Who walked the mount, counted per process.
/// </summary>
/// <remarks>
/// <para>
/// WinFsp hands the originating process through, and its own documentation says where: during
/// Create, Open and Rename, and only where the target exists. A directory somebody opens is
/// therefore attributable and a name that is not there never is, which is the whole reason
/// this counts opens and leaves the questions about absent names to the store underneath.
/// </para>
/// <para>
/// It does not separate what runs inside one process. Every icon overlay handler runs inside
/// <c>explorer.exe</c> and they all count as Explorer here; what this answers is whether the
/// program walking the tree is Explorer at all. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#84-the-mount-says-who-walked-it-and-what-the-shell-has-registered">decision 84</see>.
/// </para>
/// </remarks>
public sealed class OpenTally
{
    /// <summary>
    /// What stands where a process has no name: WinFsp had no id to hand through, or the
    /// process was gone before it was asked about.
    /// </summary>
    public const string Unnamed = "unknown";

    private readonly Lock _sync = new();

    private readonly Dictionary<int, Walker> _walkers = [];

    /// <summary>
    /// Counts one open.
    /// </summary>
    /// <param name="processId">Who asked, or zero where WinFsp had nobody to name.</param>
    /// <param name="directory">Whether what was opened is a directory.</param>
    /// <param name="waited">How long the open took.</param>
    public void Note(int processId, bool directory, TimeSpan waited)
    {
        lock (_sync)
        {
            if (!_walkers.TryGetValue(processId, out Walker walker))
            {
                // Asked once per process and never again. A crawl is hundreds of opens from
                // one process, and looking the name up every time would put a Windows call
                // in the path of every open.
                walker = new Walker(processId, NameOf(processId), 0, 0, TimeSpan.Zero);
            }

            _walkers[processId] = walker with
            {
                Opened = walker.Opened + 1,
                Directories = walker.Directories + (directory ? 1 : 0),
                Waited = walker.Waited + waited,
            };
        }
    }

    /// <summary>
    /// What is counted at this moment, the one that opened the most first.
    /// </summary>
    /// <returns>One entry per process. A mount that is still running keeps adding.</returns>
    public IReadOnlyList<Walker> Snapshot()
    {
        List<Walker> walkers;

        lock (_sync)
        {
            walkers = [.. _walkers.Values];
        }

        walkers.Sort(static (left, right) =>
        {
            int order = right.Opened.CompareTo(left.Opened);

            return order != 0 ? order : left.ProcessId.CompareTo(right.ProcessId);
        });

        return walkers;
    }

    private static string NameOf(int processId)
    {
        if (processId == 0)
        {
            return Unnamed;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);

            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // No such process any more. Documented for GetProcessById, and the ordinary case
            // for a program that asked once and ended.
            return Unnamed;
        }
        catch (InvalidOperationException)
        {
            return Unnamed;
        }
    }
}
