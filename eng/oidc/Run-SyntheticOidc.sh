#!/usr/bin/env bash
set -euo pipefail

# Disposable local OIDC qualification. The only persistent output is a caller's
# own test result; this script removes its labeled container and private files.
image='quay.io/keycloak/keycloak:26.7.4@sha256:82a77884f3af238beab1e7afd63b5f530e1b5c0590bd7aa60b40a40463e29b2c'
container=''
workdir=''
proxy_pid=''

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if [[ -n "${container}" ]]; then
    local actual_label
    actual_label="$(docker inspect --format '{{ index .Config.Labels "org.claimcore.test-run" }}' "${container}" 2>/dev/null || true)"
    if [[ "${actual_label}" == "${run_id}" ]]; then
      docker rm --force "${container}" >/dev/null 2>&1 || true
    fi
  fi
  if [[ -n "${proxy_pid}" ]]; then
    kill "${proxy_pid}" >/dev/null 2>&1 || true
    wait "${proxy_pid}" >/dev/null 2>&1 || true
  fi
  if [[ -n "${workdir}" && -d "${workdir}" && "${workdir}" == */claimcore-oidc.* ]]; then
    rm -r -- "${workdir}"
  fi
  exit "${status}"
}
trap cleanup EXIT INT TERM

for required in docker jq curl openssl node; do
  command -v "${required}" >/dev/null || {
    printf 'Missing required tool: %s\n' "${required}" >&2
    exit 69
  }
done
docker info >/dev/null 2>&1 || {
  printf 'Docker daemon unavailable.\n' >&2
  exit 69
}

if [[ $# -gt 0 && "$1" != '--' ]]; then
  printf 'Usage: %s [-- command [args...]]\n' "$0" >&2
  exit 64
fi
if [[ $# -gt 0 ]]; then shift; fi

umask 077
run_id="$(openssl rand -hex 16)"
workdir="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-oidc.XXXXXXXX")"

openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 1 \
  -subj '/CN=ClaimCore Synthetic OIDC CA' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "${workdir}/ca.key" -out "${workdir}/ca.crt" >/dev/null 2>&1
openssl req -newkey rsa:3072 -sha256 -nodes -subj '/CN=127.0.0.1' \
  -keyout "${workdir}/oidc.key" -out "${workdir}/oidc.csr" >/dev/null 2>&1
printf '%s\n' 'basicConstraints=critical,CA:FALSE' \
  'keyUsage=critical,digitalSignature,keyEncipherment' \
  'extendedKeyUsage=serverAuth' \
  'subjectAltName=IP:127.0.0.1,DNS:localhost' >"${workdir}/leaf.ext"
openssl x509 -req -in "${workdir}/oidc.csr" -CA "${workdir}/ca.crt" \
  -CAkey "${workdir}/ca.key" -CAcreateserial -days 1 -sha256 \
  -extfile "${workdir}/leaf.ext" -out "${workdir}/oidc.crt" >/dev/null 2>&1
openssl verify -CAfile "${workdir}/ca.crt" -verify_ip 127.0.0.1 \
  "${workdir}/oidc.crt" >/dev/null
if openssl verify -CAfile "${workdir}/ca.crt" -verify_ip 127.0.0.2 \
  "${workdir}/oidc.crt" >/dev/null 2>&1; then
  printf 'Synthetic certificate accepted the wrong IP.\n' >&2
  exit 1
fi

node eng/oidc/LoopbackHttpsProxy.mjs \
  "${workdir}/oidc.crt" "${workdir}/oidc.key" \
  "${workdir}/upstream-port" "${workdir}/proxy-port" \
  >"${workdir}/proxy.log" 2>&1 &
proxy_pid=$!
for ((attempt = 0; attempt < 100; attempt++)); do
  [[ -s "${workdir}/proxy-port" ]] && break
  kill -0 "${proxy_pid}" 2>/dev/null || {
    printf 'Synthetic HTTPS proxy exited.\n' >&2
    exit 1
  }
  sleep 0.1
done
[[ -s "${workdir}/proxy-port" ]] || {
  printf 'Synthetic HTTPS proxy did not start.\n' >&2
  exit 1
}
proxy_port="$(<"${workdir}/proxy-port")"
[[ "${proxy_port}" =~ ^[0-9]+$ ]] || {
  printf 'Synthetic HTTPS proxy port is invalid.\n' >&2
  exit 1
}
realm="claimcore-synthetic-${run_id}"
foreign_realm="claimcore-foreign-${run_id}"
admin_password="$(openssl rand -base64 36 | tr -d '\n')"
client_secret="$(openssl rand -base64 36 | tr -d '\n')"
unscoped_secret="$(openssl rand -base64 36 | tr -d '\n')"
foreign_secret="$(openssl rand -base64 36 | tr -d '\n')"
web_secret="$(openssl rand -base64 36 | tr -d '\n')"
owner_password="$(openssl rand -base64 36 | tr -d '\n')"
steward_password="$(openssl rand -base64 36 | tr -d '\n')"

jq -n \
  --arg realm "${realm}" \
  --arg serviceSecret "${client_secret}" \
  --arg unscopedSecret "${unscoped_secret}" \
  --arg webSecret "${web_secret}" \
  --arg ownerPassword "${owner_password}" \
  --arg stewardPassword "${steward_password}" \
  '{realm:$realm,enabled:true,sslRequired:"none",loginWithEmailAllowed:false,
    accessTokenLifespan:15,
    clients:[
      {clientId:"claimcore-web",enabled:true,publicClient:false,secret:$webSecret,
       standardFlowEnabled:true,directAccessGrantsEnabled:false,implicitFlowEnabled:false,
       serviceAccountsEnabled:false,redirectUris:["https://localhost:5443/signin-oidc"],
       attributes:{"pkce.code.challenge.method":"S256"}},
      {clientId:"claimcore-cli",enabled:true,publicClient:true,standardFlowEnabled:true,
       directAccessGrantsEnabled:false,implicitFlowEnabled:false,serviceAccountsEnabled:false,
       redirectUris:["http://127.0.0.1"],
       attributes:{"pkce.code.challenge.method":"S256"},
       protocolMappers:[{name:"claimcore-api-audience",protocol:"openid-connect",
         protocolMapper:"oidc-audience-mapper",consentRequired:false,
         config:{"included.client.audience":"claimcore-api",
           "id.token.claim":"false","access.token.claim":"true"}}]},
      {clientId:"claimcore-service",enabled:true,publicClient:false,secret:$serviceSecret,
       standardFlowEnabled:false,directAccessGrantsEnabled:false,
       implicitFlowEnabled:false,serviceAccountsEnabled:true,
       protocolMappers:[{name:"claimcore-api-audience",protocol:"openid-connect",
         protocolMapper:"oidc-audience-mapper",consentRequired:false,
         config:{"included.client.audience":"claimcore-api",
           "id.token.claim":"false","access.token.claim":"true"}}]},
      {clientId:"claimcore-unscoped",enabled:true,publicClient:false,secret:$unscopedSecret,
       standardFlowEnabled:false,directAccessGrantsEnabled:false,
       implicitFlowEnabled:false,serviceAccountsEnabled:true}
    ],
    users:[
      {username:"synthetic-owner",email:"owner@example.test",firstName:"Synthetic",
       lastName:"Owner",enabled:true,emailVerified:true,requiredActions:[],
       credentials:[{type:"password",value:$ownerPassword,temporary:false}]},
      {username:"synthetic-steward",email:"steward@example.test",firstName:"Synthetic",
       lastName:"Steward",enabled:true,emailVerified:true,requiredActions:[],
       credentials:[{type:"password",value:$stewardPassword,temporary:false}]}
    ]}' >"${workdir}/realm.json"

jq -n --arg realm "${foreign_realm}" --arg secret "${foreign_secret}" \
  '{realm:$realm,enabled:true,sslRequired:"none",accessTokenLifespan:15,
    clients:[{clientId:"claimcore-service",enabled:true,publicClient:false,secret:$secret,
      standardFlowEnabled:false,directAccessGrantsEnabled:false,
      implicitFlowEnabled:false,serviceAccountsEnabled:true,
      protocolMappers:[{name:"claimcore-api-audience",protocol:"openid-connect",
        protocolMapper:"oidc-audience-mapper",consentRequired:false,
        config:{"included.client.audience":"claimcore-api",
          "id.token.claim":"false","access.token.claim":"true"}}]}]}' \
  >"${workdir}/foreign.json"

jq -n \
  --arg realm "${realm}" \
  --arg clientSecret "${client_secret}" \
  --arg unscopedSecret "${unscoped_secret}" \
  --arg foreignRealm "${foreign_realm}" \
  --arg foreignSecret "${foreign_secret}" \
  --arg webSecret "${web_secret}" \
  --arg adminPassword "${admin_password}" \
  --arg ownerPassword "${owner_password}" \
  --arg stewardPassword "${steward_password}" \
  '{realm:$realm,publicClientId:"claimcore-cli",webClientId:"claimcore-web",
    webClientSecret:$webSecret,apiAudience:"claimcore-api",
    serviceClientId:"claimcore-service",serviceClientSecret:$clientSecret,
    unscopedClientId:"claimcore-unscoped",unscopedClientSecret:$unscopedSecret,
    foreignRealm:$foreignRealm,foreignClientSecret:$foreignSecret,
    adminUsername:"synthetic-admin",adminPassword:$adminPassword,
    users:[{username:"synthetic-owner",password:$ownerPassword},
           {username:"synthetic-steward",password:$stewardPassword}]}' >"${workdir}/credentials.json"

printf 'KC_BOOTSTRAP_ADMIN_USERNAME=synthetic-admin\nKC_BOOTSTRAP_ADMIN_PASSWORD=%s\n' \
  "${admin_password}" >"${workdir}/admin.env"

# The host parent remains owner-private; the disposable container's UID 1000 must traverse this copy.
mkdir -m 755 "${workdir}/import"
cp "${workdir}/realm.json" "${workdir}/import/realm.json"
cp "${workdir}/foreign.json" "${workdir}/import/foreign.json"
chmod 644 "${workdir}/import/realm.json" "${workdir}/import/foreign.json"

container="$(docker create \
  --name "claimcore-oidc-${run_id}" \
  --label "org.claimcore.test-run=${run_id}" \
  --publish 127.0.0.1::8080 \
  --env-file "${workdir}/admin.env" \
  "${image}" start-dev --import-realm \
  --hostname "https://127.0.0.1:${proxy_port}" --proxy-headers xforwarded)"
[[ "${container}" =~ ^[0-9a-f]{64}$ ]] || {
  printf 'Keycloak container was not created.\n' >&2
  exit 1
}
docker cp "${workdir}/import" "${container}:/opt/keycloak/data/import" >/dev/null
docker start "${container}" >/dev/null

port="$(docker port "${container}" 8080/tcp | sed -n 's/^127\.0\.0\.1:\([0-9][0-9]*\)$/\1/p')"
[[ "${port}" =~ ^[0-9]+$ ]] || {
  printf 'Loopback port mapping unavailable.\n' >&2
  exit 1
}
printf '%s' "${port}" >"${workdir}/upstream-port"
issuer="https://127.0.0.1:${proxy_port}/realms/${realm}"
discovery="${issuer}/.well-known/openid-configuration"

ready=false
for ((attempt = 0; attempt < 90; attempt++)); do
  if curl --cacert "${workdir}/ca.crt" --silent --show-error --max-time 2 \
    --output "${workdir}/discovery.json" "${discovery}" 2>/dev/null &&
    jq -e --arg issuer "${issuer}" '.issuer == $issuer and (.jwks_uri | type == "string") and (.token_endpoint | type == "string")' "${workdir}/discovery.json" >/dev/null; then
    ready=true
    break
  fi
  sleep 2
done
[[ "${ready}" == true ]] || {
  printf 'Synthetic OIDC discovery did not become ready.\n' >&2
  exit 1
}
if curl --silent --max-time 3 --output /dev/null "${discovery}" 2>/dev/null; then
  printf 'Synthetic issuer unexpectedly trusted without the temporary CA.\n' >&2
  exit 1
fi

jwks_uri="$(jq -r '.jwks_uri' "${workdir}/discovery.json")"
token_endpoint="$(jq -r '.token_endpoint' "${workdir}/discovery.json")"
curl --cacert "${workdir}/ca.crt" --fail --silent --show-error --max-time 10 \
  --output "${workdir}/jwks.json" "${jwks_uri}"
jq -e '.keys | type == "array" and length > 0 and all(.[]; .kid != null and .kty != null)' \
  "${workdir}/jwks.json" >/dev/null || {
  printf 'OIDC JWKS is invalid.\n' >&2
  exit 1
}

jq -j '"grant_type=client_credentials&client_id=" + (.serviceClientId|@uri) +
  "&client_secret=" + (.serviceClientSecret|@uri)' "${workdir}/credentials.json" >"${workdir}/token-form"
token_status="$(curl --cacert "${workdir}/ca.crt" --silent --show-error --max-time 10 \
  --output "${workdir}/token.json" \
  --write-out '%{http_code}' --header 'Content-Type: application/x-www-form-urlencoded' \
  --data-binary "@${workdir}/token-form" "${token_endpoint}")"
if [[ "${token_status}" != 200 ]]; then
  printf 'Synthetic client-credentials qualification failed: HTTP %s.\n' "${token_status}" >&2
  exit 1
fi
jq -e '.token_type == "Bearer" and (.access_token | type == "string" and length > 50)' \
  "${workdir}/token.json" >/dev/null || {
  printf 'Client credentials did not yield a bearer token.\n' >&2
  exit 1
}

printf 'Synthetic OIDC discovery, JWKS, and client-credentials grant passed.\n'
if [[ $# -gt 0 ]]; then
  # The hook lets a browser or service integration test exercise PKCE while the
  # disposable issuer remains live. It receives file paths, never secret values.
  CLAIMCORE_TEST_OIDC_ISSUER="${issuer}" \
    CLAIMCORE_TEST_OIDC_CREDENTIALS="${workdir}/credentials.json" \
    CLAIMCORE_TEST_OIDC_DISCOVERY="${workdir}/discovery.json" \
    CLAIMCORE_TEST_OIDC_JWKS="${workdir}/jwks.json" \
    CLAIMCORE_TEST_OIDC_CA_CERT="${workdir}/ca.crt" \
    "$@"
fi
