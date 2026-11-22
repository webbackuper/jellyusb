#!/usr/bin/env python3
"""Throwaway Jellyfin instance for testing JellyUSB. Never touches the production server.

  test-server.py start   <instance> <media>   create (first time) and start the instance, install the plugin
  test-server.py stop    <instance>
  test-server.py scan    <instance>           scan the library and wait for the end
  test-server.py items   <instance>           list movies with name / path
  test-server.py api     <instance> METHOD PATH [JSON]   raw API call (prints the response)

The instance uses its own data/config/cache/log folders, port 18096 and no auto-discovery.
A test account is created with a random password, stored in <instance>/credentials.json.
"""
import json
import glob
import os
import re
import secrets
import shutil
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request

PORT = int(os.environ.get("JELLYUSB_TEST_PORT", "18096"))
URL = f"http://127.0.0.1:{PORT}"
JF_BIN = "/usr/lib/jellyfin/bin/jellyfin"
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PLUGIN_BUILD = os.path.join(REPO, "Jellyfin.Plugin.JellyUsb", "bin", "Release", "net10.0")
PLUGIN_ID = "9f8d8889-7b20-47d5-ad9a-37807e42e4ce"
CLIENT = 'MediaBrowser Client="jellyusb-test", Device="script", DeviceId="jellyusb-test-script", Version="1.0"'


def call(method, path, body=None, token=None, client=CLIENT, raw=False):
    auth = client + (f', Token="{token}"' if token else "")
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(URL + path, data=data, method=method,
                                 headers={"Authorization": auth, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            content = r.read()
            if raw:
                return r.status, content
            return json.loads(content) if content else None
    except urllib.error.HTTPError as e:
        if raw:
            return e.code, e.read()
        raise


def creds(instance):
    with open(os.path.join(instance, "credentials.json")) as f:
        return json.load(f)


def wait_up(timeout=180):
    # While starting, Jellyfin answers 503 from a temporary setup server: wait for the real one.
    for _ in range(timeout):
        try:
            status, content = call("GET", "/System/Info/Public", raw=True)
            if status == 200:
                status2, _ = call("GET", "/Branding/Configuration", raw=True)
                if status2 == 200:
                    return json.loads(content)
        except Exception:
            pass
        time.sleep(1)
    raise SystemExit("server did not start")


def plugin_version():
    """The version being built (Directory.Build.props), so the instance looks like a real install."""
    with open(os.path.join(REPO, "Directory.Build.props"), encoding="utf-8") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    return m.group(1) if m else "1.0.0.0"


def install_plugin(instance):
    version = plugin_version()
    for old in glob.glob(os.path.join(instance, "data", "plugins", "JellyUSB_*")):
        shutil.rmtree(old, ignore_errors=True)
    dest = os.path.join(instance, "data", "plugins", "JellyUSB_" + version)
    shutil.rmtree(dest, ignore_errors=True)
    os.makedirs(dest)
    shutil.copy(os.path.join(PLUGIN_BUILD, "Jellyfin.Plugin.JellyUsb.dll"), dest)
    shutil.copy(os.path.join(REPO, "assets", "jellyusb.png"), os.path.join(dest, "jellyusb.png"))
    meta = {
        "category": "General", "guid": PLUGIN_ID, "name": "JellyUSB",
        "description": "test build", "overview": "test build", "owner": "webbackuper",
        "targetAbi": "12.4.0.0", "timestamp": "2026-11-21T00:00:00Z", "version": version,
        "status": "Active", "autoUpdate": False, "imagePath": "jellyusb.png", "assemblies": ["Jellyfin.Plugin.JellyUsb.dll"],
    }
    with open(os.path.join(dest, "meta.json"), "w") as f:
        json.dump(meta, f, indent=2)


def start(instance, media):
    instance, media = os.path.abspath(instance), os.path.abspath(media)
    first = not os.path.exists(os.path.join(instance, "credentials.json"))
    for d in ("config", "data", "cache", "log"):
        os.makedirs(os.path.join(instance, d), exist_ok=True)
    net = os.path.join(instance, "config", "network.xml")
    if not os.path.exists(net):
        with open(net, "w") as f:
            f.write(f"""<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <InternalHttpPort>{PORT}</InternalHttpPort>
  <PublicHttpPort>{PORT}</PublicHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
</NetworkConfiguration>
""")
    install_plugin(instance)
    log = open(os.path.join(instance, "stdout.log"), "ab")
    # Jellyfin's temp folder is <TMPDIR>/jellyfin: never let the test instance create the server's one.
    tmpdir = os.path.join(instance, "tmp")
    os.makedirs(tmpdir, exist_ok=True)
    proc = subprocess.Popen(
        [JF_BIN, "--datadir", f"{instance}/data", "--configdir", f"{instance}/config",
         "--cachedir", f"{instance}/cache", "--logdir", f"{instance}/log",
         "--webdir", "/usr/share/jellyfin/web", "--ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg"],
        stdout=log, stderr=subprocess.STDOUT, start_new_session=True, env={**os.environ, "LANG": "fr_FR.UTF-8", "TMPDIR": tmpdir})
    with open(os.path.join(instance, "server.pid"), "w") as f:
        f.write(str(proc.pid))
    info = wait_up()
    print(f"Jellyfin {info.get('Version')} up on {URL} (pid {proc.pid})")
    if not first:
        return

    password = secrets.token_urlsafe(12)
    call("POST", "/Startup/Configuration", {"UICulture": "fr", "MetadataCountryCode": "FR", "PreferredMetadataLanguage": "fr"})
    call("GET", "/Startup/User")
    call("POST", "/Startup/User", {"Name": "test", "Password": password})
    call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
    call("POST", "/Startup/Complete")
    auth = call("POST", "/Users/AuthenticateByName", {"Username": "test", "Pw": password})
    token = auth["AccessToken"]
    with open(os.path.join(instance, "credentials.json"), "w") as f:
        json.dump({"user": "test", "password": password, "token": token, "url": URL}, f)
    os.chmod(os.path.join(instance, "credentials.json"), 0o600)

    paths = [os.path.join(media, d) for d in sorted(os.listdir(media)) if d.startswith("MOVIES")]
    options = {
        "EnableRealtimeMonitor": False,
        "LocalMetadataReaderOrder": ["Nfo"],
        "MetadataSavers": ["Nfo"],
        "PreferredMetadataLanguage": "fr",
        "MetadataCountryCode": "FR",
        "EnableChapterImageExtraction": False,
        "PathInfos": [{"Path": p} for p in paths],
        "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [], "ImageFetchers": ["Screen Grabber"]}],
    }
    q = "name=Films&collectionType=movies&refreshLibrary=false"
    call("POST", "/Library/VirtualFolders?" + q, {"LibraryOptions": options}, token=token)

    cfg = call("GET", f"/Plugins/{PLUGIN_ID}/Configuration", token=token)
    cfg["BasePath"] = media.rstrip("/") + "/"
    call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", cfg, token=token)
    print(f"Instance initialised: library Films = {paths}, JellyUSB BasePath = {cfg['BasePath']}")
    print(f"Credentials stored in {instance}/credentials.json")


def stop(instance):
    pid_file = os.path.join(instance, "server.pid")
    if not os.path.exists(pid_file):
        print("not running")
        return
    pid = int(open(pid_file).read())
    try:
        os.killpg(pid, signal.SIGTERM)
        for _ in range(30):
            os.kill(pid, 0)
            time.sleep(1)
    except ProcessLookupError:
        pass
    os.remove(pid_file)
    print("stopped")


def scan(instance):
    token = creds(instance)["token"]
    call("POST", "/Library/Refresh", token=token)
    time.sleep(2)
    for _ in range(300):
        tasks = call("GET", "/ScheduledTasks?isHidden=false", token=token)
        t = next(x for x in tasks if x["Key"] == "RefreshLibrary")
        if t["State"] == "Idle":
            print("scan:", (t.get("LastExecutionResult") or {}).get("Status"))
            return
        time.sleep(1)


def items(instance):
    token = creds(instance)["token"]
    res = call("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,OriginalTitle,Overview,Genres&SortBy=SortName", token=token)
    for i in res["Items"]:
        print(json.dumps({k: i.get(k) for k in ("Id", "Name", "OriginalTitle", "Genres", "Overview", "Path")}, ensure_ascii=False))
    print("total:", res["TotalRecordCount"])


def api(instance, method, path, body=None):
    token = creds(instance)["token"]
    status, content = call(method, path, json.loads(body) if body else None, token=token, raw=True)
    print(status)
    print(content.decode("utf-8", "replace"))


if __name__ == "__main__":
    cmd, args = sys.argv[1], sys.argv[2:]
    {"start": start, "stop": stop, "scan": scan, "items": items, "api": api}[cmd](*args)
