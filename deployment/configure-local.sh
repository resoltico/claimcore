#!/usr/bin/env bash
# Fresh local evaluation inputs. Refuse existing paths; never reset a directory or database.
set -euo pipefail
umask 077
configuration="${1:?Usage: configure-local.sh NEW_ABSOLUTE_DIRECTORY [IANA_ZONE [WEB_PORT IDENTITY_PORT]]}"
zone="${2:-Etc/UTC}"
web_port="${3:-5443}"
identity_port="${4:-5444}"
[[ $# -le 4 && "${configuration}" == /* && ! -e "${configuration}" && ! -L "${configuration}" ]] || {
  printf 'Use a new absolute configuration directory.\n' >&2; exit 2;
}
[[ "${configuration}" != *$'\n'* && "${zone}" =~ ^[A-Za-z0-9_+/-]+$ ]] || exit 2
for port in "${web_port}" "${identity_port}"; do
  [[ "${port}" =~ ^[1-9][0-9]{0,4}$ && "${port}" -le 65535 ]] || exit 2
done
[[ "${web_port}" != "${identity_port}" ]] || exit 2
for tool in openssl docker; do command -v "${tool}" >/dev/null || exit 2; done
uuid() {
  local hex
  hex="$(openssl rand -hex 16)"
  printf '%s-%s-%s-%s-%s' "${hex:0:8}" "${hex:8:4}" "${hex:12:4}" "${hex:16:4}" "${hex:20:12}"
}
project="claimcore-$(openssl rand -hex 8)"
mkdir -m 700 "${configuration}"
for directory in postgres identity identity/realm web administration; do mkdir -m 755 "${configuration}/${directory}"; done
openssl req -x509 -newkey rsa:3072 -nodes -sha256 -days 365 -subj '/CN=ClaimCore local evaluation CA' \
  -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "${configuration}/ca.key" -out "${configuration}/ca.pem" >/dev/null 2>&1
leaf() {
  local directory="$1" names="$2"
  openssl req -new -newkey rsa:2048 -nodes -sha256 -subj '/CN=ClaimCore local evaluation' \
    -addext "subjectAltName=${names}" -addext 'extendedKeyUsage=serverAuth' \
    -keyout "${configuration}/${directory}/server.key" -out "${configuration}/${directory}/server.csr" >/dev/null 2>&1
  openssl x509 -req -in "${configuration}/${directory}/server.csr" -CA "${configuration}/ca.pem" \
    -CAkey "${configuration}/ca.key" -CAcreateserial -days 30 -sha256 -copy_extensions copy \
    -out "${configuration}/${directory}/server.pem" >/dev/null 2>&1
  rm "${configuration}/${directory}/server.csr"
}
leaf postgres 'DNS:postgres'
leaf identity 'DNS:identity.localhost'
leaf web 'DNS:localhost'
openssl pkcs12 -export -in "${configuration}/web/server.pem" -inkey "${configuration}/web/server.key" \
  -certfile "${configuration}/ca.pem" -passout pass: -out "${configuration}/web/server.pfx" >/dev/null 2>&1
owner_password="$(openssl rand -hex 32)"
runtime_password="$(openssl rand -hex 32)"
web_secret="$(openssl rand -hex 32)"
service_secret="$(openssl rand -hex 32)"
owner_login="$(openssl rand -hex 32)"
owner_subject="$(uuid)"
owner_operation_id="$(uuid)"
printf '%s' "${owner_password}" >"${configuration}/postgres/owner.password"
printf '%s' "${runtime_password}" >"${configuration}/postgres/runtime.password"
printf '%s' "${web_secret}" >"${configuration}/web/client.secret"
printf '%s\n' "${owner_login}" >"${configuration}/owner-login.password"
printf '%s\n' "Host=postgres;Database=claimcore;Username=claimcore_owner;Password=${owner_password};SSL Mode=VerifyFull;Root Certificate=/etc/claimcore/ca.pem" >"${configuration}/administration/owner.connection"
printf '%s\n' "Host=postgres;Database=claimcore;Username=claimcore_runtime;Password=${runtime_password};SSL Mode=VerifyFull;Root Certificate=/etc/claimcore/runtime/ca.pem" >"${configuration}/web/runtime.connection"
printf '{"operationId":"%s","issuer":"https://identity.localhost:%s/realms/claimcore","subject":"%s"}\n' "${owner_operation_id}" "${identity_port}" "${owner_subject}" >"${configuration}/administration/owner.principal"
cp "${configuration}/ca.pem" "${configuration}/web/ca.pem"
cp "${configuration}/ca.pem" "${configuration}/administration/ca.pem"
cat >"${configuration}/identity/realm/claimcore.json" <<JSON
{"realm":"claimcore","enabled":true,"sslRequired":"all","loginWithEmailAllowed":false,"clients":[
{"clientId":"claimcore-web","enabled":true,"publicClient":false,"secret":"${web_secret}","standardFlowEnabled":true,"directAccessGrantsEnabled":false,"redirectUris":["https://localhost:${web_port}/signin-oidc"],"attributes":{"pkce.code.challenge.method":"S256"}},
{"clientId":"claimcore-cli","enabled":true,"publicClient":true,"standardFlowEnabled":true,"directAccessGrantsEnabled":false,"redirectUris":["http://127.0.0.1"],"attributes":{"pkce.code.challenge.method":"S256"},"protocolMappers":[{"name":"api","protocol":"openid-connect","protocolMapper":"oidc-audience-mapper","config":{"included.custom.audience":"claimcore-api","id.token.claim":"false","access.token.claim":"true"}}]},
{"clientId":"claimcore-service","enabled":true,"publicClient":false,"secret":"${service_secret}","serviceAccountsEnabled":true,"standardFlowEnabled":false,"directAccessGrantsEnabled":false,"protocolMappers":[{"name":"api","protocol":"openid-connect","protocolMapper":"oidc-audience-mapper","config":{"included.custom.audience":"claimcore-api","id.token.claim":"false","access.token.claim":"true"}}]}
],"users":[{"id":"${owner_subject}","username":"owner","enabled":true,"email":"owner@example.test","emailVerified":true,"firstName":"Local","lastName":"Owner","requiredActions":[],"credentials":[{"type":"password","value":"${owner_login}","temporary":false}]}]}
JSON
cat >"${configuration}/web.env" <<ENV
CLAIMCORE_CONNECTION_FILE=/etc/claimcore/runtime/runtime.connection
CLAIMCORE_WEB_CERTIFICATE_PATH=/etc/claimcore/runtime/server.pfx
CLAIMCORE_WEB_ORIGIN=https://localhost:${web_port}
CLAIMCORE_WEB_LISTEN_ADDRESS=0.0.0.0
CLAIMCORE_WEB_LISTEN_PORT=5443
CLAIMCORE_WEB_PROBE_CA_CERT_FILE=/etc/claimcore/runtime/ca.pem
CLAIMCORE_OIDC_ISSUER=https://identity.localhost:${identity_port}/realms/claimcore
CLAIMCORE_OIDC_CLIENT_ID=claimcore-web
CLAIMCORE_OIDC_CLIENT_SECRET_FILE=/etc/claimcore/runtime/client.secret
CLAIMCORE_OIDC_API_AUDIENCE=claimcore-api
CLAIMCORE_OIDC_CLI_CLIENT_ID=claimcore-cli
CLAIMCORE_OIDC_CA_CERT_FILE=/etc/claimcore/runtime/ca.pem
ENV
printf 'CLAIMCORE_CONFIG_DIR=%s\nCLAIMCORE_COMPOSE_PROJECT=%s\nCLAIMCORE_BUSINESS_ZONE=%s\nCLAIMCORE_HOST_PORT=%s\nCLAIMCORE_IDENTITY_PORT=%s\n' "${configuration}" "${project}" "${zone}" "${web_port}" "${identity_port}" >"${configuration}/compose.env"
# Private files must belong to their container user on Linux as well as Docker Desktop.
# Only this newly created directory is mounted. The helper has no network or persistent data.
docker run --rm --name "${project}-permissions" --label claimcore.configuration="${project}" \
  --network none --read-only --mount "type=bind,source=${configuration},target=/config" \
  --user 0 --entrypoint /bin/sh postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722 \
  -c 'set -eu; chown -R 999:999 /config/postgres; chown -R 1000:1000 /config/identity; chown -R 1654:1654 /config/web /config/administration'
unset owner_password runtime_password web_secret service_secret owner_login
printf 'Created fresh local configuration. Username: owner. Password: %s/owner-login.password\n' "${configuration}"
printf 'Trust %s/ca.pem in your browser. Leaf certificates expire after 30 days.\n' "${configuration}"
printf 'Start: docker compose --env-file "%s/compose.env" -f deployment/compose.yaml up --build -d\n' "${configuration}"
