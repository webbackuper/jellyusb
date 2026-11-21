#!/usr/bin/env python3
"""Adds (or replaces) a version in the Jellyfin plugin repository manifest.

    update-manifest.py manifest.json <version> <targetAbi> <timestamp> <md5> <sourceUrl> <changelog>

The manifest is the file Jellyfin reads when the repository URL
https://raw.githubusercontent.com/webbackuper/jellyusb/main/manifest.json is added in
Dashboard > Plugins > Repositories. Newest version first.
"""
import json
import os
import sys

GUID = "9f8d8889-7b20-47d5-ad9a-37807e42e4ce"


def main(path, version, abi, timestamp, md5, url, changelog):
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            manifest = json.load(f)
    else:
        manifest = []

    package = next((p for p in manifest if p.get("guid") == GUID), None)
    if package is None:
        package = {"guid": GUID, "versions": []}
        manifest.append(package)

    package.update({
        "name": "JellyUSB",
        "description": (
            "Avant chaque lecture d'un film situé sous /mnt/usb/<MediaID>/, vérifie que le support USB "
            "est branché et que le fichier existe ; sinon la lecture est bloquée et le client affiche "
            "« Insérez le support <MediaID> ». Lit aussi les NFO en Windows-1252 et répare les titres "
            "mal encodés (« � », « Ã© »)."
        ),
        "overview": "Vérification du support USB avant lecture, réparation des accents",
        "owner": "webbackuper",
        "category": "General",
        "imageUrl": "https://raw.githubusercontent.com/webbackuper/jellyusb/main/assets/jellyusb.png",
    })

    versions = [v for v in package.get("versions", []) if v.get("version") != version]
    versions.insert(0, {
        "version": version,
        "changelog": changelog,
        "targetAbi": abi,
        "sourceUrl": url,
        "checksum": md5,
        "timestamp": timestamp,
    })
    package["versions"] = versions

    with open(path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"manifest: {len(versions)} version(s), newest {version}")


if __name__ == "__main__":
    main(*sys.argv[1:8])
