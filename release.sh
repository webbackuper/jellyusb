#!/usr/bin/env bash
# Publie une version de JellyUSB sur GitHub et met à jour le dépôt de plugins Jellyfin.
#
#   ./release.sh 1.0.0.0 "Première version."
#
# 1. compile (Release) et lance les tests ;
# 2. crée artifacts/jellyusb_<version>.zip (dll + meta.json + image) et son MD5 ;
# 3. ajoute la version en tête de manifest.json (le dépôt que Jellyfin lit) ;
# 4. commite, pousse, et crée la release GitHub v<version> avec le zip.
# Nécessite gh (GitHub CLI) connecté : gh auth login.
set -euo pipefail

VERSION="${1:?usage: ./release.sh <version x.y.z.w> \"<changelog>\"}"
CHANGELOG="${2:?changelog manquant}"
REPO="webbackuper/jellyusb"
TARGET_ABI="12.4.0.0"
HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$HERE"

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version attendue : x.y.z.w" >&2; exit 1; }
GH="$(command -v gh || echo "$HOME/.local/bin/gh")"
[ -x "$GH" ] || { echo "gh introuvable (GitHub CLI)" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "des modifications ne sont pas commitées" >&2; exit 1; }

echo "==> Version $VERSION"
sed -i -E "s#<(Version|AssemblyVersion|FileVersion)>[^<]*<#<\1>$VERSION<#g" Directory.Build.props
sed -i -E "s|^version: \".*\"|version: \"$VERSION\"|" build.yaml

echo "==> Compilation et tests"
dotnet build -c Release --nologo -v q
dotnet test --nologo -v q

echo "==> Paquet"
OUT="$HERE/artifacts"
STAGE="$OUT/stage"
ZIP="$OUT/jellyusb_$VERSION.zip"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp Jellyfin.Plugin.JellyUsb/bin/Release/net10.0/Jellyfin.Plugin.JellyUsb.dll "$STAGE/"
cp assets/jellyusb.png "$STAGE/"
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
python3 -I - "$STAGE/meta.json" "$VERSION" "$TARGET_ABI" "$TIMESTAMP" "$CHANGELOG" <<'EOF'
import json, sys
out, version, abi, ts, changelog = sys.argv[1:6]
json.dump({
    "category": "General", "changelog": changelog,
    "description": "Vérifie la présence du support USB (MediaID) avant la lecture et répare les caractères accentués.",
    "guid": "9f8d8889-7b20-47d5-ad9a-37807e42e4ce", "name": "JellyUSB",
    "overview": "Vérification du support USB avant lecture, réparation des accents",
    "owner": "webbackuper", "targetAbi": abi, "timestamp": ts, "version": version,
    "status": "Active", "autoUpdate": True, "imagePath": "jellyusb.png",
    "assemblies": ["Jellyfin.Plugin.JellyUsb.dll"],
}, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
EOF
rm -f "$ZIP"
(cd "$STAGE" && python3 -I -c "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],'w',zipfile.ZIP_DEFLATED); [z.write(f) for f in ('Jellyfin.Plugin.JellyUsb.dll','meta.json','jellyusb.png')]; z.close()" "$ZIP")
MD5="$(md5sum "$ZIP" | cut -d' ' -f1)"
echo "    $ZIP ($MD5)"

echo "==> manifest.json"
python3 -I tools/update-manifest.py manifest.json "$VERSION" "$TARGET_ABI" "$TIMESTAMP" "$MD5" \
    "https://github.com/$REPO/releases/download/v$VERSION/jellyusb_$VERSION.zip" "$CHANGELOG"

echo "==> Commit et publication"
git add Directory.Build.props build.yaml manifest.json
git commit -qm "Release $VERSION" -m "$CHANGELOG"
git tag -a "v$VERSION" -m "JellyUSB $VERSION"
git push origin HEAD "v$VERSION"
"$GH" release create "v$VERSION" "$ZIP" --repo "$REPO" --title "JellyUSB $VERSION" --notes "$CHANGELOG

Dépôt de plugins Jellyfin : https://raw.githubusercontent.com/$REPO/main/manifest.json"
echo "==> Terminé : https://github.com/$REPO/releases/tag/v$VERSION"
