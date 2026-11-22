#!/usr/bin/env python3
"""The Android TV app gets a "MediaID" tile in My media; nothing changes for the other clients.

    tvview-checks.py <instance> <media>

Needs a running test instance (test-server.py start) with the test media of make-media.sh.
"""
import importlib.util
import json
import os
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("ts", os.path.join(HERE, "test-server.py"))
ts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ts)


def main():
    instance = sys.argv[1]
    token = ts.creds(instance)["token"]
    atv = f'MediaBrowser Client="Jellyfin for Android TV", Device="tv", DeviceId="jellyusb-tvview", Version="0.20.0", Token="{token}"'
    web = f'MediaBrowser Client="Jellyfin Web", Device="web", DeviceId="jellyusb-tvview-web", Version="12.4.0", Token="{token}"'
    failures = []

    def check(label, ok, detail=""):
        print(("ok    " if ok else "FAIL  ") + label + ("" if ok else f"  [{detail}]"))
        if not ok:
            failures.append(label)

    def get(path, auth=atv, raw=False):
        req = urllib.request.Request(ts.URL + path, headers={"Authorization": auth} if auth else {})
        with urllib.request.urlopen(req, timeout=60) as r:
            body = r.read()
            return (r.headers.get("Content-Type"), body) if raw else json.loads(body)

    user_id = ts.call("GET", "/Users/Me", token=token)["Id"]
    views = get(f"/UserViews?userId={user_id}")["Items"]
    tile = next((v for v in views if v.get("CollectionType") == "boxsets" and v["Name"] == "MediaID"), None)
    check("Android TV: a MediaID tile after the libraries", tile is not None and views[-1] is tile, [v["Name"] for v in views])
    check("web client: no tile", all(v["Name"] != "MediaID" for v in get(f"/UserViews?userId={user_id}", web)["Items"]))
    if not tile:
        return 1

    media = get(f"/Items?ParentId={tile['Id']}&IncludeItemTypes=BoxSet&Recursive=true&Fields=Overview&userId={user_id}")
    names = [m["Name"] for m in media["Items"]]
    check("one collection per medium", "MOVIES98" in names and "MOVIES97" in names and media["TotalRecordCount"] == len(names), names)
    m98 = next(m for m in media["Items"] if m["Name"] == "MOVIES98")
    check("collection text shows the state", m98["Overview"].startswith("🟢 Disque **MOVIES98** branché"), m98["Overview"])

    movies = get(f"/Items?ParentId={m98['Id']}&IncludeItemTypes=Movie&StartIndex=0&Limit=100&Fields=Overview&userId={user_id}")
    real = ts.call("GET", f"/Items?userId={user_id}&Recursive=true&IncludeItemTypes=Movie&Fields=Path", token=token)["Items"]
    on98 = sorted(i["Id"] for i in real if "/MOVIES98/" in (i.get("Path") or ""))
    check("the collection holds the real movies of the medium", sorted(i["Id"] for i in movies["Items"]) == on98, len(movies["Items"]))
    check("no series, no others", get(f"/Items?ParentId={m98['Id']}&IncludeItemTypes=Series&userId={user_id}")["TotalRecordCount"] == 0)

    item = get(f"/Items/{m98['Id']}?userId={user_id}")
    check("collection details", item["Type"] == "BoxSet" and item["Name"] == "MOVIES98")
    ctype, body = get(f"/Items/{m98['Id']}/Images/Primary?maxWidth=400", auth=None, raw=True)
    check("collection image is a PNG", ctype == "image/png" and body[:8] == b"\x89PNG\r\n\x1a\n", ctype)
    ctype, body = get(f"/Items/{tile['Id']}/Images/Primary", auth=None, raw=True)
    check("tile image is the plugin PNG", ctype == "image/png" and len(body) > 10000, ctype)

    print("ALL TV VIEW CHECKS PASSED" if not failures else f"{len(failures)} FAILED")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
