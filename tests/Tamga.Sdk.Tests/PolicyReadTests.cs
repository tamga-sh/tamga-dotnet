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
        Assert.Equal(HeartbeatScheduler.DefaultInterval, HeartbeatScheduler.IntervalForWindow(TimeSpan.Zero));
        Assert.Equal(HeartbeatScheduler.DefaultInterval, HeartbeatScheduler.IntervalForWindow(TimeSpan.FromSeconds(-5)));
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
