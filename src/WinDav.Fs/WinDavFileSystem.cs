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
/// Everything here is read. Every operation that would change something answers
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
    private readonly MountSettings _settings;
    private readonly ILogger _log;
    private readonly ReadLayer _reads;
    private readonly byte[] _security;
    private readonly ulong _mountTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();

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
        _settings = settings;
        _log = loggerFactory?.CreateLogger(typeof(WinDavFileSystem)) ?? NullLogger.Instance;
        _reads = new ReadLayer(provider, settings.Read, _log, recovery: null, gate);
        _root = NormaliseRoot(settings.RemotePath);

        RawSecurityDescriptor descriptor = new(RootSddl);

        _security = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(_security, 0);
    }

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

        // What a WebDAV server is: names keep their case, and two names that differ only in
        // case are the same name.
        fileSystemHost.CaseSensitiveSearch = false;
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

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug("Opened {Path} in {Elapsed} ms.", path, Elapsed(started));
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

        fileInfo = ToFileInfo(open.Entry);

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

    // The end of one handle, and the only reason this is here: the window it read through
    // belongs to the mount's ceiling and has to go back, whether the file was read to the end
    // or dropped after a kilobyte. Nothing else of ours outlives an open.
    /// <inheritdoc/>
    public override void Close(object? fileNode, object fileDesc)
    {
        if (fileDesc is OpenEntry open)
        {
            open.Window.Close();
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

            // What is counted is what was fetched, which for an enumeration that was resumed
            // is what is left after the marker rather than the whole directory.
            List<RemoteEntry> children = ChildrenOf(open.Path, marker);

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Listed {Count} entries of {Path} in {Elapsed} ms.",
                    children.Count,
                    open.Path,
                    Elapsed(started));
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

    // Nothing is held back on this side, so there is nothing to flush and nothing that can
    // fail. Answering anything else would put an error on an operation that changed nothing.
    /// <inheritdoc/>
    public override int Flush(object? fileNode, object? fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;

        return STATUS_SUCCESS;
    }

    /// <inheritdoc/>
    public override int ExceptionHandler(Exception exception)
    {
        return exception is ProviderException provider
            ? ProviderStatus.From(provider)
            : base.ExceptionHandler(exception);
    }

    // == Everything that would change something ==
    //
    // All of it answers the one status Windows can phrase, so that a person is told the
    // volume is read only instead of being shown a code. CreateEx and OverwriteEx are not
    // among them: WinFsp passes them on to Create and Overwrite, which are here.

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

        return Refused(nameof(Create));
    }

    /// <inheritdoc/>
    public override int Overwrite(
        object? fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;

        return Refused(nameof(Overwrite));
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

        return Refused(nameof(Write));
    }

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
        fileInfo = default;

        return Refused(nameof(SetBasicInfo));
    }

    /// <inheritdoc/>
    public override int SetFileSize(
        object? fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;

        return Refused(nameof(SetFileSize));
    }

    // Also the answer to SetDelete, which WinFsp hands on to this.
    /// <inheritdoc/>
    public override int CanDelete(object? fileNode, object fileDesc, string fileName) =>
        Refused(nameof(CanDelete));

    /// <inheritdoc/>
    public override int Rename(
        object? fileNode,
        object fileDesc,
        string fileName,
        string newFileName,
        bool replaceIfExists) => Refused(nameof(Rename));

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

    // "The drive is read only" is the first thing a person asks about, and Windows' own
    // wording for it names no operation, so the operation is named here.
    private int Refused(string operation)
    {
        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("Refused {Operation}: everything on this volume is read only.", operation);
        }

        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    // == The seam between the two worlds ==

    // WinFsp dispatches every request on a thread of its own and expects an answer on it.
    // There is no synchronisation context to deadlock against, so waiting here is what the
    // binding is built for: the alternative would be an asynchronous file system that WinFsp
    // has no way to call.
    private static TResult Await<TResult>(Task<TResult> task) => task.GetAwaiter().GetResult();

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

    // What an open handle carries: where the entry is in the store, and what it looked like
    // when it was opened. WinFsp keeps this for us and hands it back on every call.
    private sealed class OpenEntry(string path, RemoteEntry entry, ReadWindow window)
    {
        public string Path { get; } = path;

        public RemoteEntry Entry { get; } = entry;

        public ReadWindow Window { get; } = window;
    }

    // One walk through one directory listing, kept between calls to ReadDirectoryEntry.
    private sealed class DirectoryScan(List<RemoteEntry> entries)
    {
        private int _next;

        public RemoteEntry? Next() => _next < entries.Count ? entries[_next++] : null;
    }
}
