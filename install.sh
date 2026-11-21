#!/usr/bin/env bash
# Installe (ou désinstalle) le plugin JellyUSB dans le Jellyfin de cette machine.
#
#   sudo ./install.sh              installe la version compilée (dotnet build -c Release)
#   sudo ./install.sh --uninstall  retire le plugin (la configuration est conservée)
#
# Étapes : arrêt de Jellyfin, sauvegarde de la base (jellyfin.db + -wal/-shm), copie du plugin
# dans /var/lib/jellyfin/plugins/JellyUSB_<version>/, redémarrage, vérification du chargement.
# La tâche « Réparer les titres mal encodés » n'est PAS lancée : lancez-la vous-même depuis
# Tableau de bord > Tâches planifiées > JellyUSB, une fois les supports concernés branchés.
set -euo pipefail

VERSION="1.0.0.0"
GUID="9f8d8889-7b20-47d5-ad9a-37807e42e4ce"
DATA_DIR="${JELLYFIN_DATA_DIR:-/var/lib/jellyfin}"
PLUGINS_DIR="$DATA_DIR/plugins"
DEST="$PLUGINS_DIR/JellyUSB_$VERSION"
DB_DIR="$DATA_DIR/data"
LOG_DIR="${JELLYFIN_LOG_DIR:-/var/log/jellyfin}"
URL="http://127.0.0.1:8096"
HERE="$(cd "$(dirname "$0")" && pwd)"
DLL="$HERE/Jellyfin.Plugin.JellyUsb/bin/Release/net10.0/Jellyfin.Plugin.JellyUsb.dll"
IMAGE="$HERE/assets/jellyusb.png"

die() { echo "ERREUR : $*" >&2; exit 1; }
info() { echo "==> $*"; }

[ "$(id -u)" -eq 0 ] || die "à lancer avec sudo"
command -v systemctl >/dev/null || die "systemctl introuvable"
[ -d "$PLUGINS_DIR" ] || die "$PLUGINS_DIR introuvable (Jellyfin est-il installé ?)"

wait_for_jellyfin() {
    info "Attente du redémarrage de Jellyfin..."
    for _ in $(seq 1 120); do
        if curl -fsS "$URL/System/Info/Public" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    die "Jellyfin ne répond pas sur $URL (voir journalctl -u jellyfin)"
}

if [ "${1:-}" = "--uninstall" ]; then
    info "Arrêt de Jellyfin"
    systemctl stop jellyfin
    rm -rf "$PLUGINS_DIR"/JellyUSB_*
    info "Plugin retiré (configuration conservée dans $PLUGINS_DIR/configurations/Jellyfin.Plugin.JellyUsb.xml)"
    info "Pensez à retirer « JellyUSB NFO » de l'ordre des lecteurs de métadonnées si Jellyfin l'affiche encore."
    systemctl start jellyfin
    wait_for_jellyfin
    info "Terminé."
    exit 0
fi

[ -f "$DLL" ] || die "plugin non compilé : lancez d'abord 'dotnet build -c Release' dans $HERE"

SERVER_VERSION="$(dpkg-query -W -f='${Version}' jellyfin-server 2>/dev/null || true)"
case "$SERVER_VERSION" in
    12.4*|12.5*|12.6*|12.7*|12.8*|12.9*) ;;
    "") echo "Attention : version de jellyfin-server inconnue." ;;
    *) die "JellyUSB $VERSION est compilé pour Jellyfin 12.4 (version installée : $SERVER_VERSION)" ;;
esac

STAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP="$DB_DIR/jellyusb-backup-$STAMP"

info "Arrêt de Jellyfin"
systemctl stop jellyfin

info "Sauvegarde de la base dans $BACKUP"
mkdir -p "$BACKUP"
for f in jellyfin.db jellyfin.db-wal jellyfin.db-shm; do
    [ -f "$DB_DIR/$f" ] && cp -a "$DB_DIR/$f" "$BACKUP/"
done
cp -a "$DATA_DIR/root/default" "$BACKUP/library-options"
chown -R jellyfin:jellyfin "$BACKUP"

info "Installation du plugin dans $DEST"
rm -rf "$PLUGINS_DIR"/JellyUSB_*
install -d -o jellyfin -g jellyfin -m 755 "$DEST"
install -o jellyfin -g jellyfin -m 644 "$DLL" "$DEST/"
[ -f "$IMAGE" ] && install -o jellyfin -g jellyfin -m 644 "$IMAGE" "$DEST/jellyusb.png"
cat > "$DEST/meta.json" <<EOF
{
  "category": "General",
  "changelog": "1.0.0.0 : première version.",
  "description": "Vérifie la présence du support USB (MediaID) avant la lecture et répare les caractères accentués.",
  "guid": "$GUID",
  "name": "JellyUSB",
  "overview": "Vérification du support USB avant lecture, réparation des accents",
  "owner": "webbackuper",
  "targetAbi": "12.4.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "jellyusb.png",
  "assemblies": ["Jellyfin.Plugin.JellyUsb.dll"]
}
EOF
chown jellyfin:jellyfin "$DEST/meta.json"

info "Démarrage de Jellyfin"
systemctl start jellyfin
wait_for_jellyfin

LOG="$(ls -t "$LOG_DIR"/jellyfin*.log 2>/dev/null | head -1 || true)"
if [ -n "$LOG" ] && grep -q 'Loaded plugin: "JellyUSB"' "$LOG"; then
    info "JellyUSB $VERSION est chargé."
else
    echo "Attention : le chargement de JellyUSB n'apparaît pas dans $LOG. Vérifiez Tableau de bord > Extensions."
fi

cat <<EOF

Installation terminée. Sauvegarde : $BACKUP
Pour revenir en arrière :
  sudo systemctl stop jellyfin
  sudo cp -a "$BACKUP"/jellyfin.db* "$DB_DIR"/
  sudo ./install.sh --uninstall

Prochaines étapes :
  1. Tableau de bord > Extensions > JellyUSB : vérifiez les réglages (chemin /mnt/usb/, messages).
  2. Branchez les supports dont les titres sont abîmés, puis Tableau de bord > Tâches planifiées > JellyUSB >
     « Réparer les titres mal encodés » > Lancer.
EOF
