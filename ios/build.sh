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

# The relay's URL and key come from ~/.sessiondeck-mobile/relay.env on the build machine (RELAY_URL=, RELAY_KEY=),
# never from the repository; without the file the app is the LAN app.
write_secrets() {
  local dir=SessionDeckMobile/Generated url="nil" key="nil" env="$HOME/.sessiondeck-mobile/relay.env" u k
  mkdir -p "$dir"
  if [[ -f "$env" ]]; then
    u="$(grep -E '^RELAY_URL=' "$env" | head -1 | cut -d= -f2-)"
    k="$(grep -E '^RELAY_KEY=' "$env" | head -1 | cut -d= -f2-)"
    [[ -n "$u" ]] && url="\"$u\""
    [[ -n "$k" ]] && key="\"$k\""
  fi
  cat > "$dir/RelaySecrets.swift" <<EOF
enum RelaySecrets {
    static let url: String? = $url
    static let key: String? = $key
}
EOF
  echo "relay secrets: $([[ $url == nil ]] && echo none || echo "$u")"
}

# The build carries its commit: CFBundleVersion is the commit count, SDCommit the short hash.
generate() {
  write_secrets
  xcodegen generate --quiet
  local sha count
  sha="$(git rev-parse --short HEAD)"; count="$(git rev-list --count HEAD)"
  /usr/libexec/PlistBuddy -c "Delete :SDCommit" SessionDeckMobile/Info.plist >/dev/null 2>&1 || true
  /usr/libexec/PlistBuddy -c "Add :SDCommit string $sha" SessionDeckMobile/Info.plist
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $count" SessionDeckMobile/Info.plist
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

build_device() {
  generate
  unlock_keychain
  rm -rf "$APP"
  mkdir -p "$DERIVED"
  set +e
  xcodebuild build -project "$PROJECT" -scheme SessionDeckMobile -configuration Debug \
    -destination "generic/platform=iOS" -derivedDataPath "$DERIVED" \
    -allowProvisioningUpdates -allowProvisioningDeviceRegistration DEVELOPMENT_TEAM="$SD_TEAM" > "$DERIVED/build.log" 2>&1
  local rc=$?
  set -e
  grep -E "error:|BUILD|Signing Identity|Provisioning Profile" "$DERIVED/build.log" || true
  [[ $rc -eq 0 ]] || { echo "xcodebuild failed with $rc (see $DERIVED/build.log)"; exit $rc; }
  [[ -d "$APP" ]] || { echo "build produced no app"; exit 1; }
}

case "${1:-install}" in
  test)
    generate
    SIM="$(simulator_udid)"
    mkdir -p "$DERIVED"
    set +e
    xcodebuild test -project "$PROJECT" -scheme SessionDeckMobile -configuration Debug \
      -destination "platform=iOS Simulator,id=$SIM" -derivedDataPath "$DERIVED" \
      CODE_SIGNING_ALLOWED=NO > "$DERIVED/test.log" 2>&1
    rc=$?
    set -e
    grep -E "error:|Test Suite|Executed|TEST |BUILD" "$DERIVED/test.log" || true
    [[ $rc -eq 0 ]] || { echo "xcodebuild test failed with $rc (see $DERIVED/test.log)"; exit $rc; }
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
