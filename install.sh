#!/usr/bin/env bash
# Installe (ou désinstalle) le plugin JellyUSB dans le Jellyfin de cette machine.
#
#   sudo ./install.sh              installe la version compilée (dotnet build -c Release)
#   sudo ./install.sh --uninstall  retire le plugin et ce qu'il a changé dans Jellyfin (réglages,
#                                  lecteur NFO, dépôt) ; --uninstall --purge supprime aussi les
#                                  sauvegardes de la base
#
# Étapes : arrêt de Jellyfin, sauvegarde de la base (jellyfin.db + -wal/-shm), copie du plugin
# dans /var/lib/jellyfin/plugins/JellyUSB_<version>/, redémarrage, vérification du chargement.
# La tâche « Réparer les titres mal encodés » n'est PAS lancée : lancez-la vous-même depuis
# Tableau de bord > Tâches planifiées > JellyUSB, une fois les supports concernés branchés.
set -euo pipefail

VERSION="$(sed -n "s:.*<Version>\(.*\)</Version>.*:\1:p" "$(cd "$(dirname "$0")" && pwd)/Directory.Build.props")"
GUID="9f8d8889-7b20-47d5-ad9a-37807e42e4ce"
DATA_DIR="${JELLYFIN_DATA_DIR:-/var/lib/jellyfin}"
PLUGINS_DIR="$DATA_DIR/plugins"
DEST="$PLUGINS_DIR/JellyUSB_$VERSION"
DB_DIR="$DATA_DIR/data"
LOG_DIR="${JELLYFIN_LOG_DIR:-/var/log/jellyfin}"
CONFIG_DIR="${JELLYFIN_CONFIG_DIR:-/etc/jellyfin}"
REPO_NAME="JellyUSB"
REPO_URL="https://raw.githubusercontent.com/webbackuper/jellyusb/main/manifest.json"
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
    # The same clean-up as Jellyfin's "Uninstall" button (UninstallCleaner), for a plugin removed by hand.
    PURGE="${2:-}"
    CACHE_DIR="${JELLYFIN_CACHE_DIR:-/var/cache/jellyfin}"
    TASK_FILE="d0955d59-099a-46bd-a6a0-6a1886aacefd.js"   # "Réparer les titres mal encodés"
    info "Arrêt de Jellyfin"
    systemctl stop jellyfin
    rm -rf "$PLUGINS_DIR"/JellyUSB_*
    info "Plugin retiré"
    rm -f "$PLUGINS_DIR/configurations/Jellyfin.Plugin.JellyUsb.xml" \
          "$DATA_DIR/data/ScheduledTasks/$TASK_FILE" "$CONFIG_DIR/ScheduledTasks/$TASK_FILE"
    rm -rf "$CACHE_DIR/jellyusb"
    info "Réglages, cache et fichiers de la tâche planifiée supprimés"
    python3 -I - "$DATA_DIR/root/default" "$CONFIG_DIR/system.xml" <<'PYEOF'
import glob, os, re, sys
views, system = sys.argv[1:3]
for path in glob.glob(os.path.join(views, "*", "options.xml")):
    text = open(path, encoding="utf-8").read()
    new = re.sub(r"^[ \t]*<string>JellyUSB NFO</string>[ \t]*\r?\n", "", text, flags=re.M)
    if new != text:
        open(path, "w", encoding="utf-8").write(new)
        print("==> Lecteur « JellyUSB NFO » retiré de " + os.path.basename(os.path.dirname(path)))
if os.path.exists(system):
    text = open(system, encoding="utf-8").read()
    new = re.sub(r"[ \t]*<RepositoryInfo>(?:(?!</RepositoryInfo>).)*webbackuper/jellyusb(?:(?!</RepositoryInfo>).)*</RepositoryInfo>[ \t]*\r?\n",
                 "", text, flags=re.S)
    if new != text:
        open(system, "w", encoding="utf-8").write(new)
        print("==> Dépôt de plugins JellyUSB retiré de system.xml")
PYEOF
    if [ "$PURGE" = "--purge" ]; then
        rm -rf "$DB_DIR"/jellyusb-backup-*
        info "Sauvegardes jellyusb-backup-* supprimées"
    else
        BACKUPS="$(ls -d "$DB_DIR"/jellyusb-backup-* 2>/dev/null | tr '\n' ' ' || true)"
        [ -n "$BACKUPS" ] && info "Sauvegardes conservées (--purge pour les supprimer) : $BACKUPS"
    fi
    systemctl start jellyfin
    wait_for_jellyfin
    echo "==> Terminé. Il ne reste de JellyUSB dans Jellyfin que :"
    echo "    - les titres réparés dans la base. Pour retrouver la base d'avant l'installation (les"
    echo "      changements faits depuis seront perdus) : sudo systemctl stop jellyfin ;"
    echo "      sudo cp -a <sauvegarde>/jellyfin.db* $DB_DIR/ ; sudo systemctl start jellyfin"
    echo "    - le montage des disques, s'il a été installé : sudo ./os/install-os.sh --uninstall"
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
[ -f "$CONFIG_DIR/system.xml" ] && cp -a "$CONFIG_DIR/system.xml" "$BACKUP/"
chown -R jellyfin:jellyfin "$BACKUP"

# Plugin repository: lets Jellyfin show the plugin details (otherwise "Une erreur s'est produite lors
# de la récupération des détails du plugin depuis le dépôt") and offer updates.
if [ -f "$CONFIG_DIR/system.xml" ]; then
    python3 -I - "$CONFIG_DIR/system.xml" "$REPO_NAME" "$REPO_URL" <<'PYEOF'
import re, sys
path, name, url = sys.argv[1:4]
text = open(path, encoding="utf-8").read()
if "<Url>" + url + "</Url>" in text:
    print("==> Dépôt de plugins JellyUSB déjà déclaré")
    sys.exit(0)
entry = ("    <RepositoryInfo>\n      <Name>%s</Name>\n      <Url>%s</Url>\n"
         "      <Enabled>true</Enabled>\n    </RepositoryInfo>\n" % (name, url))
if re.search(r"^\s*</PluginRepositories>", text, re.M):
    text = re.sub(r"^(\s*)</PluginRepositories>", lambda m: entry + m.group(1) + "</PluginRepositories>", text, count=1, flags=re.M)
elif re.search(r"<PluginRepositories\s*/>", text):
    text = re.sub(r"<PluginRepositories\s*/>", "<PluginRepositories>\n" + entry + "  </PluginRepositories>", text, count=1)
else:
    text = text.replace("</ServerConfiguration>", "  <PluginRepositories>\n" + entry + "  </PluginRepositories>\n</ServerConfiguration>", 1)
open(path, "w", encoding="utf-8").write(text)
print("==> Dépôt de plugins JellyUSB ajouté : " + url)
PYEOF
fi

info "Installation du plugin dans $DEST"
rm -rf "$PLUGINS_DIR"/JellyUSB_*
install -d -o jellyfin -g jellyfin -m 755 "$DEST"
install -o jellyfin -g jellyfin -m 644 "$DLL" "$DEST/"
[ -f "$IMAGE" ] && install -o jellyfin -g jellyfin -m 644 "$IMAGE" "$DEST/jellyusb.png"
cat > "$DEST/meta.json" <<EOF
{
  "category": "General",
  "changelog": "Voir https://github.com/webbackuper/jellyusb/releases",
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
