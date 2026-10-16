"""Build, archive, sign and upload Shoaling With Kin to TestFlight (pattern from bodyoracle/tools/testflight.py).

Steps: Unity scene/player setup (stamps SHOALING_BUILD_NUMBER, writes the OpenRouter key from
OPENROUTER_API_KEY) -> Unity iOS export -> xcodebuild archive -> xcodebuild -exportArchive with
destination=upload. `-allowProvisioningUpdates` lets Xcode create the distribution certificate and
App Store profile. With ASC_KEY_ID / ASC_ISSUER_ID set, xcodebuild authenticates with that App
Store Connect API key; otherwise it uses the Apple account signed into Xcode.

The App Store Connect app record for the bundle id must exist (Apple does not allow creating it
through the API): check with `tools/asc.py app org.realitydeslab.shoalingwithkin`.

Usage:
    python3 tools/testflight.py --build-number 1
    python3 tools/testflight.py --build-number 2 --skip-unity      # re-archive the existing export
    python3 tools/testflight.py --build-number 1 --skip-upload     # signed ipa only
    python3 tools/testflight.py --build-number 1 --skip-archive --profile "Shoaling With Kin App Store"
"""

from __future__ import annotations

import argparse
import os
import plistlib
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = ROOT / "app"
UNITY = "/Applications/Unity/6000.3.24f1/Unity.app/Contents/MacOS/Unity"
TEAM = "KR9H35SQQ9"
BUNDLE_ID = "org.realitydeslab.shoalingwithkin"
EXPORT = APP / "Builds/iOS"
ARCHIVE = APP / "Builds/ShoalingWithKin.xcarchive"
EXPORTED = APP / "Builds/iOS-export"
LOGS = APP / "Logs/testflight"


def run(command: list[str], log: Path, env: dict[str, str] | None = None) -> None:
    """Run one step with its output in a log file; on failure show the log's tail."""
    print("Running:", " ".join(command[:6]), "…" if len(command) > 6 else "", f"(log: {log})", flush=True)
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open("w") as stream:
        result = subprocess.run(command, check=False, cwd=APP, stdout=stream, stderr=subprocess.STDOUT, env={**os.environ, **(env or {})})
    if result.returncode != 0:
        tail = log.read_text(errors="replace").splitlines()[-40:]
        raise SystemExit(f"FAILED (exit {result.returncode}): {' '.join(command[:4])}\n" + "\n".join(tail))


def auth_args() -> list[str]:
    """App Store Connect API key arguments for xcodebuild, when the key is configured."""
    key_id, issuer = os.environ.get("ASC_KEY_ID"), os.environ.get("ASC_ISSUER_ID")
    if not key_id or not issuer:
        return []
    path = Path(os.environ.get("ASC_KEY_PATH", f"~/.appstoreconnect/private_keys/AuthKey_{key_id}.p8")).expanduser()
    return ["-authenticationKeyPath", str(path), "-authenticationKeyID", key_id, "-authenticationKeyIssuerID", issuer]


def export_options(upload: bool, profile: str | None) -> Path:
    """Automatic signing by default; with `profile`, manual signing with the Apple Distribution
    certificate in the keychain (`tools/asc.py profile …` creates and installs the profile)."""
    options = {
        "method": "app-store-connect",
        "teamID": TEAM,
        "signingStyle": "automatic",
        "uploadSymbols": True,
        "manageAppVersionAndBuildNumber": False,
        "destination": "upload" if upload else "export",
    }
    if profile:
        options.update(signingStyle="manual", signingCertificate="Apple Distribution",
                       provisioningProfiles={BUNDLE_ID: profile})
    path = LOGS / "ExportOptions.plist"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(plistlib.dumps(options))
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--build-number", required=True, help="CFBundleVersion; must be new for this version")
    parser.add_argument("--skip-unity", action="store_true", help="reuse app/Builds/iOS")
    parser.add_argument("--skip-archive", action="store_true", help="reuse app/Builds/ShoalingWithKin.xcarchive")
    parser.add_argument("--skip-upload", action="store_true", help="stop at a signed ipa")
    parser.add_argument("--profile", help="App Store profile name for manual signing (when cloud signing is unavailable)")
    args = parser.parse_args()

    env = {"SHOALING_BUILD_NUMBER": args.build_number}
    if not args.skip_unity and not args.skip_archive:
        unity = [UNITY, "-batchmode", "-nographics", "-projectPath", ".", "-buildTarget", "iOS"]
        run(unity + ["-executeMethod", "ShoalingWithKinSetup.Setup", "-logFile", str(LOGS / "unity-setup.log")],
            LOGS / "unity-setup.out", env)
        run(unity + ["-executeMethod", "ShoalingWithKinSetup.BuildIOS", "-quit", "-logFile", str(LOGS / "unity-build.log")],
            LOGS / "unity-build.out", env)

    auth = auth_args()
    if not args.skip_archive:
        run(["xcodebuild", "-project", str(EXPORT / "Unity-iPhone.xcodeproj"), "-scheme", "Unity-iPhone",
             "-configuration", "Release", "-sdk", "iphoneos", "-archivePath", str(ARCHIVE),
             "-allowProvisioningUpdates", *auth, "CODE_SIGN_STYLE=Automatic", f"DEVELOPMENT_TEAM={TEAM}", "archive"],
            LOGS / "xcode-archive.log")
    run(["xcodebuild", "-exportArchive", "-archivePath", str(ARCHIVE), "-exportPath", str(EXPORTED),
         "-exportOptionsPlist", str(export_options(not args.skip_upload, args.profile)), "-allowProvisioningUpdates", *auth],
        LOGS / "xcode-export.log")
    print("PASS:", f"build {args.build_number} uploaded to App Store Connect" if not args.skip_upload else f"signed ipa in {EXPORTED}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
