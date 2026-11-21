import fcntl
import hashlib
import json
import os
import re
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen


REPO = "CakingGG/rustdesk"
ROOT = Path("/var/lib/rustdesk-updates/public")
NAMES = ("phone-workspace-x64.msi", "phone-workspace-arm64.apk")
SLOTS = (".slot-a", ".slot-b")


def fetch(url):
    return urlopen(Request(url, headers={"User-Agent": "RustDesk-phone-workspace-mirror"}), timeout=30)


def latest():
    page = 1
    build, release = 0, None
    while True:
        with fetch("https://api.github.com/repos/{}/releases?per_page=100&page={}".format(REPO, page)) as response:
            releases = json.load(response)
        for item in releases:
            match = re.fullmatch(r"phone-workspace-([1-9][0-9]*)", item["tag_name"])
            if match and not item["draft"] and int(match[1]) > build:
                build, release = int(match[1]), item
        if len(releases) < 100:
            break
        page += 1
    if release is None:
        raise ValueError("No published phone workspace build")
    assets = {}
    for asset in release["assets"]:
        if asset["name"] in NAMES and asset["state"] == "uploaded":
            digest = re.fullmatch(r"sha256:([0-9a-f]{64})", asset.get("digest") or "")
            if digest and asset["size"] > 0:
                assets[asset["name"]] = {"size": asset["size"], "sha256": digest[1]}
    if set(assets) != set(NAMES):
        raise ValueError("GitHub has not published both verified installers")
    return {"build": build, "assets": assets}


def download(build, file, expected):
    url = "https://github.com/{}/releases/download/phone-workspace-{}/{}".format(REPO, build, file.name)
    digest = hashlib.sha256()
    with fetch(url) as source, file.open("wb") as target:
        while True:
            chunk = source.read(256 * 1024)
            if not chunk:
                break
            if target.tell() + len(chunk) > expected["size"]:
                raise ValueError("Oversized " + file.name)
            target.write(chunk)
            digest.update(chunk)
        if target.tell() != expected["size"] or digest.hexdigest() != expected["sha256"]:
            raise ValueError("Installer verification failed for " + file.name)


def clear(slot):
    for alias in slot.iterdir():
        if alias.is_symlink() and re.fullmatch(r"[0-9a-f]{64}-phone-workspace-(x64\.msi|arm64\.apk)", alias.name):
            alias.unlink()
    for name in NAMES + ("latest.json",):
        file = slot / name
        if file.exists():
            file.unlink()


def sync():
    live = ROOT / "rustdesk"
    active = os.readlink(str(live)) if live.is_symlink() else None
    if active not in (None,) + SLOTS or (active is None and live.exists()):
        raise ValueError("Unexpected download directory")
    next_slot = ROOT / (SLOTS[1] if active == SLOTS[0] else SLOTS[0])
    next_slot.mkdir(exist_ok=True)
    clear(next_slot)
    release = latest()
    if active:
        published = json.loads((live / "latest.json").read_text())
        if published == release or published["build"] > release["build"]:
            return
    try:
        for name in NAMES:
            asset = release["assets"][name]
            download(release["build"], next_slot / name, asset)
            (next_slot / (asset["sha256"] + "-" + name)).symlink_to(name)
        (next_slot / "latest.json").write_text(json.dumps(release) + "\n")
        link = ROOT / ".next"
        if link.is_symlink():
            link.unlink()
        link.symlink_to(next_slot.name)
        os.replace(str(link), str(live))
    finally:
        # Keep open downloads valid and discard only this mirror's inactive files.
        if live.is_symlink() and os.readlink(str(live)) == next_slot.name:
            if active:
                clear(ROOT / active)
        else:
            clear(next_slot)
    print("Published phone workspace #{}".format(release["build"]))


if __name__ == "__main__":
    with (ROOT / ".sync.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            sync()
        except HTTPError as error:
            raise SystemExit("GitHub returned HTTP {}".format(error.code))
