// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinDav.Abstractions;

namespace WinDav.Fs;

/// <summary>
/// The one layer between a read and the store: how much is fetched at a time, how much of it
/// is held on to, and how many requests are on the wire at once.
/// </summary>
/// <remarks>
/// <para>
/// Every byte the file system reads passes through here and nothing else does, which is what
/// makes the whole of it switchable. With a window of nothing and a width of one, this layer
/// turns every read into exactly the one request that read asked for, which is what the read
/// path did before it was built and is how a report about a wrong byte is narrowed down to
/// the layer that produced it.
/// </para>
/// <para>
/// The mount owns this; a window belongs to an open handle and is handed out by
/// <see cref="Open"/>. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions">decision 75</see>.
/// </para>
/// </remarks>
internal sealed class ReadLayer
{
    private const int TransferBufferSize = 64 * 1024;

    // Milliseconds with one place, the same as the wire records and the reads are written
    // with, so that a fetch and the request underneath it can be laid side by side.
    private const string ElapsedFormat = "0.#";

    private readonly IStorageProvider _provider;
    private readonly ILogger _log;
    private readonly long _window;

    /// <summary>
    /// Initialises a new instance of the <see cref="ReadLayer"/> class.
    /// </summary>
    /// <param name="provider">The store the bytes come from.</param>
    /// <param name="settings">How much may be fetched, how much held, how many at once.</param>
    /// <param name="log">Where what was fetched is written down.</param>
    /// <param name="recovery">
    /// How long the server has to keep taking requests before the width is raised again after
    /// a refusal, or <see langword="null"/> for the interval the gate sets itself.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal ReadLayer(
        IStorageProvider provider,
        ReadSettings settings,
        ILogger log,
        TimeSpan? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _provider = provider;
        _log = log;
        _window = Math.Max(settings.Window, 0);

        Gate = new RequestGate(settings.Requests, log, recovery);
        Budget = new ReadBudget(settings.Total);
    }

    /// <summary>
    /// Gets the gate that keeps the number of requests on the wire to what the server takes.
    /// </summary>
    internal RequestGate Gate { get; }

    /// <summary>
    /// Gets what all the windows of this mount may hold between them.
    /// </summary>
    internal ReadBudget Budget { get; }

    /// <summary>
    /// Gives out the window of one open handle.
    /// </summary>
    /// <param name="path">The entry it reads, in the store's own spelling.</param>
    /// <param name="size">
    /// How long the store said the entry is, or <see langword="null"/> when it said nothing.
    /// A store is entitled to say nothing, and a window over a file of unknown length is
    /// bounded by what comes back rather than by what was expected.
    /// </param>
    /// <returns>The window, which the handle gives back when it is closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal ReadWindow Open(string path, long? size)
    {
        ArgumentNullException.ThrowIfNull(path);

        return new ReadWindow(this, path, size, _window);
    }

    /// <summary>
    /// Fetches a range into a window.
    /// </summary>
    /// <param name="path">The entry, in the store's own spelling.</param>
    /// <param name="offset">The first byte, counted from the start of the entry.</param>
    /// <param name="count">How many bytes are wanted.</param>
    /// <param name="destination">The buffer of the window, written from its start.</param>
    /// <returns>How many bytes came back, which is fewer at the end of the file.</returns>
    /// <exception cref="ProviderException">The store refused, or could not be reached.</exception>
    internal int FetchInto(string path, long offset, int count, byte[] destination) =>
        Through(
            path,
            offset,
            count,
            stream => stream.ReadAtLeast(destination.AsSpan(0, count), count, throwOnEndOfStream: false));

    /// <summary>
    /// Fetches a range straight into the memory WinFsp handed over, which is what a read
    /// outside every window does.
    /// </summary>
    /// <param name="path">The entry, in the store's own spelling.</param>
    /// <param name="offset">The first byte, counted from the start of the entry.</param>
    /// <param name="count">How many bytes are wanted.</param>
    /// <param name="destination">The buffer of the read, written from its start.</param>
    /// <returns>How many bytes came back, which is fewer at the end of the file.</returns>
    /// <exception cref="ProviderException">The store refused, or could not be reached.</exception>
    internal int FetchInto(string path, long offset, long count, IntPtr destination) =>
        Through(path, offset, count, stream => CopyInto(stream, destination, count));

    // The buffer belongs to WinFsp and holds one transfer, so what has been written into it
    // stays well inside what an Int32 counts.
    private static int CopyInto(Stream stream, IntPtr buffer, long length)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(TransferBufferSize);

        try
        {
            int written = 0;

            while (written < length)
            {
                int room = (int)Math.Min(chunk.Length, length - written);
                int read = stream.Read(chunk, 0, room);

                if (read <= 0)
                {
                    break;
                }

                Marshal.Copy(chunk, 0, IntPtr.Add(buffer, written), read);

                written += read;
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static string Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString(ElapsedFormat, CultureInfo.InvariantCulture);

    // One request, from the moment there is room for it until its body has been read to the
    // end: a response whose body is still being copied is still on the wire, and letting the
    // next request past at the point its headers arrived would put more of them there than
    // the server was measured to want.
    private int Through(string path, long offset, long count, Func<Stream, int> take)
    {
        Gate.Enter();

        bool refused = false;
        long started = Stopwatch.GetTimestamp();

        try
        {
            // Waited on rather than awaited, for the reason WinDavFileSystem.Await sets out:
            // WinFsp dispatches every request on a thread of its own and wants the answer on
            // that thread.
            using Stream stream = _provider.OpenReadAsync(path, offset, count).GetAwaiter().GetResult();

            int filled = take(stream);

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Fetched {Count} bytes of {Path} at {Offset}, {Filled} back in {Elapsed} ms.",
                    count,
                    path,
                    offset,
                    filled,
                    Elapsed(started));
            }

            return filled;
        }
        catch (ProviderException exception)
        {
            // The one answer the width reacts to. A file that is not there, or a credential
            // the server would not take, says nothing about how many requests it can carry.
            refused = exception.Error == ProviderError.Busy;

            throw;
        }
        finally
        {
            Gate.Leave(refused);
        }
    }
}
