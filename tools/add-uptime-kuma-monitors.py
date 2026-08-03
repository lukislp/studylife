#!/usr/bin/env python3
"""Creates missing internal reachability monitors in Uptime Kuma.

Uptime Kuma has no official REST API for writing (only Socket.IO, which the
web UI itself uses) - this script uses the community package "uptime-kuma-api"
(pip install uptime-kuma-api), which cleanly wraps that protocol.

Covers the internal endpoints NOT yet covered by "StudyLife Web (internal)":
Grafana, piwatch (both directly against the service, not via ingress -
deliberately bypassing NPM/ingress-nginx for a pure app health check),
Postgres/Redis (pure TCP reachability, no real DB login needed - Uptime Kuma
does NOT need database credentials for this), as well as ingress-nginx itself.
All targets are already allowed for the uptime-kuma pod via NetworkPolicy
(k8s/12-network-policies.yaml).

Skips monitors that already exist by name (no duplicate risk when run
multiple times).

Credentials are NEVER hardcoded - either set via the environment variables
UPTIME_KUMA_URL/UPTIME_KUMA_USERNAME/UPTIME_KUMA_PASSWORD, or the script
prompts interactively (password masked via getpass).

Usage:
    pip install uptime-kuma-api
    python tools/add-uptime-kuma-monitors.py
"""
import getpass
import os
import sys

from uptime_kuma_api import UptimeKumaApi, MonitorType, Event
from uptime_kuma_api.api import _convert_monitor_input, _check_arguments_monitor


def add_monitor_with_conditions(api: UptimeKumaApi, **kwargs) -> dict:
    """Like api.add_monitor(), but also fills in the "conditions" field.

    Found live: the current PyPI version of uptime-kuma-api (1.2.1) doesn't yet know
    about the "conditions" field (newer Uptime Kuma Advanced Conditions feature) and
    never sends it - the server then rejects the INSERT with
    "SQLITE_CONSTRAINT: NOT NULL constraint failed: monitor.conditions". Builds the
    request payload via the same internal logic as add_monitor() (_build_monitor_data/
    _convert_monitor_input/_check_arguments_monitor only validate known fields, they
    don't complain about the extra key), adds "conditions": [] (empty condition list =
    "always", identical to the behavior without Advanced Conditions) and calls the same
    internal "add" method the library itself uses.
    """
    data = api._build_monitor_data(**kwargs)
    _convert_monitor_input(data)
    _check_arguments_monitor(data)
    data["conditions"] = []
    with api.wait_for_event(Event.MONITOR_LIST):
        return api._call("add", data)

MONITORS = [
    {
        "name": "Grafana (internal)",
        "type": MonitorType.HTTP,
        "url": "https://grafana.monitoring.svc.cluster.local:80/",
        "ignoreTls": True,  # internal CA, see k8s/17-grafana.yaml
        "interval": 60,
    },
    {
        "name": "piwatch (internal)",
        "type": MonitorType.HTTP,
        "url": "http://piwatch.monitoring.svc.cluster.local:80/healthz",
        "interval": 60,
    },
    {
        "name": "Postgres (internal, TCP)",
        "type": MonitorType.PORT,
        "hostname": "studylife-pg-rw.studylife-scale.svc.cluster.local",
        "port": 5432,
        "interval": 60,
    },
    {
        "name": "Redis (internal, TCP)",
        "type": MonitorType.PORT,
        "hostname": "redis-cluster.studylife-scale.svc.cluster.local",
        "port": 6379,
        "interval": 60,
    },
    {
        "name": "ingress-nginx (internal, TCP)",
        "type": MonitorType.PORT,
        "hostname": "ingress-nginx-controller.ingress-nginx.svc.cluster.local",
        "port": 80,
        "interval": 60,
    },
]


def main():
    url = os.environ.get("UPTIME_KUMA_URL") or input("Uptime Kuma URL (e.g. https://uptimekuma.home.lan): ").strip()
    username = os.environ.get("UPTIME_KUMA_USERNAME") or input("Username: ").strip()
    password = os.environ.get("UPTIME_KUMA_PASSWORD") or getpass.getpass("Password: ")

    api = UptimeKumaApi(url)
    try:
        api.login(username, password)
    except Exception as e:
        print(f"Login failed: {e}", file=sys.stderr)
        sys.exit(1)

    existing_names = {m["name"] for m in api.get_monitors()}
    print(f"Already existing monitors: {sorted(existing_names)}")

    for m in MONITORS:
        if m["name"] in existing_names:
            print(f"[skipped] '{m['name']}' already exists.")
            continue
        try:
            result = add_monitor_with_conditions(api, **m)
            print(f"[created] '{m['name']}' (monitorID={result.get('monitorID')})")
        except Exception as e:
            print(f"[FAILED]  '{m['name']}': {e}")

    api.disconnect()


if __name__ == "__main__":
    main()
