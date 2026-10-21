# Autonomous decisions, 10/20/2026: Quest 3 standalone teleop APK

Branch `nikita/feat/quest-standalone-teleop`, worktree `.claude/worktrees/quest`, based on local main.

## Decisions

- Base: local main (b718a634) plus your 7 uncommitted edits, committed as "chore: Carry over local teleop lab settings". Only 4 files had real changes (lab IPs, fault-reset text, panel distance, resizable window); the other 3 differed only in line endings and were left out. Your main checkout is untouched.
- PC VR stays buildable: Quest support goes in per-platform (Android) settings only.

- Camera IP: no manual entry. The robot's `camera_streamer_node` (iona_devel_ws/src/udp_ros) sends UDP JPEG frames to whatever IP arrives on `unity/ip`, so the Quest app will detect its own Wi-Fi IP and publish that. Same mechanism the PC uses today.
- ROS host: default `iona2.dyn.wpi.edu` (resolves to the robot's current DHCP IP; ROS-TCP-Connector's TcpClient accepts hostnames), with an in-VR override saved on the headset.
- HTTPDash (operator web page on port 8080): bind to the detected headset IP on Quest instead of the hardcoded PC IP.

- HTC VIVE OpenXR package: manifest now points at the official GitHub tag (`ViveSoftware/VIVE-OpenXR`, `versions/2.5.1`) instead of `C:/Users/dasaliba/Downloads/...tgz`. Same version, works on any machine with git + git-lfs. Not in HTC's npm registry (stops at 2.2.0); the 528 MB package was too big to commit. Affects your colleague's checkout when merged.
- ROS host override on the headset: a one-line `ros_host.txt` pushed with adb (no in-VR keyboard yet). In-VR entry is a follow-up.
- `/tracker_pose` (Vive Ultimate Tracker) is not published on Quest; the tracker does not exist there and the PC build published a frozen pose.
- Android build: OpenXR only (Oculus XR loader off), features Meta Quest + Oculus Touch + Touch Plus profiles, Vive features off on Android. IL2CPP, ARM64, min API 29, INTERNET permission, ASTC, OpenGL ES 3 (most compatible first try; Vulkan later if needed), minimal stripping, package `com.alfakentavr.nursim.teleop`. Only OculusControl + Liminal scenes; Build Settings scene list untouched.
- Build script: `Assets/Editor/QuestBuild.cs`, menu NurSim > Quest, APK to `Builds/Quest/NurSimTeleop.apk`.

- First build failed: URDF Importer and Robotics Visualizations packages only allow Windows/Mac/Linux, and simulation scripts that use them are referenced by code teleop needs. You chose to embed both packages (about 32 MB) under `Packages/` with Android added; their manifest entries are removed. Linux `libassimp.so` excluded from Android.
- Video fixes went into the Quest branch itself (not the separate `quest-camera-video-fixes` branch) because this session can only write to one worktree; that branch and `.claude/worktrees/quest-video` are unused and can be deleted. Fixes are a separate commit so they can be split out.

## Facts found

- You pinged the headset from the robot: works, so the robot can reach the Quest on WPI Wi-Fi.

- Robot iona2: wlan0 130.215.221.227 (WPI network), ros_tcp_endpoint binds 0.0.0.0. Neck camera `active_neck` sent at 640x480, JPEG q75; the node ignores its `fps` param and sends every camera frame.
- Teleop scene: OculusControl.unity loads Liminal.unity (empty room) additively and OculusInterface.prefab. No simulation, no Netcode, no SteamVR input; input goes through XRI actions.
- Blocker: Packages/manifest.json points the HTC VIVE OpenXR package at C:/Users/dasaliba/Downloads/... which does not exist here. Main checkout only works from its Library cache.

## Open questions

- Only one receiver gets camera frames: whichever app published `unity/ip` last (PC or Quest). Running both at once steals the stream. OK?

- Local main is 16 commits behind origin/main (dsaliba/NurSim). Rebase the Quest branch onto it later?

## Outcome (2026-10-21 ~01:50 local)

- Build 7 installed on the Quest 3 and working end to end with iona2 over GlobalProtect: ROS, controls, neck camera 30 fps (6 ms decode), 72 fps app. Pushed to github.com/AlfaKeNTAvR/NurSim, branch nikita/feat/quest-standalone-teleop.
- Fixed on device: black screen (simulation depth renderer feature threw without its stripped shader), point cloud shader missing in builds, first unity/ip publish lost before ROS connected (now republished every 3 s), interface freeze when another client connected to the robot's public port 10000 (now reconnects after 5 s of silence; root fix is a robot firewall, HIR-111).
- Robot web panel (HIR-112) running on iona2 port 8090, key-protected; source copied to Tools/robot_panel/. Autostart was blocked by Claude Code's safety check: start it after a reboot by hand.
- Tools/quest/: batchmode build (shares the vr-fencing Unity lock), USB install + launch, logcat and network helpers.
- Linear: HIR-103 parent; done HIR-104/105/106/107/110; open HIR-108 (in-VR host entry), HIR-109 (rebase onto origin/main), HIR-111 (firewall), HIR-112 (panel autostart), HIR-113 (left arm narrow preset, diagnosis running), HIR-114 (GL error spam, off-thread decode).
