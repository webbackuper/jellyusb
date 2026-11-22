#!/usr/bin/env bash
# Runs every integration check on a throwaway Jellyfin instance and fresh test media, then removes them.
#
#   tests/integration/run-all.sh
#
# Uses the plugin build of Jellyfin.Plugin.JellyUsb/bin/Release (dotnet build -c Release first).
# Another Jellyfin can be tested before upgrading the server:
#   JELLYUSB_JF_BIN=/path/to/jellyfin/jellyfin JELLYUSB_JF_WEBDIR=/path/to/jellyfin/jellyfin-web tests/integration/run-all.sh
# Exit status: 0 when every check passed.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
export JELLYUSB_TEST_PORT="${JELLYUSB_TEST_PORT:-18197}"
WORK="$(mktemp -d -t jellyusb-it.XXXXXX)"
INSTANCE="$WORK/jf"
MEDIA="$WORK/usb"
cleanup() {
    python3 -B "$HERE/test-server.py" stop "$INSTANCE" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

echo "==> Médiathèque de test"
"$HERE/make-media.sh" "$MEDIA" >/dev/null
echo "==> Instance Jellyfin jetable (port $JELLYUSB_TEST_PORT)"
python3 -B "$HERE/test-server.py" start "$INSTANCE" "$MEDIA" | tail -1
python3 -B "$HERE/test-server.py" scan "$INSTANCE" | tail -1

status=0
for check in playback-checks.py details-checks.py unplug-checks.py; do
    echo "==> $check"
    if ! python3 -B "$HERE/$check" "$INSTANCE" "$MEDIA" > "$WORK/$check.log" 2>&1; then
        grep -E "FAIL|Error|Traceback" "$WORK/$check.log" | head -20 || true
        status=1
    fi
    tail -1 "$WORK/$check.log"
done

if [ "$status" -ne 0 ]; then
    echo "==> ÉCHEC des tests d'intégration" >&2
fi
exit "$status"
