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
    /// <see cref="TamgaErrorMapper.ToException(TamgaApiError, Exception?)"/> was never reached and
    /// every one of this SDK's
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

    /// <summary>
    /// D18. The string <c>status</c> used to bind ONLY under <see cref="TamgaJsonOptions.Default"/>,
    /// because the flag lived on the options and not on the property. Any other option set — a
    /// caller logging an envelope with <c>new JsonSerializerOptions()</c>, a middleware, a test —
    /// threw on the whole envelope. The property now carries its own number handling.
    /// </summary>
    [Fact]
    public void TamgaApiError_BindsTheServersStringStatus_UnderDefaultOptions()
    {
        // Exact wire shape (status.as_u16().to_string()): `status` is a STRING.
        const string json = """
        {"errors":[{"id":"1","status":"422","code":"TTL_INVALID","title":"Unprocessable Entity","detail":"ttl must be > 0 and <= 31536000","source":{"pointer":"/data/meta/ttl"}}]}
        """;

        var envelope = JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(json, new JsonSerializerOptions());

        var error = Assert.Single(envelope!.Errors);
        Assert.Equal((ushort)422, error.Status);
        Assert.Equal("TTL_INVALID", error.Code);
        Assert.Equal("/data/meta/ttl", error.Pointer);
    }

    [Fact]
    public void TamgaApiError_BindsANumericStatus_UnderDefaultOptions()
    {
        // A proxy or a future server emitting a JSON number binds the same way, under the same
        // plain options — the attribute widens what is accepted, it does not narrow it.
        const string json = """
        {"errors":[{"id":"1","status":422,"code":"TTL_INVALID","title":"t","detail":"d"}]}
        """;

        var envelope = JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(json, new JsonSerializerOptions());

        Assert.Equal((ushort)422, Assert.Single(envelope!.Errors).Status);
    }

    /// <summary>
    /// The API now attaches <c>meta: {"machineId": "&lt;uuid&gt;"}</c> to a same-license
    /// <c>409 FINGERPRINT_TAKEN</c>. The envelope keeps it verbatim and the typed exception reads it.
    /// </summary>
    [Fact]
    public void FingerprintTakenException_ReadsTheExistingMachineId_FromMeta()
    {
        var machineId = Guid.NewGuid();
        // Exact wire shape from the API plan: string `status`, `meta` is {"machineId": "<uuid>"}.
        var json = "{\"errors\":[{\"id\":\"1\",\"status\":\"409\",\"code\":\"FINGERPRINT_TAKEN\",\"title\":\"Conflict\",\"detail\":\"This fingerprint is already activated\",\"meta\":{\"machineId\":\"" + machineId + "\"}}]}";

        var error = Assert.Single(JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(json, TamgaJsonOptions.Default)!.Errors);
        Assert.NotNull(error.Meta);
        Assert.Equal(JsonValueKind.Object, error.Meta!.Value.ValueKind);

        var ex = Assert.IsType<FingerprintTakenException>(TamgaErrorMapper.ToException(error));
        Assert.Equal(machineId, ex.ExistingMachineId);
    }

    [Theory]
    [InlineData("""{"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"d"}]}""")]
    [InlineData("""{"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"d","meta":null}]}""")]
    [InlineData("""{"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"d","meta":{}}]}""")]
    [InlineData("""{"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"d","meta":{"machineId":"not-a-uuid"}}]}""")]
    [InlineData("""{"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"d","meta":{"machineId":42}}]}""")]
    [InlineData("""{"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"d","meta":"unexpected"}]}""")]
    public void FingerprintTakenException_ExistingMachineId_IsNull_WhenMetaIsAbsentOrNotAMachineId(string json)
    {
        // A cross-license conflict carries no meta at all; anything malformed must degrade to
        // "not named", never throw — the conflict itself is the information the caller needs.
        var error = Assert.Single(JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(json, TamgaJsonOptions.Default)!.Errors);

        var ex = Assert.IsType<FingerprintTakenException>(TamgaErrorMapper.ToException(error));

        Assert.Null(ex.ExistingMachineId);
    }

    [Fact]
    public void TamgaApiError_Meta_RoundTripsThroughTheSharedOptions()
    {
        var original = new TamgaApiError
        {
            Status = 409,
            Code = "FINGERPRINT_TAKEN",
            Detail = "d",
            Meta = JsonSerializer.SerializeToElement(new { machineId = "0192b3e0-0000-7000-8000-000000000001" }, TamgaJsonOptions.Default),
        };

        var json = JsonSerializer.Serialize(original, TamgaJsonOptions.Default);
        var roundTripped = JsonSerializer.Deserialize<TamgaApiError>(json, TamgaJsonOptions.Default)!;

        Assert.Contains("\"meta\":{\"machineId\":\"0192b3e0-0000-7000-8000-000000000001\"}", json, StringComparison.Ordinal);
        Assert.Equal("0192b3e0-0000-7000-8000-000000000001", roundTripped.Meta!.Value.GetProperty("machineId").GetString());

        // No meta → omitted on the wire (WhenWritingNull), null after the round trip.
        var bare = JsonSerializer.Serialize(new TamgaApiError { Status = 422, Code = "X", Detail = "d" }, TamgaJsonOptions.Default);
        Assert.DoesNotContain("meta", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void ToException_MapsTheTwoNew422s_ToTheirOwnTypes_OutsideTheLimitAndAuthFamilies()
    {
        var signing = TamgaErrorMapper.ToException(new TamgaApiError { Status = 422, Code = "SIGNING_KEY_MISSING", Detail = "d" });
        var secret = TamgaErrorMapper.ToException(new TamgaApiError { Status = 422, Code = "SECRET_KEY_MISSING", Detail = "d" });

        Assert.IsType<SigningKeyMissingException>(signing);
        Assert.IsType<SecretKeyMissingException>(secret);
        Assert.IsNotAssignableFrom<TamgaLimitExceededException>(signing);
        Assert.IsNotAssignableFrom<TamgaLicenseAuthException>(signing);
        Assert.Equal("SIGNING_KEY_MISSING", signing.Error.Code);
        Assert.Equal("SECRET_KEY_MISSING", secret.Error.Code);
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
        yield return new object[] { "SIGNING_KEY_MISSING", typeof(SigningKeyMissingException) };
        yield return new object[] { "SECRET_KEY_MISSING", typeof(SecretKeyMissingException) };
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

    /// <summary>
    /// Both "base type for a family of typed subclasses" classes must be abstract, and for the
    /// same reason: the mapper never produces the bare base, so a caller able to construct one
    /// could hand SDK-shaped code an exception with no meaningful <c>EquivalentValidationCode</c>
    /// contract behind it. <c>TamgaLicenseAuthException</c> was already abstract;
    /// <c>TamgaLimitExceededException</c> was not, purely by oversight.
    /// </summary>
    [Fact]
    public void TypedExceptionBaseTypes_AreAbstract_SoOnlyRealSubclassesCanExist()
    {
        Assert.True(typeof(TamgaLimitExceededException).IsAbstract);
        Assert.True(typeof(TamgaLicenseAuthException).IsAbstract);

        // The catch-all base stays concrete on purpose: an unmodeled `code` maps to it directly.
        Assert.False(typeof(TamgaApiException).IsAbstract);
    }

    /// <summary>
    /// Built client-side, like <see cref="SchemeNotSupportedException(string)"/>: no server 422
    /// occurred, so the mapper never produces it, and Error.Code is the validate-time value the
    /// server DID send rather than a *_LIMIT_EXCEEDED code it did not.
    /// </summary>
    [Fact]
    public void MachineOverLimitException_IsAClientSideLimitExceededException()
    {
        var validation = new ValidationResult
        {
            License = new License { Id = Guid.NewGuid() },
            Meta = new ValidationMeta { Ts = DateTimeOffset.UnixEpoch, Valid = false, Detail = "over limit", Code = ValidationCode.TooMuchDisk },
        };
        var deleted = Guid.NewGuid();

        var ex = new MachineOverLimitException(validation, deleted);

        Assert.IsAssignableFrom<TamgaLimitExceededException>(ex);
        Assert.Same(validation, ex.Validation);
        Assert.Equal(deleted, ex.DeletedMachineId);
        Assert.Equal(ValidationCode.TooMuchDisk, ex.EquivalentValidationCode);
        Assert.Equal("TOO_MUCH_DISK", ex.Error.Code);
        Assert.Equal((ushort)422, ex.Error.Status);
        Assert.Null(ex.ErrorBodyParseFailure);

        // Not in the mapper: a validate-time code is not an error-envelope code.
        Assert.IsType<TamgaApiException>(TamgaErrorMapper.ToException(new TamgaApiError { Status = 422, Code = "TOO_MUCH_DISK", Detail = "d" }));
        Assert.Throws<ArgumentNullException>(() => new MachineOverLimitException(null!, deleted));
    }
}
