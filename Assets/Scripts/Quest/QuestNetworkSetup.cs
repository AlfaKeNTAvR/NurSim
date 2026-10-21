using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Network setup for the standalone Quest build. On PC VR nothing changes: the
// IPs typed into the scene and ROSConnectionPrefab are used as before.
//
// On the headset:
// - ROS host: the robot's DNS name, resolved once at startup (falls back to the
//   robot's last known IP if DNS does not answer). Override by pushing a
//   one-line text file with the host or IP:
//   adb push ros_host.txt /sdcard/Android/data/<package>/files/ros_host.txt
// - This headset's own IP is detected and published on `unity/ip` (the robot's
//   camera_streamer_node sends the UDP video there). It is re-checked every few
//   seconds, because the GlobalProtect VPN gives the headset a new IP on every
//   reconnect; a change is republished so the video follows the headset.
// - The HTTP dashboard listens on all interfaces, so it survives IP changes.
public static class QuestNetworkSetup
{
    const string DefaultRosHost = "iona2.dyn.wpi.edu";
    // iona2's address on 2026-10-21, used only when DNS does not resolve the name
    const string FallbackRobotIP = "130.215.221.227";
    const string RosHostFileName = "ros_host.txt";
    const int DnsTimeoutMs = 1500;

    public static string RosHost { get; private set; }
    public static string RobotIP { get; private set; }

    // Runs after every Awake in the first scene and before any Start, so the
    // IPs are in place before ROSConnection connects and HTTPDash binds.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ApplyOnHeadset()
    {
        if (Application.platform != RuntimePlatform.Android)
            return;

        RosHost = ReadRosHostOverride() ?? DefaultRosHost;
        RobotIP = ResolveRobotIP(RosHost);
        string headsetIP = DetectLocalIPTowards(RobotIP);
        Debug.Log($"[QuestNetworkSetup] ROS host {RosHost} -> {RobotIP}, headset IP {headsetIP ?? "unknown"}");

        // ROSConnection keeps retrying this address until the robot answers
        ROSConnection.GetOrCreateInstance().RosIPAddress = RobotIP;

        foreach (var dash in UnityEngine.Object.FindObjectsOfType<HTTPDash>())
            dash.localIP = "*";

        if (headsetIP != null)
        {
            foreach (var manager in UnityEngine.Object.FindObjectsOfType<SimulationManager>())
                manager.unitySystemIP = headsetIP;
        }
        else
        {
            Debug.LogWarning("[QuestNetworkSetup] No route to the robot yet (VPN down?); will keep checking.");
        }

        var watcher = new GameObject("QuestIPWatcher");
        UnityEngine.Object.DontDestroyOnLoad(watcher);
        watcher.AddComponent<QuestIPWatcher>().Begin(RobotIP, headsetIP);
    }

    static string ReadRosHostOverride()
    {
        string path = Path.Combine(Application.persistentDataPath, RosHostFileName);
        try
        {
            if (!File.Exists(path))
                return null;
            string host = File.ReadAllText(path).Trim();
            return host.Length > 0 ? host : null;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuestNetworkSetup] Could not read {path}: {e.Message}");
            return null;
        }
    }

    // Bounded wait so a dead network does not freeze the app at launch.
    static string ResolveRobotIP(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress literal))
            return literal.ToString();
        try
        {
            Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
            if (lookup.Wait(DnsTimeoutMs))
            {
                foreach (IPAddress address in lookup.Result)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                        return address.ToString();
                }
            }
            Debug.LogWarning($"[QuestNetworkSetup] DNS did not resolve {host} within {DnsTimeoutMs} ms");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuestNetworkSetup] DNS lookup of {host} failed: {e.Message}");
        }
        return host == DefaultRosHost ? FallbackRobotIP : host;
    }

    // A connected UDP socket sends nothing, but the OS picks the local
    // interface that routes to the target: the VPN or Wi-Fi IP the robot can reach.
    public static string DetectLocalIPTowards(string robotIP)
    {
        try
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Connect(IPAddress.Parse(robotIP), 9);
                return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
