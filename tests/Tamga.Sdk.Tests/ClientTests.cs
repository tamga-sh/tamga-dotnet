using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class ClientTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    [Fact]
    public void Constructor_OptionsOnly_OwnsAndCanDisposeItsOwnHttpClient()
    {
        var options = new TamgaClientOptions { AccountId = "a", BaseUrl = "https://api.tamga.test" };
        using var client = new TamgaClient(options);
        Assert.Equal("a", client.Options.AccountId);
    }

    [Fact]
    public void Constructor_ExternalHttpClient_DoesNotDisposeIt_WhenClientDisposed()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "a", BaseUrl = "https://api.tamga.test" };
        var client = new TamgaClient(options, httpClient);
        client.Dispose();

        // If TamgaClient had disposed the external HttpClient, this would throw ObjectDisposedException.
        var ex = Record.Exception(() => httpClient.Timeout);
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(31536001)]
    public async Task CheckOutMachineAsync_RejectsInvalidTtl_BeforeMakingAnyRequest(int ttl)
    {
        var (client, handler) = MakeClient();
        await Assert.ThrowsAsync<TtlInvalidException>(() => client.CheckOutMachineAsync(Guid.NewGuid(), ttl: ttl));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckOutLicenseAsync_ParsesReturnedCertificateIntoLicenseFile()
    {
        var (client, handler) = MakeClient();
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var innerPayload = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "licenses",
                ["id"] = "11111111-1111-1111-1111-111111111111",
                ["attributes"] = new JsonObject { ["key"] = "L" },
            },
        }.ToJsonString();
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerPayload));
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, Encoding.UTF8.GetBytes(enc)));
        var certJson = new JsonObject { ["enc"] = enc, ["sig"] = sig, ["alg"] = "base64+ed25519+v2" }.ToJsonString();
        var pem = $"-----BEGIN LICENSE FILE-----\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson))}\n-----END LICENSE FILE-----";

        var responseBody = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "license-files",
                ["id"] = "22222222-2222-2222-2222-222222222222",
                ["attributes"] = new JsonObject
                {
                    ["certificate"] = pem,
                    ["algorithm"] = "base64+ed25519",
                    ["includes"] = new JsonArray(),
                    ["ttl"] = 3600,
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, responseBody);

        var licenseFile = await client.CheckOutLicenseAsync(Guid.NewGuid());

        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        Assert.True(licenseFile.Verify(publicKey));
    }

    [Fact]
    public async Task CheckOutLicenseAsync_MapsLicenseNotEncrypted()
    {
        var (client, handler) = MakeClient();
        var errorBody = new JsonObject
        {
            ["errors"] = new JsonArray
            {
                new JsonObject { ["id"] = "1", ["status"] = 422, ["code"] = "LICENSE_NOT_ENCRYPTED", ["title"] = "t", ["detail"] = "no key set" },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, errorBody);

        await Assert.ThrowsAsync<LicenseNotEncryptedException>(() => client.CheckOutLicenseAsync(Guid.NewGuid(), encrypt: true));
    }

    [Fact]
    public async Task GenerateOfflineProofAsync_ParsesMetaProof()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "machines",
                ["id"] = machineId.ToString(),
                ["attributes"] = new JsonObject { ["fingerprint"] = "fp" },
            },
            ["meta"] = new JsonObject { ["proof"] = "v1x0.QUJD" },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, body);

        var proof = await client.GenerateOfflineProofAsync(machineId);

        Assert.Equal("QUJD", proof.RawSignatureBase64);
    }

    [Fact]
    public async Task GenerateOfflineProofAsync_Throws_ArgumentException_ForNonObjectDataset()
    {
        // Code-review regression: a non-object JsonNode (e.g. an array) must fail fast rather
        // than silently being substituted with an empty object and sent to the server as {}.
        var (client, handler) = MakeClient();
        var arrayDataset = new JsonArray { 1, 2, 3 };

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateOfflineProofAsync(Guid.NewGuid(), arrayDataset));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GenerateOfflineProofAsync_SendsEmptyDataset_ByDefault()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "machines",
                ["id"] = machineId.ToString(),
                ["attributes"] = new JsonObject { ["fingerprint"] = "fp" },
            },
            ["meta"] = new JsonObject { ["proof"] = "v1x0.QUJD" },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, body);

        await client.GenerateOfflineProofAsync(machineId);

        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        var dataset = doc.RootElement.GetProperty("meta").GetProperty("dataset");
        Assert.Equal(JsonValueKind.Object, dataset.ValueKind);
        Assert.Empty(dataset.EnumerateObject());
    }
}
