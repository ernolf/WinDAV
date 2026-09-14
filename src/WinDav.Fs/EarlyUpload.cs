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

    // How long a piece is, once the store has said. Zero until then, which starts the first
    // run with the first byte written; that run asks.
    private long _pieceSize;

    private int _pieces;

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
            if (_running || _sealed || _full || Failure is not null || !_stage.CanClaim(_pieceSize))
            {
                return;
            }

            _running = true;
            _pump = Task.Run(PumpAsync, CancellationToken.None);
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
            // Asked on every run. The answer holds for the whole upload, so only the first run
            // can have to wait for it.
            long size = await _upload.GetPieceSizeAsync().ConfigureAwait(false);

            if (!Measured(size))
            {
                return;
            }

            while (Continues(size))
            {
                // Only this claims, so a piece that was ready a moment ago still is, unless a
                // write in between went back over what had left.
                if (_stage.Claim(size) is not Stream piece)
                {
                    continue;
                }

                if (!await _upload.SendAsync(piece).ConfigureAwait(false))
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

    // Notes how long a piece is. A store that takes nothing ahead is full from the start, and
    // the whole file goes with the finish.
    private bool Measured(long size)
    {
        lock (_sync)
        {
            _pieceSize = size;

            if (size == 0)
            {
                _full = true;
                _running = false;
            }

            return size > 0;
        }
    }

    // Whether there is a piece to send. Where there is not, the run ends here, under the lock
    // a write starts the next one under, so that no piece is left waiting between the two.
    private bool Continues(long size)
    {
        lock (_sync)
        {
            _running = !_sealed && _stage.CanClaim(size);

            return _running;
        }
    }

    private Task Seal()
    {
        lock (_sync)
        {
            _sealed = true;

            return _pump;
        }
    }

    private void Filled()
    {
        lock (_sync)
        {
            _full = true;
            _running = false;
        }
    }

    private void Stopped(Exception exception)
    {
        lock (_sync)
        {
            Failure = exception;
            _running = false;
        }
    }
}
