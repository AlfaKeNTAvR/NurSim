#!/usr/bin/env bash
# Dumps recent Unity log lines from the Quest over USB.
export MSYS_NO_PATHCONV=1
adb_exe="/c/Program Files/Unity/Hub/Editor/6000.0.85f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
"$adb_exe" -d logcat -d -s Unity | grep -v -e "^--------- beginning" | tail -n "${1:-80}"
