using System.Net;
using Tamga.Sdk;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class TransportTests
{
    private static (TamgaTransport Transport, MockHttpMessageHandler Handler) MakeTransport(AuthTransport? auth = null, string? otp = null)
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions
        {
            AccountId = "acct-1",
            BaseUrl = "https://api.tamga.test",
            Auth = auth,
            Otp = otp,
        };
        return (new TamgaTransport(httpClient, options), handler);
    }

    [Fact]
    public async Task Bearer_SetsAuthorizationHeader()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.Bearer("tok-abc"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/licenses/actions/validate-key");

        var request = handler.Requests[0].Request;
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tok-abc", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task BasicEmailPassword_SetsBasicAuthorizationHeader()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.BasicEmailPassword("a@b.com", "pw"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        var auth = handler.Requests[0].Request.Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        var decoded = Convert.FromBase64String(auth.Parameter!);
        Assert.Equal("a@b.com:pw", System.Text.Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public async Task BasicToken_UsesTokenAsUsername_WithEmptyPassword()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.BasicToken("tok-xyz"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        var auth = handler.Requests[0].Request.Headers.Authorization!;
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal("tok-xyz:", decoded);
    }

    [Fact]
    public async Task BasicLicense_UsesLicensePrefix()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.BasicLicense("LIC-KEY-1"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        var auth = handler.Requests[0].Request.Headers.Authorization!;
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal("license:LIC-KEY-1", decoded);
    }

    [Fact]
    public async Task License_SetsLicenseAuthorizationScheme()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.License("LIC-KEY-1"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        var auth = handler.Requests[0].Request.Headers.Authorization!;
        Assert.Equal("License", auth.Scheme);
        Assert.Equal("LIC-KEY-1", auth.Parameter);
    }

    [Fact]
    public async Task Cookie_SetsCookieAndOriginHeaders()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.Cookie("session-uuid", "https://portal.tamga.test"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        var request = handler.Requests[0].Request;
        Assert.Equal("Tamga-Session=session-uuid", request.Headers.GetValues("Cookie").Single());
        Assert.Equal("https://portal.tamga.test", request.Headers.GetValues("Origin").Single());
    }

    [Fact]
    public async Task QueryToken_AppendsTokenQueryParam()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.QueryToken("tok-q"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        Assert.Contains("token=tok-q", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task QueryAuth_AppendsAuthQueryParam()
    {
        var (transport, handler) = MakeTransport(new AuthTransport.QueryAuth("tok-a"));
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        Assert.Contains("auth=tok-a", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task TamgaVersionHeader_IsAlwaysSent_Sanitized()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "a", BaseUrl = "https://api.tamga.test", ApiVersion = "1.0-beta!!!" };
        var transport = new TamgaTransport(httpClient, options);
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        Assert.Equal("1.0-beta", handler.Requests[0].Request.Headers.GetValues("Tamga-Version").Single());
    }

    [Fact]
    public async Task TamgaVersionHeader_TruncatesTo32Chars()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "a", BaseUrl = "https://api.tamga.test", ApiVersion = new string('9', 50) };
        var transport = new TamgaTransport(httpClient, options);
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await transport.SendAsync(HttpMethod.Get, "/x");

        Assert.Equal(32, handler.Requests[0].Request.Headers.GetValues("Tamga-Version").Single().Length);
    }

    [Fact]
    public async Task OtpHeader_SentOnlyWhenConfigured()
    {
        var (transportWithOtp, handlerWithOtp) = MakeTransport(otp: "123456");
        handlerWithOtp.Enqueue(HttpStatusCode.OK, "{}");
        await transportWithOtp.SendAsync(HttpMethod.Get, "/x");
        Assert.Equal("123456", handlerWithOtp.Requests[0].Request.Headers.GetValues("Tamga-OTP").Single());

        var (transportNoOtp, handlerNoOtp) = MakeTransport();
        handlerNoOtp.Enqueue(HttpStatusCode.OK, "{}");
        await transportNoOtp.SendAsync(HttpMethod.Get, "/x");
        Assert.False(handlerNoOtp.Requests[0].Request.Headers.Contains("Tamga-OTP"));
    }

    [Fact]
    public async Task DoesNotSend_TamgaEnvironmentHeader()
    {
        var (transport, handler) = MakeTransport();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        await transport.SendAsync(HttpMethod.Get, "/x");
        Assert.False(handler.Requests[0].Request.Headers.Contains("Tamga-Environment"));
    }

    private sealed record DummyAttributes(string Value);

    [Fact]
    public async Task SendJsonApiAsync_DeserializesJsonApiDocumentEnvelope()
    {
        var (transport, handler) = MakeTransport();
        const string body = """
        { "data": { "type": "licenses", "id": "11111111-1111-1111-1111-111111111111", "attributes": { "value": "hi" } }, "meta": { "code": "VALID" } }
        """;
        handler.Enqueue(HttpStatusCode.OK, body);

        var doc = await transport.SendJsonApiAsync<DummyAttributes>(HttpMethod.Post, "/licenses/actions/validate-key");

        Assert.NotNull(doc.Data);
        Assert.Equal("hi", doc.Data!.Attributes!.Value);
        Assert.True(doc.Meta!.Value.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task SendRawAsync_ReturnsBodyDirectly_ForQuickValidateFlatShape()
    {
        var (transport, handler) = MakeTransport();
        const string body = """{"ts":"2024-01-01T00:00:00Z","valid":true,"detail":"ok","code":"VALID"}""";
        handler.Enqueue(HttpStatusCode.OK, body, contentType: "application/json");

        var (responseBody, response) = await transport.SendRawAsync(HttpMethod.Get, "/licenses/11111111-1111-1111-1111-111111111111/actions/validate", jsonApiContentType: false);
        response.Dispose();

        Assert.DoesNotContain("\"data\"", responseBody);
        Assert.Contains("\"valid\":true", responseBody);
    }

    [Fact]
    public async Task SendJsonApiAsync_ThrowsMappedException_OnErrorStatus()
    {
        var (transport, handler) = MakeTransport();
        const string body = """{"errors":[{"id":"1","status":422,"code":"TTL_INVALID","title":"t","detail":"d"}]}""";
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, body);

        await Assert.ThrowsAsync<TtlInvalidException>(() =>
            transport.SendJsonApiAsync<DummyAttributes>(HttpMethod.Post, "/machines/1/actions/check-out"));
    }
}
