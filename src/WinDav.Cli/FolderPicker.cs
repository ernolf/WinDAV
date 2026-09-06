// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using WinDav.Abstractions;
using WinDav.Core.Configuration;

namespace WinDav.Cli;

/// <summary>
/// Walks a store so that the directory a mount is rooted at is chosen out of what is there
/// rather than typed.
/// </summary>
/// <remarks>
/// <para>
/// A path typed into <c>--path</c> is a path spelt from memory, and one letter wrong is a
/// mount that comes up empty or does not come up at all. The store knows what is on it, and
/// asking it costs one request per directory walked into.
/// </para>
/// <para>
/// Only directories are shown: what is being chosen is the root of a drive, and a file cannot
/// be one. The walk goes up as well as down, so where it begins is only where it begins.
/// </para>
/// </remarks>
internal sealed class FolderPicker
{
    // Up rather than into one of the numbered folders, which is why it is the one number that
    // never stands for a folder.
    private const string Up = "0";

    private const string Stop = "q";

    // Wide enough for a list number, so that the names line up in a directory with hundreds
    // of folders in it.
    private const int NumberWidth = 4;

    private readonly IStorageProvider _store;

    private readonly TextReader _input;

    private readonly TextWriter _output;

    /// <summary>
    /// Initialises a new instance of the <see cref="FolderPicker"/> class.
    /// </summary>
    /// <param name="store">The store to walk.</param>
    /// <param name="input">Where the answers come from.</param>
    /// <param name="output">Where what is on offer is written.</param>
    internal FolderPicker(IStorageProvider store, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _store = store;
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Builds a picker that talks to the person at the keyboard.
    /// </summary>
    /// <param name="store">The store to walk.</param>
    /// <returns>The picker.</returns>
    internal static FolderPicker Over(IStorageProvider store) => new(store, Console.In, Console.Out);

    /// <summary>
    /// Shows the store and waits until a directory is taken.
    /// </summary>
    /// <param name="start">The directory the walk begins in.</param>
    /// <param name="cancellationToken">Cancels what is being asked of the store.</param>
    /// <returns>
    /// The path that was taken, or <see langword="null"/> where the walk was stopped without
    /// taking one.
    /// </returns>
    /// <exception cref="ProviderException">
    /// The directory the walk begins in cannot be listed. One reached during the walk is said
    /// so and the walk goes on where it was.
    /// </exception>
    internal async Task<string?> PickAsync(string start, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);

        string path = start;
        IReadOnlyList<RemoteEntry> folders = await FoldersOfAsync(path, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            Show(path, folders);

            string? answer = Answer();

            // The end of the input is the end of the walk. A command whose input comes from a
            // file has nobody at the keyboard, the same way a confirmation there is a no.
            if (answer is null || string.Equals(answer, Stop, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (answer.Length == 0)
            {
                return path;
            }

            if (Chosen(path, folders, answer) is not { } asked)
            {
                continue;
            }

            // A directory that cannot be listed is one a mount could not be rooted at either.
            // It is said here, and the walk goes on where it was rather than ending in it.
            if (await ReachedAsync(asked, cancellationToken).ConfigureAwait(false) is not { } inside)
            {
                continue;
            }

            path = asked;
            folders = inside;
        }
    }

    private static string Above(string path)
    {
        int slash = path.LastIndexOf('/');

        return slash <= 0 ? MountConfiguration.RootPath : path[..slash];
    }

    private static bool IsRoot(string path) =>
        string.Equals(path, MountConfiguration.RootPath, StringComparison.Ordinal);

    // Everything the walk can do from where it stands, which is not the same everywhere: the
    // root has nothing above it, and an empty directory has nothing to go into.
    private static string Question(string path, int folders)
    {
        string take = $"Enter to take {path}, q to stop: ";

        if (IsRoot(path))
        {
            return folders == 0 ? take : $"A number to go in, {take}";
        }

        return folders == 0 ? $"0 to go up, {take}" : $"A number to go in, 0 to go up, {take}";
    }

    // The two places the walk touches the console from inside an async step, both ordinary
    // methods for the reason Program gives at its error stream: reading from one or writing
    // to one there is a report of its own (CA1849) — one that says nothing where the waiting
    // is on somebody typing.
    private string? Answer() => _input.ReadLine()?.Trim();

    private void Show(string path, IReadOnlyList<RemoteEntry> folders)
    {
        _output.WriteLine();
        _output.WriteLine(folders.Count == 0 ? $"{path} has no folders in it." : path);

        for (int index = 0; index < folders.Count; index++)
        {
            string number = (index + 1).ToString(CultureInfo.InvariantCulture);

            _output.WriteLine($"{number.PadLeft(NumberWidth)}  {folders[index].Name}");
        }

        _output.WriteLine();
        _output.Write(Question(path, folders.Count));
    }

    // The path the answer stands for, or nothing where it stands for none, in which case the
    // reason is written and the same question comes again.
    private string? Chosen(string path, IReadOnlyList<RemoteEntry> folders, string answer)
    {
        if (string.Equals(answer, Up, StringComparison.Ordinal))
        {
            if (IsRoot(path))
            {
                _output.WriteLine("There is nothing above the top of the store.");

                return null;
            }

            return Above(path);
        }

        // The culture of the console has no say in what a list number means.
        if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number >= 1
            && number <= folders.Count)
        {
            return folders[number - 1].Path;
        }

        _output.WriteLine(folders.Count == 0
            ? "There is no folder here to go into."
            : $"There is no {answer} in the list. The numbers go up to {folders.Count.ToString(CultureInfo.InvariantCulture)}.");

        return null;
    }

    private async Task<IReadOnlyList<RemoteEntry>?> ReachedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await FoldersOfAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException failure)
        {
            return Refused(failure);
        }
    }

    // The other one: written from a catch inside an async step. See Answer.
    private IReadOnlyList<RemoteEntry>? Refused(ProviderException failure)
    {
        _output.WriteLine(Program.Describe(failure));

        return null;
    }

    private async Task<IReadOnlyList<RemoteEntry>> FoldersOfAsync(string path, CancellationToken cancellationToken)
    {
        DirectoryListing listing = await _store.ListAsync(path, cancellationToken).ConfigureAwait(false);

        List<RemoteEntry> folders = [.. listing.Entries.Where(entry => entry.IsDirectory)];

        // In the order Explorer would show them, because that is the order the person doing
        // the walking is used to looking for a name in.
        folders.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        return folders;
    }
}
