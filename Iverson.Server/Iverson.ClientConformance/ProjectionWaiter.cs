using System.Diagnostics;

namespace Iverson.ClientConformance;

/// <summary>
/// The harness's one convention for waiting on an asynchronous projection.
///
/// A mapped write commits to Postgres and enqueues an outbox row; the StarRocks projection that
/// <c>Search</c>/<c>Aggregate</c> read from, and the Qdrant collection the vector RPCs read from,
/// are both populated from that outbox after the write call has already returned. A read phase
/// that queried once would be a race, and the two wrong ways to remove that race are an unbounded
/// wait (a hung harness reported as nothing) and a fixed sleep (a guess presented as determinism,
/// which passes on a fast machine and fails on a slow one for reasons no cell explains).
///
/// So: a bounded poll with an explicit timeout, whose expiry is DATA — a
/// <see cref="ProjectionWaitResult"/> the caller reports as a failed step — never an exception and
/// never a silent continue. The probe is supplied by the caller, so the same waiter serves the
/// StarRocks-backed <c>query</c> scenario and any later Qdrant-backed one: what changes between
/// them is which store is asked, not how the waiting is done.
///
/// This class judges nothing about a client library. It is harness plumbing, and a timeout here is
/// reported as the harness's own precondition failing, worded so it can never be misread as a
/// client defect.
/// </summary>
public sealed class ProjectionWaiter(TimeSpan? timeout = null, TimeSpan? interval = null)
{
    /// <summary>
    /// How long to keep polling before giving up. Generous relative to the observed projection
    /// latency (single-digit seconds on the dev stack) because the cost of being slightly too
    /// patient is a slower run, while the cost of being too impatient is a red cell that blames a
    /// client library for the outbox.
    /// </summary>
    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(90);

    /// <summary>
    /// Delay between probe attempts. The probe is always attempted once before any delay. Doubles
    /// as the floor on a single attempt's budget (see <see cref="WaitAsync"/>), so a waiter
    /// constructed with a zero interval AND a zero timeout gives its one attempt no budget at all.
    /// </summary>
    public TimeSpan Interval { get; } = interval ?? TimeSpan.FromSeconds(2);

    /// <summary>
    /// Polls <paramref name="probe"/> until it reports satisfied, the timeout expires, or
    /// <paramref name="ct"/> is cancelled. The probe returns its own outcome as data: a probe that
    /// throws is caught and its message carried into the next attempt's detail, because a
    /// projection that is not ready yet legitimately surfaces as an error from the store (an
    /// unknown table, an empty result) and one failed attempt must not end the wait.
    ///
    /// The clock is <see cref="Stopwatch"/>, not attempt counting: the interval is a floor on how
    /// often the store is asked, not a promise about how many times it will be.
    ///
    /// The bound is per ATTEMPT, not merely between attempts: each attempt runs under a token
    /// linked to <paramref name="ct"/> and cancelled after the remaining budget
    /// (<see cref="Timeout"/> minus elapsed), FLOORED at <see cref="Interval"/>. A probe that
    /// outlives its attempt budget is therefore just a failed attempt — its expiry is carried into
    /// the detail like any other — and never a hang. Cancellation of <paramref name="ct"/> itself
    /// still throws; only budget expiry is data. Because of the floor, a waiter configured with a
    /// zero timeout still grants its one attempt a full interval, so it genuinely observes the
    /// store rather than reporting a timeout it never tested; total elapsed time is then at most
    /// one interval.
    /// </summary>
    public async Task<ProjectionWaitResult> WaitAsync(
        string subject,
        Func<CancellationToken, Task<ProbeOutcome>> probe,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var lastDetail = "the probe never ran";

        while (true)
        {
            attempts++;

            // Each attempt is bounded by what is LEFT of the budget, not merely checked against it
            // afterwards. A probe that stalls inside an accepted call (a gRPC stream that never
            // completes) would otherwise block attempt 1 forever and the timeout would never fire —
            // the hung harness this class exists to prevent.
            //
            // Floored at Interval so an exhausted (or zero) budget still buys the probe a genuinely
            // usable window: handing it an already-dead token would make it hand back without
            // touching the store, and the wait would then report on a store it never asked. The
            // floor stays bounded — worst case one attempt costs one interval.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = Timeout - stopwatch.Elapsed;
            var budget = remaining > Interval ? remaining : Interval;
            if (budget > TimeSpan.Zero)
                attemptCts.CancelAfter(budget);
            else
                await attemptCts.CancelAsync();

            try
            {
                var outcome = await probe(attemptCts.Token);
                lastDetail = outcome.Detail;
                if (outcome.Satisfied)
                    return new ProjectionWaitResult(true, subject, attempts, stopwatch.Elapsed, outcome.Detail);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The CALLER cancelled. Distinguished from budget expiry by asking ct, not the
                // linked token, so the two never blur: caller cancellation throws, budget expiry
                // is data.
                throw;
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                lastDetail = $"the probe did not return within its " +
                             $"{budget.TotalSeconds:0.0}s attempt budget";
            }
            catch (Exception ex)
            {
                lastDetail = $"{ex.GetType().Name}: {ex.Message}";
            }

            // Checked AFTER the attempt, so the probe always runs at least once even with a zero
            // timeout — a waiter configured down to nothing still observes the store rather than
            // reporting a timeout it never tested.
            if (stopwatch.Elapsed >= Timeout)
                return new ProjectionWaitResult(false, subject, attempts, stopwatch.Elapsed, lastDetail);

            await Task.Delay(Interval, ct);
        }
    }
}

/// <summary>
/// One probe attempt's outcome. <see cref="Detail"/> is carried into the result either way, so a
/// timeout reports what the store last said rather than only that it said something wrong.
/// </summary>
public sealed record ProbeOutcome(bool Satisfied, string Detail)
{
    public static ProbeOutcome Ready(string detail) => new(true, detail);
    public static ProbeOutcome NotYet(string detail) => new(false, detail);
}

/// <summary>
/// The outcome of a bounded projection wait. <see cref="Satisfied"/> false is a normal return, not
/// an error: the caller turns it into a failed step naming <see cref="Subject"/>,
/// <see cref="Attempts"/>, <see cref="Elapsed"/> and <see cref="LastDetail"/>.
/// </summary>
public sealed record ProjectionWaitResult(
    bool Satisfied, string Subject, int Attempts, TimeSpan Elapsed, string LastDetail)
{
    /// <summary>The text a scenario reports when the wait expired, worded as a harness precondition.</summary>
    public string TimeoutDetail =>
        $"the harness waited for {Subject} to reach the projection and gave up after " +
        $"{Elapsed.TotalSeconds:0.0}s over {Attempts} attempt(s); last observation: {LastDetail}. " +
        "Search and Aggregate are served from the StarRocks projection, which a mapped write " +
        "reaches asynchronously through the outbox — this is the harness's own precondition " +
        "failing, not evidence about any client library.";
}
