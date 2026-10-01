#!/usr/bin/env bash
# lint-exception: LX-0041
# shellcheck disable=SC2154
# Sourced inside run_engine; keep each browser fixture in its existing isolated subshell.
repo_root="${repo_root:?The repository root is required.}"
state_dir="${state_dir:?The private browser state directory is required.}"
primary="${primary:?The primary fixture name is required.}"
witness="${witness:?The witness fixture name is required.}"
run_label="${run_label:?The fixture label is required.}"
database_dll="${database_dll:?The published Database assembly is required.}"
oidc_ca="${oidc_ca:?The synthetic issuer trust root is required.}"
image="$(jq -r '.containerImage' "${repo_root}/db/postgresql-baseline.json")"
primary_password="$(openssl rand -hex 32)"
app_password="$(openssl rand -hex 32)"
witness_password="$(openssl rand -hex 32)"
writer_password="$(openssl rand -hex 32)"
auditor_password="$(openssl rand -hex 32)"
printf '%s\n' "${primary_password}" >"${state_dir}/primary.password"
printf '%s\n' "${app_password}" >"${state_dir}/app.password"
printf '%s\n' "${witness_password}" >"${state_dir}/witness.password"
printf '%s\n' "${writer_password}" >"${state_dir}/writer.password"
jq -r '.adminPassword' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
  >"${state_dir}/admin.password"
jq -r '.users[0].password' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
  >"${state_dir}/owner.password"
printf 'POSTGRES_DB=claimcore\nPOSTGRES_USER=claimcore_primary_owner\nPOSTGRES_PASSWORD=%s\n' \
  "${primary_password}" >"${state_dir}/primary.env"
printf 'POSTGRES_DB=claimcore_witness\nPOSTGRES_USER=claimcore_witness_owner\nPOSTGRES_PASSWORD=%s\n' \
  "${witness_password}" >"${state_dir}/witness.env"
docker run --detach --rm --name "${primary}" --label "org.claimcore.test-run=${run_label}" \
  --env-file "${state_dir}/primary.env" --publish 127.0.0.1::5432 "${image}" >/dev/null
docker run --detach --rm --name "${witness}" --label "org.claimcore.test-run=${run_label}" \
  --env-file "${state_dir}/witness.env" --publish 127.0.0.1::5432 "${image}" >/dev/null
wait_pg() {
  local container="$1" user="$2" database="$3"
  for _ in $(seq 1 60); do
    docker exec "${container}" pg_isready -h 127.0.0.1 -U "${user}" -d "${database}" \
      >/dev/null 2>&1 && return 0
    sleep 1
  done
  return 1
}
if ! wait_pg "${primary}" claimcore_primary_owner claimcore ||
  ! wait_pg "${witness}" claimcore_witness_owner claimcore_witness; then
  printf 'An isolated PostgreSQL container did not become ready.\n' >&2
  exit 1
fi
primary_port="$(docker port "${primary}" 5432/tcp | sed -n 's/^127\.0\.0\.1:\([0-9][0-9]*\)$/\1/p')"
witness_port="$(docker port "${witness}" 5432/tcp | sed -n 's/^127\.0\.0\.1:\([0-9][0-9]*\)$/\1/p')"
[[ "${primary_port}" =~ ^[0-9]+$ && "${witness_port}" =~ ^[0-9]+$ ]] || exit 1
docker exec -i "${primary}" psql -X -v ON_ERROR_STOP=1 -U claimcore_primary_owner \
  -d claimcore >/dev/null 2>&1 <<SQL
CREATE ROLE claimcore_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${app_password}';
REVOKE ALL ON DATABASE claimcore FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore TO claimcore_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
docker exec -i "${witness}" psql -X -v ON_ERROR_STOP=1 -U claimcore_witness_owner \
  -d claimcore_witness >/dev/null 2>&1 <<SQL
CREATE ROLE claimcore_witness_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${writer_password}';
CREATE ROLE claimcore_witness_auditor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${auditor_password}';
REVOKE ALL ON DATABASE claimcore_witness FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore_witness TO claimcore_witness_writer, claimcore_witness_auditor;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
unset auditor_password
printf 'Host=127.0.0.1;Port=%s;Database=claimcore;Username=claimcore_primary_owner;Password=%s\n' \
  "${primary_port}" "${primary_password}" >"${state_dir}/primary-owner.connection"
printf 'Host=127.0.0.1;Port=%s;Database=claimcore;Username=claimcore_app;Password=%s\n' \
  "${primary_port}" "${app_password}" >"${state_dir}/primary-app.connection"
printf 'Host=127.0.0.1;Port=%s;Database=claimcore_witness;Username=claimcore_witness_owner;Password=%s\n' \
  "${witness_port}" "${witness_password}" >"${state_dir}/witness-owner.connection"
printf 'Host=127.0.0.1;Port=%s;Database=claimcore_witness;Username=claimcore_witness_writer;Password=%s\n' \
  "${witness_port}" "${writer_password}" >"${state_dir}/witness-writer.connection"
suppression_key_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
suppression_material="$(openssl rand -base64 32 | tr -d '\n')"
jq -n --arg id "${suppression_key_id}" --arg material "${suppression_material}" \
  '{version:1,keyId:$id,materialBase64:$material}' \
  >"${state_dir}/suppression-key.json"
unset suppression_material
CLAIMCORE_SUPPRESSION_KEY_FILE="${state_dir}/suppression-key.json" \
  bash "${repo_root}/eng/Initialize-PublishedWebDatabase.sh" "${database_dll}" \
  "${state_dir}/primary-owner.connection" "${state_dir}/diagnostics"

key_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
key_material="$(openssl rand -base64 32 | tr -d '\n')"
jq -n --arg id "${key_id}" --arg material "${key_material}" \
  '{version:1,activeKeyId:$id,keys:[{id:$id,materialBase64:$material}]}' \
  >"${state_dir}/witness-key.json"
unset key_material
openssl rand 32 >"${state_dir}/writer.capability"
artifact_key_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
artifact_encryption="$(openssl rand -base64 32 | tr -d '\n')"
artifact_mac="$(openssl rand -base64 32 | tr -d '\n')"
jq -n --arg id "${artifact_key_id}" --arg encryption "${artifact_encryption}" \
  --arg mac "${artifact_mac}" \
  '{version:1,activeKeyId:$id,artifactLifetimeSeconds:3600,keys:[{
      id:$id,encryptionBase64:$encryption,macBase64:$mac,
      issueFrom:"2020-01-01T00:00:00+00:00",
      issueUntil:"2030-01-01T00:00:00+00:00",
      verifyUntil:"2030-02-01T00:00:00+00:00",maximumExports:65536}]}' \
  >"${state_dir}/recovery-artifact-key.json"
unset artifact_encryption artifact_mac
if ! CLAIMCORE_ADMIN_CONNECTION_FILE="${state_dir}/primary-owner.connection" \
  CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE="${state_dir}/witness-owner.connection" \
  CLAIMCORE_WITNESS_KEY_FILE="${state_dir}/witness-key.json" \
  CLAIMCORE_WRITER_CAPABILITY_FILE="${state_dir}/writer.capability" \
  dotnet "${database_dll}" initialize-witness \
  >"${state_dir}/diagnostics/witness-init.out" \
  2>"${state_dir}/diagnostics/witness-init.err"; then
  printf 'Witness initialization failed: %s.\n' \
    "$(diagnostic "${state_dir}/diagnostics/witness-init.err")" >&2
  exit 1
fi
issuer_base="${CLAIMCORE_TEST_OIDC_ISSUER%/realms/*}"
jq -j '"grant_type=password&client_id=admin-cli&username=" + (.adminUsername|@uri) +
    "&password=" + (.adminPassword|@uri)' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
  >"${state_dir}/admin-token.form"
curl --cacert "${oidc_ca}" --fail --silent --show-error \
  --output "${state_dir}/admin-token.json" \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-binary "@${state_dir}/admin-token.form" \
  "${issuer_base}/realms/master/protocol/openid-connect/token"
admin_token="$(jq -r '.access_token // empty' "${state_dir}/admin-token.json")"
[[ -n "${admin_token}" ]] || {
  printf 'Synthetic admin token is absent.\n' >&2
  exit 1
}
printf '%s\n' "${admin_token}" >"${state_dir}/admin-token.secret"
printf 'header = "Authorization: Bearer %s"\n' "${admin_token}" \
  >"${state_dir}/admin-auth.curl"
username="$(jq -r '.users[0].username' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
realm="$(jq -r '.realm' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
curl --cacert "${oidc_ca}" --fail --silent --show-error \
  --get --data-urlencode "username=${username}" --data-urlencode 'exact=true' \
  --config "${state_dir}/admin-auth.curl" \
  --output "${state_dir}/owner-subject.json" \
  "${issuer_base}/admin/realms/${realm}/users"
steward_username="$(jq -r '.users[1].username' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
curl --cacert "${oidc_ca}" --fail --silent --show-error \
  --get --data-urlencode "username=${steward_username}" --data-urlencode 'exact=true' \
  --config "${state_dir}/admin-auth.curl" \
  --output "${state_dir}/steward-subject.json" \
  "${issuer_base}/admin/realms/${realm}/users"
unset admin_token
owner_subject="$(jq -er --arg username "${username}" \
  'if length == 1 and .[0].username == $username then .[0].id else empty end' \
  "${state_dir}/owner-subject.json")"
steward_subject="$(jq -er --arg username "${steward_username}" \
  'if length == 1 and .[0].username == $username then .[0].id else empty end' \
  "${state_dir}/steward-subject.json")"
[[ "${owner_subject}" != "${steward_subject}" ]] || {
  printf 'Synthetic owner and steward subjects must differ.\n' >&2
  exit 1
}
printf '%s\n' "${owner_subject}" >"${state_dir}/owner.subject"
printf '%s\n' "${steward_subject}" >"${state_dir}/steward.subject"
jq -n --arg issuer "${CLAIMCORE_TEST_OIDC_ISSUER}" \
  --arg owner "${owner_subject}" --arg steward "${steward_subject}" \
  --arg service "$(jq -r '.serviceClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
  '{issuer:$issuer,ownerSubject:$owner,stewardSubject:$steward,serviceClientId:$service}' \
  >"${state_dir}/principals.json"
jq -e 'keys == ["issuer","ownerSubject","serviceClientId","stewardSubject"] and
    (.issuer | type == "string" and startswith("https://")) and
    (.ownerSubject | type == "string" and length > 0) and
    (.stewardSubject | type == "string" and length > 0) and
    (.serviceClientId | type == "string" and length > 0) and
    .ownerSubject != .stewardSubject' "${state_dir}/principals.json" >/dev/null || {
  printf 'Synthetic immutable principal inventory is invalid.\n' >&2
  exit 1
}
jq '{issuer,subject:.ownerSubject}' "${state_dir}/principals.json" \
  >"${state_dir}/initial-owner.json"
unset owner_subject steward_subject
if ! CLAIMCORE_ADMIN_CONNECTION_FILE="${state_dir}/primary-owner.connection" \
  CLAIMCORE_WITNESS_CONNECTION_FILE="${state_dir}/witness-writer.connection" \
  CLAIMCORE_WITNESS_KEY_FILE="${state_dir}/witness-key.json" \
  CLAIMCORE_WRITER_CAPABILITY_FILE="${state_dir}/writer.capability" \
  CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE="${state_dir}/initial-owner.json" \
  dotnet "${database_dll}" provision-initial-owner \
  >"${state_dir}/diagnostics/owner-init.out" \
  2>"${state_dir}/diagnostics/owner-init.err"; then
  printf 'Initial owner provisioning failed: %s.\n' \
    "$(diagnostic "${state_dir}/diagnostics/owner-init.err")" >&2
  exit 1
fi
if CLAIMCORE_ADMIN_CONNECTION_FILE="${state_dir}/primary-owner.connection" \
  CLAIMCORE_WITNESS_CONNECTION_FILE="${state_dir}/witness-writer.connection" \
  CLAIMCORE_WITNESS_KEY_FILE="${state_dir}/witness-key.json" \
  CLAIMCORE_WRITER_CAPABILITY_FILE="${state_dir}/writer.capability" \
  CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE="${state_dir}/initial-owner.json" \
  dotnet "${database_dll}" provision-initial-owner \
  >"${state_dir}/diagnostics/owner-repeat.out" \
  2>"${state_dir}/diagnostics/owner-repeat.err"; then
  printf 'Repeated first-owner provisioning was not refused.\n' >&2
  exit 1
fi
jq -e '.operationOutcome == "NOT_COMMITTED" and .diagnostic.id == "DB_OPERATION_FAILED"' \
  "${state_dir}/diagnostics/owner-repeat.err" >/dev/null || {
  printf 'Repeated first-owner refusal was not definite.\n' >&2
  exit 1
}
