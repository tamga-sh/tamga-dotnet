using System.Net;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>
/// Process row removal. Nothing on the server removes these rows — the process reaper is not wired
/// up — so a process registered by a short-lived worker outlives it permanently unless a client
/// deletes it, and the accumulated rows count against <c>policy.max_processes</c>.
/// </summary>
public class ProcessLifecycleTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    [Fact]
    public async Task DeleteProcessAsync_SendsADeleteToTheProcessRoute()
    {
        var (client, handler) = MakeClient();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.DeleteProcessAsync(processId);

        var request = handler.Requests[0].Request;
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/v1/accounts/acct-1/processes/{processId}", request.RequestUri!.AbsolutePath);
        Assert.Null(request.Content);
    }

    [Fact]
    public async Task DeleteProcessAsync_MapsA404ToTheTypedException()
    {
        var (client, handler) = MakeClient();
        const string body = """{"errors":[{"id":"1","status":"404","code":"NOT_FOUND","title":"Not Found","detail":"process not found"}]}""";
        handler.Enqueue(HttpStatusCode.NotFound, body);

        await Assert.ThrowsAsync<TamgaNotFoundException>(() => client.DeleteProcessAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ProcessHeartbeatScheduler_DeletesTheRowOnDispose_WhenAsked()
    {
        var (client, handler) = MakeClient();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var scheduler = new ProcessHeartbeatScheduler(client, processId, TimeSpan.FromMinutes(10))
        {
            DeleteOnDispose = true,
        };
        scheduler.Start();
        await scheduler.DisposeAsync();

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/v1/accounts/acct-1/processes/{processId}", request.RequestUri!.AbsolutePath);
    }

    /// <summary>Default off: disposing a scheduler must not silently remove a row a caller still wants.</summary>
    [Fact]
    public async Task ProcessHeartbeatScheduler_DoesNotDeleteByDefault()
    {
        var (client, handler) = MakeClient();

        var scheduler = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromMinutes(10));
        scheduler.Start();
        await scheduler.DisposeAsync();

        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Disposal must not throw. A cleanup failure is worth reporting on <c>Faulted</c>, but not at
    /// the cost of replacing whatever exception was already unwinding the caller's scope.
    /// </summary>
    [Fact]
    public async Task ProcessHeartbeatScheduler_ReportsAFailedCleanupWithoutThrowingFromDispose()
    {
        var (client, handler) = MakeClient();
        const string body = """{"errors":[{"id":"1","status":"500","code":"INTERNAL_SERVER_ERROR","title":"Error","detail":"boom"}]}""";
        handler.Enqueue(HttpStatusCode.InternalServerError, body);

        Exception? reported = null;
        var scheduler = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromMinutes(10))
        {
            DeleteOnDispose = true,
        };
        scheduler.Faulted += ex => reported = ex;
        scheduler.Start();

        await scheduler.DisposeAsync();

        Assert.IsType<TamgaInternalServerErrorException>(reported);
    }

    /// <summary>
    /// The cleanup runs on its own token. Cancelling the pings IS the signal to clean up, so
    /// threading the scheduler's just-cancelled token into the delete would cancel the very work
    /// the cancellation asked for.
    /// </summary>
    [Fact]
    public async Task ProcessHeartbeatScheduler_CleansUpEvenAfterItsOwnTokenWasCancelled()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        // No tick is needed or wanted here: this asserts what DisposeAsync does, not what the
        // loop does. (It used to pass 10ms and incidentally cover the ping-failure path; that path
        // now has its own test rather than depending on a race.)
        var scheduler = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromMinutes(10))
        {
            DeleteOnDispose = true,
        };
        scheduler.Start();
        await Task.Delay(30);
        await scheduler.DisposeAsync();

        Assert.Contains(handler.Requests, r => r.Request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ProcessHeartbeatScheduler_DisposingTwiceDeletesOnlyOnce()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var scheduler = new ProcessHeartbeatScheduler(client, Guid.NewGuid(), TimeSpan.FromMinutes(10))
        {
            DeleteOnDispose = true,
        };
        scheduler.Start();
        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync();

        Assert.Single(handler.Requests);
    }
}
