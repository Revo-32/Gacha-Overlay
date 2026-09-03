#!/bin/sh
set -eu
status_port="${PORT:-8080}"
case "$status_port" in
  ''|*[!0-9]*) echo 'Invalid PORT' >&2; exit 2 ;;
esac
if [ "${#status_port}" -gt 5 ] || [ "$status_port" -lt 1 ] || [ "$status_port" -gt 65535 ]; then
  echo 'Invalid PORT' >&2
  exit 2
fi
exec httpd -f -p "0.0.0.0:$status_port" -h /www
