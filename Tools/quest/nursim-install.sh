#!/usr/bin/env bash
# Installs the NurSim teleop APK on the Quest over USB. Pass --launch to start it.
export MSYS_NO_PATHCONV=1
adb_exe="/c/Program Files/Unity/Hub/Editor/6000.0.85f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
apk="$(cd "$(dirname "$0")/../.." && pwd -W)/Builds/Quest/NurSimTeleop.apk"
package="com.alfakentavr.nursim.teleop"

"$adb_exe" devices -l
install_output=$("$adb_exe" -d install -r "$apk" 2>&1)
echo "$install_output"
echo "$install_output" | grep -q "^Success" || { echo "INSTALL FAILED"; exit 1; }
if [ "${1:-}" = "--launch" ]; then
    "$adb_exe" -d shell monkey -p "$package" -c android.intent.category.LAUNCHER 1 >/dev/null && echo launched
fi
echo "== network"; "$adb_exe" -d shell ip -4 -o addr show | grep -v " lo "
