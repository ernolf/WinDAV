// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using Xunit;

namespace WinDav.Cli.Tests;

// A path typed from memory is where a mount that is written down goes wrong, so the path is
// taken out of what the store says is there. Everything here is the walk: what is offered,
// what an answer does, and what happens to an answer that stands for nothing.
public sealed class FolderPickerTests
{
    [Fact]
    public async Task EnterTakesTheDirectoryTheWalkStandsIn()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents", "Music");

        Assert.Equal("/", await Pick(store, "/", string.Empty));
    }

    [Fact]
    public async Task ANumberGoesIntoTheFolderItStandsFor()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents", "Music");
        store.AddFolders("/Music");

        Assert.Equal("/Music", await Pick(store, "/", "2", string.Empty));
    }

    // The order a person looks for a name in, which is not the order a server sends.
    [Fact]
    public async Task FoldersAreNumberedInTheOrderExplorerWouldShowThem()
    {
        TreeStore store = new();
        store.AddFolders("/", "music", "Archive", "photos", "Backup");
        store.AddFolders("/photos");

        StringWriter written = new();

        Assert.Equal("/photos", await Pick(store, "/", written, "4", string.Empty));

        string shown = written.ToString();

        Assert.Contains("1  Archive", shown, StringComparison.Ordinal);
        Assert.Contains("2  Backup", shown, StringComparison.Ordinal);
        Assert.Contains("3  music", shown, StringComparison.Ordinal);
        Assert.Contains("4  photos", shown, StringComparison.Ordinal);
    }

    // What is being chosen is the root of a drive, and a file cannot be one.
    [Fact]
    public async Task FilesAreNotOffered()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents");
        store.AddFiles("/", "notes.txt", "report.pdf");
        store.AddFolders("/Documents");

        StringWriter written = new();

        Assert.Equal("/Documents", await Pick(store, "/", written, "1", string.Empty));

        string shown = written.ToString();

        Assert.DoesNotContain("notes.txt", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("report.pdf", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroGoesUpAgain()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents");
        store.AddFolders("/Documents", "Work");
        store.AddFolders("/Documents/Work");

        Assert.Equal("/Documents", await Pick(store, "/Documents/Work", "0", string.Empty));
    }

    [Fact]
    public async Task GoingUpFromTheTopSaysSoAndStaysThere()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents");

        StringWriter written = new();

        Assert.Equal("/", await Pick(store, "/", written, "0", string.Empty));
        Assert.Contains("nothing above", written.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatStandsForNothingIsSaidSoAndAsksAgain()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents");
        store.AddFolders("/Documents");

        StringWriter written = new();

        Assert.Equal("/Documents", await Pick(store, "/", written, "9", "1", string.Empty));
        Assert.Contains("no 9 in the list", written.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryWithNothingInItOffersNoNumber()
    {
        TreeStore store = new();
        store.AddFolders("/");

        StringWriter written = new();

        Assert.Equal("/", await Pick(store, "/", written, "1", string.Empty));
        Assert.Contains("no folders in it", written.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingTakesNothing()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents");

        Assert.Null(await Pick(store, "/", "q"));
    }

    // A command whose input comes from a file has nobody at the keyboard, and what is not
    // answered is not taken.
    [Fact]
    public async Task TheEndOfTheInputTakesNothing()
    {
        TreeStore store = new();
        store.AddFolders("/", "Documents");

        Assert.Null(await Pick(store, "/"));
    }

    [Fact]
    public async Task ADirectoryThatCannotBeListedLeavesTheWalkWhereItWas()
    {
        TreeStore store = new();
        store.AddFolders("/", "Locked");
        store.Refuse("/Locked", ProviderError.PermissionDenied);

        StringWriter written = new();

        Assert.Equal("/", await Pick(store, "/", written, "1", string.Empty));
        Assert.Contains("did not accept the credential", written.ToString(), StringComparison.Ordinal);
    }

    // The one listing there is nothing to carry on from: the walk has nowhere to stand, so it
    // is the caller's to report.
    [Fact]
    public async Task ADirectoryTheWalkCannotBeginInIsNotSwallowed()
    {
        TreeStore store = new();
        store.AddFolders("/");

        await Assert.ThrowsAsync<ProviderException>(() => Pick(store, "/Elsewhere", string.Empty));
    }

    private static Task<string?> Pick(TreeStore store, string start, params string[] answers) =>
        Pick(store, start, new StringWriter(), answers);

    // Every answer ends in a newline, the last one among them: an empty answer is Enter, and
    // joining would leave it as the end of the input instead of a line that was answered.
    private static Task<string?> Pick(TreeStore store, string start, TextWriter written, params string[] answers) =>
        new FolderPicker(
                store,
                new StringReader(string.Concat(answers.Select(answer => answer + Environment.NewLine))),
                written)
            .PickAsync(start, TestContext.Current.CancellationToken);

    // A tree held in memory, listed the way a provider lists: the entries of a directory
    // without the directory itself, and a ProviderException for what cannot be reached.
    // Nothing but listing is implemented, so a picker that asked for anything else fails
    // loudly rather than passing quietly.
    private sealed class TreeStore : IStorageProvider
    {
        private readonly Dictionary<string, List<RemoteEntry>> _entries = new(StringComparer.Ordinal);

        private readonly Dictionary<string, ProviderError> _refused = new(StringComparer.Ordinal);

        public void AddFolders(string path, params string[] names) => Add(path, names, isDirectory: true);

        public void AddFiles(string path, params string[] names) => Add(path, names, isDirectory: false);

        public void Refuse(string path, ProviderError error) => _refused[path] = error;

        public Task<DirectoryListing> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            if (_refused.TryGetValue(path, out ProviderError error))
            {
                throw new ProviderException(error, $"'{path}' is refused.");
            }

            return _entries.TryGetValue(path, out List<RemoteEntry>? entries)
                ? Task.FromResult(new DirectoryListing(entries))
                : throw new ProviderException(ProviderError.NotFound, $"There is no '{path}'.");
        }

        public Task<RemoteEntry> GetAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            string path,
            long offset = 0,
            long? count = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> WriteAsync(
            string path,
            Stream content,
            string? ifMatch = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> CreateFileAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task MoveAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StorageSpace> GetSpaceAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private void Add(string path, string[] names, bool isDirectory)
        {
            if (!_entries.TryGetValue(path, out List<RemoteEntry>? entries))
            {
                entries = [];
                _entries[path] = entries;
            }

            string above = path == "/" ? string.Empty : path;

            foreach (string name in names)
            {
                entries.Add(new RemoteEntry($"{above}/{name}", isDirectory));
            }
        }
    }
}
