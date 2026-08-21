using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class HeartbeatSchedulerTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    /// <summary>
    /// Pins the default interval against the 600s FALLBACK window only. 600s is what the server
    /// uses when <c>policy.heartbeat_duration</c> is null; a policy that sets it overrides the
    /// window, and this SDK has no policy getter with which to discover that — so this assertion
    /// says nothing about a shorter-window policy, where the caller must supply their own interval.
    /// </summary>
    [Fact]
    public void HeartbeatScheduler_DefaultInterval_IsWellInsideTheFallbackWindow()
    {
        Assert.True(HeartbeatScheduler.DefaultInterval < TimeSpan.FromSeconds(HeartbeatScheduler.ServerHeartbeatWindowSeconds));
        // "well inside" — at most half the window; the documented default targets ~1/3 of it.
        Assert.True(HeartbeatScheduler.DefaultInterval <= TimeSpan.FromSeconds(HeartbeatScheduler.ServerHeartbeatWindowSeconds / 2.0));
    }

    [Fact]
    public void ProcessHeartbeatScheduler_DefaultInterval_IsWellInsideThe30sWindow()
    {
        Assert.True(ProcessHeartbeatScheduler.DefaultInterval < TimeSpan.FromSeconds(ProcessHeartbeatScheduler.ServerProcessHeartbeatWindowSeconds));
        Assert.NotEqual(HeartbeatScheduler.DefaultInterval, ProcessHeartbeatScheduler.DefaultInterval);
    }

    /// <summary>
    /// Covers the event-wiring itself: a <c>DEAD</c> status on a response reaches the
    /// <see cref="HeartbeatScheduler.Dead"/> handler, and pinging continues. The <c>DEAD</c>
    /// response is synthetic — <c>ping-heartbeat</c> cannot produce one, since it writes
    /// <c>last_heartbeat_at = NOW()</c> and reports the status derived from it. The wiring is
    /// still worth pinning: it is what makes the event correct if a machine-read method is added.
    /// </summary>
    [Fact]
    public async Task HeartbeatScheduler_PingsRepeatedly_AndRaisesDead_OnASyntheticDeadResponse()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        string ResourceJson(string status) => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "machines",
                ["id"] = machineId.ToString(),
                ["attributes"] = new JsonObject { ["fingerprint"] = "fp", ["heartbeat_status"] = status },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, ResourceJson("ALIVE"));
        handler.Enqueue(HttpStatusCode.OK, ResourceJson("DEAD"));

        var pinged = new List<Machine>();
        Machine? dead = null;
        var deadSignal = new TaskCompletionSource();

        await using var scheduler = new HeartbeatScheduler(client, machineId, TimeSpan.FromMilliseconds(10));
        scheduler.Pinged += m => pinged.Add(m);
        scheduler.Dead += m =>
        {
            dead = m;
            deadSignal.TrySetResult();
        };
        scheduler.Start();

        await deadSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(pinged.Count >= 2);
        Assert.NotNull(dead);
        Assert.Equal(HeartbeatStatus.Dead, dead!.HeartbeatStatus);
    }

    /// <summary>
    /// Regression: no status read off a response ends the ping loop. The mocked <c>DEAD</c>
    /// responses below are DELIBERATELY SYNTHETIC — the live server cannot produce them on this
    /// route, because <c>ping-heartbeat</c> writes <c>last_heartbeat_at = NOW()</c> and then
    /// derives the status from that same timestamp, so a real ping answers <c>ALIVE</c> or
    /// <c>RESURRECTED</c>. That is exactly why the test is worth keeping: it pins the defensive
    /// property that the loop survives ANY status it is handed, including one the current server
    /// never sends and one a future machine-read method might. Breaking, returning, or
    /// short-circuiting on a status would strand a machine that was one ping away from coming
    /// back. The genuine terminal signal is a 404 — covered by the test below this one.
    /// </summary>
    [Fact]
    public async Task HeartbeatScheduler_KeepsPinging_AcrossThreeConsecutiveDeadResponses_AndRevives()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        string ResourceJson(string status) => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "machines",
                ["id"] = machineId.ToString(),
                ["attributes"] = new JsonObject { ["fingerprint"] = "fp", ["heartbeat_status"] = status },
            },
        }.ToJsonString();

        // Three consecutive synthetic DEADs, then a status the loop only ever reaches by
        // continuing to ping. Trailing ALIVEs are padding so a tick landing between the signal and
        // DisposeAsync cannot exhaust the mock's queue and turn into unrelated Faulted noise.
        handler.Enqueue(HttpStatusCode.OK, ResourceJson("DEAD"));
        handler.Enqueue(HttpStatusCode.OK, ResourceJson("DEAD"));
        handler.Enqueue(HttpStatusCode.OK, ResourceJson("DEAD"));
        handler.Enqueue(HttpStatusCode.OK, ResourceJson("RESURRECTED"));
        for (var i = 0; i < 8; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, ResourceJson("ALIVE"));
        }

        var observed = new List<HeartbeatStatus>();
        var deadCount = 0;
        var faults = new List<Exception>();
        var faultsAtRevival = -1;
        var revived = new TaskCompletionSource();

        await using (var scheduler = new HeartbeatScheduler(client, machineId, TimeSpan.FromMilliseconds(10)))
        {
            scheduler.Pinged += m =>
            {
                observed.Add(m.HeartbeatStatus);
                if (m.HeartbeatStatus == HeartbeatStatus.Resurrected)
                {
                    faultsAtRevival = faults.Count;
                    revived.TrySetResult();
                }
            };
            scheduler.Dead += _ => deadCount++;
            scheduler.Faulted += faults.Add;
            scheduler.Start();

            await revived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Four pings actually left the client: the loop did not stop at the first DEAD, nor at the
        // third.
        Assert.Equal(
            new[] { HeartbeatStatus.Dead, HeartbeatStatus.Dead, HeartbeatStatus.Dead, HeartbeatStatus.Resurrected },
            observed.Take(4));
        Assert.Equal(3, deadCount);
        Assert.True(handler.Requests.Count >= 4);
        Assert.All(handler.Requests.Take(4), r =>
            Assert.EndsWith($"/machines/{machineId}/actions/ping-heartbeat", r.Request.RequestUri!.AbsolutePath, StringComparison.Ordinal));

        // Nothing above was an error path — a DEAD machine answers a ping normally.
        Assert.Equal(0, faultsAtRevival);
    }

    /// <summary>
    /// The counterpart to the DEAD test above: a <c>404</c> from the ping — not a <c>DEAD</c>
    /// status — is the one authoritative "this machine row is gone" signal, and it arrives on
    /// <see cref="HeartbeatScheduler.Faulted"/> as a <see cref="TamgaNotFoundException"/>. That is
    /// where a caller hangs re-activation.
    /// </summary>
    [Fact]
    public async Task HeartbeatScheduler_PingReturning404_SurfacesTamgaNotFoundExceptionOnFaulted()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        handler.Enqueue(
            HttpStatusCode.NotFound,
            """{"errors":[{"id":"1","status":"404","code":"NOT_FOUND","title":"Not Found","detail":"machine not found"}]}""");

        Exception? fault = null;
        var faulted = new TaskCompletionSource();

        await using (var scheduler = new HeartbeatScheduler(client, machineId, TimeSpan.FromMilliseconds(10)))
        {
            scheduler.Faulted += ex =>
            {
                fault ??= ex;
                faulted.TrySetResult();
            };
            scheduler.Start();

            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsType<TamgaNotFoundException>(fault);
    }

    [Fact]
    public async Task HeartbeatScheduler_DisposeAsync_IsIdempotent()
    {
        // Code-review regression: a second DisposeAsync() call must not throw
        // ObjectDisposedException from the underlying CancellationTokenSource.
        var (client, _) = MakeClient();
        var scheduler = new HeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromMinutes(10));
        scheduler.Start();

        await scheduler.DisposeAsync();
        var ex = await Record.ExceptionAsync(async () => await scheduler.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task HeartbeatScheduler_ThrowingPingedHandler_DoesNotKillLoop_ReroutesToFaulted()
    {
        // Code-review regression: a throwing event subscriber must not silently terminate the
        // ping loop — it should be caught and rerouted to Faulted, and pinging must continue.
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        string ResourceJson() => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "machines",
                ["id"] = machineId.ToString(),
                ["attributes"] = new JsonObject { ["fingerprint"] = "fp", ["heartbeat_status"] = "ALIVE" },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, ResourceJson());
        handler.Enqueue(HttpStatusCode.OK, ResourceJson());

        var faultedSignal = new TaskCompletionSource();
        var secondPingSignal = new TaskCompletionSource();
        var pingCount = 0;

        await using var scheduler = new HeartbeatScheduler(client, machineId, TimeSpan.FromMilliseconds(10));
        scheduler.Pinged += _ =>
        {
            pingCount++;
            if (pingCount == 1)
            {
                throw new InvalidOperationException("boom from a consumer handler");
            }

            secondPingSignal.TrySetResult();
        };
        scheduler.Faulted += _ => faultedSignal.TrySetResult();
        scheduler.Start();

        await faultedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondPingSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(pingCount >= 2);
    }

    [Fact]
    public async Task ProcessHeartbeatScheduler_DisposeAsync_IsIdempotent()
    {
        var (client, _) = MakeClient();
        var scheduler = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromMinutes(10));
        scheduler.Start();

        await scheduler.DisposeAsync();
        var ex = await Record.ExceptionAsync(async () => await scheduler.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task ProcessHeartbeatScheduler_PingsRepeatedly()
    {
        var (client, handler) = MakeClient();
        var processId = Guid.NewGuid();
        // JSON:API-enveloped, per processes/serializer.rs — the ping response is a {type, id,
        // attributes} document, not the flat object the REQUEST bodies on these routes use.
        string ResourceJson() => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "processes",
                ["id"] = processId.ToString(),
                ["attributes"] = new JsonObject
                {
                    ["pid"] = "123",
                    ["machine_id"] = Guid.NewGuid().ToString(),
                    ["last_heartbeat_at"] = "2026-01-02T03:04:05Z",
                    ["metadata"] = new JsonObject(),
                    ["created"] = "2026-01-02T03:04:05Z",
                    ["updated"] = "2026-01-02T03:04:05Z",
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, ResourceJson());
        handler.Enqueue(HttpStatusCode.OK, ResourceJson());

        var pingedCount = 0;
        var secondPing = new TaskCompletionSource();

        await using var scheduler = new ProcessHeartbeatScheduler(client, processId, TimeSpan.FromMilliseconds(10));
        scheduler.Pinged += _ =>
        {
            pingedCount++;
            if (pingedCount >= 2)
            {
                secondPing.TrySetResult();
            }
        };
        scheduler.Start();

        await secondPing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pingedCount >= 2);
    }

    /// <summary>
    /// Reads the period a scheduler actually handed to its <see cref="PeriodicTimer"/>. Nothing
    /// public exposes it, and reflection is deliberate: without it these tests could only assert
    /// "the constructor did not throw", which would pass equally on a clamp to the default and on
    /// a clamp to some arbitrary other value.
    /// </summary>
    private static TimeSpan ConfiguredPeriod(object scheduler)
    {
        var field = scheduler.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var timer = Assert.IsType<PeriodicTimer>(field!.GetValue(scheduler));
        return timer.Period;
    }

    /// <summary>
    /// Regression: a non-positive interval falls back to <see cref="HeartbeatScheduler.DefaultInterval"/>
    /// instead of reaching <see cref="PeriodicTimer"/> and throwing
    /// <see cref="ArgumentOutOfRangeException"/> from inside it.
    /// </summary>
    /// <remarks>
    /// Reachable from real data: <c>policy.heartbeat_duration</c> carries no <c>CHECK</c>
    /// constraint server-side and <c>effective_heartbeat_duration_secs</c> hands back <c>0</c> or a
    /// negative verbatim, so only a caller who goes through
    /// <see cref="TamgaClient.GetHeartbeatIntervalAsync(Guid, CancellationToken)"/> — and so
    /// through the already-guarded <see cref="HeartbeatScheduler.IntervalForWindow"/> — is safe.
    /// One who does the division themselves lands here. tamga-go, tamga-java and tamga-swift all
    /// clamp in their scheduler constructor; this pins that tamga-dotnet now does too.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-600)]
    public async Task HeartbeatScheduler_NonPositiveInterval_FallsBackToDefault_RatherThanThrowing(int seconds)
    {
        var (client, _) = MakeClient();

        await using var scheduler = new HeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromSeconds(seconds));

        Assert.Equal(HeartbeatScheduler.DefaultInterval, ConfiguredPeriod(scheduler));
    }

    /// <summary>
    /// The clamp touches only inputs that used to throw. A positive interval, and an omitted one,
    /// still mean exactly what they always did — and <see cref="Timeout.InfiniteTimeSpan"/>, the
    /// one non-positive value <see cref="PeriodicTimer"/> accepts on net8.0 (as "never tick"),
    /// survives unchanged rather than being silently repurposed into a ~200s ping loop.
    /// </summary>
    [Fact]
    public async Task HeartbeatScheduler_IntervalsTheTimerAlreadyAccepted_AreUnchanged()
    {
        var (client, _) = MakeClient();

        await using var explicitInterval = new HeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromSeconds(7));
        await using var omitted = new HeartbeatScheduler(client, Guid.NewGuid());
        await using var infinite = new HeartbeatScheduler(client, Guid.NewGuid(), Timeout.InfiniteTimeSpan);

        Assert.Equal(TimeSpan.FromSeconds(7), ConfiguredPeriod(explicitInterval));
        Assert.Equal(HeartbeatScheduler.DefaultInterval, ConfiguredPeriod(omitted));
        Assert.Equal(Timeout.InfiniteTimeSpan, ConfiguredPeriod(infinite));
    }

    /// <summary>
    /// The process scheduler had the identical unguarded constructor and gets the identical clamp —
    /// the two must not answer the same caller mistake differently. Note the fallback is
    /// <see cref="ProcessHeartbeatScheduler.DefaultInterval"/> (~10s), not the machine one: the
    /// process heartbeat window is a hardcoded 30s server-side.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public async Task ProcessHeartbeatScheduler_NonPositiveInterval_FallsBackToDefault_RatherThanThrowing(int seconds)
    {
        var (client, _) = MakeClient();

        await using var scheduler = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromSeconds(seconds));

        Assert.Equal(ProcessHeartbeatScheduler.DefaultInterval, ConfiguredPeriod(scheduler));
    }

    /// <summary>The process-scheduler counterpart to the pass-through assertions above.</summary>
    [Fact]
    public async Task ProcessHeartbeatScheduler_IntervalsTheTimerAlreadyAccepted_AreUnchanged()
    {
        var (client, _) = MakeClient();

        await using var explicitInterval = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromSeconds(3));
        await using var omitted = new ProcessHeartbeatScheduler(client, Guid.NewGuid());
        await using var infinite = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), Timeout.InfiniteTimeSpan);

        Assert.Equal(TimeSpan.FromSeconds(3), ConfiguredPeriod(explicitInterval));
        Assert.Equal(ProcessHeartbeatScheduler.DefaultInterval, ConfiguredPeriod(omitted));
        Assert.Equal(Timeout.InfiniteTimeSpan, ConfiguredPeriod(infinite));
    }
}
