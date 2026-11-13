#!/bin/sh
# SPDX-License-Identifier: MIT

set -eu
ulimit -c 0
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf -- "$test_dir"' EXIT
pkg_flags=$(pkg-config --cflags --libs gio-2.0)

"${CXX:-c++}" -std=c++17 -Wall -Wextra -Werror -pthread \
    -I"$root/include" \
    "$root/tests/test_bluetooth_reconnect.cpp" \
    "$root/src/cp0/cp0_bluez_dbus_client.cpp" \
    "$root/src/cp0/cp0_bluetooth_error_policy.cpp" \
    "$root/src/cp0/cp0_lvgl_log.cpp" \
    $pkg_flags -o "$test_dir/test_bluetooth_reconnect"

dbus-run-session -- sh -eu -c '
    export DBUS_SYSTEM_BUS_ADDRESS="$DBUS_SESSION_BUS_ADDRESS"
    "${PYTHON:-python3}" "$1" > "$2/mock.log" 2>&1 &
    mock_pid=$!
    trap '\''kill "$mock_pid" 2>/dev/null || true; wait "$mock_pid" 2>/dev/null || true'\'' EXIT
    for attempt in $(seq 1 50); do
        grep -q "mock org.bluez ready" "$2/mock.log" && break
        sleep 0.1
    done
    if ! grep -q "mock org.bluez ready" "$2/mock.log" ||
       ! "$2/test_bluetooth_reconnect" > "$2/client.log" 2>&1; then
        if [ -f "$2/client.log" ]; then tail -35 "$2/client.log"; fi
        cat "$2/mock.log"
        exit 1
    fi
' sh "$root/../bluectl/tests/mock_bluez.py" "$test_dir"
