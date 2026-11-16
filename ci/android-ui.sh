#!/usr/bin/env bash
set -euo pipefail
collect() {
  mkdir -p android/ui-screenshots
  if [ -d android/app/build/outputs/connected_android_test_additional_output ]; then
    find android/app/build/outputs/connected_android_test_additional_output -name '*.png' -exec cp {} android/ui-screenshots/ \;
  fi
}
trap collect EXIT
android/gradlew -p android :app:connectedDebugAndroidTest --console plain
collect
adb shell settings put system font_scale 1.5
adb shell wm size 840x1440
android/gradlew -p android :app:connectedDebugAndroidTest --rerun-tasks --console plain
collect
