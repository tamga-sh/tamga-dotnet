# Changelog

## [2.0.3](https://github.com/tamga-sh/tamga-dotnet/compare/v2.0.2...v2.0.3) (2026-08-21)


### Bug Fixes

* align SDK with the current tamga-api server contract ([927d93e](https://github.com/tamga-sh/tamga-dotnet/commit/927d93ee0081368c3ba5de7927453cf028853777))
* align the SDK with the current tamga-api server contract ([54fcc51](https://github.com/tamga-sh/tamga-dotnet/commit/54fcc5191218c48d7c805ad0d3fb71b6dcc0df4a))
* chain envelope parse failures as InnerException, guard component paging ([cca1d12](https://github.com/tamga-sh/tamga-dotnet/commit/cca1d12ab7c2f294251d500f42e4aa6a05b54f61))
* correct DEAD heartbeat semantics and obsolete Machine.LicenseId ([7045697](https://github.com/tamga-sh/tamga-dotnet/commit/704569718d98b3393dafff942408174ac208de48))
* correct four route facts the turn-3 spec got wrong ([b42da80](https://github.com/tamga-sh/tamga-dotnet/commit/b42da80a3c933a770bb80f802596ac3afc07f028))
* correct heartbeat-window docs — policy-driven, 600s is only the fallback ([a931e88](https://github.com/tamga-sh/tamga-dotnet/commit/a931e88a5ff19577e75668566d4c13cb614c8940))
* correct M5 framing — no heartbeat status ends the ping loop ([6302184](https://github.com/tamga-sh/tamga-dotnet/commit/630218431f7075cd62cfd98f89f84dec2a988913))
* decode component and process responses as JSON:API documents ([46aec70](https://github.com/tamga-sh/tamga-dotnet/commit/46aec70dad0f49eaa449062ba254d14ea32dcac2))
* decode component and process responses as the JSON:API documents they are ([633491b](https://github.com/tamga-sh/tamga-dotnet/commit/633491b26a9eda9fa58a7919a0f313686322b430))
* delete the process rows nothing on the server reaps ([b0bb904](https://github.com/tamga-sh/tamga-dotnet/commit/b0bb90497fa2cf81d5ecd830c82ab9a40588f80c))
* document the endpoints this SDK can now reach, and one it still cannot ([9709bb2](https://github.com/tamga-sh/tamga-dotnet/commit/9709bb255c2c8347b16ee47d9ca31b1ece1d7bda))
* exercise the machine-file v2 verification path that shipped untested ([8308fcd](https://github.com/tamga-sh/tamga-dotnet/commit/8308fcd1619cd406eb8bb5c866b2c4126bd16e92))
* fail closed on an off-curve ECDSA key on Windows, as on Linux and macOS ([de9bbfe](https://github.com/tamga-sh/tamga-dotnet/commit/de9bbfe8c3c00e5022ae978eb0f59e4c083027f5))
* fall back instead of throwing on a non-positive scheduler interval ([f46094d](https://github.com/tamga-sh/tamga-dotnet/commit/f46094df320f09d946643be99d28d0d20ab4e1c0))
* floor the heartbeat interval at one second, not merely at positive ([bed12ac](https://github.com/tamga-sh/tamga-dotnet/commit/bed12ac26bd5dbedde7a182c2704d09f61c6ffeb))
* give activation a way out of its 409, and read the machine resource ([502eb38](https://github.com/tamga-sh/tamga-dotnet/commit/502eb38f293589cdc5695fde2846d06336346239))
* make offline-proof canonical JSON byte-identical to serde_json ([ff77566](https://github.com/tamga-sh/tamga-dotnet/commit/ff77566e56152d9e47b70b78f68480c4cb7fe334))
* narrow two overreaching quantifiers in the DEAD and window guidance ([c8f5617](https://github.com/tamga-sh/tamga-dotnet/commit/c8f56173a34a9016740b5e16673c686ac2e0bdf9))
* pin how the component and process decoders degrade on a malformed body ([01598aa](https://github.com/tamga-sh/tamga-dotnet/commit/01598aaf86178d0f2ef41fc15f7555b5b17c0bf6))
* reach the endpoint surface this SDK was missing ([30bb853](https://github.com/tamga-sh/tamga-dotnet/commit/30bb853b2419f94b7e8dfdbe39da752dc380fb93))
* reach the upgrade check and /v1/health ([fdaa953](https://github.com/tamga-sh/tamga-dotnet/commit/fdaa953a2ac14d38c2fd592bd871de0e9391068e))
* read the licence and policy resources the server already exposes ([d4c3840](https://github.com/tamga-sh/tamga-dotnet/commit/d4c38402c0ecca58ca3867553a9835b89bd8f6e3))
* scope the fingerprint lookup to the licence being activated ([34c8b34](https://github.com/tamga-sh/tamga-dotnet/commit/34c8b34e341b83844fb4fd21f73a0e12b56b8a6b))
* verify machine files against the real v2 wire format ([f09c6f6](https://github.com/tamga-sh/tamga-dotnet/commit/f09c6f67b365566740d7fe32489491b3c3af7882))
* verify machine files against the real v2 wire format ([f297f89](https://github.com/tamga-sh/tamga-dotnet/commit/f297f89b52a82118d0bb555eca70074c1c8bbb78))

## [2.0.2](https://github.com/tamga-sh/tamga-dotnet/compare/v2.0.1...v2.0.2) (2026-08-18)


### Bug Fixes

* **ci:** open release PRs with a GitHub App token so required checks run ([#26](https://github.com/tamga-sh/tamga-dotnet/issues/26)) ([4e3537e](https://github.com/tamga-sh/tamga-dotnet/commit/4e3537e4444673afa31f3c525a156c9c8a47fc07))

## [2.0.1](https://github.com/tamga-sh/tamga-dotnet/compare/v2.0.0...v2.0.1) (2026-08-18)


### Bug Fixes

* correct SDK documentation and align package metadata ([7d79ccf](https://github.com/tamga-sh/tamga-dotnet/commit/7d79ccfc87166529c7dd0be7981b600e0b341e47))

## [1.1.0](https://github.com/tamga-sh/tamga-dotnet/compare/v1.0.5...v1.1.0) (2026-08-13)


### ⚠ BREAKING CHANGES

* offline license files must be format v2 (`alg` ending in `+v2`). v1 files are rejected outright with no compatibility path. `Crypto/NaiveKey.cs` is removed, not deprecated.

### Features

* SDK v2 security contract — license-file HKDF, offline format v2, HTTP 429 handling ([0acc95a](https://github.com/tamga-sh/tamga-dotnet/commit/0acc95a2a080e17d341206d13a3357d5d16e170a))

## [1.0.5](https://github.com/tamga-sh/tamga-dotnet/compare/v1.0.4...v1.0.5) (2026-08-12)


### Bug Fixes

* clone caller-owned dataset before async serialization; add wrong-length-signature coverage ([bc3a4bb](https://github.com/tamga-sh/tamga-dotnet/commit/bc3a4bba9cb8a6d10424a22215c036251fcb7c6a))
* clone caller-owned dataset before async serialization; add wrong-length-signature regression coverage ([b39a726](https://github.com/tamga-sh/tamga-dotnet/commit/b39a726ada0169bd1faff560caa2d309f38f393e))
* enforce P-256 curve in Ecdsa.Verify (curve-confusion vulnerability) ([8b305c5](https://github.com/tamga-sh/tamga-dotnet/commit/8b305c5be3f5245b135ee1a8f561027b9e7e8dad))
* enforce P-256 curve in Ecdsa.Verify (curve-confusion vulnerability) ([b2ba1f5](https://github.com/tamga-sh/tamga-dotnet/commit/b2ba1f516da032f38d23747c008b32972e1bc649))

## [1.0.4](https://github.com/tamga-sh/tamga-dotnet/compare/v1.0.3...v1.0.4) (2026-08-11)


### Bug Fixes

* **ci:** pass explicit repo slug to codecov-action ([a177f0e](https://github.com/tamga-sh/tamga-dotnet/commit/a177f0e1dc861eea624e6b734ea95cc16c6268e2))

## [1.0.3](https://github.com/tamga-sh/tamga-dotnet/compare/v1.0.2...v1.0.3) (2026-08-11)


### Bug Fixes

* **ci:** use the trust policy creator's nuget.org username ([97b1457](https://github.com/tamga-sh/tamga-dotnet/commit/97b1457ca372d4a48ea817e4b3312d264278cc7e))

## [1.0.2](https://github.com/tamga-sh/tamga-dotnet/compare/v1.0.1...v1.0.2) (2026-08-11)


### Bug Fixes

* **ci:** grant contents:read to the publish job ([a18d744](https://github.com/tamga-sh/tamga-dotnet/commit/a18d744627b35e972483a7f0deb1241da47e43a5))
* **ci:** point Codecov at coverlet's actual output path ([d603d4d](https://github.com/tamga-sh/tamga-dotnet/commit/d603d4dcc2baef34b0175fb3b61526d77991159d))

## [1.0.1](https://github.com/tamga-sh/tamga-dotnet/compare/v1.0.0...v1.0.1) (2026-08-11)


### Bug Fixes

* **ci:** gate NuGet publish on release-please's own job output ([5e51fac](https://github.com/tamga-sh/tamga-dotnet/commit/5e51fac060d5e8a18f6b2cfde5eade9a18664c52))

## 1.0.0 (2026-08-11)


### Features

* implement client config, transport, license validation/check-in, and error/policy models (sections B-D, K) ([3c5cce1](https://github.com/tamga-sh/tamga-dotnet/commit/3c5cce120ca5bb9f65ba56508ddc6d7fe7e3f9d0))
* implement license and machine checkout crypto (sections E, F) [security-reviewed] ([8fa11a5](https://github.com/tamga-sh/tamga-dotnet/commit/8fa11a5af7425f4fee257a0f67f0373842b922ed))
* implement machine management, components/processes, and entitlements (sections G, I, J) ([c7e43b7](https://github.com/tamga-sh/tamga-dotnet/commit/c7e43b71530ff8f8a43e46213907aa20f3d325e5))
* implement machine offline proof generate/verify (section H) [security-reviewed] ([d13ca72](https://github.com/tamga-sh/tamga-dotnet/commit/d13ca72ae371e911e7fec4f8c7458698e2d2f36d))


### Bug Fixes

* address code-review findings in heartbeat schedulers, offline proof, and metadata models ([93fc1c2](https://github.com/tamga-sh/tamga-dotnet/commit/93fc1c2333c046b0d2cbce9126b1a68bbdd3ff1c))
* **ci:** add .gitattributes to force LF line endings on checkout ([0097c2f](https://github.com/tamga-sh/tamga-dotnet/commit/0097c2f3027a872310c81eee39026d7f352762c7))

## Changelog

## Unreleased
