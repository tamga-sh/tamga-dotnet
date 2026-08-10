namespace Tamga.Sdk;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §B.
//
// Intended contents:
//   - TamgaClientOptions: AccountId (required), BaseUrl/Host (required),
//     ApiVersion (default pinned to this SDK's major version, e.g. "1" —
//     NOT the server's "1.8" default), request timeout.
//   - Base URL builder: https://<host>/v1/accounts/{account_id}/... —
//     account_id segment always required in both singleplayer and
//     multiplayer server modes.
//   - Default Content-Type / Accept: application/vnd.api+json on every
//     request, EXCEPT `GET .../actions/validate` (quick-validate), which is
//     plain application/json with no `data` envelope — special-cased parsing.
//   - Auth transports, tried in this order (docs/sdk.md §1):
//       1. Authorization: Bearer <token>                          (default)
//       2. Authorization: Basic <base64>  — email:password,
//          token: (empty password), or license:<key>
//       3. Authorization: License <key>   — primary transport for this
//          embedded/client SDK
//       4. Cookie: Tamga-Session=<uuid> + matching Origin header  — browser/
//          portal only, modeled for completeness
//       5. ?token= / ?auth= query parameter fallback
//   - GOTCHA: every issued token is actually `tok-`-prefixed regardless of
//     the documented tok-/prod-/env-/activ-/lic- intent — treat all tokens
//     as opaque strings, never parse prefixes for type detection.
//   - Tamga-OTP header — attached when a TOTP 2FA code is configured.
//   - Tamga-Version header — sanitized client-side (alphanumeric + `.`/`-`,
//     max 32 chars), sent explicitly, pinned per SDK major version.
//   - Response header reader: Tamga-Version, Tamga-Edition, Tamga-Mode,
//     X-Request-Id.
//   - Do NOT implement Tamga-Environment request header (planned EE
//     feature, no server code path reads it yet).
//   - Do NOT implement client-side X-RateLimit-* parsing or 429 backoff
//     (declared in the CORS allowlist only, never actually set/returned).
//   - JsonApiDocument<T> generic envelope: data, meta, errors.
//   - Async request executor (Task<T>-returning, CancellationToken on every
//     call) wrapping HttpClient.SendAsync.
