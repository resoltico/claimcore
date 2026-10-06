#!/usr/bin/env bash
set -euo pipefail

export KC_BOOTSTRAP_ADMIN_USERNAME=local-administrator
KC_BOOTSTRAP_ADMIN_PASSWORD="$(cat /etc/claimcore-identity/admin.password)"
export KC_BOOTSTRAP_ADMIN_PASSWORD
exec /opt/keycloak/bin/kc.sh "$@"
