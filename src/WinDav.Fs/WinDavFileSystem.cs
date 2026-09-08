// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using Fsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Core.Providers;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace WinDav.Fs;

/// <summary>
/// Shows what a <see cref="IStorageProvider"/> holds as a Windows volume.
/// </summary>
/// <remarks>
/// <para>
/// What a program does with a file happens here and reaches the store: reading it, making
/// it, writing it, moving it and taking it away. What is left over answers
/// <c>STATUS_MEDIA_WRITE_PROTECTED</c>, which Windows phrases as "the media is write
/// protected" and every program understands. Refusing with a status Windows has no wording
/// for is what produces the useless "catastrophic failure" dialog, so a refusal is never
/// left to the default.
/// </para>
/// <para>
/// WinFsp calls this from its own threads, one call per request, with no synchronisation
/// context of its own. The provider underneath is asynchronous; the wait that bridges the
/// two is <see cref="Await{TResult}"/> and is explained there.
/// </para>
/// </remarks>
public sealed class WinDavFileSystem : FileSystemBase
{
    // System, administrators and everyone get full access, and the ACL is protected so that
    // nothing is inherited from elsewhere. What a person may actually do is decided by the
    // server, and is answered by refusing the operation, not by hiding the entry.
    private const string RootSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";

    // The provider names paths the way a URL does. See RemoteEntry.Path.
    private const string RemoteRoot = "/";

    private const ushort SectorSize = 4096;
    private const ushort SectorsPerAllocationUnit = 1;
    private const ushort MaxComponentLength = 255;
    private const ulong AllocationUnit = SectorSize * SectorsPerAllocationUnit;

    // What a store with no limit is shown as having left. Windows insists on a number, an
    // account without a quota has none, and this one is deliberately large enough that
    // nothing ever looks nearly full. It is room on top of what is in use and never the
    // whole volume, so a real figure for what is used is still shown beside it.
    private const ulong Headroom = 1UL << 40;

    // How long WinFsp may reuse what it was last told about an entry. Every miss is a
    // request over the network, and the Explorer asks for the same entry several times
    // while drawing one window. A second is short enough that a change on the server shows
    // up while somebody is still looking at the window.
    private const uint FileInfoTimeoutMilliseconds = 1000;

    // The same for the volume, which is a question of its own since it costs a request of
    // its own. Windows asks it whenever a window is drawn and before it starts a copy, and
    // the answer changes at the pace files are written, so ten seconds is both cheap and
    // soon enough to watch a large upload eat into a quota.
    private const uint VolumeInfoTimeoutMilliseconds = 10000;

    // Milliseconds with one place, the same as the wire records are written with, so that a
    // read and the requests underneath it can be laid side by side.
    private const string ElapsedFormat = "0.#";

    private static readonly long s_fileTimeEpochTicks =
        new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private readonly IStorageProvider _provider;

    // The store of listings, where the handles open on a directory are counted. Null where
    // the mount holds no listings, and then there is nothing that would read the count.
    private readonly DirectoryCache? _directories;

    private readonly MountSettings _settings;
    private readonly ILogger _log;
    private readonly ReadLayer _reads;
    private readonly byte[] _security;
    private readonly ulong _mountTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();

    // Set while a mount is driving this file system, and read before anything is asked of
    // WinFsp's native library.
    private bool _mounted;

    // The mount's root, as the provider spells it: empty for the whole store, otherwise a
    // path with a leading and no trailing slash, so that a child is the root and the name
    // put together with nothing in between.
    private readonly string _root;

    /// <summary>
    /// Initialises a new instance of the <see cref="WinDavFileSystem"/> class.
    /// </summary>
    /// <param name="provider">The store to show.</param>
    /// <param name="settings">How this mount presents itself.</param>
    /// <param name="loggerFactory">
    /// Where what Windows asked for is written down, or <see langword="null"/> for a file
    /// system that writes nothing, which is what a test that only wants answers asks for.
    /// </param>
    /// <param name="gate">
    /// The gate that says how many requests this mount may have on the wire, or
    /// <see langword="null"/> for one built from the settings. A mount that has layers of its
    /// own under the seam hands the one they all share in.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public WinDavFileSystem(
        IStorageProvider provider,
        MountSettings settings,
        ILoggerFactory? loggerFactory = null,
        RequestGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(settings);

        _provider = provider;
        _directories = provider as DirectoryCache;
        _settings = settings;
        _log = loggerFactory?.CreateLogger(typeof(WinDavFileSystem)) ?? NullLogger.Instance;
        _reads = new ReadLayer(provider, settings.Read, _log, recovery: null, gate);
        _root = NormaliseRoot(settings.RemotePath);

        RawSecurityDescriptor descriptor = new(RootSddl);

        _security = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(_security, 0);
    }

    /// <summary>
    /// Gets who opened something on this mount, counted per process.
    /// </summary>
    /// <remarks>
    /// Filled from <see cref="Open"/>, where WinFsp names the process that asked. Read when
    /// the mount comes down, for the report
    /// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#84-the-mount-says-who-walked-it-and-what-the-shell-has-registered">decision 84</see>
    /// has it write.
    /// </remarks>
    public OpenTally Opens { get; } = new();

    // Runs inside the mount, before WinFsp has built anything, and is the only place the
    // host may be configured.
    /// <inheritdoc/>
    public override int Init(object host)
    {
        FileSystemHost fileSystemHost = (FileSystemHost)host;

        fileSystemHost.SectorSize = SectorSize;
        fileSystemHost.SectorsPerAllocationUnit = SectorsPerAllocationUnit;
        fileSystemHost.MaxComponentLength = MaxComponentLength;
        fileSystemHost.VolumeCreationTime = _mountTime;
        fileSystemHost.VolumeSerialNumber = (uint)(_mountTime / TimeSpan.TicksPerSecond);
        fileSystemHost.FileSystemName = ProductInfo.Name;
        fileSystemHost.FileInfoTimeout = FileInfoTimeoutMilliseconds;
        fileSystemHost.VolumeInfoTimeout = VolumeInfoTimeoutMilliseconds;

        // Decision 85: what the store behind this mount is, said plainly rather than smoothed
        // over. A name goes to the server as it was typed, so two names differing only in
        // case are two names and both of them can be here at once. Windows takes this at its
        // word and every program on it is written to that word, which is exactly why the
        // easier claim is the wrong one: a volume that called the two spellings one name and
        // then answered 404 for the second would be lying to all of them at once.
        fileSystemHost.CaseSensitiveSearch = true;
        fileSystemHost.CasePreservedNames = true;
        fileSystemHost.UnicodeOnDisk = true;
        fileSystemHost.PersistentAcls = true;
        fileSystemHost.PostCleanupWhenModifiedOnly = true;

        // With a prefix the mount is a network location and Windows treats it as one;
        // without it, a local disk. Set here because WinFsp reads it when the mount is
        // built, which happens after this call and before anything else.
        if (_settings.NetworkPrefix is not null)
        {
            fileSystemHost.Prefix = _settings.NetworkPrefix;
        }

        return STATUS_SUCCESS;
    }

    // The two ends of a mount. Between them WinFsp carries requests in and stands behind
    // them; outside them there is nothing of its to ask.
    /// <inheritdoc/>
    public override int Mounted(object host)
    {
        _mounted = true;

        return STATUS_SUCCESS;
    }

    /// <inheritdoc/>
    public override void Unmounted(object host) => _mounted = false;

    /// <inheritdoc/>
    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        long started = Stopwatch.GetTimestamp();
        StorageSpace space = SpaceOfTheVolume();

        // What is left is what the store said, or the headroom when it said nothing. What
        // the volume holds altogether is that plus what is already in it, which for an
        // account with a quota is the quota itself and for one without is a number that
        // grows with the files. Neither figure is ever worked out from the other, because
        // the one that is missing is missing and not zero.
        ulong free = space.Available is long available ? (ulong)available : Headroom;
        ulong used = space.Used is long inUse ? (ulong)inUse : 0;

        volumeInfo = default;
        volumeInfo.TotalSize = free + used;
        volumeInfo.FreeSize = free;
        volumeInfo.SetVolumeLabel(_settings.VolumeLabel);

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug(
                "Asked the store for room in {Elapsed} ms: {Free} bytes free of {Total}.",
                Elapsed(started),
                free,
                free + used);
        }

        return STATUS_SUCCESS;
    }

    // Asked before an entry is opened, to decide whether the caller may. The Explorer asks
    // this for names that are not there several times per window, so the answer for a
    // missing entry has to be the cheap and ordinary one, not an error.
    /// <inheritdoc/>
    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributes,
        ref byte[]? securityDescriptor)
    {
        fileAttributes = 0;

        string path = ToRemotePath(fileName);
        long started = Stopwatch.GetTimestamp();

        try
        {
            RemoteEntry entry = Await(_provider.GetAsync(path));

            fileAttributes = AttributesOf(entry);

            // Null means the caller wants the attributes only.
            if (securityDescriptor is not null)
            {
                securityDescriptor = _security;
            }

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug("Asked about {Path} in {Elapsed} ms.", path, Elapsed(started));
            }

            return STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
            // Debug and not warning: a name that is not there is the ordinary answer to this
            // question, and it is asked for several of them per window.
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Asked about {Path} in {Elapsed} ms: {Reason}.",
                    path,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }
    }

    // A directory or a file: Windows asks for both through this one call and says which in
    // the options. Either is made in the store before this returns, so that a name already
    // taken, a permission refused and a store with no room are answered where Windows can
    // still show them to somebody. See decision 86.
    /// <inheritdoc/>
    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object? fileNode,
        out object? fileDesc,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;

        // Refused for the reason it is refused at Open, and for one more: a delete stated at
        // the open is never offered to CanDelete, so the emptiness of a directory would go
        // untested and the whole tree under it would go with the close.
        if ((createOptions & FILE_DELETE_ON_CLOSE) != 0)
        {
            return Refused("Create with delete on close");
        }

        bool directory = (createOptions & FILE_DIRECTORY_FILE) != 0;
        string path = ToRemotePath(fileName);
        long started = Stopwatch.GetTimestamp();

        try
        {
            if (directory)
            {
                Await(_provider.CreateDirectoryAsync(path));
            }
            else
            {
                // Empty: what this handle is about to write goes out when it lets go. What
                // the call is for is the name, and a store that already has it answers with
                // the collision WinFsp turns into an Open of what is there.
                Await(_provider.CreateFileAsync(path));
            }

            // What the store answers to the making of an entry says nothing about it, and
            // the handle being opened has to carry one. Asked for rather than made up:
            // the times and what may be done with it are the store's to say, and this is the
            // entry an Open a moment later would be given.
            RemoteEntry entry = Await(_provider.GetAsync(path));

            // The attributes, the descriptor and the size that were asked for are dropped. An
            // entry in a store like this has nowhere to keep any of them, and the volume
            // hands out one descriptor for everything it is asked about.
            //
            // Emptied, because it was just made: a write to part of it has nothing to fetch.
            fileDesc = new OpenEntry(path, entry, _reads.Open(path, entry.Length)) { Emptied = true };
            fileInfo = ToFileInfo(entry);

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug("Created {Path} in {Elapsed} ms.", path, Elapsed(started));
            }

            // Entered here for the reason Open enters it: what comes back is an open handle,
            // and Close leaves what either of the two put in.
            if (directory)
            {
                _directories?.Handles.Enter(path);
            }

            return STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Creating {Path} failed after {Elapsed} ms: {Reason}.",
                    path,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }
    }

    /// <inheritdoc/>
    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object? fileNode,
        out object? fileDesc,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;

        // Left null: the name the caller used is the name the entry has, because a store
        // that keeps case has nothing to correct. Sending one back would cost a second
        // request to learn what we already know.
        normalizedName = null;

        string path = ToRemotePath(fileName);
        long started = Stopwatch.GetTimestamp();

        try
        {
            RemoteEntry entry = Await(_provider.GetAsync(path));

            if (entry.IsDirectory && (createOptions & FILE_NON_DIRECTORY_FILE) != 0)
            {
                return STATUS_FILE_IS_A_DIRECTORY;
            }

            if (!entry.IsDirectory && (createOptions & FILE_DIRECTORY_FILE) != 0)
            {
                return STATUS_NOT_A_DIRECTORY;
            }

            // Deletion stated as an option to the open. Refused here rather than at close,
            // where the caller has stopped listening and Windows drops the reason.
            if ((createOptions & FILE_DELETE_ON_CLOSE) != 0)
            {
                return Refused("Open with delete on close");
            }

            fileDesc = new OpenEntry(path, entry, _reads.Open(path, entry.Length));
            fileInfo = ToFileInfo(entry);

            // Decision 84: here and nowhere else. WinFsp has an originating process during
            // Open and only where the target exists, so this is the one place on the walking
            // path where the question "who is doing this" has an answer at all.
            Opens.Note(
                ProcessId(),
                entry.IsDirectory,
                Stopwatch.GetElapsedTime(started));

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug("Opened {Path} in {Elapsed} ms.", path, Elapsed(started));
            }

            // Counted where the open has succeeded, because that is where the handle begins
            // to exist: WinFsp closes what it was given a success for and nothing else. What
            // reads the count is the store of listings, which reads ahead of a directory
            // somebody is standing in and not of one a walk is passing through.
            if (entry.IsDirectory)
            {
                _directories?.Handles.Enter(path);
            }

            return STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Opening {Path} failed after {Elapsed} ms: {Reason}.",
                    path,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }
    }

    // Answered from what the open already fetched. WinFsp holds it for FileInfoTimeout, so
    // asking the server again here would double the traffic of every window.
    /// <inheritdoc/>
    public override int GetFileInfo(object? fileNode, object fileDesc, out FileInfo fileInfo)
    {
        OpenEntry open = (OpenEntry)fileDesc;

        fileInfo = FileInfoOf(open);

        return STATUS_SUCCESS;
    }

    /// <inheritdoc/>
    public override int GetSecurity(object? fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _security;

        return STATUS_SUCCESS;
    }

    /// <inheritdoc/>
    public override int Read(
        object? fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;

        OpenEntry open = (OpenEntry)fileDesc;

        if (open.Entry.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        // What this handle has written is here and not there. Reading around the stage would
        // answer with the file as it was before the write, which is what Windows does when it
        // reads back a file it has just copied.
        if (open.Stage is WriteStage staged)
        {
            bytesTransferred = (uint)staged.Read((long)offset, length, buffer);

            return bytesTransferred == 0 ? STATUS_END_OF_FILE : STATUS_SUCCESS;
        }

        long wanted = length;

        // Only clamped when the size is known. A store that named none must not have every
        // read of it turned into an end of file; there the stream running dry is what says
        // the file has ended.
        if (open.Entry.Length is long size)
        {
            if (offset >= (ulong)size)
            {
                return STATUS_END_OF_FILE;
            }

            wanted = Math.Min(wanted, size - (long)offset);
        }

        // Before the wait, so that a read which never comes back has still said what it was
        // after. That is the difference between trace and debug on this side.
        if (_log.IsEnabled(LogLevel.Trace))
        {
            _log.LogTrace("Reading {Wanted} bytes of {Path} at {Offset}.", wanted, open.Path, offset);
        }

        long started = Stopwatch.GetTimestamp();

        try
        {
            // What is written down here is what Windows asked for and how long it waited.
            // Whether that cost a request, and which one, is the read layer's own record.
            bytesTransferred = (uint)open.Window.Read((long)offset, wanted, buffer);

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Read {Wanted} bytes of {Path} at {Offset}, {Transferred} back in {Elapsed} ms.",
                    wanted,
                    open.Path,
                    offset,
                    bytesTransferred,
                    Elapsed(started));
            }

            return bytesTransferred == 0 ? STATUS_END_OF_FILE : STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
            // Warning, unlike the questions above: a read that fails is an error Windows puts
            // in front of whoever asked for the file, and the reason for it belongs in the
            // file whether a recording was asked for or not.
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(
                    exception,
                    "Reading {Path} at {Offset} failed after {Elapsed} ms.",
                    open.Path,
                    offset,
                    Elapsed(started));
            }

            return ProviderStatus.From(exception);
        }
    }

    // The half of a delete that has somewhere to put a refusal. The deletion itself falls
    // due in Cleanup, which Windows gives no way to fail, so whatever can be tested is
    // tested here. Also the answer to SetDelete, which WinFsp hands on to this.
    /// <inheritdoc/>
    public override int CanDelete(object? fileNode, object fileDesc, string fileName)
    {
        OpenEntry open = (OpenEntry)fileDesc;

        if (!open.Entry.IsDirectory)
        {
            return STATUS_SUCCESS;
        }

        long started = Stopwatch.GetTimestamp();

        try
        {
            // One request takes a directory with everything under it, and Windows empties a
            // tree from the leaves up. A directory that still holds something is therefore
            // one the caller does not know about, and it is answered the way NTFS answers.
            List<RemoteEntry> children = ChildrenOf(open.Path, marker: null);

            if (children.Count == 0)
            {
                return STATUS_SUCCESS;
            }

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Refused to delete {Path}: {Count} entries are still in it.",
                    open.Path,
                    children.Count);
            }

            return STATUS_DIRECTORY_NOT_EMPTY;
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Asking what is in {Path} failed after {Elapsed} ms: {Reason}.",
                    open.Path,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }
    }

    // The last call on a handle that can still reach the store, and Windows gives it no way
    // to report a failure. Two things fall due here. An entry that was written to has its
    // contents sent unless a Flush already sent them; an entry that is being deleted is
    // taken away, which is where a delete happens because Windows puts it here. Both are
    // written down and that is all, which is what CanDelete is for.
    //
    // Reached for exactly those two, because Init sets PostCleanupWhenModifiedOnly.
    /// <inheritdoc/>
    public override void Cleanup(object? fileNode, object fileDesc, string? fileName, uint flags)
    {
        if (fileDesc is not OpenEntry open)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();

        if ((flags & CleanupDelete) == 0)
        {
            try
            {
                Send(open);
            }
            catch (ProviderException exception)
            {
                // Nobody is listening any more: the program that wrote the file has been told
                // the write went through, and this is the only place the truth is kept.
                _log.LogWarning(
                    "Writing {Path} failed after {Elapsed} ms: {Reason}.",
                    open.Path,
                    Elapsed(started),
                    exception.Error);
            }

            return;
        }

        try
        {
            Await(_provider.DeleteAsync(open.Path));

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug("Deleted {Path} in {Elapsed} ms.", open.Path, Elapsed(started));
            }
        }
        catch (ProviderException exception)
        {
            // Written at a level nobody has to switch on. Windows has told the person that
            // the entry is gone, and this is the only place where the truth is kept.
            _log.LogWarning(
                "Deleting {Path} failed after {Elapsed} ms: {Reason}.",
                open.Path,
                Elapsed(started),
                exception.Error);
        }
    }

    // The end of one handle. The window it read through belongs to the mount's ceiling and
    // has to go back, whether the file was read to the end or dropped after a kilobyte; what
    // it wrote through is a file of its own and goes with it; a directory is held by one
    // handle fewer, which is what says whether anybody is still standing in it. Nothing else
    // of ours outlives an open.
    /// <inheritdoc/>
    public override void Close(object? fileNode, object fileDesc)
    {
        if (fileDesc is OpenEntry open)
        {
            open.Window.Close();
            open.Stage?.Dispose();

            if (open.Entry.IsDirectory)
            {
                _directories?.Handles.Leave(open.Path);
            }
        }
    }

    // Moving an entry, which is also how it is renamed: where it is and what it is called
    // are one and the same. What an open handle holds is left pointing at the name it was
    // opened with, because the window it reads through and the count of who is standing in
    // a directory were both taken out under that name and have to be given back under it.
    /// <inheritdoc/>
    public override int Rename(
        object? fileNode,
        object fileDesc,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        string source = ToRemotePath(fileName);
        string destination = ToRemotePath(newFileName);
        long started = Stopwatch.GetTimestamp();

        try
        {
            Await(_provider.MoveAsync(source, destination, replaceIfExists));

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Moved {Path} to {Destination} in {Elapsed} ms.",
                    source,
                    destination,
                    Elapsed(started));
            }

            return STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Moving {Path} to {Destination} failed after {Elapsed} ms: {Reason}.",
                    source,
                    destination,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }
    }

    // Called until it answers false. The listing is fetched once and kept in the context,
    // because a request per entry would turn one directory into as many round trips.
    //
    // The pattern is ignored on purpose: PassQueryDirectoryPattern is left off, so WinFsp
    // matches it against what we return and a store that cannot filter is not asked to.
    //
    // A failure of the provider leaves as an exception, because the signature has no room
    // for a status; ExceptionHandler turns it into the same one every other call returns.
    /// <inheritdoc/>
    public override bool ReadDirectoryEntry(
        object? fileNode,
        object fileDesc,
        string? pattern,
        string? marker,
        ref object? context,
        out string? fileName,
        out FileInfo fileInfo)
    {
        fileName = null;
        fileInfo = default;

        OpenEntry open = (OpenEntry)fileDesc;

        if (!open.Entry.IsDirectory)
        {
            return false;
        }

        if (context is not DirectoryScan scan)
        {
            long started = Stopwatch.GetTimestamp();

            // Read before the listing, so that what is written down is the number the store
            // of listings saw when it decided whether to read ahead. Nothing else on record
            // says whether a reader holds a directory more than once at a time.
            int handles = _directories?.Handles.Count(open.Path) ?? 0;

            // What is counted is what was fetched, which for an enumeration that was resumed
            // is what is left after the marker rather than the whole directory.
            List<RemoteEntry> children = ChildrenOf(open.Path, marker);

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Listed {Count} entries of {Path} in {Elapsed} ms, held by {Handles}.",
                    children.Count,
                    open.Path,
                    Elapsed(started),
                    handles);
            }

            scan = new DirectoryScan(children);
            context = scan;
        }

        if (scan.Next() is not RemoteEntry child)
        {
            return false;
        }

        fileName = child.Name;
        fileInfo = ToFileInfo(child);

        if (_log.IsEnabled(LogLevel.Trace))
        {
            _log.LogTrace("Handed {Name} of {Path} back.", child.Name, open.Path);
        }

        return true;
    }

    // Where a program that asked for its file to be on the server is told whether it is.
    // Windows sends this only when something asks for it, so it is not where an upload is
    // sure to happen; it is where one can be answered for. Cleanup takes whatever is left.
    /// <inheritdoc/>
    public override int Flush(object? fileNode, object? fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;

        // The whole volume rather than one file, and this side holds nothing of its own.
        if (fileDesc is not OpenEntry open)
        {
            return STATUS_SUCCESS;
        }

        long started = Stopwatch.GetTimestamp();

        try
        {
            Send(open);
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(
                    "Writing {Path} failed after {Elapsed} ms: {Reason}.",
                    open.Path,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }

        fileInfo = FileInfoOf(open);

        return STATUS_SUCCESS;
    }

    /// <inheritdoc/>
    public override int ExceptionHandler(Exception exception)
    {
        return exception is ProviderException provider
            ? ProviderStatus.From(provider)
            : base.ExceptionHandler(exception);
    }

    // == What a file is written with ==
    //
    // A store like this takes a file whole and Windows hands one over in pieces, so the
    // pieces land in a staging file and go out as one upload when the handle lets go. See
    // decision 86. OverwriteEx is not among these: WinFsp passes it on to Overwrite.

    // A file opened to be replaced: what was in it counts as gone from this moment, and what
    // is written now is written onto nothing. Nothing leaves here.
    /// <inheritdoc/>
    public override int Overwrite(
        object? fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        OpenEntry open = (OpenEntry)fileDesc;

        // Set before the stage is asked for, so that nothing is fetched to be thrown away.
        open.Emptied = true;

        StageOf(open).Truncate(0);

        fileInfo = FileInfoOf(open);

        return STATUS_SUCCESS;
    }

    /// <inheritdoc/>
    public override int Write(
        object? fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;

        OpenEntry open = (OpenEntry)fileDesc;

        if (open.Entry.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        WriteStage stage;
        long started = Stopwatch.GetTimestamp();

        try
        {
            // The one part of a write that can fail against the store: a write to part of a
            // file the mount has not got here yet has to fetch the whole of it first.
            stage = StageOf(open);
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(
                    "Fetching {Path} to write to it failed after {Elapsed} ms: {Reason}.",
                    open.Path,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }

        long at = writeToEndOfFile ? stage.Length : (long)offset;
        long count = length;

        // What the cache manager writes back out of pages it is holding. It must not make the
        // file longer than it is, and a piece that lies past the end is not a piece any more.
        if (constrainedIo)
        {
            if (at >= stage.Length)
            {
                fileInfo = FileInfoOf(open);

                return STATUS_SUCCESS;
            }

            count = Math.Min(count, stage.Length - at);
        }

        stage.Write(at, buffer, (int)count);

        bytesTransferred = (uint)count;
        fileInfo = FileInfoOf(open);

        return STATUS_SUCCESS;
    }

    // The times Windows sets on a file it has just copied. Where the file is still on its
    // way out, they are kept on the handle and travel with it, which costs nothing; where
    // there is nothing left to send, or the entry is a directory, they go on their own.
    //
    // The attributes are not among them. A store like this holds none of what the bits say,
    // and a handle has nowhere to keep them either, so they are taken and dropped rather
    // than refused: read-only is a matter of the permissions the store names, and the rest
    // is Windows bookkeeping about a local file.
    //
    // Nothing here fails the call. A time is worth less than the file it belongs to, and a
    // copy that has otherwise gone through must not be reported as failed over one.
    /// <inheritdoc/>
    public override int SetBasicInfo(
        object? fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        OpenEntry open = (OpenEntry)fileDesc;

        // The access time is not kept: nothing on the other side has a place for it, and
        // writing it down here would show a file as touched by the act of reading the
        // listing it is in. The change time is the time of the metadata and not of the
        // contents, which is the modification time and is asked for separately.
        EntryTimes asked = new(FromFileTime(creationTime), FromFileTime(lastWriteTime));

        if (!asked.IsEmpty)
        {
            // What was named replaces what was named before, and what was left out leaves
            // the earlier half of an answer standing.
            open.Times = new(
                asked.Created ?? open.Times.Created,
                asked.LastModified ?? open.Times.LastModified);

            if (open.Stage is not WriteStage { Pending: true })
            {
                SetTimes(open);
            }
        }

        fileInfo = FileInfoOf(open);

        return STATUS_SUCCESS;
    }

    // How long the file is. An allocation size is Windows saying how much room it expects to
    // need, which a store that is handed the file whole does not act on; a file size is the
    // length of the file itself, and it is cut or stretched to it here.
    /// <inheritdoc/>
    public override int SetFileSize(
        object? fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;

        OpenEntry open = (OpenEntry)fileDesc;
        long started = Stopwatch.GetTimestamp();

        try
        {
            if (!setAllocationSize)
            {
                StageOf(open).Truncate((long)newSize);
            }
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(
                    "Fetching {Path} to resize it failed after {Elapsed} ms: {Reason}.",
                    open.Path,
                    Elapsed(started),
                    exception.Error);
            }

            return ProviderStatus.From(exception);
        }

        fileInfo = FileInfoOf(open);

        return STATUS_SUCCESS;
    }

    // == What is still turned away ==
    //
    // What Windows would keep beside a file and this store has nowhere to keep. Both answer
    // the one status Windows can phrase, so that a person is told the volume will not take it
    // instead of being shown a code.

    /// <inheritdoc/>
    public override int SetSecurity(
        object? fileNode,
        object fileDesc,
        AccessControlSections sections,
        byte[] securityDescriptor) => Refused(nameof(SetSecurity));

    /// <inheritdoc/>
    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        volumeInfo = default;

        return Refused(nameof(SetVolumeLabel));
    }

    // == What is written down ==

    // The always-on levels are the mount going up and coming down, which ProviderMount
    // writes, and a read that failed. Everything else here is one of the two levels that are
    // switched on for a while: debug for what was asked of the store, with what it cost, and
    // trace for the steps in between. See decision 74.

    private static string Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString(ElapsedFormat, CultureInfo.InvariantCulture);

    // The status Windows has for a volume that will not take something, and its own wording
    // for it names no operation, so the operation is named here.
    private int Refused(string operation)
    {
        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("Refused {Operation}: this volume does not take it.", operation);
        }

        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    // == The seam between the two worlds ==

    // WinFsp dispatches every request on a thread of its own and expects an answer on it.
    // There is no synchronisation context to deadlock against, so waiting here is what the
    // binding is built for: the alternative would be an asynchronous file system that WinFsp
    // has no way to call.
    private static TResult Await<TResult>(Task<TResult> task) => task.GetAwaiter().GetResult();

    private static void Await(Task task) => task.GetAwaiter().GetResult();

    // Who is doing this is WinFsp's to answer, out of its native library and only inside a
    // request that library carried in. Asked outside a mount, the call is not an error that
    // comes back: it reads an operation that is not there and takes the process with it,
    // which is nothing .NET can catch. So it is put only while a mount is standing, and a
    // report that names no process is the answer everywhere else.
    private int ProcessId() => _mounted ? GetOperationProcessId() : 0;

    private static string NormaliseRoot(string remotePath)
    {
        string trimmed = remotePath.Trim().TrimEnd('/');

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith('/') ? trimmed : RemoteRoot + trimmed;
    }

    private static uint AttributesOf(RemoteEntry entry)
    {
        if (entry.IsDirectory)
        {
            // Never with ReadOnly beside it. On a directory Windows does not read that bit
            // as "cannot be changed" but as "this folder has been customised", and then goes
            // looking for a desktop.ini that is not there.
            return (uint)FileAttributes.Directory;
        }

        // A store that said nothing about permissions is not saying no. Only an explicit
        // absence of the right to write earns the bit.
        if (entry.Permissions is EntryPermissions permissions && (permissions & EntryPermissions.Write) == 0)
        {
            return (uint)FileAttributes.ReadOnly;
        }

        return (uint)FileAttributes.Normal;
    }

    private static ulong AllocationSizeOf(ulong fileSize) =>
        (fileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;

    private static ulong ToFileTime(DateTimeOffset? time, ulong fallback)
    {
        if (time is not DateTimeOffset value)
        {
            return fallback;
        }

        long ticks = value.UtcDateTime.Ticks - s_fileTimeEpochTicks;

        // Windows counts from 1601 and has no room for anything before it. A store that
        // names such a date has said something Windows cannot hold, and the entry is worth
        // more than the date.
        return ticks < 0 ? fallback : (ulong)ticks;
    }

    // The inverse, and the two answers it will not give. A zero is Windows saying it has
    // not touched this time, and the all-ones is it saying it will not touch it again for
    // the life of the handle. Both are silence, and silence is not a date to write anywhere.
    private static DateTimeOffset? FromFileTime(ulong time)
    {
        if (time is 0 or ulong.MaxValue)
        {
            return null;
        }

        long ticks = s_fileTimeEpochTicks + (long)time;

        // Past what a date holds on this side. Windows has no business sending one, and
        // taking it would fail the call over a value nobody meant.
        return ticks < 0 || ticks > DateTime.MaxValue.Ticks
            ? null
            : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private StorageSpace SpaceOfTheVolume()
    {
        try
        {
            return Await(_provider.GetSpaceAsync(ToRemotePath(RemoteRoot)));
        }
        catch (ProviderException)
        {
            // Windows asks this while it is drawing a window and while it is deciding
            // whether a copy will fit, and neither is a place to fail: a drive that answers
            // its size with an error is a drive that looks broken. A server that cannot be
            // reached leaves the volume shown with its headroom, and the next question,
            // which is due within seconds, asks again.
            return StorageSpace.Unknown;
        }
    }

    private string ToRemotePath(string fileName)
    {
        // WinFsp names paths the way the kernel does: backslashes, and a bare one for the
        // root. Nothing else about them has to change, because a name is a name on both
        // sides and the escaping is the provider's business.
        string relative = fileName.Replace('\\', '/');

        if (relative.Length == 0 || string.Equals(relative, RemoteRoot, StringComparison.Ordinal))
        {
            return _root.Length == 0 ? RemoteRoot : _root;
        }

        return _root + relative;
    }

    private List<RemoteEntry> ChildrenOf(string path, string? marker)
    {
        List<RemoteEntry> children = [.. Await(_provider.ListAsync(path)).Entries];

        // Ordinal and ignoring case, which is the search the volume declared in Init. The
        // order matters beyond looks: WinFsp resumes an interrupted enumeration by naming
        // the last entry it saw, and everything up to and including it is done with.
        children.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        if (marker is null)
        {
            return children;
        }

        return children.FindAll(entry =>
            string.Compare(entry.Name, marker, StringComparison.OrdinalIgnoreCase) > 0);
    }

    // The staging file this handle writes through, opened the first time it is wanted. A
    // store like this is handed a file whole, so writing to part of one means having the
    // whole of it here first; a file that was just made or just emptied has nothing to fetch.
    private WriteStage StageOf(OpenEntry open)
    {
        if (open.Stage is WriteStage already)
        {
            return already;
        }

        // The tag the fetch or the open saw. It is what the upload is made conditional on, so
        // that a file somebody else has changed in the meantime is not written over.
        WriteStage stage = new(open.Entry.ETag);

        try
        {
            if (!open.Emptied)
            {
                using Stream source = Await(_provider.OpenReadAsync(open.Path));

                stage.Fill(source);
            }
        }
        catch
        {
            stage.Dispose();

            throw;
        }

        open.Stage = stage;

        return stage;
    }

    // Where the contents of a file leave this side, and the only place they do. Flush comes
    // here when a program asks for it and Cleanup when the handle lets go; whichever is first
    // sends the file, and the other finds nothing left to send.
    private void Send(OpenEntry open)
    {
        if (open.Stage is not WriteStage stage || !stage.Pending)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        long length = stage.Length;

        // The times go with every upload from this handle and are not cleared once they
        // have gone. A program that has set a time has said what the file is to carry, and
        // an upload that left them out would put the time of the upload there instead.
        stage.Delivered(Await(_provider.WriteAsync(open.Path, stage.Rewound(), stage.ETag, open.Times)));

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug(
                "Wrote {Length} bytes to {Path} in {Elapsed} ms.",
                length,
                open.Path,
                Elapsed(started));
        }
    }

    // The times on their own, for a handle with nothing left to send. A store that will not
    // set them says so by doing nothing, so the only thing that arrives here is a store that
    // tried and could not, and that is written down and gone no further with.
    private void SetTimes(OpenEntry open)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            Await(_provider.SetTimesAsync(open.Path, open.Times));
        }
        catch (ProviderException exception)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning(
                    "Setting the times on {Path} failed after {Elapsed} ms: {Reason}.",
                    open.Path,
                    Elapsed(started),
                    exception.Error);
            }

            return;
        }

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("Set the times on {Path} in {Elapsed} ms.", open.Path, Elapsed(started));
        }
    }

    // What the handle looks like now: what the store said when it was opened, with the length
    // it has been written to on this side and the times that have been set on it, which are
    // the only parts of it a write changes before the upload.
    private FileInfo FileInfoOf(OpenEntry open)
    {
        FileInfo info = ToFileInfo(open.Entry);

        if (open.Stage is WriteStage stage)
        {
            info.FileSize = (ulong)stage.Length;
            info.AllocationSize = AllocationSizeOf(info.FileSize);
        }

        // Answered from the handle rather than from the store: whoever set a time is
        // entitled to read it back, whether or not the store has it yet, and whether or not
        // the store will ever have it.
        if (open.Times.Created is DateTimeOffset created)
        {
            info.CreationTime = ToFileTime(created, info.CreationTime);
        }

        if (open.Times.LastModified is DateTimeOffset modified)
        {
            ulong written = ToFileTime(modified, info.LastWriteTime);

            info.LastWriteTime = written;
            info.ChangeTime = written;
        }

        return info;
    }

    private FileInfo ToFileInfo(RemoteEntry entry)
    {
        ulong size = 0;

        if (!entry.IsDirectory && entry.Length is long length && length > 0)
        {
            size = (ulong)length;
        }

        // A store that named no time gets the time of the mount. A zero would be shown as
        // the first of January 1601, which looks like a defect rather than a silence.
        ulong written = ToFileTime(entry.LastModified, _mountTime);
        ulong created = ToFileTime(entry.Created, written);

        return new FileInfo
        {
            FileAttributes = AttributesOf(entry),
            AllocationSize = AllocationSizeOf(size),
            FileSize = size,
            CreationTime = created,
            LastAccessTime = written,
            LastWriteTime = written,
            ChangeTime = written,
        };
    }

    // What an open handle carries: where the entry is in the store, what it looked like when
    // it was opened, and what has been written to it since. WinFsp keeps this for us and hands
    // it back on every call.
    private sealed class OpenEntry(string path, RemoteEntry entry, ReadWindow window)
    {
        public string Path { get; } = path;

        public RemoteEntry Entry { get; } = entry;

        public ReadWindow Window { get; } = window;

        // Opened by the first write and let go at Close. While it is here it is what the file
        // is: what has been written into it is read back out of it.
        public WriteStage? Stage { get; set; }

        // Whether what the store holds under this name is beside the point. Set where the
        // entry was just made and where it was opened to be replaced; it is what says that a
        // write to part of the file has nothing to fetch first.
        public bool Emptied { get; set; }

        // What has been set on this handle and is to be true of the entry, which is empty
        // until somebody says otherwise. Windows sets the times of a copied file on the
        // handle it copied it through, and this is where they wait for the upload.
        public EntryTimes Times { get; set; }
    }

    // One walk through one directory listing, kept between calls to ReadDirectoryEntry.
    private sealed class DirectoryScan(List<RemoteEntry> entries)
    {
        private int _next;

        public RemoteEntry? Next() => _next < entries.Count ? entries[_next++] : null;
    }
}
