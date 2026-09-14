// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace WinDav.Core.Tests;

public sealed class WindowStreamTests
{
    [Fact]
    public async Task AWindowReadsItsStretchAndNothingElse()
    {
        byte[] file = Pattern(100);
        using MemoryStream source = new(file);
        using WindowStream window = new(source, 10, 20, new Lock());
        using MemoryStream copy = new();

        await window.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(file[10..30], copy.ToArray());
    }

    // The way two chunks of one file are out at once: each window puts the position where it
    // needs it, so neither reads what belongs to the other.
    [Fact]
    public void WindowsOntoOneStreamReadByTurnsKeepToTheirOwnStretch()
    {
        byte[] file = Pattern(100);
        using MemoryStream source = new(file);
        Lock gate = new();
        using WindowStream front = new(source, 0, 50, gate);
        using WindowStream back = new(source, 50, 50, gate);
        byte[] first = new byte[50];
        byte[] second = new byte[50];

        for (int offset = 0; offset < 50; offset += 10)
        {
            front.ReadExactly(first, offset, 10);
            back.ReadExactly(second, offset, 10);
        }

        Assert.Equal(file[..50], first);
        Assert.Equal(file[50..], second);
    }

    // Positions are counted from the start of the window, not of the stream.
    [Fact]
    public void AWindowIsSoughtWithinItsOwnStretch()
    {
        byte[] file = Pattern(100);
        using MemoryStream source = new(file);
        using WindowStream window = new(source, 40, 10, new Lock());
        byte[] read = new byte[10];

        window.ReadExactly(read);
        window.Position = 0;
        window.ReadExactly(read);

        Assert.Equal(file[40..50], read);
        Assert.Equal(10L, window.Length);
        Assert.Equal(7L, window.Seek(-3, SeekOrigin.End));
    }

    // The window promised its length. A stream that has come up short since then would send
    // something that is not the file, which is a failure and not an early end.
    [Fact]
    public void AStreamThatEndsInsideTheWindowIsAFailure()
    {
        using MemoryStream source = new(Pattern(30));
        using WindowStream window = new(source, 20, 20, new Lock());
        byte[] buffer = new byte[20];

        window.ReadExactly(buffer, 0, 10);

        Assert.Throws<EndOfStreamException>(() => window.Read(buffer, 10, 10));
    }

    [Fact]
    public void TheStreamOutlivesItsWindows()
    {
        using MemoryStream source = new(Pattern(10));

        new WindowStream(source, 0, 10, new Lock()).Dispose();

        Assert.True(source.CanRead);
    }

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }
}
