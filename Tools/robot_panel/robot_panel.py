#!/usr/bin/env python3
"""Robot control panel for iona: start/stop launch files and call services from a browser.

Open http://<robot>:8090 in the Quest browser (over the GlobalProtect VPN) or any lab machine.
Only VPN, WPI and local addresses are served; everything else gets 403.

Launches run detached (own session), so they keep running if the panel restarts. Status and
Stop work on launches started from a terminal too, matched by their launch file name.
Stop sends Ctrl+C (SIGINT) to roslaunch, so it shuts its nodes down cleanly.

Edit LAUNCHES / SERVICES below to add buttons. Restart: pkill -f robot_panel.py, then
  setsid python3 ~/robot_panel/robot_panel.py >> ~/robot_panel/panel.log 2>&1 &
Autostart: crontab -l (an @reboot line).
"""

import hmac
import html
import ipaddress
import json
import os
import secrets
import signal
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

PORT = 8090
WORKSPACE = Path.home() / "catkin_workspaces" / "iona_devel_ws"
LOG_DIR = Path.home() / "robot_panel" / "logs"
# Access key: open http://<robot>:8090/?key=<key>. Delete the file and restart for a new key.
KEY_FILE = Path.home() / "robot_panel" / "key.txt"
ROS_ENV = (
    "source /opt/ros/noetic/setup.bash && "
    f"source {WORKSPACE}/devel/setup.bash && "
    "export ROS_MASTER_URI=http://localhost:11311 ROS_HOSTNAME=freight1943 && "
)
ALLOWED_NETWORKS = [
    ipaddress.ip_network(net)
    for net in (
        "127.0.0.0/8",
        "10.0.0.0/8",
        "130.215.0.0/16",
        "192.168.0.0/16",
        "172.16.0.0/12",
    )
]
STOP_GRACE_SECONDS = 20
SERVICE_TIMEOUT_SECONDS = 15
LOG_TAIL_LINES = 80

# key, label, roslaunch arguments (the last one is the launch file, used to find the process)
LAUNCHES = [
    ("head", "Head: camera + Unity endpoint", "hirolab_iona_demos iona_head.launch"),
    ("controls", "Controls", "hirolab_iona_demos iona_controls.launch"),
    (
        "kinovas",
        "Kinova arms + camera stream",
        "hirolab_iona_demos iona_kinovas.launch",
    ),
]

# key, label, rosservice call arguments, ask for confirmation
SERVICES = [
    ("aux1_on", "Aux 1 ON", '/aux_breaker_1 "enable: true"', False),
    ("aux1_off", "Aux 1 OFF", '/aux_breaker_1 "enable: false"', True),
    ("base_on", "Base breaker ON", "/base_breaker true", True),
    ("base_off", "Base breaker OFF", "/base_breaker false", True),
    ("torso_home", "Torso home", '/chest_control/home "{}"', True),
]

last_results = {}
results_lock = threading.Lock()


def launch_file(arguments):
    return arguments.split()[-1]


def find_launch_pids(arguments):
    pattern = f"roslaunch .*{launch_file(arguments)}"
    found = subprocess.run(["pgrep", "-f", pattern], capture_output=True, text=True)
    return [int(pid) for pid in found.stdout.split()]


def start_launch(key, arguments):
    if find_launch_pids(arguments):
        return "already running"
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    log = open(LOG_DIR / f"{key}.log", "a")
    log.write(f"\n===== started {time.ctime()} =====\n")
    log.flush()
    subprocess.Popen(
        ["bash", "-c", ROS_ENV + f"exec roslaunch {arguments}"],
        cwd=WORKSPACE,
        stdout=log,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        start_new_session=True,
    )
    return "starting"


def stop_launch(arguments):
    pids = find_launch_pids(arguments)
    if not pids:
        return "not running"
    for pid in pids:
        os.kill(pid, signal.SIGINT)

    def escalate():
        deadline = time.time() + STOP_GRACE_SECONDS
        while time.time() < deadline and find_launch_pids(arguments):
            time.sleep(1)
        for pid in find_launch_pids(arguments):
            os.kill(pid, signal.SIGTERM)

    threading.Thread(target=escalate, daemon=True).start()
    return "stopping"


def call_service(key, label, arguments):
    try:
        done = subprocess.run(
            ["bash", "-c", ROS_ENV + f"rosservice call {arguments}"],
            capture_output=True,
            text=True,
            timeout=SERVICE_TIMEOUT_SECONDS,
        )
        output = (done.stdout + done.stderr).strip() or "(no output)"
        status = "ok" if done.returncode == 0 else f"failed (exit {done.returncode})"
    except subprocess.TimeoutExpired:
        output, status = f"no answer within {SERVICE_TIMEOUT_SECONDS} s", "timeout"
    with results_lock:
        last_results[key] = {
            "label": label,
            "status": status,
            "output": output,
            "time": time.strftime("%H:%M:%S"),
        }


def tail_log(key):
    path = LOG_DIR / f"{key}.log"
    if not path.exists():
        return "(no log: started outside the panel or not started yet)"
    lines = path.read_text(errors="replace").splitlines()
    return "\n".join(lines[-LOG_TAIL_LINES:])


def status_snapshot():
    launches = {
        key: bool(find_launch_pids(arguments)) for key, _, arguments in LAUNCHES
    }
    with results_lock:
        results = dict(last_results)
    return {"launches": launches, "results": results}


PAGE = """<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>iona panel</title><style>
body{font-family:sans-serif;background:#111;color:#eee;margin:0;padding:16px;max-width:900px}
h1{font-size:28px;margin:0 0 12px} h2{font-size:22px;margin:24px 0 8px;color:#aaa}
.row{display:flex;align-items:center;gap:12px;margin:10px 0;flex-wrap:wrap}
.name{flex:1;min-width:220px;font-size:22px}
.dot{display:inline-block;width:18px;height:18px;border-radius:50%;background:#555;margin-right:8px}
.on{background:#2ecc40} button{font-size:22px;padding:18px 26px;border:0;border-radius:10px;cursor:pointer}
.start{background:#2e7d32;color:#fff} .stop{background:#c62828;color:#fff} .log{background:#444;color:#fff}
.svc{background:#1565c0;color:#fff;margin:6px} pre{background:#000;padding:10px;white-space:pre-wrap;font-size:14px;max-height:360px;overflow:auto}
#msg{font-size:18px;color:#ffca28;min-height:24px}
</style></head><body><h1>iona robot panel</h1><div id="msg"></div>
<h2>Launches</h2>LAUNCH_ROWS
<h2>Services</h2><div>SERVICE_BUTTONS</div><pre id="results"></pre>
<h2>Log</h2><pre id="log">Tap Log on a launch to see its output.</pre>
<script>
const H = {"X-Panel-Key": new URLSearchParams(location.search).get("key") || ""};
async function post(path, label, ask){
  if(ask && !confirm(label + "?")) return;
  document.getElementById("msg").textContent = label + "...";
  const r = await fetch(path, {method:"POST", headers:H}); const t = await r.text();
  document.getElementById("msg").textContent = label + ": " + t; refresh();
}
async function showLog(key){
  const r = await fetch("/log/" + key, {headers:H}); document.getElementById("log").textContent = await r.text();
}
async function refresh(){
  const s = await (await fetch("/status", {headers:H})).json();
  for (const [k, on] of Object.entries(s.launches)){
    document.getElementById("dot-" + k).className = "dot" + (on ? " on" : "");
    document.getElementById("state-" + k).textContent = on ? "running" : "stopped";
  }
  document.getElementById("results").textContent = Object.values(s.results)
    .map(r => `[${r.time}] ${r.label}: ${r.status}\\n${r.output}`).join("\\n\\n") || "No service calls yet.";
}
refresh(); setInterval(refresh, 3000);
</script></body></html>"""


def render_page():
    launch_rows = "".join(
        f'<div class="row"><span class="name"><span id="dot-{key}" class="dot"></span>{html.escape(label)}'
        f' <small id="state-{key}"></small></span>'
        f"<button class=\"start\" onclick=\"post('/start/{key}', 'Start {html.escape(label)}', false)\">Start</button>"
        f"<button class=\"stop\" onclick=\"post('/stop/{key}', 'Stop {html.escape(label)}', true)\">Stop</button>"
        f'<button class="log" onclick="showLog(\'{key}\')">Log</button></div>'
        for key, label, _ in LAUNCHES
    )
    service_buttons = "".join(
        f"<button class=\"svc\" onclick=\"post('/call/{key}', '{html.escape(label)}', {str(ask).lower()})\">"
        f"{html.escape(label)}</button>"
        for key, label, _, ask in SERVICES
    )
    return PAGE.replace("LAUNCH_ROWS", launch_rows).replace(
        "SERVICE_BUTTONS", service_buttons
    )


class PanelHandler(BaseHTTPRequestHandler):
    def client_allowed(self):
        address = ipaddress.ip_address(self.client_address[0])
        if not any(address in network for network in ALLOWED_NETWORKS):
            return False
        url = urlsplit(self.path)
        given = parse_qs(url.query).get("key", [""])[0] or self.headers.get(
            "X-Panel-Key", ""
        )
        self.route = url.path
        return hmac.compare_digest(given, PANEL_KEY)

    def reply(self, code, body, content_type="text/plain; charset=utf-8"):
        data = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if not self.client_allowed():
            return self.reply(403, "forbidden")
        if self.route == "/":
            return self.reply(200, render_page(), "text/html; charset=utf-8")
        if self.route == "/status":
            return self.reply(200, json.dumps(status_snapshot()), "application/json")
        if self.route.startswith("/log/"):
            key = self.route[len("/log/") :]
            if key in {k for k, _, _ in LAUNCHES}:
                return self.reply(200, tail_log(key))
        self.reply(404, "not found")

    def do_POST(self):
        if not self.client_allowed():
            return self.reply(403, "forbidden")
        parts = self.route.strip("/").split("/")
        if len(parts) == 2:
            action, key = parts
            launches = {k: arguments for k, _, arguments in LAUNCHES}
            services = {k: (label, arguments) for k, label, arguments, _ in SERVICES}
            if action == "start" and key in launches:
                return self.reply(200, start_launch(key, launches[key]))
            if action == "stop" and key in launches:
                return self.reply(200, stop_launch(launches[key]))
            if action == "call" and key in services:
                label, arguments = services[key]
                call_service(key, label, arguments)
                return self.reply(200, last_results[key]["status"])
        self.reply(404, "not found")

    def log_message(self, format, *args):
        print(f"{self.client_address[0]} {format % args}", flush=True)


def load_or_create_key():
    if KEY_FILE.exists():
        return KEY_FILE.read_text().strip()
    KEY_FILE.parent.mkdir(parents=True, exist_ok=True)
    key = secrets.token_hex(3)
    KEY_FILE.write_text(key + "\n")
    KEY_FILE.chmod(0o600)
    return key


PANEL_KEY = load_or_create_key()

if __name__ == "__main__":
    print(f"robot panel on port {PORT}, {time.ctime()}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), PanelHandler).serve_forever()
