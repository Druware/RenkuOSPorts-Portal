#!/bin/sh
# Validates API_UPSTREAM_* before 20-envsubst-on-templates.sh renders them into the nginx config.
# The stock /docker-entrypoint.sh runs with `set -e`, so a non-zero exit here stops the container
# before nginx starts: a bad value never reaches a rendered config.

set -eu

ME=$(basename "$0")

fail() {
    echo "$ME: ERROR: $*" >&2
    exit 1
}

host=${API_UPSTREAM_HOST-}
port=${API_UPSTREAM_PORT-}
scheme=${API_UPSTREAM_SCHEME-}

# Host: a DNS name or IPv4 address only. The character check comes first so that whitespace,
# newlines, `;`, `{`, `$`, quotes and so on can never reach the config; the structure check then
# enforces RFC 1123 labels (1-63 chars, alphanumeric at both ends, hyphens inside), <= 253 total.
case "$host" in
    '') fail "API_UPSTREAM_HOST is empty" ;;
    *[!A-Za-z0-9.-]*) fail "API_UPSTREAM_HOST '$host' contains characters outside [A-Za-z0-9.-]" ;;
esac
[ "${#host}" -le 253 ] || fail "API_UPSTREAM_HOST is longer than 253 characters"
label='[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?'
printf '%s' "$host" | grep -Eqx "$label(\\.$label)*" \
    || fail "API_UPSTREAM_HOST '$host' is not a valid hostname"

# Port: decimal 1-65535, no sign, no leading zero.
case "$port" in
    '' | *[!0-9]* | 0*) fail "API_UPSTREAM_PORT '$port' is not a port number (1-65535)" ;;
esac
[ "${#port}" -le 5 ] && [ "$port" -le 65535 ] \
    || fail "API_UPSTREAM_PORT '$port' is not a port number (1-65535)"

case "$scheme" in
    http | https) ;;
    *) fail "API_UPSTREAM_SCHEME '$scheme' must be 'http' or 'https'" ;;
esac

echo "$ME: API upstream is $scheme://$host:$port"
