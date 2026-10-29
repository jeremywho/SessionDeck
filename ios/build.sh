#!/bin/zsh
# Build, test and install the iPhone companion from the Mac Studio over SSH.
#   ios/build.sh test      unit tests on a simulator
#   ios/build.sh build     signed device build for any iPhone (SD_TEAM, default PCWH4GSLHZ)
#   ios/build.sh install   build + install + launch on the iPhone (SD_UDID, default: the iPhone 16 Pro Max)
set -euo pipefail
cd "$(dirname "$0")"

SD_TEAM="${SD_TEAM:-PCWH4GSLHZ}"
SD_UDID="${SD_UDID:-4CDC7C2B-A70C-5A0A-A6F9-11AE7041FC79}"
BUNDLE_ID="com.jeremywho.sessiondeck"
DERIVED="$PWD/build"
PROJECT="SessionDeckMobile.xcodeproj"
APP="$DERIVED/Build/Products/Debug-iphoneos/SessionDeckMobile.app"

unlock_keychain() {
  if [[ -f "$HOME/.claude-keychain-pw" ]]; then
    security unlock-keychain -p "$(cat "$HOME/.claude-keychain-pw")" "$HOME/Library/Keychains/login.keychain-db" || true
  fi
}

generate() { xcodegen generate --quiet; }

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

build_device() {
  generate
  unlock_keychain
  rm -rf "$APP"
  xcodebuild build -project "$PROJECT" -scheme SessionDeckMobile -configuration Debug \
    -destination "generic/platform=iOS" -derivedDataPath "$DERIVED" \
    -allowProvisioningUpdates -allowProvisioningDeviceRegistration DEVELOPMENT_TEAM="$SD_TEAM" 2>&1 \
    | grep -E "error:|BUILD|Signing Identity|Provisioning Profile" || true
  local rc=${pipestatus[1]}
  [[ $rc -eq 0 ]] || { echo "xcodebuild failed with $rc"; exit $rc; }
  [[ -d "$APP" ]] || { echo "build produced no app"; exit 1; }
}

case "${1:-install}" in
  test)
    generate
    SIM="$(simulator_udid)"
    xcodebuild test -project "$PROJECT" -scheme SessionDeckMobile -configuration Debug \
      -destination "platform=iOS Simulator,id=$SIM" -derivedDataPath "$DERIVED" \
      CODE_SIGNING_ALLOWED=NO 2>&1 | grep -E "error:|Test Suite|Executed|TEST |BUILD" || true
    rc=${pipestatus[1]}
    [[ $rc -eq 0 ]] || { echo "xcodebuild test failed with $rc"; exit $rc; }
    ;;
  build)
    build_device
    ;;
  install)
    build_device
    xcrun devicectl device install app --device "$SD_UDID" "$APP"
    xcrun devicectl device process launch --device "$SD_UDID" --terminate-existing "$BUNDLE_ID"
    ;;
  *)
    echo "usage: ios/build.sh test|build|install"; exit 2 ;;
esac
