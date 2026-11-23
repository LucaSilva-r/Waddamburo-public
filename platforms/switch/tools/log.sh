#!/usr/bin/env bash
# Prints a file from the console's SD card over FTP (sys-ftpd), default the game's log. Needs SWITCH_IP, and
# SWITCH_FTP_USER / SWITCH_FTP_PASSWORD / SWITCH_FTP_PORT (default 5000) as set up in sys-ftpd's config.
# usage: platforms/switch/tools/log.sh [sd path]   e.g. wdb-spike.log (the shim's stderr, [hitch] lines)
set -euo pipefail
: "${SWITCH_IP:?set SWITCH_IP to the address of the console}"
curl -s --max-time 20 -u "${SWITCH_FTP_USER:?}:${SWITCH_FTP_PASSWORD:?}" \
    "ftp://$SWITCH_IP:${SWITCH_FTP_PORT:-5000}/${1:-switch/taiko/USRDIR/waddamburo/logs/latest.log}"
