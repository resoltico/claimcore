# Service operating model design challenge

This is the separate skeptical review of [the design](service-operations-design.md), before
implementation. Findings are design constraints, not passing-test claims.

## Counterexamples and resulting decisions

1. **Publish a localhost listener through Docker.** Published bridge ports cannot reach a listener
   bound only to the container's loopback device. Introduce explicit listener address/port instead
   of host networking or weakening HTTPS checks.
2. **Keep the existing peer-locality check unchanged.** Docker-forwarded traffic can have a
   non-loopback peer. Bind-locality and exact public authority must be separate. Retain peer
   rejection for explicit loopback binding; prove that explicit container binding still enforces
   TLS, exact Host/origin, authentication and grants.
3. **Use Compose secret UID/mode declarations as evidence.** File-backed secrets are bind mounts;
   declarations do not prove effective ownership. Verify actual Linux metadata, keep strict
   private-file admission and refuse broad or wrong-owner inputs. Never chmod caller files at boot.
4. **Mount the whole bootstrap directory into Web.** That exposes schema-owner and issuer keys.
   Separate mounts by responsibility; inspect runtime mounts, image contents and real database
   permissions. Filenames and image targets alone are insufficient.
5. **Run the test fixture forever.** Disposable account/session assumptions, shared temporary
   credentials and teardown are not a durable deployment contract. Reuse mechanisms, not its
   lifecycle. Persist identities, certificates and data and document explicit initial authority.
6. **Use synchronous pre-host startup.** SIGTERM can arrive before ConsoleLifetime exists. Register
   stop before OIDC/runtime I/O and verify interruption with a real process and blocked boundary.
7. **Return from bounded Dispose while admitted work owns resources.** Process exit can defeat
   deferred cleanup. Add an awaited shutdown path while preserving the existing synchronous
   disposal contract and exact operation uncertainty at the manager's hard deadline.
8. **Restart means recover.** A restart may reread state, but must not initialize, regenerate,
   replay a new operation identity or infer non-commit. Unsupported namespaces remain untouched.
9. **Healthy means ready for claimant data.** A live HTTPS process can be synthetic, quarantined
   or awaiting activation. Keep independent readiness observations; never make local topology
   satisfy separate-host/custody evidence.
10. **A green native publish proves the container.** It does not prove port binding, UID ownership,
    mounts, signals or persistence. Exercise the produced image and actual Compose graph in CI.
11. **Copy the checkout into an image indiscriminately.** Private `.local` state, credentials,
    reports and Git metadata must not enter the build context. Qualify exclusions with a negative
    canary and retain corresponding source/license material for delivered executable bytes.
12. **Hide platform differences behind more wrappers.** The process runs on Linux; Docker supplies
    supervision. Keep host requirements explicit and test Docker Desktop separately without
    creating another process manager.

## Docker Desktop ownership experiment

The first actual container run observed an empty macOS bind root as UID 0 inside
Docker Desktop. Initial configuration creation therefore accepts a private empty root
owned by either the requested runtime UID or root, and assigns the requested ownership
while creating it. This is explicit one-time provisioning, never runtime permission
repair. Existing, linked, broad or other-owner roots remain refused. The generated
files must still pass actual native private-file admission before Web can run.

## Identity-provider startup experiment

The actual Keycloak development command enabled an HTTP listener despite the supplied
HTTPS-only setting. Its management health interface also inherited HTTPS, invalidating
a plain HTTP check. The local overlay now uses `start` with explicitly selected `dev-file`
storage and disabled public HTTP. Its unpublished management port explicitly uses HTTP
for the native local health check, as documented by [Keycloak](https://www.keycloak.org/server/health).
This retains a persistent synthetic evaluation database; it does not qualify production
identity storage.

## Scope and assurance limits

Source previews remain source previews. Remotely accessible HTTPS and durable process operation
are not independent witness custody, backup certification, real-data activation, or owner approval.
Preserve the existing gates and report actual execution separately from source/design review.

Implementation must revisit this challenge if an assumption fails. In particular, credential
ownership, local-domain resolution, bootstrap uncertainty and shutdown ordering require real-boundary
experiments before the design can be considered delivered.

## PostgreSQL revocation experiment and design revision

An isolated real Linux connection succeeded with revocation checking disabled and failed with
it enabled. Production code requires the enabled check for remote PostgreSQL and remains unchanged.
The synthetic CA therefore needs signed, reachable revocation evidence, in addition to a trusted root.

The local configuration creator will issue an empty signed CRL covering its initial synthetic
certificates. A separate read-only, non-root HTTP publication container serves only those public
signed bytes; Web receives neither its CA private key nor publication controls. Native clients keep
chain, hostname and online revocation validation. A missing or invalid CRL must refuse access.
HTTP publication cannot authenticate a certificate: the CA signature does that; altered bytes fail
verification. This extra process is required by the existing protection, not a second supervisor.

The local certificates and CRL expire after one year. This bounded synthetic installation is not a
production CA or a certificate-renewal system. Real deployments supply their managed CA, timely
revocation publication and rotation procedure through the existing external-dependency model.

## Witness initialization challenge

Source review confirmed that fresh witness initialization is deliberately not an idempotent
namespace-adoption command. The deployment initializer therefore refuses an existing primary
before any initialization write. Ordinary replacement never reruns initialization; qualification
checks that an explicit repeat refuses without changing the installation identity. A partial
primary/witness setup remains owner reconciliation work, preserving the original evidence.

## Shared runtime credential paths

Actual initialization opened both schemas, then stopped at initial-owner provisioning because
the witness writer connection's CA path differed between Web and the administration container.
The runtime input mount is now `/etc/claimcore/runtime` in both processes. Owner inputs remain
a separate mount. One runtime connection file therefore retains the same certificate path
for runtime and explicitly authorized owner work, without duplicating credentials or mounting
schema-owner inputs into Web.

## Startup-stop observation

A one-second delay after container creation did not prove that startup had entered remote I/O.
The process qualification now observes an established TCP connection to the deliberately paused
issuer inside Web's Linux network namespace before sending SIGTERM. This makes the blocked
transport boundary observable without adding a product test hook or logging provider details.
The separate native cancellation test checks suspended metadata read cancellation directly.

## Linux cleanup ownership

The complete Linux container checks passed, then cleanup refused the role-private directories
owned by PostgreSQL and the identity provider. Successful test teardown now uses an isolated root
container mounted only on this run's newly created synthetic configuration, checks its format/scope,
and removes its contents before the host removes the empty directory. Database volumes are still
removed only after exact test-project label verification. Failed runs retain their evidence.
No product initializer or runtime gains a deletion, reset or permission-repair path.

## Docker Desktop private-input storage revision

A real wrong-UID probe ran as UID 65001 and observed the host-bound private connection file as
owned by UID 65001, although configuration creation had assigned UID 501. Docker Desktop's host
sharing remaps ownership. Native admission correctly checked the observed metadata, but the bind
model did not provide the cross-UID isolation asserted by the design.

Private local inputs must therefore live on a Docker Linux volume, with role-specific subpath
mounts. Public metadata (environment settings containing paths/URLs, installation parameters, and
the public CA) is exported to a host directory for Compose and browser trust. No service/provider password, private key, client secret or connection string is exported.
The generated human login password is delivered separately in an owner-private operator file;
no running service mounts the host operator directory. The creator refuses an occupied volume
or metadata root; starts never populate, repair or replace inputs. Production host-bind inputs
require a Linux backing filesystem with real UID/mode semantics.

Challenge the revised graph with actual wrong-UID and broad-mode refusals on both Docker Desktop
and Linux; inspect subpath mounts so Web cannot traverse into owner/provider/CA inputs. Test
teardown may alter only its own synthetic volume. Public export failure preserves the private
installation for owner inspection; it does not authorize resetting the volume or regenerating keys.
