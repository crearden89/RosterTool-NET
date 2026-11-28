#!/usr/bin/env bash
# New: starts the Roster web app in the dev container, on port 5066, seeded with invented
# data. devcontainer.json runs it with --background as postStartCommand, on every start.
# It waits until SQL Server accepts a login, then runs
#   dotnet run --project src/Roster.Web -- --urls http://0.0.0.0:5066 --seed
# and tries again a few times if that fails (the database may still be starting). --seed
# only seeds an empty database, so it is safe on every start. It must come after --urls.
#
#   bash .devcontainer/start-app.sh --background   start it detached; log in /tmp/roster-app.log
#   bash .devcontainer/start-app.sh --stop         stop it
#   bash .devcontainer/start-app.sh                run it in this terminal (Ctrl+C stops it)
#
# The database is ConnectionStrings__Roster, from docker-compose.yml. DB_HOST and DB_NAME
# point it at another server or database instead, with the SA password in MSSQL_SA_PASSWORD
# (to try this script outside the container). ROSTER_APP_LOG, ROSTER_APP_PORT,
# ROSTER_APP_ATTEMPTS, ROSTER_DB_WAIT_SECONDS and ROSTER_DOTNET (the dotnet command)
# change the other defaults.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
log="${ROSTER_APP_LOG:-/tmp/roster-app.log}"
pidfile="${ROSTER_APP_PIDFILE:-/tmp/roster-app.pid}"
port="${ROSTER_APP_PORT:-5066}"
attempts="${ROSTER_APP_ATTEMPTS:-5}"
wait_seconds="${ROSTER_DB_WAIT_SECONDS:-300}"
read -r -a dotnet <<< "${ROSTER_DOTNET:-dotnet}"

# This script's process when it is running. The pid file can outlive a restart of the
# container, when its number may belong to something else: check what that process is.
running() {
  local pid command
  pid="$(cat "$pidfile" 2>/dev/null)" || return 1
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null || return 1
  command="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null)" || return 1
  [[ "$command" == *start-app.sh* ]]
}

case "${1:-}" in
  --background)
    if running; then
      echo "Roster is already running (process $(cat "$pidfile")). Log: $log"
      exit 0
    fi
    # A session of its own, so that it outlives the command that started it.
    setsid nohup bash "$here/start-app.sh" > "$log" 2>&1 < /dev/null &
    echo "Roster is starting in the background on port $port. Log: $log"
    exit 0
    ;;
  --stop)
    if running; then
      pid="$(cat "$pidfile")"
      # Its whole process group: this script, dotnet run and the app.
      kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid"
      echo "Stopped Roster (process $pid)."
    else
      echo "Roster is not running."
    fi
    rm -f "$pidfile"
    exit 0
    ;;
  "") ;;
  *)
    echo "usage: $0 [--background | --stop]" >&2
    exit 2
    ;;
esac

if running; then
  echo "Roster is already running (process $(cat "$pidfile")). Stop it first: $0 --stop" >&2
  exit 1
fi
echo "$$" > "$pidfile"
trap 'rm -f "$pidfile"' EXIT
trap 'exit 143' TERM
cd "$repo"

say() { echo "[$(date -u +%H:%M:%S)Z] $*"; }

if [ -n "${DB_HOST:-}${DB_NAME:-}" ] || [ -z "${ConnectionStrings__Roster:-}" ]; then
  if [ -z "${MSSQL_SA_PASSWORD:-}" ]; then
    say "error: ConnectionStrings__Roster is not set, and there is no MSSQL_SA_PASSWORD to make it with."
    exit 1
  fi
  export ConnectionStrings__Roster="Server=${DB_HOST:-db};Database=${DB_NAME:-Roster};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"
fi

if [ ! -e /usr/share/zoneinfo/Australia/Sydney ]; then
  say "warning: this machine has no time zone data, which the app needs: sudo apt-get update && sudo apt-get install -y tzdata"
fi
if [ -n "${CODESPACE_NAME:-}" ] && [ -n "${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-}" ]; then
  say "In this codespace the app will be at https://$CODESPACE_NAME-$port.$GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN/"
fi

say "Waiting for SQL Server to accept a login..."
if ! "${dotnet[@]}" run --file "$here/wait-for-sql.cs" -- "$wait_seconds"; then
  say "Starting the app anyway: it is tried again if the database is not ready."
fi

for attempt in $(seq 1 "$attempts"); do
  say "Starting Roster on port $port (attempt $attempt of $attempts)..."
  status=0
  "${dotnet[@]}" run --project src/Roster.Web -- --urls "http://0.0.0.0:$port" --seed || status=$?
  if [ "$status" -eq 0 ]; then
    say "Roster stopped."
    exit 0
  fi
  say "Roster exited with code $status."
  if [ "$attempt" -lt "$attempts" ]; then sleep 15; fi
done
say "Roster did not start in $attempts attempts. The messages above say why."
exit 1
