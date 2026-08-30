// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Fsp;
using Xunit;

namespace WinDav.Fs.Tests;

// What the read path costs, counted in requests. The store writes down every range it was
// asked for, so what is asserted here is how often the server was troubled — and, because a
// window served from the wrong place is worse than a slow mount, that the bytes handed back
// are the bytes of the file.
public sealed class ReadAheadTests
{
    private const string Path = "/big.bin";
    private const string Name = "\\big.bin";

    // Small enough to read through in a test, and no number a multiple of another, so that a
    // window read from the wrong offset lands on the wrong bytes.
    private static readonly ReadSettings s_window = new() { Window = 4096, Total = 1 << 20 };

    [Fact]
    public void AReadThatContinuesTheLastOneFillsTheWindowAndTheRestComesOutOfIt()
    {
        FakeStore store = new();
        byte[] content = store.AddFileOfSize(Path, 20000);

        WinDavFileSystem fileSystem = Mount(store, s_window);
        object handle = Open(fileSystem);

        for (int offset = 0; offset < 6000; offset += 1000)
        {
            Assert.Equal(content[offset..(offset + 1000)], ReadAt(fileSystem, handle, offset, 1000));
        }

        // Six reads, three requests. The first read of a handle continues nothing and is a
        // request of its own; the two others are windows, and the four reads between them
        // came out of memory.
        (long Offset, long? Count)[] expected = [(0, 1000), (1000, 4096), (5000, 4096)];

        Assert.Equal(expected, store.Reads);
    }

    [Fact]
    public void AReadThatJumpsIsARequestOfItsOwnAndLeavesTheWindowWhereItWas()
    {
        FakeStore store = new();
        byte[] content = store.AddFileOfSize(Path, 20000);

        WinDavFileSystem fileSystem = Mount(store, s_window);
        object handle = Open(fileSystem);

        ReadAt(fileSystem, handle, 0, 1000);
        ReadAt(fileSystem, handle, 1000, 1000);

        // Away from the window, then back into it.
        Assert.Equal(content[12000..13000], ReadAt(fileSystem, handle, 12000, 1000));
        Assert.Equal(content[2000..3000], ReadAt(fileSystem, handle, 2000, 1000));

        // The jump fetched what it asked for and not a byte beyond it, and what it skipped
        // over was still there for the read that came back.
        (long Offset, long? Count)[] expected = [(0, 1000), (1000, 4096), (12000, 1000)];

        Assert.Equal(expected, store.Reads);
    }

    [Fact]
    public void AReadWiderThanTheWindowIsNeverServedFromIt()
    {
        FakeStore store = new();
        byte[] content = store.AddFileOfSize(Path, 20000);

        WinDavFileSystem fileSystem = Mount(store, s_window);
        object handle = Open(fileSystem);

        ReadAt(fileSystem, handle, 0, 1000);

        // Twice the window. Serving this out of a window would hand back less than was asked
        // for, and a short transfer is how the cache manager is told about the end of a file.
        Assert.Equal(content[1000..9192], ReadAt(fileSystem, handle, 1000, 8192));

        // Nothing was kept, so a read back over the same bytes costs its own request again.
        Assert.Equal(content[1000..1100], ReadAt(fileSystem, handle, 1000, 100));

        (long Offset, long? Count)[] expected = [(0, 1000), (1000, 8192), (1000, 100)];

        Assert.Equal(expected, store.Reads);
    }

    [Fact]
    public void AFileThatFitsInTheWindowIsFetchedWholeAndOnce()
    {
        FakeStore store = new();
        byte[] content = store.AddFileOfSize(Path, 300);

        WinDavFileSystem fileSystem = Mount(store, s_window);
        object handle = Open(fileSystem);

        // The first read lands in the middle and the file is fetched from its start anyway:
        // a second request would cost more than the bytes it saves.
        Assert.Equal(content[100..200], ReadAt(fileSystem, handle, 100, 100));
        Assert.Equal(content[0..50], ReadAt(fileSystem, handle, 0, 50));
        Assert.Equal(content[250..300], ReadAt(fileSystem, handle, 250, 50));

        (long Offset, long? Count)[] expected = [(0, 300)];

        Assert.Equal(expected, store.Reads);
    }

    [Fact]
    public void AWindowNeverAsksForMoreThanTheFileHolds()
    {
        FakeStore store = new();
        byte[] content = store.AddFileOfSize(Path, 5000);

        WinDavFileSystem fileSystem = Mount(store, s_window);
        object handle = Open(fileSystem);

        ReadAt(fileSystem, handle, 0, 1000);
        ReadAt(fileSystem, handle, 1000, 1000);

        // The window would reach to 5096; the file ends at 5000, and that is where the
        // request ends. The last bytes of the file are in it.
        Assert.Equal(content[4500..5000], ReadAt(fileSystem, handle, 4500, 500));

        (long Offset, long? Count)[] expected = [(0, 1000), (1000, 4000)];

        Assert.Equal(expected, store.Reads);
    }

    [Fact]
    public void EveryHandleReadsIntoItsOwnWindow()
    {
        FakeStore store = new();
        byte[] content = store.AddFileOfSize(Path, 20000);

        WinDavFileSystem fileSystem = Mount(store, s_window);
        object first = Open(fileSystem);
        object second = Open(fileSystem);

        ReadAt(fileSystem, first, 0, 1000);
        ReadAt(fileSystem, first, 1000, 1000);

        // The same file and the same bytes, through another handle: the window of the first
        // is none of its business, and its own first read is a request like any other.
        Assert.Equal(content[0..1000], ReadAt(fileSystem, second, 0, 1000));

        (long Offset, long? Count)[] expected = [(0, 1000), (1000, 4096), (0, 1000)];

        Assert.Equal(expected, store.Reads);
    }

    [Fact]
    public void TheCeilingIsSharedBetweenHandlesAndGivenBackWhenOneCloses()
    {
        FakeStore store = new();

        store.AddFileOfSize(Path, 20000);

        // Room for exactly one window.
        WinDavFileSystem fileSystem = Mount(store, new ReadSettings { Window = 4096, Total = 4096 });

        object first = Open(fileSystem);

        ReadAt(fileSystem, first, 0, 1000);
        ReadAt(fileSystem, first, 1000, 1000);

        store.Reads.Clear();

        // Nothing left to read ahead into, so this handle reads the way a mount without a
        // window reads: a request per read, and no failure anywhere.
        object second = Open(fileSystem);

        ReadAt(fileSystem, second, 0, 1000);
        ReadAt(fileSystem, second, 1000, 1000);

        (long Offset, long? Count)[] refused = [(0, 1000), (1000, 1000)];

        Assert.Equal(refused, store.Reads);

        store.Reads.Clear();

        // The first handle gives its window back, and the next handle gets one.
        fileSystem.Close(null, first);

        object third = Open(fileSystem);

        ReadAt(fileSystem, third, 0, 1000);
        ReadAt(fileSystem, third, 1000, 1000);

        (long Offset, long? Count)[] granted = [(0, 1000), (1000, 4096)];

        Assert.Equal(granted, store.Reads);
    }

    [Fact]
    public void AWindowOfNothingIsARequestPerRead()
    {
        FakeStore store = new();
        byte[] content = store.AddFileOfSize(Path, 20000);

        WinDavFileSystem fileSystem = Mount(store, new ReadSettings { Window = 0 });
        object handle = Open(fileSystem);

        // What the mount did before there was a window at all, and what a report about a
        // wrong byte is narrowed down with.
        Assert.Equal(content[0..1000], ReadAt(fileSystem, handle, 0, 1000));
        Assert.Equal(content[1000..2000], ReadAt(fileSystem, handle, 1000, 1000));

        (long Offset, long? Count)[] expected = [(0, 1000), (1000, 1000)];

        Assert.Equal(expected, store.Reads);
    }

    [Fact]
    public void ACeilingOfNothingIsNoCeilingAtAll()
    {
        FakeStore store = new();

        store.AddFileOfSize(Path, 20000);

        WinDavFileSystem fileSystem = Mount(store, new ReadSettings { Window = 4096, Total = 0 });

        (long Offset, long? Count)[] expected = [(0, 1000), (1000, 4096)];

        // What bounds the windows now is how many files are open, which is what switching a
        // ceiling off means and why it is not the default.
        for (int handle = 0; handle < 3; handle++)
        {
            object open = Open(fileSystem);

            store.Reads.Clear();

            ReadAt(fileSystem, open, 0, 1000);
            ReadAt(fileSystem, open, 1000, 1000);

            Assert.Equal(expected, store.Reads);
        }
    }

    private static WinDavFileSystem Mount(FakeStore store, ReadSettings reads)
    {
        return new WinDavFileSystem(
            store,
            new MountSettings { VolumeLabel = "Test", Read = reads });
    }

    private static object Open(WinDavFileSystem fileSystem)
    {
        int status = fileSystem.Open(Name, 0, 0, out _, out object? fileDesc, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.NotNull(fileDesc);

        return fileDesc;
    }

    private static byte[] ReadAt(WinDavFileSystem fileSystem, object handle, long offset, int count)
    {
        IntPtr buffer = Marshal.AllocHGlobal(count);

        try
        {
            int status = fileSystem.Read(
                null, handle, buffer, (ulong)offset, (uint)count, out uint transferred);

            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);

            byte[] taken = new byte[transferred];

            Marshal.Copy(buffer, taken, 0, (int)transferred);

            return taken;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
