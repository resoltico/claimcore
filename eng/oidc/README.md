# Disposable synthetic OIDC issuer

Run `bash eng/oidc/Run-SyntheticOidc.sh` from the repository root. The harness
starts the official Keycloak 26.8.0 image at a pinned multi-platform digest,
generates a private one-day CA and server certificate, then publishes a random
loopback HTTPS reverse proxy. It imports fresh synthetic realms and
checks discovery, JWKS, and the confidential client's client-credentials grant.
It removes its exact labeled container, proxy process and private temporary
directory at exit.
It creates no Docker volume and does not use adopted users or databases.

After building `eng/oidc/ClaimCore.OidcQualification.fsproj`, run the live Web
authentication qualification:

```sh
bash eng/oidc/Run-SyntheticOidc.sh -- \
  dotnet run --project eng/oidc/ClaimCore.OidcQualification.fsproj \
  --configuration Release --no-build --no-restore
```

It exercises Web's Authorization Code + S256 PKCE callback, server-side cookie,
real provider-issued JWT admission and rejection, expiry, and Keycloak RSA key
rotation. The issuer and all test-client traffic use certificate-validated
loopback HTTPS. Keycloak's own HTTP listener is bound to loopback behind that
disposable proxy. The Web auth test seam trusts only the temporary CA and
disables PAR and clock skew solely to make those synthetic checks observable;
production configuration requires HTTPS and retains its stricter settings.
Algorithm/type header mutations also break the signature, so those negative
requests prove refusal; the configured algorithm/type allowlists are checked
separately as source policy. This fixture does not grant case-work authority.

To keep the issuer alive during another qualification, pass a command after
`--`.

The command receives `CLAIMCORE_TEST_OIDC_ISSUER`,
`CLAIMCORE_TEST_OIDC_CREDENTIALS`, `CLAIMCORE_TEST_OIDC_DISCOVERY`,
`CLAIMCORE_TEST_OIDC_JWKS`, and `CLAIMCORE_TEST_OIDC_CA_CERT`. The private
certificate path is passed explicitly to clients; no system trust store is
changed. The credentials path is mode-restricted and contains
two separate synthetic human users, a public PKCE client, a confidential Web
client, service clients, and synthetic realm-admin credentials used only for
key rotation. Treat it as ephemeral test-only secret material; the harness
deletes it when the command exits.

This is a local functional fixture, not qualification of a separately operated
identity provider or its custody and availability controls.
