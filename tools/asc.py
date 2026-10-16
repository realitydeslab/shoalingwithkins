"""Minimal App Store Connect API helper for TestFlight: find the app, its builds, testers and groups.

Auth: ASC_KEY_ID, ASC_ISSUER_ID, and the key at ASC_KEY_PATH (default
~/.appstoreconnect/private_keys/AuthKey_<id>.p8). Key material stays outside the repo.

Usage (needs pyjwt + cryptography, e.g. `uv run --with pyjwt --with cryptography python tools/asc.py …`):
    asc.py app io.holokit.shoalingwithkin
    asc.py builds <app-id>
    asc.py groups <app-id>
    asc.py user <email>
    asc.py add-internal <app-id> <email> [--group "Shoaling testers"]
    asc.py add-external <app-id> <email> [--group "Shoaling external"]
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

import jwt

API = "https://api.appstoreconnect.apple.com/v1"


def token() -> str:
    """A 15-minute ES256 JWT for the App Store Connect API."""
    key_id = os.environ["ASC_KEY_ID"]
    issuer = os.environ["ASC_ISSUER_ID"]
    path = Path(os.environ.get("ASC_KEY_PATH", f"~/.appstoreconnect/private_keys/AuthKey_{key_id}.p8")).expanduser()
    now = int(time.time())
    return jwt.encode({"iss": issuer, "iat": now, "exp": now + 900, "aud": "appstoreconnect-v1"},
                      path.read_text(), algorithm="ES256", headers={"kid": key_id, "typ": "JWT"})


def call(method: str, path: str, body: dict[str, Any] | None = None, **query: str) -> dict[str, Any]:
    """One API request; raises SystemExit with Apple's error detail on failure."""
    url = f"{API}{path}" + (f"?{urllib.parse.urlencode(query)}" if query else "")
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data, method=method, headers={
        "Authorization": f"Bearer {token()}", "Content-Type": "application/json"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                raw = response.read()
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as e:
            if e.code >= 500 and attempt < 3:
                time.sleep(2 ** attempt * 5)
                continue
            detail = e.read().decode(errors="replace")
            raise SystemExit(f"{method} {path}: HTTP {e.code}\n{detail[:1500]}") from e
        except (urllib.error.URLError, TimeoutError) as e:
            # Network blips (timeouts, resets) are retried; a long watch must not die on one.
            if attempt == 3:
                raise SystemExit(f"{method} {path}: network error after retries: {e}") from e
            time.sleep(2 ** attempt * 5)
    raise AssertionError("unreachable")


def app_by_bundle(bundle_id: str) -> dict[str, Any] | None:
    found = call("GET", "/apps", **{"filter[bundleId]": bundle_id})["data"]
    return found[0] if found else None


def groups(app_id: str) -> list[dict[str, Any]]:
    return call("GET", f"/apps/{app_id}/betaGroups", limit="50")["data"]


def ensure_group(app_id: str, name: str, internal: bool) -> dict[str, Any]:
    """An existing group with this name, or a new one (internal groups see every build)."""
    for g in groups(app_id):
        if g["attributes"]["name"] == name:
            return g
    attributes: dict[str, Any] = {"name": name}
    if internal:
        attributes.update(isInternalGroup=True, hasAccessToAllBuilds=True)
    return call("POST", "/betaGroups", {"data": {
        "type": "betaGroups", "attributes": attributes,
        "relationships": {"app": {"data": {"type": "apps", "id": app_id}}}}})["data"]


def tester_by_email(email: str) -> dict[str, Any] | None:
    found = call("GET", "/betaTesters", **{"filter[email]": email})["data"]
    return found[0] if found else None


def add_to_group(group: dict[str, Any], email: str) -> None:
    tester = tester_by_email(email)
    if tester is None:
        call("POST", "/betaTesters", {"data": {
            "type": "betaTesters", "attributes": {"email": email},
            "relationships": {"betaGroups": {"data": [{"type": "betaGroups", "id": group["id"]}]}}}})
    else:
        call("POST", f"/betaGroups/{group['id']}/relationships/betaTesters",
             {"data": [{"type": "betaTesters", "id": tester["id"]}]})
    print(f"{email} is in beta group '{group['attributes']['name']}' ({group['id']})")


def install_profile(bundle_id: str, certificate_id: str, name: str) -> None:
    """Create or reuse an App Store profile and write it where Xcode looks for profiles."""
    bundles = [b for b in call("GET", "/bundleIds", **{"filter[identifier]": bundle_id})["data"]
               if b["attributes"]["identifier"] == bundle_id]
    if not bundles:
        raise SystemExit(f"Register {bundle_id} first (bundle-id --register).")
    existing = [p for p in call("GET", "/profiles", **{"filter[name]": name})["data"]
                if p["attributes"]["profileState"] == "ACTIVE"]
    profile = existing[0] if existing else call("POST", "/profiles", {"data": {
        "type": "profiles", "attributes": {"name": name, "profileType": "IOS_APP_STORE"},
        "relationships": {
            "bundleId": {"data": {"type": "bundleIds", "id": bundles[0]["id"]}},
            "certificates": {"data": [{"type": "certificates", "id": certificate_id}]}}}})["data"]
    attributes = profile["attributes"]
    content = base64.b64decode(attributes["profileContent"])
    for folder in ("~/Library/MobileDevice/Provisioning Profiles", "~/Library/Developer/Xcode/UserData/Provisioning Profiles"):
        path = Path(folder).expanduser()
        path.mkdir(parents=True, exist_ok=True)
        (path / f"{attributes['uuid']}.mobileprovision").write_bytes(content)
    print(json.dumps({"name": attributes["name"], "uuid": attributes["uuid"], "expires": attributes["expirationDate"]}))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("app").add_argument("bundle_id")
    sub.add_parser("builds").add_argument("app_id")
    sub.add_parser("groups").add_argument("app_id")
    sub.add_parser("user").add_argument("email")
    p = sub.add_parser("bundle-id", help="find the bundle id; --register NAME creates it (iOS)")
    p.add_argument("identifier")
    p.add_argument("--register", metavar="NAME")
    p = sub.add_parser("profile", help="create (or reuse) an IOS_APP_STORE profile and install it for Xcode")
    p.add_argument("bundle_id")
    p.add_argument("certificate_id", help="Apple Distribution certificate id (e.g. from /certificates)")
    p.add_argument("--name", required=True)
    p = sub.add_parser("create-app", help="create the App Store Connect app record")
    p.add_argument("bundle_id")
    p.add_argument("name")
    p.add_argument("sku")
    for name, default in (("add-internal", "Shoaling testers"), ("add-external", "Shoaling external")):
        p = sub.add_parser(name)
        p.add_argument("app_id")
        p.add_argument("email")
        p.add_argument("--group", default=default)
    args = parser.parse_args()

    if args.command == "app":
        app = app_by_bundle(args.bundle_id)
        print(json.dumps(app and {"id": app["id"], **{k: app["attributes"][k] for k in ("name", "bundleId", "sku")}}))
    elif args.command == "builds":
        for b in call("GET", "/builds", **{"filter[app]": args.app_id, "sort": "-uploadedDate", "limit": "5"})["data"]:
            a = b["attributes"]
            print(a["version"], a["processingState"], a["uploadedDate"], "expired" if a["expired"] else "")
    elif args.command == "groups":
        for g in groups(args.app_id):
            a = g["attributes"]
            print(g["id"], a["name"], "internal" if a["isInternalGroup"] else "external", a.get("publicLink") or "")
    elif args.command == "bundle-id":
        found = call("GET", "/bundleIds", **{"filter[identifier]": args.identifier})["data"]
        exact = [b for b in found if b["attributes"]["identifier"] == args.identifier]
        if not exact and args.register:
            exact = [call("POST", "/bundleIds", {"data": {"type": "bundleIds", "attributes": {
                "identifier": args.identifier, "name": args.register, "platform": "IOS"}}})["data"]]
        print(json.dumps([{"id": b["id"], **b["attributes"]} for b in exact]))
    elif args.command == "profile":
        install_profile(args.bundle_id, args.certificate_id, args.name)
    elif args.command == "create-app":
        bundles = [b for b in call("GET", "/bundleIds", **{"filter[identifier]": args.bundle_id})["data"]
                   if b["attributes"]["identifier"] == args.bundle_id]
        if not bundles:
            raise SystemExit(f"Register {args.bundle_id} first (bundle-id --register).")
        app = call("POST", "/apps", {"data": {"type": "apps", "attributes": {
            "name": args.name, "primaryLocale": "en-US", "sku": args.sku, "bundleId": args.bundle_id},
            "relationships": {"bundleId": {"data": {"type": "bundleIds", "id": bundles[0]["id"]}}}}})["data"]
        print(json.dumps({"id": app["id"], **{k: app["attributes"][k] for k in ("name", "bundleId", "sku")}}))
    elif args.command == "user":
        users = call("GET", "/users", **{"filter[username]": args.email})["data"]
        print(json.dumps([{"id": u["id"], "roles": u["attributes"]["roles"]} for u in users]))
    else:
        add_to_group(ensure_group(args.app_id, args.group, internal=args.command == "add-internal"), args.email)
    return 0


if __name__ == "__main__":
    sys.exit(main())
