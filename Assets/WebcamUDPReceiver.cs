using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

[RequireComponent(typeof(RawImage))]
public class UDPWebcamReceiver : MonoBehaviour
{
    public string cameraName;
    public int port = -1;
    public float fragmentTimeout = 0.2f;
    // Logs one line per second with receive / display / decode stats for this camera
    public bool logStats = true;

    // Receive blocks at most this long, so a stopped thread notices within this time
    private const int ReceiveTimeoutMs = 200;
    // Sleep after an unexpected receive error so a broken socket does not spin the thread
    private const int ReceiveErrorBackoffMs = 100;
    private const double ReceiveErrorLogIntervalSeconds = 5.0;
    // A frame id this far below the newest one means the sender restarted and counts from 0 again
    private const long SenderRestartIdJump = 1000;
    private const int HeaderLength = 8;
    private const float StatsLogIntervalSeconds = 1f;

    private RawImage rawImageUI;
    private Texture2D receivedTexture;

    private object frameLock = new object();
    private byte[] completeFrame;
    private static Dictionary<int, UdpClient> udpClients = new Dictionary<int, UdpClient>();
    private static Dictionary<int, UDPWebcamReceiver> socketHogs = new Dictionary<int, UDPWebcamReceiver>();

    protected ROSConnection ros;

    private Stopwatch stopwatch = Stopwatch.StartNew();

    // One receive thread and the socket it reads. Each thread gets its own session, so stopping
    // an old thread (port change, socket taken by another receiver) never affects the new one.
    private class ReceiveSession
    {
        public UdpClient client;
        public int port;
        public Thread thread;
        public volatile bool isRunning = true;
    }

    // Only touched on the main thread (ROS callbacks run there)
    private ReceiveSession session;

    private class FrameBuffer
    {
        public int totalFragments;
        // Whole received packets, payload starts at HeaderLength
        public byte[][] fragments;
        public int receivedCount;
        public float timestamp;
    }

    private Dictionary<uint, FrameBuffer> frameBuffers = new Dictionary<uint, FrameBuffer>();

    // Guarded by frameLock: newest frame id handed to the main thread
    private bool hasLatestFrameId;
    private uint latestFrameId;

    // Guarded by frameLock: counters since the last stats line
    private int completedCount;
    private int staleCount;
    private int incompleteCount;

    // Main thread only
    private List<uint> expiredFrameIds = new List<uint>();
    private int displayedWidth = -1;
    private int displayedHeight = -1;
    private Stopwatch decodeStopwatch = new Stopwatch();
    private int shownCount;
    private int decodeCount;
    private double decodeTotalMs;
    private double decodeMaxMs;
    private float statsWindowStart;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        rawImageUI = GetComponent<RawImage>();

        ros.Subscribe<StringMsg>("udpcam/ports", msg =>
        {
            int announcedPort = FindAnnouncedPort(msg.data);
            if (announcedPort < 0)
                return;

            // Same port as before: keep the current socket (also when another receiver took it over)
            if (announcedPort == port && session != null)
                return;

            SwitchToPort(announcedPort);
        });

        receivedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        statsWindowStart = (float)stopwatch.Elapsed.TotalSeconds;
    }

    // Returns the port registered for cameraName in "name:port&name:port", or -1
    int FindAnnouncedPort(string registry)
    {
        if (string.IsNullOrEmpty(registry))
            return -1;

        string[] existing = registry.Split('&');
        foreach (string entry in existing)
        {
            string[] item = entry.Split(':');
            if (item.Length == 2 && item[0] == cameraName && int.TryParse(item[1], out int announced))
                return announced;
        }
        return -1;
    }

    void SwitchToPort(int newPort)
    {
        bool isReconnect = session != null;
        ReleaseSession();

        port = newPort;
        UdpClient client;
        try
        {
            client = ConnectToPort();
        }
        catch (Exception e)
        {
            Debug.LogError($"[UDPWebcam {cameraName}] cannot open UDP port {port}: {e.Message}");
            return;
        }

        lock (frameLock)
        {
            frameBuffers.Clear();
            completeFrame = null;
            hasLatestFrameId = false;
        }

        ReceiveSession newSession = new ReceiveSession { client = client, port = port };
        newSession.thread = new Thread(() => ReceiveLoop(newSession)) { IsBackground = true };
        session = newSession;
        newSession.thread.Start();

        if (isReconnect)
            Debug.Log($"[UDPWebcam {cameraName}] port changed, now listening on {port}");
    }

    UdpClient ConnectToPort()
    {
        if (udpClients.ContainsKey(port))
        {
            socketHogs[port].StopThread();
            socketHogs[port] = this;
            return udpClients[port];
        }

        UdpClient client = new UdpClient(port);
        client.Client.ReceiveTimeout = ReceiveTimeoutMs;
        udpClients.Add(port, client);
        socketHogs.Add(port, this);
        return client;
    }

    // Signals the receive thread to stop without waiting for it. Used when another receiver
    // takes over the shared socket; the old thread exits within ReceiveTimeoutMs.
    void StopThread()
    {
        if (session != null)
            session.isRunning = false;
    }

    // Stops the current session. If this receiver still owns the socket, close it: that wakes the
    // blocked Receive right away and frees the port, so the main thread never waits on a join.
    void ReleaseSession()
    {
        if (session == null)
            return;

        session.isRunning = false;
        if (socketHogs.TryGetValue(session.port, out UDPWebcamReceiver hog) && hog == this)
        {
            socketHogs.Remove(session.port);
            udpClients.Remove(session.port);
            session.client.Close();
        }
        session = null;
    }

    void ReceiveLoop(ReceiveSession receiveSession)
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, receiveSession.port);
        double lastErrorLogTime = double.NegativeInfinity;
        int suppressedErrors = 0;

        while (receiveSession.isRunning)
        {
            byte[] data;
            try
            {
                data = receiveSession.client.Receive(ref remoteEP);
            }
            catch (Exception e)
            {
                // Normal timeout of the blocking Receive, lets the loop re-check isRunning
                if (e is SocketException socketError && IsReceiveTimeout(socketError))
                    continue;

                // Socket closed on purpose by ReleaseSession / OnApplicationQuit
                if (!receiveSession.isRunning)
                    break;

                double now = stopwatch.Elapsed.TotalSeconds;
                if (now - lastErrorLogTime >= ReceiveErrorLogIntervalSeconds)
                {
                    string suppressed = suppressedErrors > 0 ? $" ({suppressedErrors} similar errors suppressed)" : "";
                    Debug.LogWarning($"[UDPWebcam {cameraName}] receive error: {e.Message}{suppressed}");
                    lastErrorLogTime = now;
                    suppressedErrors = 0;
                }
                else
                {
                    suppressedErrors++;
                }
                Thread.Sleep(ReceiveErrorBackoffMs);
                continue;
            }

            // Another receiver took the socket while we were blocked; leave the packets to it
            if (!receiveSession.isRunning)
                break;

            HandlePacket(data);
        }
    }

    static bool IsReceiveTimeout(SocketException e)
    {
        return e.SocketErrorCode == SocketError.TimedOut || e.SocketErrorCode == SocketError.WouldBlock;
    }

    void HandlePacket(byte[] data)
    {
        if (data.Length < HeaderLength)
            return;

        uint frameId = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
        ushort totalFragments = (ushort)((data[4] << 8) | data[5]);
        ushort fragmentIndex = (ushort)((data[6] << 8) | data[7]);

        if (totalFragments == 0)
            return;

        lock (frameLock)
        {
            if (!frameBuffers.TryGetValue(frameId, out var buffer))
            {
                buffer = new FrameBuffer
                {
                    totalFragments = totalFragments,
                    fragments = new byte[totalFragments][],
                    receivedCount = 0,
                    timestamp = (float)stopwatch.Elapsed.TotalSeconds
                };
                frameBuffers[frameId] = buffer;
            }

            // Keep the received array as is (no payload copy); assembly skips the header
            if (fragmentIndex < buffer.fragments.Length && buffer.fragments[fragmentIndex] == null)
            {
                buffer.fragments[fragmentIndex] = data;
                buffer.receivedCount++;
            }

            if (buffer.receivedCount == buffer.totalFragments)
            {
                frameBuffers.Remove(frameId);
                completedCount++;

                if (hasLatestFrameId)
                {
                    long idDelta = (long)frameId - latestFrameId;
                    if (idDelta < -SenderRestartIdJump)
                    {
                        // Sender restarted and counts from 0 again: forget the old sequence
                        Debug.Log($"[UDPWebcam {cameraName}] frame id dropped from {latestFrameId} to {frameId}, assuming sender restart");
                        frameBuffers.Clear();
                    }
                    else if (idDelta <= 0)
                    {
                        // Older than (or same as) a frame already handed to the main thread
                        staleCount++;
                        return;
                    }
                }

                int totalLength = 0;
                foreach (var frag in buffer.fragments)
                    totalLength += frag.Length - HeaderLength;

                byte[] frameData = new byte[totalLength];
                int offset = 0;
                foreach (var frag in buffer.fragments)
                {
                    Buffer.BlockCopy(frag, HeaderLength, frameData, offset, frag.Length - HeaderLength);
                    offset += frag.Length - HeaderLength;
                }

                // Previous frame was never displayed; it is overwritten by this newer one
                if (completeFrame != null)
                    staleCount++;

                completeFrame = frameData;
                latestFrameId = frameId;
                hasLatestFrameId = true;
            }
        }
    }

    void Update()
    {
        float now = (float)stopwatch.Elapsed.TotalSeconds;
        byte[] frameToDisplay;

        lock (frameLock)
        {
            expiredFrameIds.Clear();
            foreach (var kvp in frameBuffers)
            {
                if (now - kvp.Value.timestamp > fragmentTimeout)
                    expiredFrameIds.Add(kvp.Key);
            }

            foreach (var id in expiredFrameIds)
            {
                frameBuffers.Remove(id);
            }
            incompleteCount += expiredFrameIds.Count;

            frameToDisplay = completeFrame;
            completeFrame = null;
        }

        if (frameToDisplay != null)
            DisplayFrame(frameToDisplay);

        if (logStats)
            LogStatsEverySecond(now);
    }

    void DisplayFrame(byte[] frameToDisplay)
    {
        // Kept readable (markNonReadable: false): repeated LoadImage into a texture that was
        // marked non-readable is not documented for Unity 2022.3
        decodeStopwatch.Restart();
        bool decoded = receivedTexture.LoadImage(frameToDisplay, false);
        decodeStopwatch.Stop();

        double decodeMs = decodeStopwatch.Elapsed.TotalMilliseconds;
        decodeCount++;
        decodeTotalMs += decodeMs;
        decodeMaxMs = Math.Max(decodeMaxMs, decodeMs);

        if (!decoded)
        {
            Debug.LogWarning("Failed to decode image.");
            return;
        }

        shownCount++;

        // LoadImage reuses the same texture object, so only resize the UI when the size changes
        if (rawImageUI.texture != receivedTexture
            || receivedTexture.width != displayedWidth
            || receivedTexture.height != displayedHeight)
        {
            rawImageUI.texture = receivedTexture;
            rawImageUI.SetNativeSize();
            displayedWidth = receivedTexture.width;
            displayedHeight = receivedTexture.height;
        }
    }

    void LogStatsEverySecond(float now)
    {
        float window = now - statsWindowStart;
        if (window < StatsLogIntervalSeconds)
            return;

        int completed, stale, incomplete;
        lock (frameLock)
        {
            completed = completedCount;
            stale = staleCount;
            incomplete = incompleteCount;
            completedCount = 0;
            staleCount = 0;
            incompleteCount = 0;
        }

        // Only the receiver that currently owns the socket reports; idle or taken-over ones stay quiet
        if (session != null && session.isRunning)
        {
            double decodeAvgMs = decodeCount > 0 ? decodeTotalMs / decodeCount : 0.0;
            Debug.Log(FormattableString.Invariant(
                $"[UDPWebcam {cameraName}] completed {completed / window:0}/s, shown {shownCount / window:0}/s, stale {stale / window:0}/s, incomplete {incomplete / window:0}/s, decode avg {decodeAvgMs:0.0} ms max {decodeMaxMs:0.0} ms, port {port}"));
        }

        shownCount = 0;
        decodeCount = 0;
        decodeTotalMs = 0.0;
        decodeMaxMs = 0.0;
        statsWindowStart = now;
    }

    void OnApplicationQuit()
    {
        if (session == null)
            return;

        session.isRunning = false;
        session.client?.Close();
        // Close wakes the blocked Receive; the timeout bounds the wait if it does not
        session.thread?.Join(ReceiveTimeoutMs * 2);
    }
}
