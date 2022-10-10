#!/usr/bin/env bash

###
### Copyright (c) 2022 Carbon Community
### All rights reserved
###

set -e

BASE="$(cd -- "$(dirname "${0}")" >/dev/null 2>&1; pwd -P)"
WORKING="${BASE}/../../RustDedicated_Data/Managed"

FILE=Assembly-CSharp.dll
PID=${1:-0}

[ ${PID} -gt 0 ] && tail --pid=${PID} -f /dev/null

cp -f "${WORKING}/${FILE}" "${WORKING}/${FILE}.bak"
cp -f "${WORKING}/__${FILE}" "${WORKING}/${FILE}"