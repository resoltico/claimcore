# Development

## First checkout

Use the pinned SDK in global.json and Node/npm versions declared by .node-version and
web/package.json. Docker is required for integration and published-process/browser acceptance.
Restore locked NuGet/npm inputs, generate the contract, then build the solution. Ordinary
.NET builds do not invoke npm. Frontend publication requires the separately built assets.

## Complete verification

The complete local command is `bash eng/verify.sh`. It restores locked inputs, generates the
contract, builds, runs formatting/lint/types and all native/frontend tests. Its fast option
excludes Docker-backed qualification and cannot establish complete acceptance. Tests use native
Microsoft Testing Platform; every module is discovered with [<Tests>] and guarded against
missing source membership, undiscovered tests and focused/pending cases. Counts are measured
floors; never lower one to clear a failure. No campaign, registry or generated inventory is needed.

Acceptance owns fresh labelled PostgreSQL/Keycloak and published processes. Three isolated
engine cases run sequentially, each with every browser specification, no retries and private
outputs. Failures retain private evidence; successful fixtures remove their resources.
Never target an existing evaluation stack. Generated contracts/assets, private inputs and
reports stay ignored. Only remove resources created by the current run.

## Changes

A migration is a new numbered SQL file under db/migrations; never rewrite an applied script.
Update readers/writers and prove atomic application, exact prefix/checksum admission and refusal
of unsupported history. An endpoint is one catalogue entry with schemas, typed core operation,
codec/rendering and every outcome/host-failure test. Regenerate artifacts and review the lock.
A command field belongs to Domain validation and RecordFormat encoding, then its authoritative
contract projection, consumers and independent tests. Do not add adapter-only rules or fields.

Function/member/lambda length50 and complexity12 remain absolute across production and tests.
Inline tool exceptions name one rule and their reason. File/module/value size is not a gate;
organize by capability. Required properties execute fixed200 cases with state sequences up to20
and deterministic seeds. Failure diagnostics retain safe replay information, not generated payload.
No environment switch selects a longer campaign or silently bypasses other properties.

## Dependency pins

Directory.Packages.props supplies direct package versions and deliberate transitive pins.
Microsoft.Testing.Platform.MSBuild, VSTestBridge and TestPlatform.ObjectModel keep the native
Expecto adapter/tool platform aligned. Its telemetry dependencies reach ApplicationInsights,
Azure.Monitor.OpenTelemetry.Exporter, Azure.Core, System.ClientModel and System.Memory.Data;
OpenTelemetry core/API/provider/exporter/storage packages remain pinned together. They are
library dependencies, not a ClaimCore telemetry or campaign system; SDK/platform telemetry is
turned off in CI.

Identity.Client/MSAL, IdentityModel protocol/token/logging/JWT packages and ProtectedData support
OIDC/MSAL delivery and are pinned with the current authentication dependencies. Microsoft.Extensions
configuration, DI, options, hosting/logging, primitives and file-provider families stay aligned to
.NET10 servicing; Bcl.AsyncInterfaces/Cryptography do likewise. TypeShape is the Hedgehog
transitive dependency. Active CentralTransitive lock entries demonstrate these paths; inspect them
with dotnet nuget why rather than deleting a pin based on its name.
