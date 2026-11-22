#!/usr/bin/env python3
"""The details of a movie show its disk and the state of the drive, for the Android TV app only.

    details-checks.py <instance> <media>

Needs a running test instance (test-server.py start) with the test media of make-media.sh. MOVIES98
stays plugged in; MOVIES97 is "unplugged" during the checks (renamed MOVIES97.unplugged, then
restored).
"""
import importlib.util
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("ts", os.path.join(HERE, "test-server.py"))
ts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ts)

ATV = 'MediaBrowser Client="Jellyfin for Android TV", Device="test-tv", DeviceId="jellyusb-details-tv", Version="0.19.10"'
WEB = 'MediaBrowser Client="Jellyfin Web", Device="test-web", DeviceId="jellyusb-details-web", Version="12.4.0"'


def get(path, token, client):
    auth = client + f', Token="{token}"'
    status, body = ts.call("GET", path, token=None, client=auth, raw=True)
    return status, (json.loads(body) if body else None)


def main():
    instance, media = sys.argv[1], sys.argv[2]
    token = ts.creds(instance)["token"]
    user_id = ts.call("GET", "/Users/Me", token=token)["Id"]
    movies = ts.call("GET", f"/Items?userId={user_id}&Recursive=true&IncludeItemTypes=Movie&Fields=Path,Overview", token=token)["Items"]
    by_media = {}
    for m in movies:
        for mid in ("MOVIES98", "MOVIES97"):
            # a movie whose file is there (playback-checks.py deletes one on purpose)
            if f"/{mid}/" in (m.get("Path") or "") and os.path.exists(m["Path"]):
                by_media.setdefault(mid, m)
    failures = []

    def check(label, ok, detail=""):
        print(("ok    " if ok else "FAIL  ") + label + ("" if ok else f"  [{detail}]"))
        if not ok:
            failures.append(label)

    present, absent = by_media.get("MOVIES98"), by_media.get("MOVIES97")
    check("test media: a movie on MOVIES98 and one on MOVIES97", present is not None and absent is not None)
    if not present or not absent:
        return 1

    m97 = os.path.join(os.path.abspath(media), "MOVIES97")
    os.rename(m97, m97 + ".unplugged")
    try:
        run(token, user_id, present, absent, check)
    finally:
        os.rename(m97 + ".unplugged", m97)

    print("ALL DETAILS CHECKS PASSED" if not failures else f"{len(failures)} FAILED")
    return 1 if failures else 0


def run(token, user_id, present, absent, check):
    for route in (f"/Items/{{id}}?userId={user_id}", f"/Users/{user_id}/Items/{{id}}"):
        name = route.split("{id}")[0].rstrip("/") or route
        status, dto = get(route.format(id=present["Id"]), token, ATV)
        ov = (dto or {}).get("Overview") or ""
        check(f"Android TV {name}: plugged disk shown first", status == 200 and ov.startswith("🟢 Disque **MOVIES98** branché"), ov[:80])
        check(f"Android TV {name}: the overview is kept", (present.get("Overview") or "") in ov, ov[:120])
        status, dto = get(route.format(id=absent["Id"]), token, ATV)
        ov = (dto or {}).get("Overview") or ""
        check(f"Android TV {name}: absent disk shown first", status == 200 and ov.startswith("🔴 Disque **MOVIES97** absent"), ov[:80])
        check(f"Android TV {name}: rest of the item intact", dto and dto.get("Id") == absent["Id"] and dto.get("Name") == absent["Name"])

    status, dto = get(f"/Items/{present['Id']}?userId={user_id}", token, WEB)
    check("web client: overview unchanged", status == 200 and (dto.get("Overview") or "") == (present.get("Overview") or ""), (dto or {}).get("Overview", "")[:80])

    # nothing stored: the regular listing has the plain overview
    again = ts.call("GET", f"/Items?userId={user_id}&Ids={absent['Id']}&Fields=Overview", token=token)["Items"][0]
    check("nothing stored in the database", (again.get("Overview") or "") == (absent.get("Overview") or ""))

    status, _ = get(f"/Items/{present['Id']}?userId={user_id}", "wrong-token", ATV)
    check("no token: Jellyfin's own 401", status == 401, status)


if __name__ == "__main__":
    sys.exit(main())
