using System.Text.Json;
using Tamga.Sdk;
using Xunit;

namespace Tamga.Sdk.Tests;

public class ErrorsTests
{
    [Fact]
    public void TamgaApiErrorEnvelope_ParsesFullJsonApiErrorEnvelope_IncludingSourcePointer()
    {
        const string json = """
        {
            "errors": [
                {
                    "id": "01926b3e-0000-7000-8000-000000000000",
                    "status": 422,
                    "code": "TTL_INVALID",
                    "title": "Unprocessable Entity",
                    "detail": "ttl must be > 0 and <= 31536000",
                    "source": { "pointer": "/data/meta/ttl" }
                }
            ]
        }
        """;

        var envelope = JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(json, TamgaJsonOptions.Default);

        Assert.NotNull(envelope);
        var error = Assert.Single(envelope!.Errors);
        Assert.Equal("01926b3e-0000-7000-8000-000000000000", error.Id);
        Assert.Equal((ushort)422, error.Status);
        Assert.Equal("TTL_INVALID", error.Code);
        Assert.Equal("ttl must be > 0 and <= 31536000", error.Detail);
        Assert.Equal("/data/meta/ttl", error.Pointer);
    }

    public static IEnumerable<object[]> KnownCodeMappings()
    {
        yield return new object[] { "CHECK_IN_NOT_REQUIRED", typeof(CheckInNotRequiredException) };
        yield return new object[] { "FINGERPRINT_TAKEN", typeof(FingerprintTakenException) };
        yield return new object[] { "PID_TAKEN", typeof(PidTakenException) };
        yield return new object[] { "KEY_TAKEN", typeof(KeyTakenException) };
        yield return new object[] { "TTL_INVALID", typeof(TtlInvalidException) };
        yield return new object[] { "LICENSE_NOT_ENCRYPTED", typeof(LicenseNotEncryptedException) };
        yield return new object[] { "LICENSE_KEY_MISSING", typeof(LicenseKeyMissingException) };
        yield return new object[] { "SCHEME_NOT_SUPPORTED", typeof(SchemeNotSupportedException) };
        yield return new object[] { "DATASET_INVALID", typeof(DatasetInvalidException) };
        yield return new object[] { "NOT_FOUND", typeof(TamgaNotFoundException) };
        yield return new object[] { "UNAUTHORIZED", typeof(TamgaUnauthorizedException) };
        yield return new object[] { "FORBIDDEN", typeof(TamgaForbiddenException) };
        yield return new object[] { "INTERNAL_SERVER_ERROR", typeof(TamgaInternalServerErrorException) };
    }

    [Theory]
    [MemberData(nameof(KnownCodeMappings))]
    public void ToException_MapsEachModeledCode_ToItsSpecificType(string code, Type expectedType)
    {
        var error = new TamgaApiError { Status = 400, Code = code, Detail = "detail" };
        var exception = TamgaErrorMapper.ToException(error);
        Assert.IsType(expectedType, exception);
    }

    [Fact]
    public void ToException_FallsBackToTamgaApiException_ForUnmodeledCode()
    {
        var error = new TamgaApiError { Status = 500, Code = "SOME_UNMODELED_CODE", Detail = "detail" };
        var exception = TamgaErrorMapper.ToException(error);
        Assert.IsType<TamgaApiException>(exception);
        Assert.Equal("SOME_UNMODELED_CODE", exception.Error.Code);
    }

    [Fact]
    public void ToException_DoesNotMap_TooManyRequests_ToATypedException()
    {
        // GOTCHA regression: 429 TOO_MANY_REQUESTS is declared server-side but never returned —
        // this SDK deliberately has no typed exception for it; it must fall back to the catch-all.
        var error = new TamgaApiError { Status = 429, Code = "TOO_MANY_REQUESTS", Detail = "detail" };
        var exception = TamgaErrorMapper.ToException(error);
        Assert.IsType<TamgaApiException>(exception);
    }
}
