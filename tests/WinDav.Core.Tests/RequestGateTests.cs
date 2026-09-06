// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Core.Providers;
using Xunit;

namespace WinDav.Core.Tests;

// How many requests the mount lets out at once, and what a refusal does to that number. The
// recovery interval is a parameter here so that the retreat can be watched without waiting
// half a minute for it.
public sealed class RequestGateTests
{
    private static readonly TimeSpan s_never = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_atOnce = TimeSpan.Zero;

    [Fact]
    public void TheWidthStartsAtWhatItWasGiven()
    {
        Recorder log = new();

        Assert.Equal(4, new RequestGate(4, log, s_never).Width);

        // One is the value that switches the idea off, and nothing below it means anything.
        Assert.Equal(1, new RequestGate(1, log, s_never).Width);
        Assert.Equal(1, new RequestGate(0, log, s_never).Width);
        Assert.Equal(1, new RequestGate(-3, log, s_never).Width);
    }

    [Fact]
    public void ARefusalLowersTheWidthAtOnceAndNeverBelowOne()
    {
        Recorder log = new();
        RequestGate gate = new(3, log, s_never);

        Refuse(gate);

        Assert.Equal(2, gate.Width);

        Refuse(gate);

        Assert.Equal(1, gate.Width);

        // A server that keeps refusing keeps the mount at one request at a time. There is
        // nothing below that to retreat to.
        Refuse(gate);

        Assert.Equal(1, gate.Width);

        // The retreat is a lasting change to the way the mount behaves, so it is written down
        // at a level that is on without anybody asking for a recording.
        string[] said =
        [
            "The server would not take the request. 2 at a time from now on.",
            "The server would not take the request. 1 at a time from now on.",
        ];

        Assert.Equal(said, log.Written);
    }

    [Fact]
    public void AnAnswerThatIsNotARefusalChangesNothingBeforeTheIntervalHasPassed()
    {
        Recorder log = new();
        RequestGate gate = new(3, log, s_never);

        Refuse(gate);

        for (int request = 0; request < 20; request++)
        {
            Pass(gate);
        }

        // Twenty requests the server took, and the width is still where the refusal left it:
        // what the recovery is counted in is the clock, not how many requests went by.
        Assert.Equal(2, gate.Width);
    }

    [Fact]
    public void TheWidthIsRaisedAgainOnceTheServerHasTakenSomething()
    {
        Recorder log = new();
        RequestGate gate = new(3, log, s_atOnce);

        Refuse(gate);
        Refuse(gate);

        Assert.Equal(1, gate.Width);

        Pass(gate);

        Assert.Equal(2, gate.Width);

        Pass(gate);

        Assert.Equal(3, gate.Width);

        // Never past the number the mount was given, however long the server behaves.
        Pass(gate);
        Pass(gate);

        Assert.Equal(3, gate.Width);
    }

    [Fact]
    public void AWidthOfOneIsNeverRaised()
    {
        Recorder log = new();
        RequestGate gate = new(1, log, s_atOnce);

        Pass(gate);
        Pass(gate);

        // Switched off is switched off: the recovery has nothing to give back, because
        // nothing was taken away.
        Assert.Equal(1, gate.Width);
        Assert.Empty(log.Written);
    }

    [Fact]
    public async Task ARequestWaitsUntilThereIsRoomForIt()
    {
        Recorder log = new();
        RequestGate gate = new(1, log, s_never);

        gate.Enter();

        Task waiting = Enters(gate, ahead: false);

        // The room is taken, so the second request is still outside the gate. This is the
        // whole of what the number does: it holds a thread back, it starts nothing.
        await Outside(waiting);

        gate.Leave(refused: false);

        await Inside(waiting);
    }

    [Fact]
    public async Task AReadGoesInFrontOfWhatIsReadAhead()
    {
        Recorder log = new();
        RequestGate gate = new(1, log, s_never);

        gate.Enter();

        // The round arrives first and the read after it, so that the order they were let in
        // cannot be the order they came in: what settles it is the rank and nothing else.
        Task ahead = Enters(gate, ahead: true);
        await Outside(ahead);

        Task read = Enters(gate, ahead: false);
        await Outside(read);

        gate.Leave(refused: false);

        // The one place went to the read. The round is where it was, and what it waits for
        // now is a place the read is not waiting for.
        await Inside(read);
        await Outside(ahead);

        gate.Leave(refused: false);

        await Inside(ahead);
    }

    [Fact]
    public async Task WhatIsReadAheadTakesTheRoomNobodyIsWaitingFor()
    {
        Recorder log = new();
        RequestGate gate = new(2, log, s_never);

        gate.Enter();

        // Room left over and nobody outside it: standing back costs a round nothing where
        // there is nobody to stand back for. That is the whole of the rank.
        await Inside(Enters(gate, ahead: true));
    }

    [Fact]
    public async Task RoomThatOpensWiderLetsTheReadInAndTheRoundBehindIt()
    {
        Recorder log = new();
        RequestGate gate = new(2, log, s_atOnce);

        Refuse(gate);

        Assert.Equal(1, gate.Width);

        gate.Enter();

        Task ahead = Enters(gate, ahead: true);
        await Outside(ahead);

        Task read = Enters(gate, ahead: false);
        await Outside(read);

        // One request ends, and the width grows back on the way out of it: two places open
        // where one was given up. The read takes the first, and the round takes the second
        // rather than wait for a request to end that nothing is going to end.
        gate.Leave(refused: false);

        await Inside(read);
        await Inside(ahead);
    }

    // A request outside the gate that takes its place and keeps it. The room is given back by
    // the test rather than by the thread that took it, so that what is let in next is the
    // gate's decision and not a race between two threads leaving.
    private static Task Enters(RequestGate gate, bool ahead) =>
        Task.Run(() => gate.Enter(ahead), TestContext.Current.CancellationToken);

    // Long enough that a thread which was going to get in has got in, short enough to be
    // worth waiting for twice in one test.
    private static async Task Outside(Task waiting)
    {
        Task first = await Task.WhenAny(
            waiting,
            Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken))
            .ConfigureAwait(false);

        Assert.NotSame(waiting, first);
    }

    private static Task Inside(Task waiting) =>
        waiting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private static void Refuse(RequestGate gate)
    {
        gate.Enter();
        gate.Leave(refused: true);
    }

    private static void Pass(RequestGate gate)
    {
        gate.Enter();
        gate.Leave(refused: false);
    }

    // Only what the gate says about itself, which is written at a level that is on by default.
    private sealed class Recorder : ILogger
    {
        internal List<string> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (IsEnabled(logLevel))
            {
                Written.Add(formatter(state, exception));
            }
        }
    }
}
