#!/usr/bin/env bash
set -euo pipefail
collect() {
  mkdir -p android/ui-screenshots
  adb pull /sdcard/Android/data/app.yspotify.debug/files/screenshots/. android/ui-screenshots || true
}
trap collect EXIT
android/gradlew -p android :app:connectedDebugAndroidTest --console plain
adb shell settings put system font_scale 1.5
adb shell wm size 840x1440
android/gradlew -p android :app:connectedDebugAndroidTest --rerun-tasks --console plain
