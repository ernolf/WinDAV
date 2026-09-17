// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using WinDav.Abstractions;

namespace WinDav.Fs;

/// <summary>
/// The front of a file on its way to the store while the rest of it is still being written.
/// </summary>
/// <remarks>
/// <para>
/// A store that takes a file in pieces is sent each piece as soon as the file has been
/// written past it, so that sending and writing overlap instead of following one another.
/// The pieces go where the file is not, and the file is put in place by the upload and not
/// before, which is what
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#86-a-write-is-finished-when-the-server-has-it">decision 86</see>
/// asks.
/// </para>
/// <para>
/// It is an attempt and no more. A piece written over after it left, a store that refuses a
/// piece, or anything else that goes wrong leaves the file to go whole, as if nothing had
/// gone ahead, and what had is taken away.
/// </para>
/// </remarks>
internal sealed class EarlyUpload : IAsyncDisposable
{
    // How long a held write waits before it looks again without being woken. A write on
    // another thread can stop the writes from being followed, and nothing wakes a held one
    // for that.
    private static readonly TimeSpan s_holdRecheck = TimeSpan.FromSeconds(1);

    private readonly IUpload _upload;

    private readonly WriteStage _stage;

    private readonly Lock _sync = new();

    // The one run of sending there is at a time. A write starts it when a piece is ready and
    // it is not running already, and it ends once it has caught up with the writes.
    private Task _pump = Task.CompletedTask;

    private bool _running;

    // Set once the file is about to go, after which nothing more is started.
    private bool _sealed;

    // Set once the store takes no more pieces ahead.
    private bool _full;

    // How long the next piece is, as the store last said. Zero until it first has, which
    // starts the first run with the first byte written; that run asks.
    private long _pieceSize;

    private int _pieces;

    // How long the file may get and still go whole, as the store said with the first piece
    // size. No piece goes before the writes are past it, unless the file was made longer
    // than that before it was written.
    private long _longestWhole;

    // How far the writes may be ahead of what the pieces have read out: every piece that can
    // be on its way at once, and the one being written through behind them, all as long as
    // the next piece.
    private long _lead;

    // How much the pieces have read out, which is how far the network has taken them. A piece
    // that ends without having been read through counts as read, since it is on its way no
    // longer.
    private long _sent;

    // Completed and replaced whenever the pieces are read further or the sending stops, which
    // is what a held write waits for.
    private TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private EarlyUpload(IUpload upload, WriteStage stage)
    {
        _upload = upload;
        _stage = stage;
    }

    /// <summary>
    /// Begins sending a file ahead, where the store takes one in pieces.
    /// </summary>
    /// <param name="provider">The store.</param>
    /// <param name="path">The file.</param>
    /// <param name="stage">Where the file is being written, and the pieces are read from.</param>
    /// <returns>
    /// The upload, or <see langword="null"/> where the store takes a file only whole.
    /// </returns>
    internal static EarlyUpload? Begin(IStorageProvider provider, string path, WriteStage stage) =>
        provider.BeginUpload(path) is IUpload upload ? new EarlyUpload(upload, stage) : null;

    /// <summary>
    /// Gets how many pieces the store has taken.
    /// </summary>
    internal int Pieces => Volatile.Read(ref _pieces);

    /// <summary>
    /// Gets what stopped the sending, where the store refused a piece or the stage could not
    /// be read.
    /// </summary>
    internal Exception? Failure { get; private set; }

    /// <summary>
    /// Gets what broke the sending in a way nothing here expected, once it has been sealed.
    /// </summary>
    internal Exception? Fault { get; private set; }

    /// <summary>
    /// Gets a value indicating whether what went ahead can be finished into the file.
    /// </summary>
    internal bool Usable => Pieces > 0 && Failure is null && Fault is null && !_stage.Disturbed;

    /// <summary>
    /// Starts sending where a piece is ready and nothing is being sent already.
    /// </summary>
    internal void Nudge()
    {
        lock (_sync)
        {
            if (_running || _sealed || _full || Failure is not null || !_stage.CanClaim(Needed(_pieceSize)))
            {
                return;
            }

            _running = true;
            _pump = Task.Run(PumpAsync, CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits while the writes are further ahead of the upload than the pieces on their way
    /// need, so that they keep pace with it instead of running ahead of it.
    /// </summary>
    /// <remarks>
    /// A program copying a file counts its progress by the writes it has handed over. Without
    /// this, those are taken as fast as the local disk allows, the count reaches the end long
    /// before the store has the file, and the rest of the upload is spent at the last percent,
    /// in the close that waits for it. Held here, the writes stay a fixed distance ahead of
    /// what the pieces have read out while they travel: as many pieces as the store takes at
    /// once, which have to be written through before they can go, and one more. The count
    /// then moves as the bytes go out, not a piece at a time, and every piece the store has
    /// room for still goes. That distance is not given from the first byte on: until the
    /// pieces have read out enough, the writes may be one piece and a tenth of what has gone
    /// out ahead, so that they run at most a tenth faster than the upload. A program copying a
    /// file scales its speed graph to the fastest it has seen, and a whole distance taken at
    /// the speed of the local disk would leave the speed of the upload a flat line below that.
    /// While the store is still being asked how long a piece is, the writes wait for the
    /// answer. Once the whole distance is given, nothing is held while no piece is ready to go,
    /// so that a piece the store has room for never waits for a held write, which can happen
    /// where the pieces on their way are longer than the next one. Before that, a store that
    /// takes its pieces at once and reads them on its way would otherwise never have a write
    /// held until all of its pieces are out. Nothing is held either where nothing
    /// is being sent ahead: where the store takes no pieces, before the file is long enough for
    /// the first one, once it is full, after a failure, or once a write went back over what had
    /// left.
    /// </remarks>
    internal void Hold()
    {
        while (true)
        {
            Task moved;
            Task? pump;

            lock (_sync)
            {
                // Until the store has said how long a piece is, the run that asks it is waited
                // for, or the first writes would all go by before anything is measured.
                bool measuring = _pieceSize == 0;

                long ahead = Math.Min(_lead, _pieceSize + (_sent / 10));
                bool ramping = ahead < _lead;

                // A run that broke on something nothing here expected ends without saying so,
                // and is taken for one that stopped. A run that is over has found no piece
                // ready, and the next one starts once a write has made one.
                if ((_running && _pump.IsCompleted) || _sealed || _full || Failure is not null
                    || (!_running && (measuring || !ramping || _pieces == 0))
                    || _stage.Front is not long front
                    || (!measuring && front - _sent <= ahead))
                {
                    return;
                }

                moved = _moved.Task;
                pump = _running ? _pump : null;
            }

            // A run that is over would end the wait at once, so only a running one is waited for.
            Task.WaitAny(pump is null ? [moved] : [moved, pump], s_holdRecheck);
        }
    }

    /// <summary>
    /// Stops the sending and waits until what was on its way has arrived or failed.
    /// </summary>
    /// <returns>A task that completes once nothing is being sent any more.</returns>
    internal async Task SealAsync()
    {
        Task pump = Seal();

        await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Reported rather than thrown: the file still goes, whole.
        Fault = pump.Exception?.InnerException;

        _stage.StopClaims();
    }

    /// <summary>
    /// Sends what is left after the pieces and puts the file in place.
    /// </summary>
    /// <param name="ifMatch">The version the upload is made conditional on.</param>
    /// <param name="times">The times the file is to carry.</param>
    /// <param name="mustBeNew">Whether the upload has to refuse to overwrite.</param>
    /// <returns>What the store answered with.</returns>
    internal async Task<string?> FinishAsync(string? ifMatch, EntryTimes times, bool mustBeNew)
    {
        using Stream rest = _stage.Rest();

        return await _upload.FinishAsync(rest, ifMatch, times, mustBeNew).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await SealAsync().ConfigureAwait(false);
        await _upload.DisposeAsync().ConfigureAwait(false);
    }

    // Sends piece after piece for as long as the writes are a piece ahead of it.
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Every piece is handed to the upload, which disposes of it whether it takes the piece or not. The rule cannot follow a transfer through an interface.")]
    private async Task PumpAsync()
    {
        try
        {
            int atOnce = 0;
            long longestWhole = 0;

            while (true)
            {
                // Asked before every piece, because the store may size each one by how long the
                // ones before it took.
                long size = await _upload.GetPieceSizeAsync().ConfigureAwait(false);

                // Only asked where pieces go at all. The answers hold for the whole upload.
                if (atOnce == 0 && size > 0)
                {
                    atOnce = await _upload.GetPiecesAtOnceAsync().ConfigureAwait(false);
                    longestWhole = await _upload.GetLongestWholeAsync().ConfigureAwait(false);
                }

                if (!Measured(size, atOnce, longestWhole) || !Continues(size))
                {
                    return;
                }

                // Only this claims, so a piece that was ready a moment ago still is, unless a
                // write in between went back over what had left.
                if (_stage.Claim(size) is not Stream claimed)
                {
                    continue;
                }

                if (!await _upload.SendAsync(new CountedPiece(claimed, this)).ConfigureAwait(false))
                {
                    // The store takes no more ahead, and the piece goes with the rest.
                    _stage.Unclaim(size);
                    Filled();

                    return;
                }

                Interlocked.Increment(ref _pieces);
            }
        }
        catch (ProviderException exception)
        {
            Stopped(exception);
        }
        catch (IOException exception)
        {
            Stopped(exception);
        }
        catch (OperationCanceledException exception)
        {
            Stopped(exception);
        }
    }

    // Notes how long a piece is, how many go at once and how long the file may get and still
    // go whole. A store that takes nothing ahead is full from the start, and the whole file
    // goes with the finish.
    private bool Measured(long size, int atOnce, long longestWhole)
    {
        lock (_sync)
        {
            _pieceSize = size;
            _longestWhole = longestWhole;
            _lead = (atOnce + 1L) * size;

            if (size == 0)
            {
                _full = true;
                _running = false;
            }

            Signal();

            return size > 0;
        }
    }

    // Whether there is a piece to send. Where there is not, the run ends here, under the lock
    // a write starts the next one under, so that no piece is left waiting between the two.
    private bool Continues(long size)
    {
        lock (_sync)
        {
            _running = !_sealed && _stage.CanClaim(Needed(size));

            return _running;
        }
    }

    // How far the writes have to be past what was claimed before a piece of this size can go:
    // the piece itself, and before the first one the whole length a file may go whole up to.
    // A program copying a file sets its length before it writes, and a file already longer
    // than that goes ahead from the first piece on, so that the writes are held from the
    // start and a copy does not show the speed of the local disk until they reach it.
    // Called under the lock.
    private long Needed(long size) =>
        _pieces == 0 && _stage.Length <= _longestWhole ? Math.Max(size, _longestWhole) : size;

    private Task Seal()
    {
        lock (_sync)
        {
            _sealed = true;

            Signal();

            return _pump;
        }
    }

    private void Filled()
    {
        lock (_sync)
        {
            _full = true;
            _running = false;

            Signal();
        }
    }

    private void Stopped(Exception exception)
    {
        lock (_sync)
        {
            Failure = exception;
            _running = false;

            Signal();
        }
    }

    private void Sent(long count)
    {
        lock (_sync)
        {
            _sent += count;

            Signal();
        }
    }

    // Wakes the writes held for the sending. Called under the lock.
    private void Signal()
    {
        _moved.TrySetResult();
        _moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // A piece that tells the upload how far it has been read. The store reads it while it
    // travels, so that is how far the network has taken it.
    private sealed class CountedPiece(Stream inner, EarlyUpload upload) : Stream
    {
        // The furthest it has been read. A piece read again from its start, as a request sent
        // a second time is, counts only what goes past that.
        private long _reached;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);

            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);

            Reached(inner.Position);

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);

            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            Reached(inner.Position);

            return read;
        }

        public override void Flush()
        {
            // Nothing is written, so there is nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Taken, refused or failed, it is on its way no longer.
                Reached(inner.Length);

                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Reached(long position)
        {
            if (position > _reached)
            {
                upload.Sent(position - _reached);

                _reached = position;
            }
        }
    }
}
