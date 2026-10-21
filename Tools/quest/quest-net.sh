#!/usr/bin/env bash
# Shows the Quest's IPv4 interfaces and which one routes to the robot. Pass --clear-log to clear logcat.
export MSYS_NO_PATHCONV=1
adb_exe="/c/Program Files/Unity/Hub/Editor/6000.0.85f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
if [ "${1:-}" = "--clear-log" ]; then "$adb_exe" -d logcat -c; fi
"$adb_exe" -d shell ip -4 -o addr show
echo "== route to robot"
"$adb_exe" -d shell ip route get 130.215.221.227
"$adb_exe" -d shell dumpsys connectivity | grep -i -e "VPN" | head -5
