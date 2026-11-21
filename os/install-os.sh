#!/bin/bash
# install-os.sh -- install (or remove) the jellyusb OS-side mount helper.
#
#   sudo ./install-os.sh                 install, then PRINT the fstab migration steps
#   sudo ./install-os.sh --uninstall     stop all jellyusb-mount@ units, remove the files
#   sudo ./install-os.sh --uninstall --purge   ... and /etc/default/jellyusb-mount
#   ./install-os.sh --dry-run [...]      show what would be done (no root needed)
#
# The fstab migration is never applied automatically: it is printed for you to
# review and run.

set -eu
umask 022

SRC_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

RULES_DST=/etc/udev/rules.d/99-jellyusb.rules
UNIT_DST=/etc/systemd/system/jellyusb-mount@.service
BIN_DST=/usr/local/sbin/jellyusb-mount
CONF_DST=/etc/default/jellyusb-mount
BASE=/mnt/usb

MODE=install
PURGE=0
DRY_RUN=0

usage() {
    sed -n '2,10s/^# \{0,1\}//p' "${BASH_SOURCE[0]}" >&2
    exit 2
}

for arg in "$@"; do
    case $arg in
        --uninstall) MODE=uninstall ;;
        --purge)     PURGE=1 ;;
        --dry-run|-n) DRY_RUN=1 ;;
        -h|--help)   usage ;;
        *) echo "unknown argument: $arg" >&2; usage ;;
    esac
done

run() {
    if [ "$DRY_RUN" = 1 ]; then
        printf '[dry-run]'; printf ' %q' "$@"; printf '\n'
    else
        "$@"
    fi
}

if [ "$DRY_RUN" != 1 ] && [ "$(id -u)" != 0 ]; then
    echo "This must be run as root: sudo $0 $*" >&2
    exit 1
fi

do_install() {
    local f
    for f in 99-jellyusb.rules jellyusb-mount@.service jellyusb-mount jellyusb-mount.default; do
        [ -f "$SRC_DIR/$f" ] || { echo "missing $SRC_DIR/$f" >&2; exit 1; }
    done
    bash -n "$SRC_DIR/jellyusb-mount"

    run install -D -o root -g root -m 0755 "$SRC_DIR/jellyusb-mount"          "$BIN_DST"
    run install -D -o root -g root -m 0644 "$SRC_DIR/jellyusb-mount@.service" "$UNIT_DST"
    run install -D -o root -g root -m 0644 "$SRC_DIR/99-jellyusb.rules"       "$RULES_DST"
    if [ -e "$CONF_DST" ]; then
        echo "$CONF_DST exists, kept as is (new template: $CONF_DST.dist)"
        run install -D -o root -g root -m 0644 "$SRC_DIR/jellyusb-mount.default" "$CONF_DST.dist"
    else
        run install -D -o root -g root -m 0644 "$SRC_DIR/jellyusb-mount.default" "$CONF_DST"
    fi
    run install -d -o root -g root -m 0755 "$BASE"

    run udevadm control --reload
    run systemctl daemon-reload

    echo
    echo "Installed:"
    echo "  $BIN_DST"
    echo "  $UNIT_DST"
    echo "  $RULES_DST"
    echo "  $CONF_DST"
    print_migration
}

print_migration() {
    cat <<'EOF'

==========================================================================
 MIGRATION FROM /etc/fstab  --  NOT applied, review and run it yourself
==========================================================================
Current /mnt/usb lines in /etc/fstab:
EOF
    grep -n '/mnt/usb/' /etc/fstab 2>/dev/null | sed 's/^/    /' || echo "    (none)"
    cat <<'EOF'

1) Back up fstab, then comment out the /mnt/usb/MOVIESnn lines
   (ntfs-3g defaults,nofail,x-systemd.automount,...):

    sudo cp -a /etc/fstab /etc/fstab.bak-jellyusb
    sudo sed -i -E 's|^([^#].*[[:space:]]/mnt/usb/MOVIES[0-9][0-9][[:space:]].*)$|# jellyusb: \1|' /etc/fstab
    grep -n '/mnt/usb/' /etc/fstab        # check: both lines start with "# jellyusb:"

2) Stop the legacy automount (and mount, if active) units:

    sudo systemctl stop 'mnt-usb-MOVIES*.automount'
    sudo systemctl stop 'mnt-usb-MOVIES*.mount'   # no-op if not mounted

3) Let systemd forget the fstab-generated units:

    sudo systemctl daemon-reload
    findmnt -R /mnt/usb                   # nothing may remain under /mnt/usb

4) Remove the empty placeholder directories (rmdir refuses non-empty ones):

    for d in /mnt/usb/MOVIES[0-9][0-9]; do
        [ -d "$d" ] && ! mountpoint -q "$d" && sudo rmdir -v -- "$d"
    done
    ls -la /mnt/usb                       # only the drives that are plugged in

5) Mount the drives that are currently plugged in (or unplug/replug them):

    sudo systemctl start jellyusb-mount@MOVIES00   # one per plugged drive
    # or, for all block devices:  sudo udevadm trigger --action=add --subsystem-match=block

6) Check:

    sudo jellyusb-mount status
    journalctl -t jellyusb-mount -b

Rollback: sudo ./install-os.sh --uninstall, then restore /etc/fstab.bak-jellyusb,
"sudo systemctl daemon-reload" and recreate the mount points.
==========================================================================
EOF
}

do_uninstall() {
    local units u
    if command -v systemctl >/dev/null 2>&1; then
        units=$(systemctl list-units --all --plain --no-legend 'jellyusb-mount@*.service' 2>/dev/null | awk '{print $1}')
        for u in $units; do
            echo "stopping $u (unmounts the drive)"
            run systemctl stop -- "$u" || echo "warning: could not stop $u" >&2
        done
    fi
    run rm -f -- "$RULES_DST" "$UNIT_DST" "$BIN_DST"
    if [ "$PURGE" = 1 ]; then
        run rm -f -- "$CONF_DST" "$CONF_DST.dist"
    else
        echo "kept $CONF_DST (use --purge to remove it)"
    fi
    run udevadm control --reload
    run systemctl daemon-reload
    run systemctl reset-failed 'jellyusb-mount@*' || true
    cat <<'EOF'

Uninstalled. /mnt/usb is kept. To go back to the old setup, restore the
fstab lines (sudo cp -a /etc/fstab.bak-jellyusb /etc/fstab, or un-comment the
"# jellyusb:" lines), run "sudo systemctl daemon-reload" and recreate the
mount points if needed (sudo mkdir -p /mnt/usb/MOVIESnn).
EOF
}

case $MODE in
    install)   do_install ;;
    uninstall) do_uninstall ;;
esac
