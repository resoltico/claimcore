#!/usr/bin/env bash
set -euo pipefail

state_dir="${1:?Pass the private fixture directory.}"

openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -subj '/CN=ClaimCore Synthetic Web CA' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "${state_dir}/web-ca.key" -out "${state_dir}/web-ca.pem" >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj /CN=localhost \
  -keyout "${state_dir}/web.key" -out "${state_dir}/web.csr" >/dev/null 2>&1
printf '%s\n' 'basicConstraints=critical,CA:FALSE' \
  'keyUsage=critical,digitalSignature,keyEncipherment' \
  'extendedKeyUsage=serverAuth' 'subjectAltName=DNS:localhost' \
  >"${state_dir}/web.ext"
openssl x509 -req -in "${state_dir}/web.csr" -CA "${state_dir}/web-ca.pem" \
  -CAkey "${state_dir}/web-ca.key" -CAcreateserial -days 1 \
  -extfile "${state_dir}/web.ext" -out "${state_dir}/web.pem" >/dev/null 2>&1
openssl verify -CAfile "${state_dir}/web-ca.pem" -verify_hostname localhost \
  "${state_dir}/web.pem" >/dev/null
openssl pkcs12 -export -out "${state_dir}/web.pfx" \
  -inkey "${state_dir}/web.key" -in "${state_dir}/web.pem" \
  -certfile "${state_dir}/web-ca.pem" -passout pass: >/dev/null 2>&1
