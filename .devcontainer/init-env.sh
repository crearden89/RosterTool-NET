#!/usr/bin/env bash
# New: the dev container's SQL Server needs an SA password, and none may be committed. This
# makes one at random, in .devcontainer/.env, the first time the dev container starts.
# devcontainer.json runs it as initializeCommand, on the machine that runs Docker, before
# the containers start; docker-compose.yml reads the file. Once the file exists it is left
# alone, so the password stays the one the database was made with. The file is in .gitignore.
#
# Delete .env only together with the database: rebuild the container afterwards (in
# Codespaces: Rebuild Container), and the app seeds the new database again.
set -eu

env_file="$(cd "$(dirname "$0")" && pwd)/.env"

if [ -e "$env_file" ]; then
  if grep -q '^MSSQL_SA_PASSWORD=.' "$env_file"; then
    echo "Keeping the SA password in $env_file."
  else
    echo "warning: $env_file has no MSSQL_SA_PASSWORD line. Delete it to have a new one made." >&2
  fi
  exit 0
fi

# Characters from one set, read from the kernel's random source.
random_chars() { LC_ALL=C tr -dc "$1" < /dev/urandom | head -c "$2"; }

# SQL Server's rules: at least 8 characters, from at least three of upper case, lower case,
# digits and symbols. This has 32, from all four. The symbols need no quoting in a connection
# string, an env file or a shell.
password="$(random_chars 'A-Z' 6)$(random_chars 'a-z' 6)$(random_chars '0-9' 6)$(random_chars '._-' 2)$(random_chars 'A-Za-z0-9' 12)"
if [ "${#password}" -ne 32 ]; then
  echo "error: could not make a random password (is /dev/urandom readable?)" >&2
  exit 1
fi

umask 077
# noclobber: never overwrite a file that appeared in the meantime.
( set -o noclobber
  printf '%s\n' \
    "# Made by .devcontainer/init-env.sh for this dev container's SQL Server. Not committed." \
    "# See init-env.sh before deleting it." \
    "MSSQL_SA_PASSWORD=$password" > "$env_file" )
echo "Made $env_file with a new random SA password."
