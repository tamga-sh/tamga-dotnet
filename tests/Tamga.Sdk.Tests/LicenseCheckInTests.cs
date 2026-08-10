using System.Net;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class LicenseCheckInTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    [Fact]
    public async Task CheckInAsync_ReturnsUpdatedResource_WithBumpedLastCheckInAt()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var bumpedAt = DateTimeOffset.Parse("2024-06-01T12:00:00Z");
        var body = $$"""
        {
            "data": {
                "type": "licenses",
                "id": "{{licenseId}}",
                "attributes": { "key": "LIC-1", "suspended": false, "uses": 0, "last_check_in_at": "{{bumpedAt:O}}" }
            }
        }
        """;
        handler.Enqueue(HttpStatusCode.OK, body);

        var license = await client.CheckInAsync(licenseId);

        Assert.Equal(licenseId, license.Id);
        Assert.Equal(bumpedAt, license.LastCheckInAt);
        Assert.Equal("/v1/accounts/acct-1/licenses/" + licenseId + "/actions/check-in", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CheckInAsync_MapsCheckInNotRequired_ToTypedException()
    {
        var (client, handler) = MakeClient();
        const string errorBody = """{"errors":[{"id":"1","status":422,"code":"CHECK_IN_NOT_REQUIRED","title":"t","detail":"not required"}]}""";
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, errorBody);

        var ex = await Assert.ThrowsAsync<CheckInNotRequiredException>(() => client.CheckInAsync(Guid.NewGuid()));
        Assert.Equal("CHECK_IN_NOT_REQUIRED", ex.Error.Code);
    }
}
