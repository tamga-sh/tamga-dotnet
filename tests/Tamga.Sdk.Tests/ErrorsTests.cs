using System.Text.Json;
using Tamga.Sdk;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests;

public class ErrorsTests
{
    /// <summary>
    /// THE regression test for this SDK's error model. The server serializes a JSON:API error's
    /// <c>status</c> with <c>status.as_u16().to_string()</c>, so the real wire shape is
    /// <c>"status": "422"</c> — a STRING. Binding that to a <c>ushort</c> without
    /// <c>AllowReadingFromString</c> throws, which took out the whole envelope, which meant
    /// <see cref="TamgaErrorMapper.ToException"/> was never reached and every one of this SDK's
    /// typed exceptions was unreachable in production.
    /// </summary>
    [Fact]
    public void TamgaApiErrorEnvelope_ParsesTheServersRealWireShape_WhereStatusIsAString()
    {
        const string json = """
        {
            "errors": [
                {
                    "id": "01926b3e-0000-7000-8000-000000000000",
                    "status": "422",
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

    [Fact]
    public void TamgaApiErrorEnvelope_StillParsesANumericStatus()
    {
        // Tolerated as well, so a proxy or a future server that emits a JSON number does not
        // reintroduce the same outage.
        const string json = """
        {"errors":[{"id":"1","status":422,"code":"TTL_INVALID","title":"t","detail":"d"}]}
        """;

        var envelope = JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(json, TamgaJsonOptions.Default);

        var error = Assert.Single(envelope!.Errors);
        Assert.Equal((ushort)422, error.Status);
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
        yield return new object[] { "MACHINE_LIMIT_EXCEEDED", typeof(MachineLimitExceededException) };
        yield return new object[] { "CORE_LIMIT_EXCEEDED", typeof(CoreLimitExceededException) };
        yield return new object[] { "MEMORY_LIMIT_EXCEEDED", typeof(MemoryLimitExceededException) };
        yield return new object[] { "DISK_LIMIT_EXCEEDED", typeof(DiskLimitExceededException) };
        yield return new object[] { "TOO_MANY_PROCESSES", typeof(TooManyProcessesException) };
        yield return new object[] { "LICENSE_SUSPENDED", typeof(LicenseSuspendedException) };
        yield return new object[] { "LICENSE_EXPIRED", typeof(LicenseExpiredException) };
        yield return new object[] { "LICENSE_NOT_ALLOWED", typeof(LicenseNotAllowedException) };
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

    public static IEnumerable<object[]> CreateTimeLimitCodes()
    {
        yield return new object[] { "MACHINE_LIMIT_EXCEEDED", ValidationCode.TooManyMachines };
        yield return new object[] { "CORE_LIMIT_EXCEEDED", ValidationCode.TooManyCores };
        yield return new object[] { "MEMORY_LIMIT_EXCEEDED", ValidationCode.TooMuchMemory };
        yield return new object[] { "DISK_LIMIT_EXCEEDED", ValidationCode.TooMuchDisk };
        yield return new object[] { "TOO_MANY_PROCESSES", ValidationCode.TooManyProcesses };
    }

    [Theory]
    [MemberData(nameof(CreateTimeLimitCodes))]
    public void ToException_NormalizesEachCreateTimeLimitCode_ToItsValidateTimeEquivalent(string code, ValidationCode expected)
    {
        // A limit can surface either at create (422 <CODE>_LIMIT_EXCEEDED) or at validate
        // (meta.code), depending on the policy's overage strategy. Callers need one value to
        // dispatch on across both paths.
        var error = new TamgaApiError { Status = 422, Code = code, Detail = "limit" };

        var exception = Assert.IsAssignableFrom<TamgaLimitExceededException>(TamgaErrorMapper.ToException(error));

        Assert.Equal(expected, exception.EquivalentValidationCode);
        Assert.Equal(code, exception.Error.Code);
    }

    [Fact]
    public void ToException_GroupsTheLicenseKeyAuthRefusals_UnderOneCatchableBaseType()
    {
        foreach (var code in new[] { "LICENSE_SUSPENDED", "LICENSE_EXPIRED", "LICENSE_NOT_ALLOWED" })
        {
            var error = new TamgaApiError { Status = 401, Code = code, Detail = "refused" };
            var exception = TamgaErrorMapper.ToException(error);
            Assert.IsAssignableFrom<TamgaLicenseAuthException>(exception);
            Assert.Equal(code, exception.Error.Code);
        }
    }

    [Fact]
    public void ToException_DoesNotMap_TooManyRequests_ToATypedException()
    {
        // 429 is real and is absorbed by TamgaTransport's retry loop, so anything reaching this
        // mapper has already exhausted the retry budget. A typed exception here would report
        // something the caller can no longer act on — it must fall back to the catch-all.
        var error = new TamgaApiError { Status = 429, Code = "TOO_MANY_REQUESTS", Detail = "detail" };
        var exception = TamgaErrorMapper.ToException(error);
        Assert.IsType<TamgaApiException>(exception);
    }
}
