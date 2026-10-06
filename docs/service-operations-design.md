# Service operating model design

This record describes the operating-model audit and selected design against `c623f9a`.
[Web](web.md), [Operations](operations.md), [Development](development.md), and the architecture
manifest remain the owners of supported contracts. This design is not execution evidence or
production-installation approval.

## Problem and audit

The case-work executable is real, but its supported first run ends with disposable qualification
fixtures. The root Compose file supplies only one development primary. Private launch scripts have
filled the gap without providing a maintained installation, lifecycle, or distribution contract.

Source inspection found these concrete gaps:

- No shipped Web container or complete persistent local deployment definition.
- The listener, origin parser, certificate identity check, and peer admission all assume localhost.
  A loopback listener inside a bridge-network container is unreachable through published ports.
- Process shutdown handling begins after synchronous OIDC discovery and an uncancelled runtime
  opening/full audit. Startup cannot participate in the same controlled-stop boundary.
- Required configuration is spread across Web and Hosting. Help omits the writer capability and
  conditional backup-health inputs. The environment is supported, but its complete contract is not
  exposed consistently.
- Docker file-backed secrets do not provide the UID/mode remapping that strict private-file
  admission needs. Merely declaring secret modes cannot establish runtime access or privacy.
- Publication verifies frontend and native bytes, but no container delivery consumes that boundary.
- Liveness and real-data readiness already have distinct meanings. Real-data initialization and
  activation deliberately refuse the generic build's absent independent publication root.

## Selected approach

Docker manages Linux processes. Host clients may use Linux or Docker Desktop; application rules do
not fork by host OS. Compose defines deployment, mounts, networking, persistence, restart and stop
behavior. Do not add a second custom process manager or native system-service abstraction.

Use separate image targets for the Web host, owner administration/client tools, and local
configuration creation. Build from digest-pinned official SDK, Node and ASP.NET inputs, the existing
locked dependency graphs, canonical contract generator, frontend producer and publication verifier.
The Web image contains its published runtime tree; it does not contain the Database entry point,
schema-owner inputs, configuration creator, or Docker socket. Runtime storage assemblies remain
necessary behind Hosting; their presence is not schema-owner authority.

The base Compose deployment runs Web against explicitly provisioned external primary, witness,
OIDC and key-custody inputs. A maintained local overlay adds separately credentialed primary and
witness servers, native HTTPS OIDC, persistent volumes and explicit owner initialization. It creates
only `SYNTHETIC_ONLY` installations. One machine is not independent-host or real-data qualification.

Keep the existing environment/private-file input model, make its complete setting inventory
consistent, and ship reviewed examples. Credentials remain file contents, not environment values
or arguments. Local configuration creation is an explicit one-time operation; it must refuse
incomplete, incompatible or foreign existing state rather than rotate identities or reset storage.
Normal starts never initialize schemas, provision actors, regenerate keys or repair uncertainty.
Owner initialization remains behind Database; case grants use the authenticated actor-bound API.

Run Web as a non-root UID that owns its private runtime inputs and persistent state. Mount only
its runtime input directory, read-only, and its state directory writable. Local configuration
creation establishes ownership of newly created role-specific files; ordinary startup never fixes
existing permissions. Owner, PostgreSQL-server and identity-provider inputs have distinct mounts.

## Transport and lifecycle

Separate the public HTTPS origin from the listener address and port. Default native binding remains
loopback; Docker explicitly binds a container interface and publishes host ports on loopback by
default. Remote publication is an operator choice. Validate the certificate against the configured
DNS/IP identity, expiry, private key and server-authentication purpose. Retain exact Host/origin,
TLS, OIDC, credential-mode and antiforgery checks. Never trust forwarded headers implicitly.

Peer locality follows explicit loopback binding rather than assuming every localhost-named origin
has a loopback TCP peer; Docker NAT disproves that assumption. Non-loopback binding does not grant
actor authority. Local `.localhost` deployment names must work through both browser loopback
resolution and Docker network aliases without disabling TLS validation.

Register controlled stop before remote startup I/O. Await cancellable OIDC and runtime opening.
Stop HTTP admission, drain admitted work and await runtime cleanup. Preserve the established
commit boundary: cancellation after dispatch cannot prove rollback, change identity or rewrite
request bytes. A manager's hard deadline can still produce transport uncertainty; do not advertise
stop as proof that every operation returned a receipt.

Provide a bounded HTTPS probe of this instance's listener with the configured public identity.
Certificate validation remains enabled. Container health means liveness; the existing real-data
readiness endpoint remains independent and cannot become healthy merely because dependencies run.
Restarting a process never initiates schema, authority, restoration or privacy transitions.

## Verification and deliverables

Update transport/startup contracts, generated diagnostic artifacts, named tests and inventories,
operator documentation, source distribution instructions and changelog together. Qualification
must exercise real container boundaries: strict file ownership/refusals, HTTPS hostname mismatch,
foreign Host/origin, persisted state across replacement, controlled stop during startup and work,
restart, separate owner/runtime authority and refusal of unsupported installations.

Build/format/unit checks alone cannot establish container operation. Required CI must execute the
container qualification and include it in Gate. Use only synthetic data and isolated resources;
retain useful failure evidence and remove only this run's temporary resources.

Do not claim this work creates an independently qualified deployment, approves real-data activation,
changes the published release baseline, or supplies legal/retention policy. Those remain explicit
owner and deployment-evidence boundaries. The machinery must be production-quality without
inventing production evidence.
