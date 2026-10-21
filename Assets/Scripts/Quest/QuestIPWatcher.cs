using System.Threading;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

// Re-detects the headset IP in the background and republishes `unity/ip` on the
// main thread when it changes (VPN reconnect, Wi-Fi change).
public class QuestIPWatcher : MonoBehaviour
{
    const int CheckIntervalMs = 3000;
    const string UnityIPTopic = "unity/ip";

    string robotIP;
    string publishedIP;
    volatile string detectedIP;
    volatile bool running;

    public void Begin(string robotAddress, string initialIP)
    {
        robotIP = robotAddress;
        // SimulationManager publishes the initial IP on its first Update
        publishedIP = initialIP;
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
        string current = detectedIP;
        if (current == null || current == publishedIP)
            return;

        Debug.Log($"[QuestNetworkSetup] Headset IP changed {publishedIP ?? "unknown"} -> {current}, republishing {UnityIPTopic}");
        foreach (var manager in FindObjectsOfType<SimulationManager>())
            manager.unitySystemIP = current;
        // SimulationManager registers this topic as a latched publisher in Start
        ROSConnection.GetOrCreateInstance().Publish(UnityIPTopic, new StringMsg(current));
        publishedIP = current;
    }

    void OnDestroy()
    {
        running = false;
    }
}
