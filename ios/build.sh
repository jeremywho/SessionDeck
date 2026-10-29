#!/bin/zsh
# Build, test and install the iPhone companion from the Mac Studio over SSH.
#   ios/build.sh test      unit tests on a simulator
#   ios/build.sh build     device build (signed with SD_TEAM, default PCWH4GSLHZ)
#   ios/build.sh install   build + install + launch on the iPhone (SD_UDID, default: the first paired iPhone)
set -euo pipefail
cd "$(dirname "$0")"

SD_TEAM="${SD_TEAM:-PCWH4GSLHZ}"
BUNDLE_ID="com.jeremywho.sessiondeck"
DERIVED="$PWD/build"
PROJECT="SessionDeckMobile.xcodeproj"

unlock_keychain() {
  if [[ -f "$HOME/.claude-keychain-pw" ]]; then
    security unlock-keychain -p "$(cat "$HOME/.claude-keychain-pw")" "$HOME/Library/Keychains/login.keychain-db" || true
  fi
}

generate() { xcodegen generate --quiet; }

device_udid() {
  if [[ -n "${SD_UDID:-}" ]]; then echo "$SD_UDID"; return; fi
  xcrun devicectl list devices 2>/dev/null | awk '/iPhone/ && /available/ { for (i = 1; i <= NF; i++) if ($i ~ /^[0-9A-F-]{36}$/) { print $i; exit } }'
}

simulator_udid() {
  xcrun simctl list devices available -j | python3 -c '
import json, sys
d = json.load(sys.stdin)["devices"]
for runtime, devices in sorted(d.items(), reverse=True):
    if "iOS" not in runtime: continue
    for dev in devices:
        if dev["isAvailable"] and dev["name"].startswith("iPhone"):
            print(dev["udid"]); sys.exit(0)
sys.exit(1)'
}

case "${1:-install}" in
  test)
    generate
    SIM="$(simulator_udid)"
    xcodebuild test -project "$PROJECT" -scheme SessionDeckMobile -configuration Debug \
      -destination "platform=iOS Simulator,id=$SIM" -derivedDataPath "$DERIVED" \
      CODE_SIGNING_ALLOWED=NO -quiet 2>&1 | grep -E "error:|Test Suite|Executed|passed|failed|BUILD" || true
    ;;
  build|install)
    generate
    unlock_keychain
    UDID="$(device_udid)"
    [[ -n "$UDID" ]] || { echo "no iPhone paired; set SD_UDID"; exit 1; }
    xcodebuild build -project "$PROJECT" -scheme SessionDeckMobile -configuration Debug \
      -destination "id=$UDID" -derivedDataPath "$DERIVED" \
      -allowProvisioningUpdates -allowProvisioningDeviceRegistration DEVELOPMENT_TEAM="$SD_TEAM" -quiet 2>&1 | grep -E "error:|warning: .*deprecated|BUILD" || true
    APP="$DERIVED/Build/Products/Debug-iphoneos/SessionDeckMobile.app"
    [[ -d "$APP" ]] || { echo "build produced no app"; exit 1; }
    if [[ "${1:-install}" == "install" ]]; then
      xcrun devicectl device install app --device "$UDID" "$APP"
      xcrun devicectl device process launch --device "$UDID" --terminate-existing "$BUNDLE_ID"
    fi
    ;;
  *)
    echo "usage: ios/build.sh test|build|install"; exit 2 ;;
esac
