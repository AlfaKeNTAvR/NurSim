#!/usr/bin/env bash
# Builds the NurSim Quest APK in batchmode, sharing the vr-fencing Unity lock
# so only one Unity runs on this laptop at a time.
set -u

PROJECT="$(cd "$(dirname "$0")/../.." && pwd -W)"
UNITY="C:/Program Files/Unity/Hub/Editor/2022.3.19f1/Editor/Unity.exe"
LOCK="/c/Users/bogni/projects/vr-fencing/.git/vr-fencing-unity.lock"
LOG="$PROJECT/Builds/Quest/build.log"
mkdir -p "$PROJECT/Builds/Quest"
POLL_SECONDS=30

unity_running() {
    tasklist //FI "IMAGENAME eq Unity.exe" 2>/dev/null | grep -qi "Unity.exe"
}

echo "$(date -u +%FT%TZ) waiting for lock and for no Unity.exe"
while true; do
    if ! unity_running && mkdir "$LOCK" 2>/dev/null; then
        break
    fi
    sleep "$POLL_SECONDS"
done

release_lock() {
    rm -f "$LOCK/owner"
    rmdir "$LOCK" 2>/dev/null
    echo "$(date -u +%FT%TZ) lock released"
}
trap release_lock EXIT

shell_winpid=$(cat /proc/$$/winpid 2>/dev/null || echo unknown)
"$UNITY" -batchmode -quit -projectPath "$PROJECT" -buildTarget Android \
    -executeMethod QuestBuild.BuildFromCommandLine -logFile "$LOG" &
unity_pid=$!
unity_winpid=$(cat /proc/$unity_pid/winpid 2>/dev/null || echo unknown)
printf 'shell_winpid=%s\nunity_winpid=%s\nscript=quest-build.sh\nworktree=%s\nstarted=%s\n' \
    "$shell_winpid" "$unity_winpid" "$PROJECT" "$(date -u +%FT%TZ)" > "$LOCK/owner"
echo "$(date -u +%FT%TZ) Unity started, winpid $unity_winpid"

wait "$unity_pid"
status=$?
echo "$(date -u +%FT%TZ) Unity exited with $status"
grep -n -e "\[QuestBuild\]" -e "error CS" -e "Build Finished" "$LOG" | tail -40
exit "$status"
