using System.Net;
using System.Text.Json.Nodes;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>
/// The policy and licence read surface: <c>GET /policies/{id}</c>,
/// <c>GET /licenses/{id}/policy</c>, <c>GET /licenses/{id}</c>, and the policy-derived heartbeat
/// interval those unlock.
/// </summary>
public class PolicyReadTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    /// <summary>
    /// Every one of the 30 attributes the server's policy serializer emits, with the spellings and
    /// casings it actually uses. This fixture is the regression test for the defect the licence
    /// model shipped with and this one inherited: a model that quietly reads 16 of 30 fields and
    /// reports the rest as absent.
    /// </summary>
    private static readonly JsonObject FullPolicyAttributes = new()
    {
        ["product_id"] = "3f2a1c40-0000-4000-8000-000000000001",
        ["name"] = "Pro annual",
        ["duration"] = 31536000L,
        ["strict"] = true,
        ["floating"] = true,
        ["scheme"] = "ED25519_SIGN",
        ["encrypted"] = true,
        ["use_pool"] = true,
        ["protected"] = true,
        ["require_check_in"] = true,
        ["check_in_interval"] = "weekly",
        ["check_in_interval_count"] = 2,
        ["require_heartbeat"] = true,
        ["heartbeat_duration"] = 120,
        ["heartbeat_cull_strategy"] = "KEEP_DEAD",
        ["heartbeat_resurrection_strategy"] = "5_MINUTE_REVIVE",
        ["machine_uniqueness_strategy"] = "UNIQUE_PER_POLICY",
        ["expiration_strategy"] = "REVOKE_ACCESS",
        ["expiration_basis"] = "FROM_FIRST_ACTIVATION",
        ["renewal_basis"] = "FROM_NOW",
        ["authentication_strategy"] = "LICENSE",
        ["overage_strategy"] = "ALLOW_1_25X_OVERAGE",
        ["max_machines"] = 5,
        ["max_cores"] = 32,
        ["max_uses"] = 100,
        ["max_processes"] = 4,
        ["max_users"] = 3,
        ["metadata"] = new JsonObject { ["tier"] = "pro" },
        ["created"] = "2026-01-02T03:04:05Z",
        ["updated"] = "2026-01-03T03:04:05Z",
    };

    private static string PolicyBody(Guid id, JsonObject? attributes = null) => new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["type"] = "policies",
            ["id"] = id.ToString(),
            ["attributes"] = (attributes ?? FullPolicyAttributes).DeepClone(),
        },
    }.ToJsonString();

    [Fact]
    public async Task GetPolicyAsync_BindsEveryAttributeTheServerEmits()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, PolicyBody(id));

        var policy = await client.GetPolicyAsync(id);

        // The id comes from data.id, not from attributes.
        Assert.Equal(id, policy.Id);

        Assert.Equal(Guid.Parse("3f2a1c40-0000-4000-8000-000000000001"), policy.ProductId);
        Assert.Equal("Pro annual", policy.Name);
        Assert.Equal(31536000L, policy.Duration);
        Assert.True(policy.Strict);
        Assert.True(policy.Floating);
        Assert.Equal(LicenseScheme.Ed25519Sign, policy.Scheme);
        Assert.True(policy.Encrypted);
        Assert.True(policy.UsePool);
        Assert.True(policy.Protected);
        Assert.True(policy.RequireCheckIn);
        Assert.Equal(CheckInInterval.Week, policy.CheckInInterval);
        Assert.Equal(2, policy.CheckInIntervalCount);
        Assert.True(policy.RequireHeartbeat);
        Assert.Equal(120, policy.HeartbeatDuration);
        Assert.Equal(HeartbeatCullStrategy.KeepDead, policy.HeartbeatCullStrategy);
        Assert.Equal(HeartbeatResurrectionStrategy.FiveMinuteRevive, policy.HeartbeatResurrectionStrategy);
        Assert.Equal("UNIQUE_PER_POLICY", policy.MachineUniquenessStrategy);
        Assert.Equal(PolicyStrategies.RevokeAccess, policy.ExpirationStrategy);
        Assert.Equal("FROM_FIRST_ACTIVATION", policy.ExpirationBasis);
        Assert.Equal(PolicyStrategies.FromNow, policy.RenewalBasis);
        Assert.Equal(PolicyStrategies.License, policy.AuthenticationStrategy);
        Assert.Equal(OverageStrategy.Allow125xOverage, policy.OverageStrategy);
        Assert.Equal(5, policy.MaxMachines);
        Assert.Equal(32, policy.MaxCores);
        Assert.Equal(100, policy.MaxUses);
        Assert.Equal(4, policy.MaxProcesses);
        Assert.Equal(3, policy.MaxUsers);
        Assert.NotNull(policy.Metadata);
        Assert.NotNull(policy.Created);
        Assert.NotNull(policy.Updated);

        // The two that go the other way: enforced server-side, never serialized.
        Assert.Null(policy.MaxMemory);
        Assert.Null(policy.MaxDisk);

        Assert.Equal($"/v1/accounts/acct-1/policies/{id}", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// The stored spelling is the adverbial form — the column's own CHECK constraint permits only
    /// <c>daily|weekly|monthly|yearly</c>. Decoding <c>weekly</c> as <c>Day</c> (which the
    /// fall-through did, silently, before the noun-only mapping was corrected) understates the
    /// interval by a factor of seven.
    /// </summary>
    [Theory]
    [InlineData("daily", CheckInInterval.Day)]
    [InlineData("weekly", CheckInInterval.Week)]
    [InlineData("monthly", CheckInInterval.Month)]
    [InlineData("yearly", CheckInInterval.Year)]
    [InlineData("day", CheckInInterval.Day)]
    [InlineData("week", CheckInInterval.Week)]
    [InlineData("month", CheckInInterval.Month)]
    [InlineData("year", CheckInInterval.Year)]
    [InlineData("fortnightly", CheckInInterval.Day)]
    public async Task GetPolicyAsync_DecodesEveryCheckInIntervalSpelling(string wire, CheckInInterval expected)
    {
        var (client, handler) = MakeClient();
        var attrs = FullPolicyAttributes.DeepClone().AsObject();
        attrs["check_in_interval"] = wire;
        handler.Enqueue(HttpStatusCode.OK, PolicyBody(Guid.NewGuid(), attrs));

        var policy = await client.GetPolicyAsync(Guid.NewGuid());

        Assert.Equal(expected, policy.CheckInInterval);
    }

    /// <summary>
    /// A freshly created policy really does report two strings that are not variants of anything.
    /// Neither may throw, and neither may become a distinct C# member implying a restriction the
    /// server does not apply.
    /// </summary>
    [Fact]
    public async Task GetPolicyAsync_SurvivesTheNonRealEnumStringsAFreshPolicyReports()
    {
        var (client, handler) = MakeClient();
        var attrs = FullPolicyAttributes.DeepClone().AsObject();
        attrs["overage_strategy"] = "DENY_ACCESS";
        attrs["heartbeat_resurrection_strategy"] = "NO_RESURRECTION";
        handler.Enqueue(HttpStatusCode.OK, PolicyBody(Guid.NewGuid(), attrs));

        var policy = await client.GetPolicyAsync(Guid.NewGuid());

        Assert.Equal(OverageStrategy.NoOverage, policy.OverageStrategy);
        Assert.Equal(HeartbeatResurrectionStrategy.NoRevive, policy.HeartbeatResurrectionStrategy);
    }

    [Fact]
    public async Task GetLicensePolicyAsync_ReadsTheLicensesGoverningPolicy()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, PolicyBody(policyId));

        var policy = await client.GetLicensePolicyAsync(licenseId);

        Assert.Equal(policyId, policy.Id);
        Assert.Equal($"/v1/accounts/acct-1/licenses/{licenseId}/policy", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void EffectiveHeartbeatDuration_UsesThePolicyValue_AndFallsBackTo600()
    {
        Assert.Equal(120, new Policy { HeartbeatDuration = 120 }.EffectiveHeartbeatDurationSeconds);
        Assert.Equal(600, new Policy().EffectiveHeartbeatDurationSeconds);
        Assert.Equal(TimeSpan.FromSeconds(600), new Policy().EffectiveHeartbeatWindow);
    }

    [Fact]
    public void IntervalForWindow_IsAThirdOfTheWindow_AndGuardsNonPositiveInput()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(120)));
        Assert.Equal(HeartbeatScheduler.DefaultInterval, HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(600)));
        // PeriodicTimer rejects a non-positive period, so a nonsense window must not become one.
        // A non-positive window means "unspecified", not "very short", so it takes the default
        // rather than the floor — the same split tamga-java draws.
        Assert.Equal(HeartbeatScheduler.DefaultInterval, HeartbeatScheduler.IntervalForWindow(TimeSpan.Zero));
        Assert.Equal(HeartbeatScheduler.DefaultInterval, HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(-5)));
    }

    /// <summary>
    /// A window short enough that a third of it lands under a second yields the floor instead, so
    /// it is the divisor's two-loss promise that degrades rather than the ping rate that runs away.
    /// </summary>
    [Fact]
    public void IntervalForWindow_FloorsAShortWindowAtOneSecond()
    {
        // 3s is the first window where floor and divisor agree exactly; below it the floor binds.
        Assert.Equal(TimeSpan.FromSeconds(1), HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(1), HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(1), HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(1)));
        // Just above the agreement point the divisor governs again, unfloored.
        Assert.Equal(TimeSpan.FromSeconds(2), HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(6)));
    }

    /// <summary>
    /// The age at which a machine first reads <c>DEAD</c>, in the server's own terms.
    /// </summary>
    /// <remarks>
    /// ⚠ The server's rule is <b>not</b> <c>age &gt; window</c>. From
    /// <c>tamga-api/src/features/machines/model.rs::heartbeat_status_within</c>:
    /// <code>
    /// let age_secs = (Utc::now() - hb_ts).num_seconds();
    /// let within_window = age_secs &lt;= window_secs;
    /// </code>
    /// and chrono's <c>num_seconds()</c> returns <em>whole</em> seconds, truncating —
    /// <c>Duration::milliseconds(1999).num_seconds() == 1</c>. So a machine reads <c>DEAD</c> only
    /// once its age reaches <c>window_secs + 1</c>, and every window carries one free second on top
    /// of its nominal value. Reading this pessimistically makes a 1s window look unserveable at a
    /// 1s ping when it in fact has two seconds of slack, and that misreading is what makes the
    /// floor look broken when it is not.
    /// </remarks>
    private static TimeSpan DeadAtAge(int windowSeconds) => TimeSpan.FromSeconds(windowSeconds + 1);

    /// <summary>
    /// Consecutive pings that can be lost before a read sees <c>DEAD</c>, given a scheduler ticking
    /// every <paramref name="interval"/>. After <c>m</c> misses the age reaches
    /// <c>(m + 1) * interval</c>; <c>-1</c> means the window is not held even when no ping is lost.
    /// </summary>
    private static int LossesTolerated(int windowSeconds, TimeSpan interval) =>
        (int)Math.Ceiling(DeadAtAge(windowSeconds).TotalMilliseconds / interval.TotalMilliseconds) - 2;

    /// <summary>
    /// The floor and the divisor, in one place, against the server's real liveness rule — window
    /// value by window value, so the interaction is readable rather than re-derived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These two numbers interact: for a short enough policy window the floor binds and
    /// <see cref="HeartbeatScheduler.IntervalForWindow"/>'s stated promise ("two consecutive pings
    /// can be lost") stops holding. Window 3 is where floor and divisor first agree; 2 keeps one
    /// spare ping, 1 keeps none, and steady state still holds all three. The one window the floor
    /// cannot hold is <c>0</c> — not <c>1</c>. <c>heartbeat_duration</c> is an unconstrained
    /// <c>INTEGER</c> server-side with no <c>CHECK</c>, and
    /// <c>effective_heartbeat_duration_secs</c> returns <c>0</c> verbatim, so every one of these is
    /// storable.
    /// </para>
    /// <para>
    /// ⚠ <b>Standing caveat — this is the test that breaks first.</b> Every row here rests on
    /// <see cref="DeadAtAge"/>, i.e. on <c>num_seconds()</c> truncating. If the server ever
    /// compares sub-second, that free second disappears: window <c>0</c> becomes unserveable at any
    /// rate, window <c>1</c> becomes a genuine boundary case rather than a comfortable one, and
    /// every loss figure below drops by one. Re-derive the table from the server rule before
    /// changing any number in it — do not adjust an expectation to make this go green.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(600, 200_000, 2)]   // the fallback window: divisor governs, floor irrelevant
    [InlineData(60, 20_000, 2)]     // an ordinary policy: same
    [InlineData(3, 1_000, 2)]       // first window where floor and divisor agree exactly
    [InlineData(2, 1_000, 1)]       // floor binds: promise degraded from 2 losses to 1
    [InlineData(1, 1_000, 0)]       // floor binds hardest: steady state fine, no loss spare
    [InlineData(0, 200_000, -1)]    // "unspecified", so the default — and not held either way
    public void HeartbeatDuration_PinsTheIntervalItProduces_AndTheLossesItTolerates(
        int heartbeatDuration, int expectedIntervalMs, int expectedLosses)
    {
        var window = new Policy { HeartbeatDuration = heartbeatDuration }.EffectiveHeartbeatWindow;

        var interval = HeartbeatScheduler.IntervalForWindow(window);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedIntervalMs), interval);
        Assert.Equal(expectedLosses, LossesTolerated(heartbeatDuration, interval));
        // Steady state holds exactly when the loss budget is non-negative.
        Assert.Equal(expectedLosses >= 0, interval < DeadAtAge(heartbeatDuration));
        // Whatever the window, the SDK never pings faster than once a second.
        Assert.True(interval >= TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// <c>heartbeat_duration = 0</c> is the one window the floor cannot hold — and the SDK
    /// deliberately does not chase it.
    /// </summary>
    /// <remarks>
    /// Truncation gives a 0s window exactly 1000ms of grace, which is precisely the floor, so even
    /// a ping at the floor arrives at the instant the age reaches the <c>DEAD</c> threshold. A
    /// ~333ms ping would in fact hold it, and that is exactly why it is not done: it would buy one
    /// absurd policy value by pinning this SDK's request rate to <c>num_seconds()</c> truncation,
    /// a server implementation artifact rather than a protocol guarantee. See the standing caveat
    /// on <see cref="HeartbeatDuration_PinsTheIntervalItProduces_AndTheLossesItTolerates"/>.
    /// </remarks>
    [Fact]
    public void HeartbeatDurationZero_IsTheOneWindowTheFloorCannotHold()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), DeadAtAge(0));
        // Not held at the floor — the counterfactual the fleet contract's table names.
        Assert.Equal(-1, LossesTolerated(0, TimeSpan.FromSeconds(1)));
        // Not held at the default this SDK actually produces for it, either.
        Assert.Equal(-1, LossesTolerated(0, HeartbeatScheduler.IntervalForWindow(TimeSpan.Zero)));
        // A sub-second ping would hold it. This assertion is the one that flips if the server
        // ever stops truncating; it is deliberately not what the SDK does.
        Assert.True(LossesTolerated(0, TimeSpan.FromMilliseconds(333)) >= 0);
    }

    /// <summary>
    /// Truncation is what makes a 1s window comfortable rather than a boundary case, so pin it
    /// directly instead of leaving it implied by the table.
    /// </summary>
    [Fact]
    public void TruncationGivesEveryWindowAFullExtraSecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DeadAtAge(1));
        Assert.Equal(TimeSpan.FromSeconds(3), DeadAtAge(2));
        Assert.Equal(TimeSpan.FromSeconds(601), DeadAtAge(600));
        // The pessimistic reading — DEAD the instant age passes the nominal window — would put a
        // 1s window's deadline at 1000ms and make the 1s floor a boundary case. It is 2000ms, so
        // the floor has 2x margin.
        Assert.True(DeadAtAge(1) > TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The half of the heartbeat-window story that used to be missing: a scheduler can now be sized
    /// from the policy that actually governs the machine, in one call, instead of from the 600s
    /// fallback.
    /// </summary>
    [Fact]
    public async Task GetHeartbeatIntervalAsync_SizesTheIntervalFromTheGoverningPolicy()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, PolicyBody(Guid.NewGuid()));   // heartbeat_duration = 120

        var interval = await client.GetHeartbeatIntervalAsync(Guid.NewGuid());

        Assert.Equal(TimeSpan.FromSeconds(40), interval);
        // And it is nothing like the fallback-derived default the scheduler would otherwise use.
        Assert.NotEqual(HeartbeatScheduler.DefaultInterval, interval);
    }

    [Fact]
    public async Task GetHeartbeatIntervalAsync_FallsBackTo600_WhenThePolicySetsNoDuration()
    {
        var (client, handler) = MakeClient();
        var attrs = FullPolicyAttributes.DeepClone().AsObject();
        attrs["heartbeat_duration"] = null;
        handler.Enqueue(HttpStatusCode.OK, PolicyBody(Guid.NewGuid(), attrs));

        Assert.Equal(HeartbeatScheduler.DefaultInterval, await client.GetHeartbeatIntervalAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// The licence model already carries all 21 attributes the serializer emits; this pins that so
    /// the read route cannot reintroduce a partial mapping.
    /// </summary>
    [Fact]
    public async Task GetLicenseAsync_BindsEveryAttributeTheServerEmits()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "licenses",
                ["id"] = id.ToString(),
                ["attributes"] = new JsonObject
                {
                    ["name"] = "Acme seat",
                    ["key"] = "LIC-XXXX",
                    ["status"] = "ACTIVE",
                    ["expiry"] = "2027-01-02T03:04:05Z",
                    ["suspended"] = false,
                    ["protected"] = true,
                    ["uses"] = 7,
                    ["scheme"] = "ED25519_SIGN",
                    ["encrypted"] = true,
                    ["strict"] = true,
                    ["floating"] = false,
                    ["max_machines"] = 3,
                    ["max_uses"] = 50,
                    ["max_users"] = 2,
                    ["last_validated_at"] = "2026-01-02T03:04:05Z",
                    ["last_check_in_at"] = "2026-01-03T03:04:05Z",
                    ["last_check_out_at"] = "2026-01-04T03:04:05Z",
                    ["machines_count"] = 2,
                    ["metadata"] = new JsonObject { ["seat"] = "1" },
                    ["created"] = "2025-12-01T00:00:00Z",
                    ["updated"] = "2026-01-04T00:00:00Z",
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, body);

        var license = await client.GetLicenseAsync(id);

        Assert.Equal(id, license.Id);
        Assert.Equal("Acme seat", license.Name);
        Assert.Equal("LIC-XXXX", license.Key);
        Assert.Equal("ACTIVE", license.Status);
        Assert.NotNull(license.Expiry);
        Assert.False(license.Suspended);
        Assert.True(license.Protected);
        Assert.Equal(7, license.Uses);
        Assert.Equal("ED25519_SIGN", license.Scheme);
        Assert.True(license.Encrypted);
        Assert.True(license.Strict);
        Assert.False(license.Floating);
        Assert.Equal(3, license.MaxMachines);
        Assert.Equal(50, license.MaxUses);
        Assert.Equal(2, license.MaxUsers);
        Assert.NotNull(license.LastValidatedAt);
        Assert.NotNull(license.LastCheckInAt);
        Assert.NotNull(license.LastCheckOutAt);
        Assert.Equal(2, license.MachinesCount);
        Assert.NotNull(license.Metadata);
        Assert.NotNull(license.Created);
        Assert.NotNull(license.Updated);

        var request = handler.Requests[0].Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/v1/accounts/acct-1/licenses/{id}", request.RequestUri!.AbsolutePath);
        // A plain read: no body, and no validate/check-in side effect to trigger.
        Assert.Null(request.Content);
    }

    /// <summary>
    /// No serializer in the API emits a <c>relationships</c> object, and the licence resource has
    /// no <c>policy_id</c> attribute either — so a read cannot populate the four obsolete id
    /// properties, and <c>GET /licenses/{id}/policy</c> is the only route from a licence to its
    /// policy.
    /// </summary>
    [Fact]
    public async Task GetLicenseAsync_LeavesTheObsoleteRelationshipIdsNull()
    {
        var (client, handler) = MakeClient();
        var body = """{"data":{"type":"licenses","id":"11111111-1111-4111-8111-111111111111","attributes":{"status":"ACTIVE"}}}""";
        handler.Enqueue(HttpStatusCode.OK, body);

        var license = await client.GetLicenseAsync(Guid.Parse("11111111-1111-4111-8111-111111111111"));

#pragma warning disable CS0618 // pinning the documented "always null" behaviour is the point
        Assert.Null(license.PolicyId);
        Assert.Null(license.ProductId);
        Assert.Null(license.UserId);
        Assert.Null(license.EnvironmentId);
#pragma warning restore CS0618
    }

    [Fact]
    public async Task GetPolicyAsync_MapsA404ToTheTypedException()
    {
        var (client, handler) = MakeClient();
        const string body = """{"errors":[{"id":"1","status":"404","code":"NOT_FOUND","title":"Not Found","detail":"policy not found"}]}""";
        handler.Enqueue(HttpStatusCode.NotFound, body);

        await Assert.ThrowsAsync<TamgaNotFoundException>(() => client.GetPolicyAsync(Guid.NewGuid()));
    }
}
