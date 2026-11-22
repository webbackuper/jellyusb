#!/usr/bin/env python3
"""A drive that goes away must never remove its movies from the library.

    unplug-checks.py <instance> <media>

Needs a running test instance (test-server.py start) with the test media of make-media.sh. For one
medium (MOVIES98 by default, with its movies in MOVIES98/MOVIES98/), it:
  1. counts the movies of the medium;
  2. "unplugs" it: moves its content away, leaving the empty folder an unmounted drive leaves;
  3. makes Jellyfin re-read the folder of its movies (as a scan does when the drive goes away
     after the root of the medium was read), then runs a full library scan;
  4. checks that no movie was removed, then "plugs" the drive back and checks again.
The test media are restored even when a check fails.
"""
import importlib.util
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("ts", os.path.join(HERE, "test-server.py"))
ts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ts)

MEDIA_ID = os.environ.get("JELLYUSB_UNPLUG_MEDIA", "MOVIES98")


def movies(token, user_id, root):
    r = ts.call("GET", f"/Items?userId={user_id}&Recursive=true&IncludeItemTypes=Movie&Fields=Path,ParentId", token=token)
    return [i for i in r["Items"] if (i.get("Path") or "").startswith(root + "/")]


def wait_refresh(token, seconds=20):
    # item refreshes are queued: wait until the server has been idle for a few seconds
    end = time.time() + seconds
    while time.time() < end:
        tasks = ts.call("GET", "/ScheduledTasks", token=token)
        if not any(t.get("State") == "Running" for t in tasks):
            time.sleep(3)
            return
        time.sleep(1)


def main():
    instance, media = sys.argv[1], sys.argv[2]
    c = ts.creds(instance)
    token = c["token"]
    user_id = ts.call("GET", "/Users/Me", token=token)["Id"]
    root = os.path.join(os.path.abspath(media), MEDIA_ID)
    inner = os.path.join(root, MEDIA_ID)
    parked = os.path.join(os.path.abspath(media), "." + MEDIA_ID + ".unplugged-content")
    failures = []

    def check(label, ok):
        print(("ok    " if ok else "FAIL  ") + label)
        if not ok:
            failures.append(label)

    before = movies(token, user_id, root)
    check(f"{MEDIA_ID} has movies before the test ({len(before)})", len(before) > 0)
    folder_ids = sorted({m["ParentId"] for m in before})
    if not before or not os.path.isdir(inner):
        print("nothing to test")
        return 1

    if os.path.exists(parked):
        shutil.rmtree(parked)
    os.makedirs(parked)
    try:
        # unplug: the mount point stays, empty
        for name in os.listdir(root):
            shutil.move(os.path.join(root, name), os.path.join(parked, name))

        # the folder of the movies is re-read while the drive is gone
        for fid in folder_ids:
            ts.call("POST", f"/Items/{fid}/Refresh?Recursive=true&MetadataRefreshMode=Default&ImageRefreshMode=Default", token=token)
        wait_refresh(token)
        check("re-reading the folder of an unplugged drive removes nothing",
              len(movies(token, user_id, root)) == len(before))

        ts.scan(instance)
        check("a full scan with the drive unplugged removes nothing",
              len(movies(token, user_id, root)) == len(before))
    finally:
        # plug back
        for name in os.listdir(parked):
            shutil.move(os.path.join(parked, name), os.path.join(root, name))
        os.rmdir(parked)

    ts.scan(instance)
    after = movies(token, user_id, root)
    check("the movies are still there once the drive is back", len(after) == len(before))
    check("same items (no re-import)", sorted(m["Id"] for m in after) == sorted(m["Id"] for m in before))

    print("ALL UNPLUG CHECKS PASSED" if not failures else f"{len(failures)} FAILED")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
