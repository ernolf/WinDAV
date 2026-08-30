// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Fsp;
using WinDav.Abstractions;
using Xunit;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace WinDav.Fs.Tests;

// Everything here runs without the WinFsp driver: the file system is a plain object and is
// called the way WinFsp would call it. Only FileSystemHost needs the driver, and nothing
// here touches it.
public sealed class WinDavFileSystemTests
{
    private const int Refused = FileSystemBase.STATUS_MEDIA_WRITE_PROTECTED;

    [Fact]
    public void AStoreThatSaysNothingAboutItsRoomNamesASizeWithNothingInUse()
    {
        WinDavFileSystem fileSystem = Mount(new FakeStore());

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.GetVolumeInfo(out VolumeInfo volumeInfo));
        Assert.NotEqual(0UL, volumeInfo.TotalSize);
        Assert.Equal(volumeInfo.TotalSize, volumeInfo.FreeSize);
    }

    [Fact]
    public void TheVolumeIsWhatIsInUseAndWhatIsLeftPutTogether()
    {
        FakeStore store = new() { Space = new StorageSpace { Used = 3000, Available = 7000 } };

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.GetVolumeInfo(out VolumeInfo volumeInfo));
        Assert.Equal(10000UL, volumeInfo.TotalSize);
        Assert.Equal(7000UL, volumeInfo.FreeSize);
    }

    [Fact]
    public void AStoreWithoutALimitIsStillShownWithWhatItHolds()
    {
        // An account without a quota: what it holds is a figure, what is left in it is not.
        FakeStore store = new() { Space = new StorageSpace { Used = 3000 } };

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.GetVolumeInfo(out VolumeInfo volumeInfo));

        // The headroom is a figure of this program's own, so what is asserted is what it is
        // for: room enough that nothing looks full, with the real amount in use beside it.
        Assert.Equal(volumeInfo.FreeSize + 3000UL, volumeInfo.TotalSize);
        Assert.True(volumeInfo.FreeSize > 3000UL);
    }

    [Fact]
    public void AStoreThatCannotBeReachedStillNamesASize()
    {
        FakeStore store = new() { FailWith = ProviderError.Unreachable };

        WinDavFileSystem fileSystem = Mount(store);

        // A drive that answers its own size with an error is a drive that looks broken.
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.GetVolumeInfo(out VolumeInfo volumeInfo));
        Assert.NotEqual(0UL, volumeInfo.TotalSize);
        Assert.Equal(volumeInfo.TotalSize, volumeInfo.FreeSize);
    }

    [Fact]
    public void ANameThatIsNotThereIsAnsweredByName()
    {
        WinDavFileSystem fileSystem = Mount(new FakeStore());

        byte[]? descriptor = null;

        Assert.Equal(
            FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND,
            fileSystem.GetSecurityByName("\\gone.txt", out uint attributes, ref descriptor));

        Assert.Equal(0U, attributes);
    }

    [Fact]
    public void ADirectoryIsNeverReadOnly()
    {
        FakeStore store = new();
        store.AddDirectory("/photos");

        WinDavFileSystem fileSystem = Mount(store);

        byte[]? descriptor = [];

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.GetSecurityByName("\\photos", out uint attributes, ref descriptor));

        // Alone, without ReadOnly beside it: on a directory Windows reads that bit as
        // "customised" and goes looking for a desktop.ini.
        Assert.Equal((uint)FileAttributes.Directory, attributes);
        Assert.NotNull(descriptor);
        Assert.NotEmpty(descriptor);
    }

    [Fact]
    public void AFileTheStoreCallsUnwritableIsReadOnly()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello", EntryPermissions.Read);

        WinDavFileSystem fileSystem = Mount(store);

        byte[]? descriptor = null;

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.GetSecurityByName("\\note.txt", out uint attributes, ref descriptor));

        Assert.Equal((uint)FileAttributes.ReadOnly, attributes);
    }

    [Fact]
    public void AFileTheStoreSaidNothingAboutIsOrdinary()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        byte[]? descriptor = null;

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.GetSecurityByName("\\note.txt", out uint attributes, ref descriptor));

        // Saying nothing is not saying no.
        Assert.Equal((uint)FileAttributes.Normal, attributes);
    }

    [Fact]
    public void OpeningAFileAsADirectoryIsRefusedInWordsWindowsKnows()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_NOT_A_DIRECTORY,
            fileSystem.Open("\\note.txt", FileSystemBase.FILE_DIRECTORY_FILE, 0, out _, out _, out _, out _));
    }

    [Fact]
    public void OpeningADirectoryAsAFileIsRefusedInWordsWindowsKnows()
    {
        FakeStore store = new();
        store.AddDirectory("/photos");

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_FILE_IS_A_DIRECTORY,
            fileSystem.Open("\\photos", FileSystemBase.FILE_NON_DIRECTORY_FILE, 0, out _, out _, out _, out _));
    }

    [Fact]
    public void AskingToDeleteOnCloseIsRefusedAtTheOpen()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        // Refused here rather than at the close, where the caller has stopped listening.
        Assert.Equal(
            Refused,
            fileSystem.Open("\\note.txt", FileSystemBase.FILE_DELETE_ON_CLOSE, 0, out _, out _, out _, out _));
    }

    [Fact]
    public void AMountBelowTheRootShowsOnlyWhatIsUnderIt()
    {
        FakeStore store = new();
        store.AddDirectory("/photos");
        store.AddFile("/photos/note.txt", "hello");
        store.AddFile("/elsewhere.txt", "not yours");

        WinDavFileSystem fileSystem = Mount(store, "/photos");

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, Read(fileSystem, fileDesc, 0, 64, out byte[] taken));
        Assert.Equal("hello", Encoding.UTF8.GetString(taken));

        // The store was asked for the path it knows, not the one Windows used.
        Assert.Equal("/photos/note.txt", Assert.Single(store.Opened));

        byte[]? descriptor = null;

        Assert.Equal(
            FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND,
            fileSystem.GetSecurityByName("\\elsewhere.txt", out _, ref descriptor));
    }

    [Fact]
    public void WhatIsOpenedIsAnsweredWithoutAskingTheStoreAgain()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.GetFileInfo(null, fileDesc, out FileInfo fileInfo));
        Assert.Equal(5UL, fileInfo.FileSize);

        // Rounded up to the allocation unit, which is what a volume reports.
        Assert.Equal(4096UL, fileInfo.AllocationSize);
    }

    [Fact]
    public void AnEntryTheStoreGaveNoTimeForIsDatedToTheMount()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.GetFileInfo(null, fileDesc, out FileInfo fileInfo));

        // A zero would be shown as the first of January 1601, which reads as a defect.
        Assert.NotEqual(0UL, fileInfo.LastWriteTime);
        Assert.Equal(fileInfo.LastWriteTime, fileInfo.CreationTime);
    }

    [Fact]
    public void ATimeTheStoreGaveIsTheOneThatIsShown()
    {
        DateTimeOffset written = new(2026, 3, 1, 12, 30, 0, TimeSpan.Zero);

        FakeStore store = new();
        store.AddFile("/note.txt", "hello", lastModified: written);

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.GetFileInfo(null, fileDesc, out FileInfo fileInfo));
        Assert.Equal((ulong)written.UtcDateTime.ToFileTimeUtc(), fileInfo.LastWriteTime);
    }

    [Fact]
    public void ReadingPastTheEndSaysSoInsteadOfReturningNothing()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(FileSystemBase.STATUS_END_OF_FILE, Read(fileSystem, fileDesc, 5, 64, out _));

        // The store was never troubled with a read that could not return anything.
        Assert.Empty(store.Opened);
    }

    [Fact]
    public void AReadIsClampedToWhatTheStoreSaidTheFileHolds()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, Read(fileSystem, fileDesc, 2, 64, out byte[] taken));
        Assert.Equal("llo", Encoding.UTF8.GetString(taken));

        // Never past the end, whatever was asked for. A file this small fits in the window
        // and is fetched whole from its start, so the range is the file.
        Assert.Equal(0L, store.LastOffset);
        Assert.Equal(5L, store.LastCount);
    }

    [Fact]
    public void AFileWhoseLengthTheStoreDidNotNameIsStillRead()
    {
        FakeStore store = new();
        store.AddFileOfUnknownLength("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        // An unknown length must not turn every read of the file into an end of file.
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, Read(fileSystem, fileDesc, 0, 64, out byte[] taken));
        Assert.Equal("hello", Encoding.UTF8.GetString(taken));
        Assert.Equal(64L, store.LastCount);
    }

    [Fact]
    public void AStoreThatGoesAwayMidReadKeepsItsMeaning()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        store.FailWith = ProviderError.Unreachable;

        Assert.Equal(FileSystemBase.STATUS_UNEXPECTED_NETWORK_ERROR, Read(fileSystem, fileDesc, 0, 64, out _));
    }

    [Fact]
    public void ADirectoryIsListedInTheOrderTheVolumeDeclared()
    {
        FakeStore store = new();
        store.AddFile("/beta.txt", "b");
        store.AddFile("/Alpha.txt", "a");
        store.AddDirectory("/gamma");

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal("Alpha.txt, beta.txt, gamma", string.Join(", ", Listing(fileSystem, marker: null)));
    }

    [Fact]
    public void AListingResumesAfterTheEntryItWasGiven()
    {
        FakeStore store = new();
        store.AddFile("/beta.txt", "b");
        store.AddFile("/Alpha.txt", "a");
        store.AddDirectory("/gamma");

        WinDavFileSystem fileSystem = Mount(store);

        // What WinFsp does when an enumeration was interrupted: it names the last entry it
        // saw, and everything up to and including it is done with.
        Assert.Equal("beta.txt, gamma", string.Join(", ", Listing(fileSystem, "Alpha.txt")));
    }

    [Fact]
    public void AnEmptyDirectoryEndsAtOnce()
    {
        WinDavFileSystem fileSystem = Mount(new FakeStore());

        Assert.Empty(Listing(fileSystem, marker: null));
    }

    [Fact]
    public void EverythingThatWouldChangeSomethingSaysTheMediaIsWriteProtected()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(
            Refused,
            fileSystem.Create("\\new.txt", 0, 0, 0, [], 0, out _, out _, out _, out _));

        Assert.Equal(Refused, fileSystem.Overwrite(null, fileDesc, 0, false, 0, out _));
        Assert.Equal(Refused, fileSystem.Write(null, fileDesc, IntPtr.Zero, 0, 0, false, false, out _, out _));
        Assert.Equal(Refused, fileSystem.SetBasicInfo(null, fileDesc, 0, 0, 0, 0, 0, out _));
        Assert.Equal(Refused, fileSystem.SetFileSize(null, fileDesc, 0, false, out _));
        Assert.Equal(Refused, fileSystem.CanDelete(null, fileDesc, "\\note.txt"));
        Assert.Equal(Refused, fileSystem.Rename(null, fileDesc, "\\note.txt", "\\other.txt", false));
        Assert.Equal(Refused, fileSystem.SetSecurity(null, fileDesc, AccessControlSections.Access, []));
        Assert.Equal(Refused, fileSystem.SetVolumeLabel("Anything", out _));
    }

    [Fact]
    public void NothingIsHeldBackSoFlushingSucceeds()
    {
        WinDavFileSystem fileSystem = Mount(new FakeStore());

        // Also the answer when WinFsp flushes the whole volume, which it does with nothing
        // in hand at all.
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Flush(null, null, out _));
    }

    [Fact]
    public void AFailureThatLeavesAsAnExceptionStillNamesItsStatus()
    {
        WinDavFileSystem fileSystem = Mount(new FakeStore());

        // The way a failure out of ReadDirectoryEntry gets answered: its signature has no
        // room for a status, so WinFsp routes the exception through here.
        Assert.Equal(
            FileSystemBase.STATUS_ACCESS_DENIED,
            fileSystem.ExceptionHandler(new ProviderException(ProviderError.PermissionDenied)));
    }

    private static WinDavFileSystem Mount(FakeStore store, string remotePath = "/")
    {
        return new WinDavFileSystem(
            store,
            new MountSettings { RemotePath = remotePath, VolumeLabel = "Test" });
    }

    private static object OpenExisting(WinDavFileSystem fileSystem, string fileName)
    {
        int status = fileSystem.Open(fileName, 0, 0, out _, out object? fileDesc, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.NotNull(fileDesc);

        return fileDesc;
    }

    private static int Read(
        WinDavFileSystem fileSystem,
        object fileDesc,
        ulong offset,
        uint length,
        out byte[] taken)
    {
        IntPtr buffer = Marshal.AllocHGlobal((int)length);

        try
        {
            int status = fileSystem.Read(null, fileDesc, buffer, offset, length, out uint transferred);

            taken = new byte[transferred];

            Marshal.Copy(buffer, taken, 0, (int)transferred);

            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<string> Listing(WinDavFileSystem fileSystem, string? marker)
    {
        object directory = OpenExisting(fileSystem, "\\");

        object? context = null;
        List<string> names = [];

        while (fileSystem.ReadDirectoryEntry(null, directory, null, marker, ref context, out string? name, out _))
        {
            Assert.NotNull(name);

            names.Add(name);
        }

        return names;
    }
}
