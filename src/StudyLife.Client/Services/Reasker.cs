// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

namespace StudyLife.Client.Services;

/// <summary>What <see cref="Reasker"/> does after one question.</summary>
public enum ReaskVerdict
{
    /// <summary>Definite answer: stop asking.</summary>
    Done,
    /// <summary>Could not ask (offline, server down): ask again after the policy's next delay.</summary>
    Retry,
    /// <summary>Answered, but the answer will not fix itself (wrong certificate, not a StudyLife server):
    /// do not poll, ask again only when poked (connectivity back, app resumed).</summary>
    WaitForPoke,
}

/// <summary>The waiting times between questions: a few quick ones, then a steady, slow rhythm. Pure.</summary>
public sealed class RetryPolicy(IReadOnlyList<TimeSpan> steps, TimeSpan steady)
{
    /// <summary>2 s, 5 s, 10 s, 30 s, then every 60 s.</summary>
    public static RetryPolicy Default { get; } = new(
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)],
        TimeSpan.FromSeconds(60));

    /// <summary>The wait after the given number of unanswered questions in a row (0 = after the first).</summary>
    public TimeSpan DelayAfter(int unanswered) =>
        unanswered < 0 ? steps[0] : unanswered < steps.Count ? steps[unanswered] : steady;
}

/// <summary>
/// Asks a question until the answer is definite, without busy loops: one question at a time (single
/// flight), waits per <see cref="RetryPolicy"/>, and asks again immediately when poked - by the platform's
/// <see cref="IConnectivitySignal"/> (network back) or by the owner (app resumed). Stops for good on
/// <see cref="ReaskVerdict.Done"/> unless created restartable, and on <see cref="Dispose"/>, which also
/// cancels the question in flight. Nothing runs between questions except one timer, and not even that while
/// the signal says <see cref="IConnectivitySignal.CanAsk"/> is false (app in the background): it waits for the
/// next poke, then continues with the normal rhythm.
/// </summary>
public sealed class Reasker : IDisposable
{
    private readonly Func<CancellationToken, Task<ReaskVerdict>> _ask;
    private readonly RetryPolicy _policy;
    private readonly IConnectivitySignal? _signal;
    private readonly bool _restartAfterDone;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _life = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _wait;
    private bool _running, _poked, _finished, _disposed;

    /// <param name="ask">One question; an exception counts as <see cref="ReaskVerdict.Retry"/>.</param>
    /// <param name="signal">Optional: a poke whenever the platform reports connectivity back.</param>
    /// <param name="policy">Waiting times; defaults to <see cref="RetryPolicy.Default"/>.</param>
    /// <param name="restartAfterDone">True: a poke after a definite answer asks again (the app's server check);
    /// false: a definite answer ends it for good (the sign-in form).</param>
    /// <param name="delay">Test seam for the waiting; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public Reasker(Func<CancellationToken, Task<ReaskVerdict>> ask, IConnectivitySignal? signal = null,
        RetryPolicy? policy = null, bool restartAfterDone = false, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ask = ask;
        _policy = policy ?? RetryPolicy.Default;
        _signal = signal;
        _restartAfterDone = restartAfterDone;
        _delay = delay ?? Task.Delay;
        if (_signal is not null) _signal.Restored += Poke;
    }

    /// <summary>Starts asking (no-op while running); the task completes once the first question was answered
    /// or given up, so a page can await the first answer like a plain call.</summary>
    public Task StartAsync()
    {
        Poke();
        return _first.Task;
    }

    /// <summary>Ask again now (network back, app resumed): cuts a running wait short and restarts the rhythm.</summary>
    public void Poke()
    {
        bool start;
        lock (_gate)
        {
            if (_disposed || (_finished && !_restartAfterDone)) return;
            _poked = true;
            _wait?.Cancel();
            start = !_running;
            if (start)
            {
                _running = true;
                _finished = false;
            }
        }
        if (start) _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var ct = _life.Token;
        var unanswered = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (_gate) _poked = false;
                ReaskVerdict verdict;
                if (_signal is { CanAsk: false }) verdict = ReaskVerdict.WaitForPoke; // paused (app in the background): no question, no timer
                else
                {
                    try { verdict = await _ask(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch { verdict = ReaskVerdict.Retry; }
                }
                _first.TrySetResult();

                CancellationTokenSource wait;
                lock (_gate)
                {
                    if (_poked) { unanswered = 0; continue; } // asked again while this one was in flight
                    if (verdict == ReaskVerdict.Done) { _finished = true; _running = false; return; } // under the lock: a later poke starts a new loop
                    wait = _wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                }
                try
                {
                    if (verdict == ReaskVerdict.Retry) await _delay(_policy.DelayAfter(unanswered++), wait.Token);
                    else await Task.Delay(Timeout.InfiniteTimeSpan, wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { unanswered = 0; }
                catch (OperationCanceledException) { return; }
                finally
                {
                    lock (_gate) { _wait = null; }
                    wait.Dispose();
                }
            }
        }
        finally
        {
            _first.TrySetResult(); // _running ends under the lock above, or with Dispose
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _wait?.Cancel();
        }
        if (_signal is not null) _signal.Restored -= Poke;
        _life.Cancel();
    }
}
