// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using WinDav.Abstractions;
using WinDav.Core;
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

    // A size has to be named and Windows shows it, but the seam has no call that asks a
    // store how much room it has. Until it does, this is a figure and not a measurement,
    // and it is deliberately large enough that nothing looks nearly full.
    private const ulong Capacity = 1UL << 40;

    // How long WinFsp may reuse what it was last told about an entry. Every miss is a
    // request over the network, and the Explorer asks for the same entry several times
    // while drawing one window. A second is short enough that a change on the server shows
    // up while somebody is still looking at the window.
    private const uint FileInfoTimeoutMilliseconds = 1000;

    private const int TransferBufferSize = 64 * 1024;

    private static readonly long s_fileTimeEpochTicks =
        new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private readonly IStorageProvider _provider;
    private readonly MountSettings _settings;
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
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public WinDavFileSystem(IStorageProvider provider, MountSettings settings)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(settings);

        _provider = provider;
        _settings = settings;
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
        volumeInfo = default;
        volumeInfo.TotalSize = Capacity;
        volumeInfo.FreeSize = Capacity;
        volumeInfo.SetVolumeLabel(_settings.VolumeLabel);

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

        try
        {
            RemoteEntry entry = Await(_provider.GetAsync(ToRemotePath(fileName)));

            fileAttributes = AttributesOf(entry);

            // Null means the caller wants the attributes only.
            if (securityDescriptor is not null)
            {
                securityDescriptor = _security;
            }

            return STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
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
                return STATUS_MEDIA_WRITE_PROTECTED;
            }

            fileDesc = new OpenEntry(path, entry);
            fileInfo = ToFileInfo(entry);

            return STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
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

        try
        {
            using Stream stream = Await(_provider.OpenReadAsync(open.Path, (long)offset, wanted));

            bytesTransferred = CopyInto(stream, buffer, (uint)wanted);

            return bytesTransferred == 0 ? STATUS_END_OF_FILE : STATUS_SUCCESS;
        }
        catch (ProviderException exception)
        {
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
            scan = new DirectoryScan(ChildrenOf(open.Path, marker));
            context = scan;
        }

        if (scan.Next() is not RemoteEntry child)
        {
            return false;
        }

        fileName = child.Name;
        fileInfo = ToFileInfo(child);

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

        return STATUS_MEDIA_WRITE_PROTECTED;
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

        return STATUS_MEDIA_WRITE_PROTECTED;
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

        return STATUS_MEDIA_WRITE_PROTECTED;
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

        return STATUS_MEDIA_WRITE_PROTECTED;
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

        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    // Also the answer to SetDelete, which WinFsp hands on to this.
    /// <inheritdoc/>
    public override int CanDelete(object? fileNode, object fileDesc, string fileName) =>
        STATUS_MEDIA_WRITE_PROTECTED;

    /// <inheritdoc/>
    public override int Rename(
        object? fileNode,
        object fileDesc,
        string fileName,
        string newFileName,
        bool replaceIfExists) => STATUS_MEDIA_WRITE_PROTECTED;

    /// <inheritdoc/>
    public override int SetSecurity(
        object? fileNode,
        object fileDesc,
        AccessControlSections sections,
        byte[] securityDescriptor) => STATUS_MEDIA_WRITE_PROTECTED;

    /// <inheritdoc/>
    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        volumeInfo = default;

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

    private static uint CopyInto(Stream stream, IntPtr buffer, uint length)
    {
        // The buffer belongs to WinFsp and is at most one transfer long, so the offset into
        // it stays well inside what an Int32 holds.
        byte[] chunk = ArrayPool<byte>.Shared.Rent(TransferBufferSize);

        try
        {
            uint written = 0;

            while (written < length)
            {
                int room = (int)Math.Min((uint)chunk.Length, length - written);
                int read = stream.Read(chunk, 0, room);

                if (read <= 0)
                {
                    break;
                }

                Marshal.Copy(chunk, 0, IntPtr.Add(buffer, (int)written), read);

                written += (uint)read;
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
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
        List<RemoteEntry> children = [.. Await(_provider.ListAsync(path))];

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
    private sealed class OpenEntry(string path, RemoteEntry entry)
    {
        public string Path { get; } = path;

        public RemoteEntry Entry { get; } = entry;
    }

    // One walk through one directory listing, kept between calls to ReadDirectoryEntry.
    private sealed class DirectoryScan(List<RemoteEntry> entries)
    {
        private int _next;

        public RemoteEntry? Next() => _next < entries.Count ? entries[_next++] : null;
    }
}
