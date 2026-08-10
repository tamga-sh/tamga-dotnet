namespace Tamga.Sdk;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §K.
//
// Intended contents:
//   - TamgaApiError: { Status: ushort, Code: string, Detail: string, Pointer: string? },
//     parsed from the JSON:API `{"errors": [{ id, status, code, title, detail,
//     source: { pointer } }]}` envelope (docs/sdk.md §11).
//   - Dispatch/match on `Code` (stable) rather than `Detail` (human text, may change).
//   - Typed exception hierarchy, one per modeled `code`:
//       CheckInNotRequiredException   (422 CHECK_IN_NOT_REQUIRED)
//       FingerprintTakenException     (409 FINGERPRINT_TAKEN)
//       PidTakenException             (409 PID_TAKEN)
//       TtlInvalidException           (422 TTL_INVALID)
//       LicenseNotEncryptedException  (422 LICENSE_NOT_ENCRYPTED)
//       SchemeNotSupportedException   (422 SCHEME_NOT_SUPPORTED)
//     plus a catch-all TamgaApiException for unmodeled codes.
//   - Fixed-status codes to model: NOT_FOUND (404), UNAUTHORIZED (401),
//     FORBIDDEN (403), INTERNAL_SERVER_ERROR (500, generic — never assume
//     leaked DB detail is parseable).
//
// GOTCHA (do not "fix" this later without re-reading docs/sdk.md §11):
// `429 TOO_MANY_REQUESTS` is declared in the server's error enum but has NO
// constructor and is never returned by any code path today. Do not build
// client-side 429/backoff handling expecting the server to send it.
