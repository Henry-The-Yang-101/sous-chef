using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin
{
    [BepInPlugin("dev.souschef.observer", "Sous Chef Observer", "0.1.0")]
    public sealed class BridgePlugin : BaseUnityPlugin
    {
        private StreamServer server;
        private SceneCapture capture;
        private readonly string session = Guid.NewGuid().ToString("N");
        private long sequence, fixedTicks;
        private bool wasInRound;
        private float nextCapture, nextStatus;
        private float interval;
        private string lastError;

        private void Awake()
        {
            try
            {
                int port = Config.Bind("Stream", "Port", 24702, "IPv4 loopback TCP port; read-only NDJSON.").Value;
                if (port < 1024 || port > 65535) throw new ArgumentException("Port must be in 1024..65535");
                int hz = Math.Max(1, Math.Min(30, Config.Bind("Capture", "Hz", 5, "Snapshot rate, 1..30. Profile before increasing.").Value));
                interval = 1f / hz;
                string assembly = typeof(PlayerControls).Assembly.Location;
                string hash;
                using (var sha = SHA256.Create()) using (var file = File.OpenRead(assembly))
                    hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                capture = new SceneCapture(session);
                server = new StreamServer(port, Json.Object("type", "hello", "protocolVersion", 1,
                    "bridgeVersion", "0.1.0", "sessionId", session, "unityVersion", Application.unityVersion,
                    "gameAssemblySha256", hash, "captureHz", hz, "readOnly", true), LogWorkerError);
                server.Start();
                Logger.LogInfo("Observer listening on 127.0.0.1:" + port + "; active rounds only; assembly=" + hash);
                StartCoroutine(CaptureLoop());
            }
            catch (Exception e) { Logger.LogError("Observer startup failed: " + e); if (server != null) server.Dispose(); server = null; }
        }

        private void FixedUpdate() { fixedTicks++; }
        private void Update()
        {
            // Unity logging is also kept off worker threads.
            string error = System.Threading.Interlocked.Exchange(ref lastError, null);
            if (error != null) Logger.LogWarning(error);
        }
        private void LogWorkerError(string error) { System.Threading.Interlocked.Exchange(ref lastError, error); }

        private IEnumerator CaptureLoop()
        {
            var boundary = new WaitForEndOfFrame();
            while (true)
            {
                yield return boundary;
                if (server == null || Time.realtimeSinceStartup < nextCapture) continue;
                nextCapture = Time.realtimeSinceStartup + interval;
                try
                {
                    KitchenGate gate = KitchenGate.Inspect();
                    if (gate.InRound)
                    {
                        server.Publish(capture.Capture(gate, ++sequence, fixedTicks, server.CoalescedCaptures));
                    }
                    else
                    {
                        if (wasInRound) capture.EndRound();
                        if (wasInRound || Time.realtimeSinceStartup >= nextStatus)
                        {
                            nextStatus = Time.realtimeSinceStartup + 1f;
                            PublishStatus("outside_active_round", gate.Role);
                        }
                    }
                    wasInRound = gate.InRound;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("Capture failed: " + e);
                    // Invalidate downstream state instead of silently leaving stale
                    // kitchen data as the most recently published frame.
                    PublishStatus("capture_error", "unknown");
                }
            }
        }

        private void PublishStatus(string reason, string role)
        {
            server.Publish(new CapturePacket { Sequence = ++sequence, Epoch = capture.Epoch,
                Frame = Json.Object("type", "status", "protocolVersion", 1, "sessionId", session,
                    "sequence", sequence, "levelEpoch", capture.Epoch, "inRound", false, "reason", reason,
                    "role", role, "realtime", Time.realtimeSinceStartup) });
        }

        private void OnDestroy() { StopAllCoroutines(); if (server != null) { server.Dispose(); server = null; } }
    }
}
