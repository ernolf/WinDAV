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

    // Two dates far enough apart to tell which of them ended up where.
    private static readonly DateTimeOffset s_created = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static readonly DateTimeOffset s_modified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

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
    public void WhatTheVolumeStillWillNotTakeSaysSoInWordsWindowsKnows()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(Refused, fileSystem.SetSecurity(null, fileDesc, AccessControlSections.Access, []));
        Assert.Equal(Refused, fileSystem.SetVolumeLabel("Anything", out _));
    }

    [Fact]
    public void AFileIsMadeEmptyBeforeAnythingIsWrittenToIt()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, "\\new.txt", out object? fileDesc, out FileInfo fileInfo));

        Assert.NotNull(fileDesc);
        Assert.Equal(0UL, fileInfo.FileSize);

        // What Windows is told about a name the store has never heard of, which is what a
        // file just made is: a file and not a directory, and nothing in it. See decision 86.
        Assert.Equal((uint)FileAttributes.Normal, fileInfo.FileAttributes);
        Assert.Null(store.At("/new.txt"));

        fileSystem.Close(null, fileDesc);
    }

    [Fact]
    public void AFileThatIsAlreadyThereIsACollisionAndNotAFailure()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        // The status WinFsp answers by falling back to an Open, which is how FILE_OPEN_IF is
        // served without anything being written over.
        Assert.Equal(
            FileSystemBase.STATUS_OBJECT_NAME_COLLISION,
            CreateFile(fileSystem, "\\note.txt", out _, out _));

        Assert.Equal("hello", store.ContentOf("/note.txt"));
    }

    [Fact]
    public void AFileIsNotSentWhenItIsMadeButWhenItIsLetGo()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, @"\new.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        // Nothing has gone out. An upload here would be an upload of nothing, made worthless
        // a moment later by the one that carries the contents.
        Assert.Empty(store.Writes);
        Assert.Null(store.At("/new.txt"));

        WriteAt(fileSystem, fileDesc, 0, "hello");

        fileSystem.Cleanup(null, fileDesc, @"\new.txt", 0);
        fileSystem.Close(null, fileDesc);

        Assert.Equal("hello", store.ContentOf("/new.txt"));
        Assert.True(Assert.Single(store.Writes).MustBeNew);
    }

    [Fact]
    public void AFileMadeAndLetGoWithNothingInItStillReachesTheStore()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, @"\empty.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        fileSystem.Cleanup(null, fileDesc, @"\empty.txt", 0);
        fileSystem.Close(null, fileDesc);

        // One upload rather than none: a file nobody wrote a byte into is still a file.
        Assert.Empty(Assert.Single(store.Writes).Content);
        Assert.NotNull(store.At("/empty.txt"));
    }

    [Fact]
    public void AWriteOntoAFileThatIsAlreadyThereDoesNotAskToBeTheOneMakingIt()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, @"\note.txt");

        WriteAt(fileSystem, fileDesc, 0, "HELLO");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Flush(null, fileDesc, out _));

        Assert.False(Assert.Single(store.Writes).MustBeNew);
    }

    [Fact]
    public void ANameMadeHereIsThereBeforeTheStoreHasHeardOfIt()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, @"\new.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        byte[]? descriptor = null;

        // Windows has been told the file exists, so the mount has to keep saying so while it
        // is the only one that knows.
        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.GetSecurityByName(@"\new.txt", out uint attributes, ref descriptor));

        Assert.Equal((uint)FileAttributes.Normal, attributes);
    }

    [Fact]
    public void ASecondCreateOfANameMadeHereIsACollision()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, @"\new.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        // The answer a store that already had the name would have given, which is the one
        // WinFsp turns into an Open of what is there.
        Assert.Equal(
            FileSystemBase.STATUS_OBJECT_NAME_COLLISION,
            CreateFile(fileSystem, @"\new.txt", out _, out _));
    }

    [Fact]
    public void AFileMadeAndTakenAwayAgainNeverReachesTheStore()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, @"\scratch.tmp", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        WriteAt(fileSystem, fileDesc, 0, "hello");

        fileSystem.Cleanup(null, fileDesc, @"\scratch.tmp", FileSystemBase.CleanupDelete);
        fileSystem.Close(null, fileDesc);

        // Neither the upload nor the delete: the store was never told the name, so there is
        // nothing there to write and nothing there to take away.
        Assert.Empty(store.Writes);
        Assert.Null(store.At("/scratch.tmp"));

        // And the name is free again.
        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, @"\scratch.tmp", out object? again, out _));

        Assert.NotNull(again);

        fileSystem.Close(null, again);
    }

    [Fact]
    public void AFileMadeHereIsSentBeforeItIsMoved()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, @"\new.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        WriteAt(fileSystem, fileDesc, 0, "hello");

        // A name the store has not been given has nothing there to move, so what the handle
        // holds goes up first and the move then has something to move.
        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.Rename(null, fileDesc, @"\new.txt", @"\note.txt", false));

        fileSystem.Cleanup(null, fileDesc, @"\note.txt", 0);
        fileSystem.Close(null, fileDesc);

        Assert.Equal("hello", store.ContentOf("/note.txt"));
        Assert.Null(store.At("/new.txt"));
        Assert.Single(store.Writes);
    }

    [Fact]
    public void WhatIsWrittenIsReadBackFromTheHandleThatWroteIt()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        WriteAt(fileSystem, fileDesc, 0, "HELLO");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, Read(fileSystem, fileDesc, 0, 5, out byte[] taken));
        Assert.Equal("HELLO", Encoding.UTF8.GetString(taken));

        // Nothing has gone out yet, so what is read back can only have come from this side.
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void WritingToPartOfAFileSendsTheWholeOfItBack()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        WriteAt(fileSystem, fileDesc, 0, "H");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Flush(null, fileDesc, out _));

        // A store like this is handed a file whole, so the rest of it had to be fetched here
        // before one letter of it could be changed.
        Assert.Equal("Hello", store.ContentOf("/note.txt"));
    }

    [Fact]
    public void WhatIsWrittenGoesOutAtTheFlushAndNotAgainAtTheCleanup()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, "\\new.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        WriteAt(fileSystem, fileDesc, 0, "hello");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Flush(null, fileDesc, out _));

        fileSystem.Cleanup(null, fileDesc, "\\new.txt", 0);
        fileSystem.Close(null, fileDesc);

        Assert.Single(store.Writes);
        Assert.Equal("hello", store.ContentOf("/new.txt"));
    }

    [Fact]
    public void WhatIsNeverFlushedStillGoesOutAtTheCleanup()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, "\\new.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        // What the Explorer does when it copies a file: it never asks for the buffers to be
        // flushed, and Cleanup is the last call that can still reach the store.
        WriteAt(fileSystem, fileDesc, 0, "hello");

        fileSystem.Cleanup(null, fileDesc, "\\new.txt", 0);
        fileSystem.Close(null, fileDesc);

        Assert.Single(store.Writes);
        Assert.Equal("hello", store.ContentOf("/new.txt"));
    }

    [Fact]
    public void AWriteIsMadeConditionalOnTheTagTheFileWasOpenedWith()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello", eTag: "v1");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        WriteAt(fileSystem, fileDesc, 0, "HELLO");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Flush(null, fileDesc, out _));

        Assert.Equal("v1", Assert.Single(store.Writes).IfMatch);
    }

    [Fact]
    public void AFileSomebodyElseChangedIsNotWrittenOver()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello", eTag: "v1");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        WriteAt(fileSystem, fileDesc, 0, "HELLO");

        store.AddFile("/note.txt", "somebody else", eTag: "v2");

        // The condition the upload was made on does not hold any more, and Windows has a
        // wording for that: the file is in use by somebody else.
        Assert.Equal(
            FileSystemBase.STATUS_SHARING_VIOLATION,
            fileSystem.Flush(null, fileDesc, out _));

        Assert.Equal("somebody else", store.ContentOf("/note.txt"));
    }

    [Fact]
    public void AFileOpenedToBeReplacedIsNotFetchedFirst()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.Overwrite(null, fileDesc, 0, false, 0, out FileInfo emptied));

        Assert.Equal(0UL, emptied.FileSize);

        WriteAt(fileSystem, fileDesc, 0, "new");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Flush(null, fileDesc, out _));

        Assert.Equal("new", store.ContentOf("/note.txt"));

        // What was in the file was on its way out, so fetching it would have been a request
        // spent on bytes nobody was going to look at.
        Assert.Empty(store.Opened);
    }

    [Fact]
    public void ASizeCutsTheFileAndAnAllocationSizeLeavesItAlone()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        // Windows says how much room it expects to need before it writes; a store that is
        // handed the file whole has nothing to do with the figure.
        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.SetFileSize(null, fileDesc, 4096, true, out FileInfo hinted));

        Assert.Equal(5UL, hinted.FileSize);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.SetFileSize(null, fileDesc, 3, false, out FileInfo cut));

        Assert.Equal(3UL, cut.FileSize);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Flush(null, fileDesc, out _));

        Assert.Equal("hel", store.ContentOf("/note.txt"));
    }

    [Fact]
    public void TheTimesWindowsSetsOnACopyTravelWithIt()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            CreateFile(fileSystem, "\\new.txt", out object? fileDesc, out _));

        Assert.NotNull(fileDesc);

        WriteAt(fileSystem, fileDesc, 0, "hello");

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.SetBasicInfo(null, fileDesc, 0, FileTime(s_created), 0, FileTime(s_modified), 0, out _));

        fileSystem.Cleanup(null, fileDesc, "\\new.txt", 0);
        fileSystem.Close(null, fileDesc);

        // On the upload and nowhere else: a request of their own would be one more round
        // trip for every file of a copy.
        Assert.Equal(new EntryTimes(s_created, s_modified), Assert.Single(store.Writes).Times);
        Assert.Empty(store.TimesSet);
    }

    [Fact]
    public void TheTimesGoOnTheirOwnWhereThereIsNothingLeftToSend()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.SetBasicInfo(null, fileDesc, 0, FileTime(s_created), 0, FileTime(s_modified), 0, out _));

        Assert.Equal(new EntryTimes(s_created, s_modified), Assert.Single(store.TimesSet).Times);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void ATimeWindowsDidNotNameIsNotSet()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        // A zero is Windows saying it has not touched the time, and the all-ones is it
        // saying it will not touch it again. Neither is a date, and a call carrying nothing
        // but those two is a call with nothing to do.
        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.SetBasicInfo(null, fileDesc, 0, 0, ulong.MaxValue, ulong.MaxValue, 0, out _));

        Assert.Empty(store.TimesSet);
    }

    [Fact]
    public void HalfATimeLeavesTheOtherHalfAsItWas()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        fileSystem.SetBasicInfo(null, fileDesc, 0, FileTime(s_created), 0, 0, 0, out _);
        fileSystem.SetBasicInfo(null, fileDesc, 0, 0, 0, FileTime(s_modified), 0, out _);

        Assert.Equal(new EntryTimes(s_created, null), store.TimesSet[0].Times);
        Assert.Equal(new EntryTimes(s_created, s_modified), store.TimesSet[1].Times);
    }

    [Fact]
    public void WhatWasSetIsWhatTheHandleAnswersWith()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.SetBasicInfo(
                null,
                fileDesc,
                0,
                FileTime(s_created),
                0,
                FileTime(s_modified),
                0,
                out FileInfo info));

        Assert.Equal(FileTime(s_created), info.CreationTime);
        Assert.Equal(FileTime(s_modified), info.LastWriteTime);
        Assert.Equal(FileTime(s_modified), info.ChangeTime);
    }

    [Fact]
    public void ATimeTheStoreWillNotTakeDoesNotFailTheCall()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        store.FailWith = ProviderError.PermissionDenied;

        // A time is worth less than the file it belongs to, and this is the last call of a
        // copy that has otherwise gone through.
        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.SetBasicInfo(null, fileDesc, 0, FileTime(s_created), 0, FileTime(s_modified), 0, out _));
    }

    [Fact]
    public void AFileThatIsBeingDeletedIsNotUploadedFirst()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        WriteAt(fileSystem, fileDesc, 0, "HELLO");

        fileSystem.Cleanup(null, fileDesc, "\\note.txt", FileSystemBase.CleanupDelete);
        fileSystem.Close(null, fileDesc);

        Assert.Empty(store.Writes);
        Assert.Null(store.At("/note.txt"));
    }

    [Fact]
    public void ADirectoryIsMadeAndComesBackOpen()
    {
        FakeStore store = new();

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            Create(fileSystem, "\\photos", out object? fileDesc, out FileInfo fileInfo));

        Assert.NotNull(fileDesc);
        Assert.Equal((uint)FileAttributes.Directory, fileInfo.FileAttributes);

        RemoteEntry? made = store.At("/photos");

        Assert.NotNull(made);
        Assert.True(made.IsDirectory);

        // The handle is one WinFsp will close, so what a directory holds has to be enterable
        // and leavable through it like any other.
        fileSystem.Close(null, fileDesc);
    }

    [Fact]
    public void ADirectoryThatIsAlreadyThereIsACollisionAndNotAFailure()
    {
        FakeStore store = new();
        store.AddDirectory("/photos");

        WinDavFileSystem fileSystem = Mount(store);

        Assert.Equal(
            FileSystemBase.STATUS_OBJECT_NAME_COLLISION,
            Create(fileSystem, "\\photos", out _, out _));
    }

    [Fact]
    public void AskingToDeleteOnCloseIsRefusedAtTheCreateAsWell()
    {
        WinDavFileSystem fileSystem = Mount(new FakeStore());

        // Nothing asks CanDelete about a file opened this way, so the one test a directory
        // gets before it goes would be skipped.
        Assert.Equal(
            Refused,
            fileSystem.Create(
                "\\photos",
                FileSystemBase.FILE_DIRECTORY_FILE | FileSystemBase.FILE_DELETE_ON_CLOSE,
                0,
                0,
                [],
                0,
                out _,
                out _,
                out _,
                out _));
    }

    [Fact]
    public void AFileIsGoneWhenTheLastHandleToItGoes()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.CanDelete(null, fileDesc, "\\note.txt"));

        fileSystem.Cleanup(null, fileDesc, "\\note.txt", FileSystemBase.CleanupDelete);
        fileSystem.Close(null, fileDesc);

        Assert.Null(store.At("/note.txt"));
    }

    [Fact]
    public void ADirectoryWithSomethingInItIsNotDeleted()
    {
        FakeStore store = new();
        store.AddDirectory("/photos");
        store.AddFile("/photos/holiday.jpg", "picture");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\photos");

        // One request would take the whole tree, and the caller asked about a directory it
        // believes is empty.
        Assert.Equal(
            FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY,
            fileSystem.CanDelete(null, fileDesc, "\\photos"));

        Assert.NotNull(store.At("/photos/holiday.jpg"));
    }

    [Fact]
    public void ADirectoryWithNothingInItIsDeleted()
    {
        FakeStore store = new();
        store.AddDirectory("/photos");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\photos");

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.CanDelete(null, fileDesc, "\\photos"));

        fileSystem.Cleanup(null, fileDesc, "\\photos", FileSystemBase.CleanupDelete);
        fileSystem.Close(null, fileDesc);

        Assert.Null(store.At("/photos"));
    }

    [Fact]
    public void ACleanupThatIsNotADeleteTakesNothingAway()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        fileSystem.Cleanup(null, fileDesc, null, FileSystemBase.CleanupSetLastWriteTime);
        fileSystem.Close(null, fileDesc);

        Assert.NotNull(store.At("/note.txt"));
    }

    [Fact]
    public void ADeleteTheStoreRefusesGoesNoFurtherThanTheLog()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        store.FailWith = ProviderError.PermissionDenied;

        // Windows has no way to hear this one, and a failure that leaves as an exception
        // would take the mount with it.
        fileSystem.Cleanup(null, fileDesc, "\\note.txt", FileSystemBase.CleanupDelete);

        store.FailWith = null;

        Assert.NotNull(store.At("/note.txt"));
    }

    [Fact]
    public void ARenameMovesTheEntryInTheStore()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.Rename(null, fileDesc, "\\note.txt", "\\other.txt", false));

        Assert.Null(store.At("/note.txt"));
        Assert.NotNull(store.At("/other.txt"));
    }

    [Fact]
    public void ARenameOntoSomethingThatIsThereNeedsToBeAllowedTo()
    {
        FakeStore store = new();
        store.AddFile("/note.txt", "hello");
        store.AddFile("/other.txt", "there");

        WinDavFileSystem fileSystem = Mount(store);

        object fileDesc = OpenExisting(fileSystem, "\\note.txt");

        Assert.Equal(
            FileSystemBase.STATUS_OBJECT_NAME_COLLISION,
            fileSystem.Rename(null, fileDesc, "\\note.txt", "\\other.txt", false));

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.Rename(null, fileDesc, "\\note.txt", "\\other.txt", true));

        Assert.Null(store.At("/note.txt"));
    }

    [Fact]
    public void ARenameUnderAMountedFolderStaysUnderIt()
    {
        FakeStore store = new();
        store.AddDirectory("/photos");
        store.AddFile("/photos/holiday.jpg", "picture");

        WinDavFileSystem fileSystem = Mount(store, "/photos");

        object fileDesc = OpenExisting(fileSystem, "\\holiday.jpg");

        Assert.Equal(
            FileSystemBase.STATUS_SUCCESS,
            fileSystem.Rename(null, fileDesc, "\\holiday.jpg", "\\beach.jpg", false));

        Assert.NotNull(store.At("/photos/beach.jpg"));
    }

    [Fact]
    public void FlushingTheWholeVolumeHasNothingToDoAndSaysSo()
    {
        WinDavFileSystem fileSystem = Mount(new FakeStore());

        // WinFsp flushes the volume with nothing in hand at all. Nothing is held back that
        // does not belong to a handle, so there is nothing here that could fail.
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

    // A date as Windows hands it over, which is what a file time is.
    private static ulong FileTime(DateTimeOffset time) => (ulong)time.UtcDateTime.ToFileTimeUtc();

    private static WinDavFileSystem Mount(FakeStore store, string remotePath = "/")
    {
        return new WinDavFileSystem(
            store,
            new MountSettings { RemotePath = remotePath, VolumeLabel = "Test" });
    }

    private static int Create(
        WinDavFileSystem fileSystem,
        string fileName,
        out object? fileDesc,
        out FileInfo fileInfo)
    {
        return fileSystem.Create(
            fileName,
            FileSystemBase.FILE_DIRECTORY_FILE,
            0,
            0,
            [],
            0,
            out _,
            out fileDesc,
            out fileInfo,
            out _);
    }

    private static int CreateFile(
        WinDavFileSystem fileSystem,
        string fileName,
        out object? fileDesc,
        out FileInfo fileInfo)
    {
        return fileSystem.Create(
            fileName,
            0,
            0,
            0,
            [],
            0,
            out _,
            out fileDesc,
            out fileInfo,
            out _);
    }

    private static void WriteAt(
        WinDavFileSystem fileSystem,
        object fileDesc,
        ulong offset,
        string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);

            int status = fileSystem.Write(
                null,
                fileDesc,
                buffer,
                offset,
                (uint)bytes.Length,
                false,
                false,
                out uint transferred,
                out _);

            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
            Assert.Equal((uint)bytes.Length, transferred);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
