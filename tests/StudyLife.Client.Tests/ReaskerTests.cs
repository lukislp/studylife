// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Client.Services;

namespace StudyLife.Client.Tests;

/// <summary>The retry policy and the single-flight re-asker behind the sign-in checks.</summary>
public class ReaskerTests
{
    private sealed class FakeSignal : IConnectivitySignal
    {
        public event Action? Restored;
        public void Raise() => Restored?.Invoke();
        public bool HasSubscribers => Restored is not null;
        public bool Allowed { get; set; } = true;
        public bool CanAsk => Allowed;
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public void Policy_FollowsTheBackoffSequence_AndCapsAtOneMinute()
    {
        var p = RetryPolicy.Default;
        Assert.Equal([2, 5, 10, 30, 60, 60, 60], Enumerable.Range(0, 7).Select(i => (int)p.DelayAfter(i).TotalSeconds));
    }

    [Fact]
    public async Task Retries_WithTheBackoff_UntilTheAnswerIsDefinite_ThenStops()
    {
        var waits = new List<TimeSpan>();
        var asked = 0;
        using var asker = new Reasker(
            _ => Task.FromResult(++asked < 6 ? ReaskVerdict.Retry : ReaskVerdict.Done),
            delay: (d, _) => { waits.Add(d); return Task.CompletedTask; });

        await asker.StartAsync();
        await WaitUntil(() => asked == 6);
        await Task.Delay(50);

        Assert.Equal(6, asked);
        Assert.Equal([2, 5, 10, 30, 60], waits.Select(w => (int)w.TotalSeconds));
        asker.Poke(); // a definite answer ended it for good
        await Task.Delay(50);
        Assert.Equal(6, asked);
    }

    [Fact]
    public async Task AnUnansweredQuestion_IsRepeated_AndAPokeAsksAtOnceAndRestartsTheRhythm()
    {
        var waits = new List<TimeSpan>();
        var asked = 0;
        var signal = new FakeSignal();
        using var asker = new Reasker(
            _ => { asked++; return Task.FromResult(ReaskVerdict.Retry); }, signal,
            delay: (d, ct) => { waits.Add(d); return Task.Delay(Timeout.InfiniteTimeSpan, ct); });

        await asker.StartAsync();
        await WaitUntil(() => waits.Count == 1);
        Assert.Equal(1, asked); // waiting for the 2 s step, nothing busy

        signal.Raise();
        await WaitUntil(() => asked == 2 && waits.Count == 2);

        Assert.Equal([2, 2], waits.Select(w => (int)w.TotalSeconds)); // restarted at the first step
    }

    [Fact]
    public async Task WaitForPoke_DoesNotPoll_ButAsksAgainWhenPoked_AndAfterADefiniteAnswerIfRestartable()
    {
        var asked = 0;
        var verdicts = new Queue<ReaskVerdict>([ReaskVerdict.WaitForPoke, ReaskVerdict.Done, ReaskVerdict.Done]);
        using var asker = new Reasker(_ => { asked++; return Task.FromResult(verdicts.Dequeue()); },
            restartAfterDone: true, delay: (_, _) => throw new InvalidOperationException("must not poll"));

        await asker.StartAsync();
        await Task.Delay(100);
        Assert.Equal(1, asked);

        asker.Poke(); // resumed: answers Done
        await WaitUntil(() => asked == 2);
        await Task.Delay(50);
        asker.Poke(); // restartable: asks again even after Done
        await WaitUntil(() => asked == 3);
    }

    [Fact]
    public async Task WhilePaused_NothingIsAskedAndNoTimerRuns_AndTheResumePokeAsksAtOnce()
    {
        var asked = 0;
        var waits = 0;
        var signal = new FakeSignal { Allowed = false }; // app in the background
        using var asker = new Reasker(_ => { asked++; return Task.FromResult(ReaskVerdict.Retry); }, signal,
            delay: (_, ct) => { waits++; return Task.Delay(Timeout.InfiniteTimeSpan, ct); });

        await asker.StartAsync(); // does not hang while paused
        await Task.Delay(100);
        signal.Raise(); // a poke that is still paused changes nothing
        await Task.Delay(50);
        Assert.Equal(0, asked);
        Assert.Equal(0, waits);

        signal.Allowed = true;
        signal.Raise(); // resumed

        await WaitUntil(() => asked == 1 && waits == 1);
    }

    [Fact]
    public async Task ASignalWithoutCanAsk_KeepsWorking()
    {
        var asked = 0;
        var signal = new LegacySignal();
        using var asker = new Reasker(_ => { asked++; return Task.FromResult(ReaskVerdict.Done); }, signal);

        await asker.StartAsync();

        Assert.Equal(1, asked);
    }

    private sealed class LegacySignal : IConnectivitySignal
    {
        public event Action? Restored { add { } remove { } }
    }

    [Fact]
    public async Task Dispose_CancelsTheQuestionInFlight_AndUnsubscribes()
    {
        var cancelled = new TaskCompletionSource();
        var signal = new FakeSignal();
        var asker = new Reasker(async ct =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return ReaskVerdict.Done;
        }, signal);
        _ = asker.StartAsync();
        Assert.True(signal.HasSubscribers);

        asker.Dispose();

        await cancelled.Task.WaitAsync(Limit);
        Assert.False(signal.HasSubscribers);
    }

    [Fact]
    public async Task Dispose_StopsTheWaitingTimer()
    {
        var cancelled = new TaskCompletionSource();
        var asker = new Reasker(_ => Task.FromResult(ReaskVerdict.Retry), delay: async (_, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        });
        await asker.StartAsync();
        await Task.Delay(50);

        asker.Dispose();

        await cancelled.Task.WaitAsync(Limit);
    }

    [Fact]
    public async Task APokeDuringAQuestion_DoesNotRunTwoAtOnce_ButAsksAgainAfterwards()
    {
        var running = 0;
        var maxRunning = 0;
        var asked = 0;
        var gate = new TaskCompletionSource();
        using var asker = new Reasker(async _ =>
        {
            var n = Interlocked.Increment(ref asked);
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            if (n == 1) await gate.Task;
            Interlocked.Decrement(ref running);
            return ReaskVerdict.Done;
        }, restartAfterDone: true);
        _ = asker.StartAsync();
        await WaitUntil(() => asked == 1);

        asker.Poke();
        gate.SetResult();
        await WaitUntil(() => asked == 2);

        Assert.Equal(1, maxRunning);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
