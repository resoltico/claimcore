import { randomUUID, randomBytes } from "node:crypto";

const secret = () => randomBytes(32).toString("hex");

/** @param {string} webSecret */
function clients(webSecret) {
  return [
    {
      clientId: "claimcore-web",
      enabled: true,
      publicClient: false,
      secret: webSecret,
      standardFlowEnabled: true,
      directAccessGrantsEnabled: false,
      redirectUris: ["https://app.localhost:5443/signin-oidc"],
      attributes: { "pkce.code.challenge.method": "S256" },
    },
    {
      clientId: "claimcore-cli",
      enabled: true,
      publicClient: true,
      standardFlowEnabled: true,
      directAccessGrantsEnabled: false,
      redirectUris: ["http://127.0.0.1"],
      attributes: { "pkce.code.challenge.method": "S256" },
      protocolMappers: [audience()],
    },
    {
      clientId: "claimcore-service",
      enabled: true,
      publicClient: false,
      secret: secret(),
      serviceAccountsEnabled: true,
      standardFlowEnabled: false,
      directAccessGrantsEnabled: false,
      protocolMappers: [audience()],
    },
  ];
}

export function identityConfiguration() {
  const ownerSubject = randomUUID();
  const ownerPassword = secret();
  const webSecret = secret();
  const issuer = "https://identity.localhost:5444/realms/claimcore";
  return {
    issuer,
    ownerSubject,
    ownerPassword,
    webSecret,
    adminPassword: secret(),
    realm: {
      realm: "claimcore",
      enabled: true,
      sslRequired: "all",
      loginWithEmailAllowed: false,
      clients: clients(webSecret),
      users: [
        {
          id: ownerSubject,
          username: "owner",
          email: "owner@example.test",
          firstName: "Local",
          lastName: "Owner",
          enabled: true,
          emailVerified: true,
          requiredActions: [],
          credentials: [{ type: "password", value: ownerPassword, temporary: false }],
        },
      ],
    },
  };
}

function audience() {
  return {
    name: "claimcore-api-audience",
    protocol: "openid-connect",
    protocolMapper: "oidc-audience-mapper",
    consentRequired: false,
    config: {
      "included.custom.audience": "claimcore-api",
      "id.token.claim": "false",
      "access.token.claim": "true",
    },
  };
}
