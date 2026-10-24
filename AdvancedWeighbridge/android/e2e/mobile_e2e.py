#!/usr/bin/env python3
"""
End-to-end test of the Android app on an emulator against a running AWMS cloud server.

The script prepares the server through its API (site, site key, a manager account that must change its password,
tickets including a manual weight, an alert, live heartbeats), then drives the installed app with adb and
UI Automator exactly as a user would:

  sign in -> forced password change -> dashboard shows LIVE -> tickets -> ticket details -> ticket PDF ->
  cancellation request -> reports -> alerts -> account -> heartbeats stop -> dashboard shows NOT LIVE

It fails on any missing screen element, on an app crash, or when the server does not record the cancellation
request. Screenshots of every step are written to --out.

Usage (the emulator must be running and the server reachable from this machine and from the emulator):
  python3 mobile_e2e.py --apk app-debug.apk --server http://localhost:5080 --device-server http://10.0.2.2:5080 \
      --admin-password '<bootstrap password>' --out out/mobile-e2e
"""
import argparse
import json
import pathlib
import re
import shlex
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone

PACKAGE = "com.advancedweighbridge.mobile.debug"
ACTIVITY = "com.advancedweighbridge.mobile.MainActivity"
SITE = "QRY"
MANAGER = "manager1"
START_PASSWORD = "StartMobile2026"
NEW_PASSWORD = "RaviMobile2026x"


def iso(t: datetime) -> str:
    return t.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def log(message: str) -> None:
    print(f"[{datetime.now().strftime('%H:%M:%S')}] {message}", flush=True)


# ---------------------------------------------------------------------------------------------------- server side

class Cloud:
    def __init__(self, base: str):
        self.base = base.rstrip("/") + "/api/v1"

    def call(self, path, body=None, token=None, key=None, method=None):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(self.base + path, data=data, method=method or ("POST" if body is not None else "GET"))
        req.add_header("Content-Type", "application/json")
        if token:
            req.add_header("Authorization", "Bearer " + token)
        if key:
            req.add_header("X-AWMS-Site-Key", key)
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                raw = r.read()
                return json.loads(raw) if raw and r.headers.get_content_type() == "application/json" else raw
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"{method or ('POST' if body is not None else 'GET')} {path} -> {e.code}: {e.read()[:300]!r}") from None


def measurement(stage, grams, at, source="Indicator", manual_reason=None, raw=None):
    return {
        "uid": str(uuid.uuid4()), "stage": stage, "source": source, "weightG": grams,
        "reportedValue": f"{grams / 1000:.1f}", "reportedUnit": "kg", "isStable": source == "Indicator",
        "unstableOverride": False, "overrideReason": None, "manualReason": manual_reason, "capturedUtc": iso(at),
        "readingAgeMs": 120 if source == "Indicator" else None, "rawText": raw, "profileName": "Example ST,GS profile" if source == "Indicator" else None,
        "profileVersion": 3 if source == "Indicator" else None, "operatingMode": "SemiAutomatic", "operatorName": "Kamala Silva",
        "correctsMeasurementUid": None, "correctionReason": None,
    }


def transaction(no, vehicle, customer, material, first, second, at, manual=False, completed=True):
    gross, tare = (max(first, second), min(first, second)) if completed else (None, None)
    m = [measurement(1, first, at - timedelta(minutes=25), raw=f"ST,GS,+{first / 1000:08.1f}kg")]
    if completed:
        m.append(measurement(2, second, at, source="Manual" if manual else "Indicator",
                             manual_reason="Indicator display failed; value read from the backup scale" if manual else None,
                             raw=None if manual else f"ST,GS,+{second / 1000:08.1f}kg"))
    return {
        "uid": str(uuid.uuid4()), "ticketNo": no, "version": 2 if completed else 1, "status": "Completed" if completed else "Pending",
        "direction": "Outbound", "operatingMode": "SemiAutomatic", "siteCode": SITE, "weighbridgeCode": "WB1", "vehicleReg": vehicle,
        "driverName": None, "customerCode": "C001", "customerName": customer, "supplierCode": None, "supplierName": None,
        "materialCode": "M34", "materialName": material, "orderNo": None, "deliveryNoteNo": "DN-4471", "referenceNo": None, "remarks": None,
        "firstG": first, "secondG": second if completed else None, "grossG": gross, "tareG": tare, "netG": (gross - tare) if completed else None,
        "firstIsGross": first > second if completed else None, "tareMethod": "Measured" if completed else None,
        "hasManualWeight": manual, "hasOverride": False, "createdBy": "kamala", "createdUtc": iso(at - timedelta(minutes=25)),
        "completedBy": "kamala" if completed else None, "completedUtc": iso(at) if completed else None,
        "cancelledBy": None, "cancelledUtc": None, "cancelReason": None, "updatedUtc": iso(at), "replacesTicketNo": None, "measurements": m,
    }


class Heartbeats(threading.Thread):
    """Sends a heartbeat with a stable live weight every 10 seconds until stopped, like a site PC."""

    def __init__(self, cloud: Cloud, key: str):
        super().__init__(daemon=True)
        self.cloud, self.key, self.stop = cloud, key, threading.Event()

    def beat(self):
        now = datetime.now(timezone.utc)
        self.cloud.call("/sync/heartbeat", {
            "siteCode": SITE, "siteName": "Kegalle quarry", "appVersion": "1.0.0", "sentUtc": iso(now),
            "weighbridges": [{"code": "WB1", "name": "Main weighbridge", "connection": "Connected", "liveG": 31240000, "stable": True,
                              "measuredUtc": iso(now), "displayStatus": "Stable", "operatingMode": "SemiAutomatic", "automationState": None,
                              "lastError": None, "pendingTransactions": 1}],
            "health": {"diskFreeGb": 182.4, "lastBackupUtc": iso(now - timedelta(hours=3)), "syncPending": 0, "syncFailed": 0,
                       "databaseProvider": "Sqlite", "warnings": []},
        }, key=self.key)

    def run(self):
        while not self.stop.is_set():
            try:
                self.beat()
            except Exception as e:  # keep beating; the test checks the result on screen
                log(f"heartbeat failed: {e}")
            self.stop.wait(10)


def prepare(cloud: Cloud, admin_password: str):
    log("Preparing the server")
    token = cloud.call("/auth/login", {"username": "admin", "password": admin_password})["accessToken"]
    me = cloud.call("/me", token=token)
    if me.get("mustChangePassword"):
        token = cloud.call("/me/password", {"currentPassword": admin_password, "newPassword": admin_password + "!"}, token=token)["accessToken"]
    key = cloud.call("/admin/sites", {"code": SITE, "name": "Kegalle quarry"}, token=token)["siteKey"]
    cloud.call("/admin/users", {"username": MANAGER, "displayName": "Ravi Kumar", "password": START_PASSWORD, "role": "Manager", "sites": [SITE]}, token=token)
    now = datetime.now(timezone.utc)
    txs = [
        # Completed a few minutes ago so that both fall into the report's "Today" (the device runs on UTC).
        transaction("QRY-2026-000101", "WP CAB-1234", "Lanka Builders (Pvt) Ltd", "Metal 3/4\"", 32460000, 12310000, now - timedelta(minutes=3)),
        transaction("QRY-2026-000102", "NC LK-5521", "Road Development Authority", "Metal 3/4\"", 24050000, 9850000, now - timedelta(minutes=2), manual=True),
        transaction("QRY-2026-000103", "SP KA-7788", "Lanka Builders (Pvt) Ltd", "Metal 3/4\"", 15390000, 0, now - timedelta(minutes=1), completed=False),
    ]
    result = cloud.call("/sync/transactions", {"items": txs}, key=key)
    assert all(r["status"] == "Accepted" for r in result["results"]), result
    manual = txs[1]
    cloud.call("/sync/notifications", {"items": [{
        "uid": str(uuid.uuid4()), "atUtc": iso(now - timedelta(minutes=5)), "severity": "Warning", "category": "ManualWeight",
        "title": "Manual weight entered", "message": "NC LK-5521: 9,850 kg entered manually by Kamala Silva.",
        "entityType": "Transaction", "entityId": manual["uid"]}]}, key=key)
    beats = Heartbeats(cloud, key)
    beats.beat()
    beats.start()
    return token, key, txs, beats


# ---------------------------------------------------------------------------------------------------- device side

class Device:
    def __init__(self, out: pathlib.Path):
        self.out = out
        self.shots = 0
        size = re.findall(r"(\d+)x(\d+)", self.adb("shell", "wm size").decode())
        self.width, self.height = map(int, size[-1])

    @staticmethod
    def adb(*args, check=True, capture=True):
        r = subprocess.run(["adb", *args], capture_output=capture, timeout=120)
        if check and r.returncode != 0:
            raise RuntimeError(f"adb {' '.join(args)} failed: {r.stderr.decode(errors='replace')}")
        return r.stdout

    def shell(self, command: str):
        return self.adb("shell", command)

    def dump(self):
        for _ in range(5):
            r = subprocess.run(["adb", "shell", "uiautomator dump /sdcard/awms-ui.xml >/dev/null && cat /sdcard/awms-ui.xml"], capture_output=True, timeout=60)
            text = r.stdout.decode("utf-8", errors="replace")
            start = text.find("<?xml")
            if start >= 0:
                try:
                    return ET.fromstring(text[start:])
                except ET.ParseError:
                    pass
            time.sleep(1)
        raise RuntimeError("UI Automator could not dump the screen")

    @staticmethod
    def nodes(root):
        return list(root.iter("node"))

    def find(self, text=None, desc=None, contains=None, root=None):
        root = root if root is not None else self.dump()
        for n in self.nodes(root):
            t, d = n.get("text", ""), n.get("content-desc", "")
            if text is not None and t.casefold() == text.casefold():
                return n
            if desc is not None and d == desc:
                return n
            if contains is not None and (contains in t or contains in d):
                return n
        return None

    def wait(self, timeout=40, **kw):
        deadline = time.time() + timeout
        while time.time() < deadline:
            n = self.find(**kw)
            if n is not None:
                return n
            self.check_alive()
            time.sleep(2)
        self.screenshot("FAILED-" + "-".join(str(v) for v in kw.values())[:40])
        raise AssertionError(f"Not shown within {timeout} s: {kw}")

    def visible_text(self):
        return "\n".join(filter(None, (n.get("text") for n in self.nodes(self.dump()))))

    @staticmethod
    def center(node):
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", node.get("bounds")))
        return (x1 + x2) // 2, (y1 + y2) // 2

    def tap(self, node):
        x, y = self.center(node)
        self.shell(f"input tap {x} {y}")
        time.sleep(0.8)

    def tap_text(self, text, timeout=30):
        self.tap(self.wait(text=text, timeout=timeout))

    def type_into(self, node, value, clear=0):
        self.tap(node)
        if clear:
            self.shell("input keyevent KEYCODE_MOVE_END " + " ".join(["KEYCODE_DEL"] * clear))
        self.shell("input text " + shlex.quote(value.replace(" ", "%s")))
        time.sleep(0.5)

    def enter(self):
        self.shell("input keyevent KEYCODE_ENTER")
        time.sleep(1)

    def scroll_to(self, timeout=30, **kw):
        deadline = time.time() + timeout
        while time.time() < deadline:
            n = self.find(**kw)
            if n is not None:
                return n
            x = self.width // 2
            self.shell(f"input swipe {x} {self.height * 3 // 4} {x} {self.height // 4} 400")
            time.sleep(1)
        raise AssertionError(f"Not found while scrolling: {kw}")

    def screenshot(self, name):
        self.shots += 1
        path = self.out / f"{self.shots:02d}-{name}.png"
        with open(path, "wb") as f:
            f.write(self.adb("exec-out", "screencap", "-p"))
        log(f"screenshot {path.name}")

    def check_alive(self):
        crashes = self.adb("logcat", "-d", "-b", "crash", check=False).decode(errors="replace")
        if PACKAGE in crashes:
            (self.out / "crash.txt").write_text(crashes)
            raise AssertionError("The app crashed:\n" + crashes[-3000:])


def run(args):
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    cloud = Cloud(args.server)
    token, key, txs, beats = prepare(cloud, args.admin_password)
    d = Device(out)

    log("Installing and starting the app")
    d.adb("install", "-r", "-g", args.apk)
    d.adb("logcat", "-c", check=False)
    d.shell(f"am start -W -n {PACKAGE}/{ACTIVITY}")

    log("Sign in")
    server = d.wait(desc="Server address", timeout=60)
    d.screenshot("login")
    d.type_into(server, args.device_server, clear=12)
    d.type_into(d.wait(desc="User name"), MANAGER)
    d.type_into(d.wait(desc="Password"), START_PASSWORD)
    d.enter()

    log("Forced password change")
    d.wait(text="Choose a new password to continue.", timeout=40)
    d.type_into(d.wait(desc="New password"), NEW_PASSWORD)
    d.type_into(d.wait(desc="Repeat new password"), NEW_PASSWORD)
    d.screenshot("change-password")
    d.enter()

    log("Dashboard with live data")
    d.wait(text="LIVE · STABLE", timeout=60)
    d.wait(text="31,240 kg")
    d.screenshot("dashboard-live")

    log("Tickets")
    d.tap_text("Tickets")
    d.wait(contains="QRY-2026-000102", timeout=40)
    assert d.find(text="MANUAL") is not None, "manual weight badge missing"
    d.screenshot("tickets")
    d.tap(d.wait(contains="QRY-2026-000101"))

    log("Ticket details and PDF")
    d.wait(text="Weighings", timeout=40)
    d.wait(text="20,150 kg")
    d.screenshot("ticket-details")
    d.tap(d.scroll_to(text="View ticket"))
    d.scroll_to(desc="Ticket page 1", timeout=60)
    d.screenshot("ticket-pdf")

    log("Cancellation request")
    d.tap(d.scroll_to(text="Request cancellation"))
    d.type_into(d.wait(desc="Reason"), "Wrong customer selected at the weighbridge")
    d.screenshot("cancel-request")
    d.tap_text("Send request")
    d.scroll_to(contains=f"Cancellation requested by {MANAGER}", timeout=40)
    detail = cloud.call(f"/transactions/{txs[0]['uid']}", token=token)
    commands = [c for c in detail["commands"] if c["type"] == "CancelTransaction"]
    assert commands and commands[0]["status"] == "Pending", detail["commands"]
    d.screenshot("cancel-pending")
    d.shell("input keyevent KEYCODE_BACK")

    log("Manual-weight ticket")
    d.tap(d.wait(contains="QRY-2026-000102", timeout=30))
    d.scroll_to(contains="MANUAL ENTRY — typed in by the operator")
    d.screenshot("manual-entry")
    d.shell("input keyevent KEYCODE_BACK")

    log("Reports, alerts and account")
    d.tap_text("Reports")
    d.wait(text="Total net weight", timeout=40)
    d.wait(text="34,350 kg")
    d.screenshot("reports")
    d.tap_text("Alerts")
    d.wait(contains="Manual weight entered", timeout=40)
    d.screenshot("alerts")
    d.tap_text("Account")
    d.wait(text="Ravi Kumar", timeout=30)
    d.screenshot("account")

    log("The site stops sending heartbeats: the dashboard must say NOT LIVE")
    beats.stop.set()
    d.tap_text("Dashboard")
    d.wait(text="NOT LIVE", timeout=180)
    assert d.find(text="LIVE · STABLE") is None, "a stale weight is still shown as live"
    d.screenshot("dashboard-not-live")

    d.check_alive()
    (out / "logcat.txt").write_bytes(d.adb("logcat", "-d", check=False))
    log("Android end-to-end test passed")


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--apk", required=True)
    p.add_argument("--server", required=True, help="server address as seen from this machine")
    p.add_argument("--device-server", required=True, help="server address as seen from the emulator")
    p.add_argument("--admin-password", required=True)
    p.add_argument("--out", default="out/mobile-e2e")
    try:
        run(p.parse_args())
    except Exception as e:
        log(f"FAILED: {e}")
        sys.exit(1)
