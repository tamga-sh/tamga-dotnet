using System.Net;
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

    [Fact]
    public void HeartbeatScheduler_DefaultInterval_IsWellInsideThe600sWindow()
    {
        Assert.True(HeartbeatScheduler.DefaultInterval < TimeSpan.FromSeconds(HeartbeatScheduler.ServerHeartbeatWindowSeconds));
        // "well inside" — at most half the window, per the ~1/3 guidance in the plan.
        Assert.True(HeartbeatScheduler.DefaultInterval <= TimeSpan.FromSeconds(HeartbeatScheduler.ServerHeartbeatWindowSeconds / 2.0));
    }

    [Fact]
    public void ProcessHeartbeatScheduler_DefaultInterval_IsWellInsideThe30sWindow()
    {
        Assert.True(ProcessHeartbeatScheduler.DefaultInterval < TimeSpan.FromSeconds(ProcessHeartbeatScheduler.ServerProcessHeartbeatWindowSeconds));
        Assert.NotEqual(HeartbeatScheduler.DefaultInterval, ProcessHeartbeatScheduler.DefaultInterval);
    }

    [Fact]
    public async Task HeartbeatScheduler_PingsRepeatedly_AndRaisesDead_WhenStatusObservedDead()
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

    [Fact]
    public async Task ProcessHeartbeatScheduler_PingsRepeatedly()
    {
        var (client, handler) = MakeClient();
        var processId = Guid.NewGuid();
        string ResourceJson() => $$"""{"id":"{{processId}}","machine_id":"{{Guid.NewGuid()}}","pid":"123"}""";
        handler.Enqueue(HttpStatusCode.OK, ResourceJson(), contentType: "application/json");
        handler.Enqueue(HttpStatusCode.OK, ResourceJson(), contentType: "application/json");

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
}
