# Changelog

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
