// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Runtime.InteropServices;
using WinDav.Core;

namespace WinDav.Fs;

/// <summary>
/// Where the contents of one open handle are kept until the store has them.
/// </summary>
/// <remarks>
/// <para>
/// A store like this takes a file whole, and Windows hands it over in blocks, at any offset
/// and in any order. So the blocks need somewhere local to land before there is anything to
/// send, and that is what this is. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#86-a-write-is-finished-when-the-server-has-it">decision 86</see>.
/// </para>
/// <para>
/// Somewhere local is a file of its own in the temporary directory, opened so that Windows
/// takes it away the moment the handle to it closes. Nothing is left behind by a mount that
/// ends, however it ends, and nothing of a write survives a restart, which is the line
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#66-a-store-that-feels-like-a-sync-client-without-being-one">decision 66</see>
/// draws.
/// </para>
/// </remarks>
internal sealed class WriteStage : IDisposable
{
    // One trip between the memory WinFsp handed over and the staged file. The same size the
    // read path copies with, and for the same reason: it is a buffer, not a window.
    private const int TransferBufferSize = 64 * 1024;

    private const string StagedSuffix = ".part";

    private readonly FileStream _file;

    /// <summary>
    /// Initialises a new instance of the <see cref="WriteStage"/> class.
    /// </summary>
    /// <param name="eTag">
    /// What the entry carried when the handle was opened, or <see langword="null"/> when the
    /// store named none. It travels with the upload as its condition.
    /// </param>
    internal WriteStage(string? eTag)
    {
        ETag = eTag;

        _file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"{ProductInfo.Slug}-{Guid.NewGuid():N}{StagedSuffix}"),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.DeleteOnClose,
            });
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="WriteStage"/> class for a name the store
    /// has not been given yet.
    /// </summary>
    /// <remarks>
    /// It stands pending with nothing in it, so that a file made and closed again without a
    /// byte ever going into it still reaches the store, as one upload rather than none. There
    /// is no version to make it conditional on, because there is nothing there yet to have
    /// one; what stands in its place is the store being asked to refuse the write if the name
    /// has been taken in the meantime.
    /// </remarks>
    internal WriteStage()
        : this(eTag: null)
    {
        MustBeNew = true;
        Pending = true;
    }

    /// <summary>
    /// Gets the version the next upload is made conditional on, or <see langword="null"/>
    /// when there is none to name.
    /// </summary>
    internal string? ETag { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the store still has to be told this name at all, so
    /// that the next upload is the one that makes it and has to refuse to overwrite.
    /// </summary>
    internal bool MustBeNew { get; private set; }

    /// <summary>
    /// Gets a value indicating whether there is something here the store has not got.
    /// </summary>
    internal bool Pending { get; private set; }

    /// <summary>
    /// Gets how long the file is as it stands here.
    /// </summary>
    internal long Length => _file.Length;

    /// <summary>
    /// Takes what was handed over at an offset, lengthening the file where it has to.
    /// </summary>
    /// <param name="offset">Where the bytes go, counted from the start of the file.</param>
    /// <param name="buffer">The memory WinFsp handed over.</param>
    /// <param name="count">How many bytes of it are the write.</param>
    internal void Write(long offset, IntPtr buffer, int count)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(TransferBufferSize);

        try
        {
            _file.Position = offset;

            for (int written = 0; written < count;)
            {
                int piece = Math.Min(TransferBufferSize, count - written);

                Marshal.Copy(buffer + written, chunk, 0, piece);
                _file.Write(chunk, 0, piece);

                written += piece;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        Pending = true;
    }

    /// <summary>
    /// Reads back what is here, which is what a program that has just written gets.
    /// </summary>
    /// <param name="offset">The first byte, counted from the start of the file.</param>
    /// <param name="count">How many bytes are wanted.</param>
    /// <param name="destination">The memory WinFsp handed over.</param>
    /// <returns>How many bytes were put there, which is none at the end of the file.</returns>
    internal int Read(long offset, long count, IntPtr destination)
    {
        long left = _file.Length - offset;

        if (left <= 0)
        {
            return 0;
        }

        int wanted = (int)Math.Min(count, left);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(TransferBufferSize);

        try
        {
            _file.Position = offset;

            int taken = 0;

            while (taken < wanted)
            {
                int piece = _file.Read(chunk, 0, Math.Min(TransferBufferSize, wanted - taken));

                if (piece == 0)
                {
                    break;
                }

                Marshal.Copy(chunk, 0, destination + taken, piece);

                taken += piece;
            }

            return taken;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Makes the file exactly this long, cutting it short or padding it with zeroes.
    /// </summary>
    /// <param name="length">What the length becomes.</param>
    internal void Truncate(long length)
    {
        _file.SetLength(length);

        Pending = true;
    }

    /// <summary>
    /// Puts what the store already holds here, so that a write to part of a file sends the
    /// whole of it back unchanged elsewhere.
    /// </summary>
    /// <param name="source">The contents as the store handed them over.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    internal void Fill(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _file.Position = 0;

        source.CopyTo(_file);
    }

    /// <summary>
    /// Gives the whole file back to be read from its start, which is how it is uploaded.
    /// </summary>
    /// <returns>The staged file, positioned at its first byte.</returns>
    internal Stream Rewound()
    {
        _file.Flush();
        _file.Position = 0;

        return _file;
    }

    /// <summary>
    /// Notes that the store has what is here, and under which version it has it.
    /// </summary>
    /// <param name="eTag">
    /// What the store answered with, or <see langword="null"/> when it named nothing. A
    /// store that names nothing leaves the next upload with no condition to make.
    /// </param>
    internal void Delivered(string? eTag)
    {
        ETag = eTag;
        Pending = false;
        MustBeNew = false;
    }

    /// <inheritdoc/>
    public void Dispose() => _file.Dispose();
}
