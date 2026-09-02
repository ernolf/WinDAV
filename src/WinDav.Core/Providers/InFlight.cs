// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace WinDav.Core.Providers;

/// <summary>
/// A record of the fetches that are running, so that a question already on its way is waited
/// for instead of being asked a second time.
/// </summary>
/// <remarks>
/// <para>
/// What is held answers only once the first answer is back, so two callers a fraction of a
/// second apart are two requests. Counted at a live mount over one minute, six of 146
/// requests were an identical one sent while the first was still in flight, two of them a
/// full listing of the root; in one pair the second went out a millisecond before the first
/// was answered and then ran 415 ms of its own. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#79-a-request-that-is-already-in-flight-is-waited-for-instead-of-sent-again">decision 79</see>.
/// </para>
/// <para>
/// Nothing is trusted for longer than it is otherwise and nothing more is kept: an entry
/// lives as long as the fetch it stands for and goes when that fetch ends, whether it answers
/// or throws, so the next question asks again. What a caller that joins gets is what the
/// fetch it joined gets, its failure included; one that gives up stops waiting without taking
/// the fetch away from the others.
/// </para>
/// <para>
/// One of these belongs to each kind of question in each layer. A listing and a single entry
/// on the same path are different questions, and one record for both would answer one of them
/// with the other.
/// </para>
/// </remarks>
/// <typeparam name="TResult">What the fetch answers with.</typeparam>
internal sealed class InFlight<TResult>
{
    // Ordinal, for the reason the stores above are ordinal: a server that keeps case has two
    // paths where these differ, and one answer would be handed back for the other.
    private readonly ConcurrentDictionary<string, Lazy<Task<TResult>>> _running =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Waits on the fetch for a path, starting it if nothing has.
    /// </summary>
    /// <param name="path">What the question is about.</param>
    /// <param name="fetch">
    /// What asks the store. It runs at most once per path at a time, and it belongs to none
    /// of the callers waiting on it: what ends it is the layer coming down, not one of them
    /// walking away.
    /// </param>
    /// <param name="cancellationToken">Ends the waiting. It does not end the fetch.</param>
    /// <returns>What the fetch answered with.</returns>
    public Task<TResult> JoinAsync(
        string path,
        Func<Task<TResult>> fetch,
        CancellationToken cancellationToken)
    {
        // The dictionary is free to build more than one of these and keep one; what must
        // happen once is the fetch, and only the one that was kept is ever asked for its
        // value.
        Lazy<Task<TResult>> running = _running.GetOrAdd(
            path,
            _ => new Lazy<Task<TResult>>(
                () => RunAsync(path, fetch),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return running.Value.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Waits on the fetch for a path where one is running, and answers with nothing where
    /// none is.
    /// </summary>
    /// <param name="path">What the question is about.</param>
    /// <param name="cancellationToken">Ends the waiting. It does not end the fetch.</param>
    /// <returns>
    /// What the running fetch answers with, or <see langword="null"/> where nothing is
    /// running for the path. This is for a caller that has an answer of its own and wants
    /// only the one already on its way; it asks for no fetch that nobody wanted.
    /// </returns>
    public Task<TResult>? Joined(string path, CancellationToken cancellationToken) =>
        _running.TryGetValue(path, out Lazy<Task<TResult>>? running)
            ? running.Value.WaitAsync(cancellationToken)
            : null;

    private async Task<TResult> RunAsync(string path, Func<Task<TResult>> fetch)
    {
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        finally
        {
            // Before this answers, so that nobody joins a fetch that is over. Whatever is
            // under the path is this one: a later fetch is written down only once this has
            // been taken out.
            _running.TryRemove(path, out _);
        }
    }
}
