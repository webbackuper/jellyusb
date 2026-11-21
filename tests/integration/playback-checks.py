#!/usr/bin/env python3
"""Checks JellyUSB's playback behaviour on the throwaway instance, for each client family.

  playback-checks.py <instance> <media>

Before running: the media made by make-media.sh, scanned. The script itself:
  - deletes "Fichier Supprime (2020).mkv"         -> file missing on a present medium
  - renames MOVIES97 to MOVIES97.unplugged        -> medium absent
and restores MOVIES97 at the end (the deleted file stays deleted).
"""
import importlib.util
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("ts", os.path.join(HERE, "test-server.py"))
ts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ts)

CLIENTS = {
    "web": ("Jellyfin Web", "10.12.0"),
    "android": ("Jellyfin for Android", "2.6.3"),
    "androidtv": ("Jellyfin for Android TV", "0.19.4"),
    "kodi": ("Kodi", "1.0.0"),
    "jellycon": ("Kodi JellyCon", "0.9.0"),
    "swiftfin": ("Swiftfin iOS", "1.3.0"),
    "findroid": ("Findroid", "0.15.0"),
}

failures = []


def check(label, condition, detail=""):
    print(("  OK   " if condition else "  FAIL ") + label + (f"  [{detail}]" if detail and not condition else ""))
    if not condition:
        failures.append(label)


def login(instance, name):
    client, version = CLIENTS[name]
    header = f'MediaBrowser Client="{client}", Device="test-{name}", DeviceId="jellyusb-{name}", Version="{version}"'
    pw = ts.creds(instance)["password"]
    token = ts.call("POST", "/Users/AuthenticateByName", {"Username": "test", "Pw": pw}, client=header)["AccessToken"]
    return header, token


def playback_info(header, token, item_id):
    body = {"IsPlayback": True, "AutoOpenLiveStream": True, "MediaSourceId": item_id}
    status, content = ts.call("POST", f"/Items/{item_id}/PlaybackInfo", body, token=token, client=header, raw=True)
    try:
        return status, json.loads(content) if content else {}
    except ValueError:
        return status, {"text": content.decode("utf-8", "replace")}


def main(instance, media):
    token = ts.creds(instance)["token"]
    deleted_path = os.path.join(media, "MOVIES98", "MOVIES98", "Fichier Supprime (2020).mkv")

    def list_items():
        return {i["Name"]: i for i in ts.call("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path", token=token)["Items"]}

    items = list_items()
    if "Fichier Supprime" not in items:
        # Removed by a previous scan: recreate it and scan again.
        subprocess.run(["/usr/lib/jellyfin-ffmpeg/ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                        "-i", "testsrc2=size=320x240:duration=5", "-c:v", "libx264", "-preset", "ultrafast",
                        "-pix_fmt", "yuv420p", deleted_path], check=True)
        ts.scan(instance)
        items = list_items()
    present = items["Larguées"]["Id"]
    deleted = items["Fichier Supprime"]["Id"]
    absent = items["Le Fabuleux Destin d'Amélie Poulain"]["Id"]

    if os.path.exists(deleted_path):
        os.remove(deleted_path)
    m97 = os.path.join(media, "MOVIES97")
    os.rename(m97, m97 + ".unplugged")
    try:
        for name in CLIENTS:
            header, tok = login(instance, name)
            print(f"[{name}] {CLIENTS[name][0]}")

            status, res = playback_info(header, tok, present)
            check("present file plays", status == 200 and res.get("ErrorCode") is None and len(res.get("MediaSources", [])) == 1, f"{status} {res}")

            web = name in ("web", "android")
            status, res = playback_info(header, tok, absent)
            if web:
                # MessageOnTop: request refused (one generic web dialog), our message pushed on top.
                check("absent medium: request refused for the web (404 + message)", status == 404, status)
            else:
                code = res.get("ErrorCode")
                check("absent medium: NotAllowed", code == "NotAllowed", code)
                check("absent medium: no media source", res.get("MediaSources") == [], res)

            status, res = playback_info(header, tok, deleted)
            if web:
                check("missing file: refused for the web", status == 404, status)
            else:
                check("missing file: NotAllowed", res.get("ErrorCode") == "NotAllowed", res)

            status, content = ts.call("GET", f"/Videos/{absent}/stream?static=true&mediaSourceId={absent}", token=tok, client=header, raw=True)
            check("absent medium: stream answers 404 at once", status == 404 and "MOVIES97" in content.decode(), f"{status} {content[:80]}")

            status, content = ts.call("GET", f"/Videos/{present}/stream?static=true&mediaSourceId={present}", token=tok, client=header, raw=True)
            check("present file: stream 200", status == 200, status)

        # Unauthenticated request: JellyUSB must not answer (no title leak), Jellyfin returns 401.
        status, content = ts.call("POST", f"/Items/{absent}/PlaybackInfo", {}, client='MediaBrowser Client="x", Device="x", DeviceId="x", Version="1"', raw=True)
        check("unauthenticated request is left to Jellyfin (401)", status == 401, status)
    finally:
        os.rename(m97 + ".unplugged", m97)

    print()
    print("FAILED: " + ", ".join(failures) if failures else "ALL CHECKS PASSED")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main(os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2]))
