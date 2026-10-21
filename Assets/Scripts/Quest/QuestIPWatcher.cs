using System.Threading;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Re-detects the headset IP in the background and republishes `unity/ip` on the
// main thread every few seconds. Republishing (not only on change) covers the
// first publish being lost before ROS connects, a ROS or camera node restart,
// and VPN reconnects that give the headset a new IP.
public class QuestIPWatcher : MonoBehaviour
{
    const int CheckIntervalMs = 3000;
    const float PublishIntervalSeconds = 3f;
    const string UnityIPTopic = "unity/ip";
    const float IncomingStallSeconds = 5f;
    const float ReconnectCooldownSeconds = 10f;

    string robotIP;
    string publishedIP;
    float nextPublishTime;
    float nextReconnectAllowedTime;
    volatile string detectedIP;
    volatile bool running;

    public void Begin(string robotAddress, string initialIP)
    {
        robotIP = robotAddress;
        detectedIP = initialIP;
        running = true;
        new Thread(WatchLoop) { IsBackground = true, Name = "QuestIPWatcher" }.Start();
    }

    void WatchLoop()
    {
        while (running)
        {
            Thread.Sleep(CheckIntervalMs);
            string current = QuestNetworkSetup.DetectLocalIPTowards(robotIP);
            if (current != null)
                detectedIP = current;
        }
    }

    void Update()
    {
        ReconnectIfIncomingStalled();

        string current = detectedIP;
        if (current == null || Time.unscaledTime < nextPublishTime)
            return;
        nextPublishTime = Time.unscaledTime + PublishIntervalSeconds;

        if (current != publishedIP)
        {
            Debug.Log($"[QuestNetworkSetup] Headset IP {publishedIP ?? "unpublished"} -> {current}, publishing on {UnityIPTopic}");
            foreach (var manager in FindObjectsOfType<SimulationManager>())
                manager.unitySystemIP = current;
            publishedIP = current;
        }
        // SimulationManager registers this topic as a latched publisher in Start
        ROSConnection.GetOrCreateInstance().Publish(UnityIPTopic, new StringMsg(current));
    }

    // ros_tcp_endpoint sends robot-to-Unity topics only to its newest TCP client.
    // Any other connection to port 10000 (an internet scanner hitting the robot's
    // public IP, a second Unity) steals that stream: the headset keeps publishing
    // controls but the interface stops updating. The robot publishes status at
    // 20-100 Hz, so silence means the stream was stolen; reconnecting takes it back.
    void ReconnectIfIncomingStalled()
    {
        ROSConnection ros = ROSConnection.GetOrCreateInstance();
        float now = Time.realtimeSinceStartup;
        // HasConnectionError is not checked: a stolen stream makes the reader time
        // out, which sets it, and that is exactly the case to recover from.
        if (!ros.HasConnectionThread || now < nextReconnectAllowedTime)
            return;
        if (ros.LastMessageReceivedRealtime <= 0f || now - ros.LastMessageReceivedRealtime < IncomingStallSeconds)
            return;

        Debug.LogWarning($"[QuestNetworkSetup] No ROS messages for {now - ros.LastMessageReceivedRealtime:F1} s, reconnecting");
        nextReconnectAllowedTime = now + ReconnectCooldownSeconds;
        ros.Disconnect();
        ros.Connect();
        // Republish right away so the camera node keeps this headset as its target
        nextPublishTime = 0f;
    }

    void OnDestroy()
    {
        running = false;
    }
}
