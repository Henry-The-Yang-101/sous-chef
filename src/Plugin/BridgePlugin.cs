using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using SousChef.Core;
using SousChef.Plugin.Control;
using UnityEngine;

namespace SousChef.Plugin
{
    [BepInPlugin("dev.souschef.observer", "Sous Chef Observer", "0.8.6")]
    public sealed class BridgePlugin : BaseUnityPlugin
    {
        private StreamServer server, gameplayServer;
        private long gameplaySequence;
        private float nextGameplay, gameplayInterval;
        private SceneCapture capture;
        private Player2Control control;
        private WorkerHost worker;
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
                int gameplayHz = Math.Max(1, Math.Min(30, Config.Bind("Gameplay", "Hz", 20, "Compact gameplay samples per second, 1..30; render-frame limited.").Value));
                int gameplayPort = Config.Bind("Gameplay", "Port", 24704, "Loopback gameplay stream, independent of full snapshots.").Value;
                gameplayInterval = 1f / gameplayHz;
                string assembly = typeof(PlayerControls).Assembly.Location;
                string hash;
                using (var sha = SHA256.Create()) using (var file = File.OpenRead(assembly))
                    hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                string workerDirectory = Path.GetDirectoryName(typeof(BridgePlugin).Assembly.Location);
                string runtimeFile = Path.Combine(workerDirectory, "worker-dotnet.txt");
                string workerAssembly = Path.Combine(Path.Combine(workerDirectory, "worker"), "Worker.dll");
                if (Config.Bind("Worker", "Enabled", true, "Run detached planning and encoding in a separate process; native checks remain inside Unity.").Value &&
                    Environment.GetEnvironmentVariable("SOUS_CHEF_WORKER") != "0" && File.Exists(runtimeFile) && File.Exists(workerAssembly))
                {
                    worker = new WorkerHost(File.ReadAllText(runtimeFile).Trim(), workerAssembly);
                    WorkerHost.Current = worker; worker.Start();
                }
                capture = new SceneCapture(session);
                if (NativeSimulator.Enabled)
                {
                    var simulator = gameObject.AddComponent<NativeSimulator>();
                    simulator.GameAssemblyHash = hash; simulator.BridgeSession = session;
                }
                int controlPort = Config.Bind("Control", "Port", 24703, "Loopback port for direct control of existing local Player 2.").Value;
                if (controlPort < 1024 || controlPort > 65535 || controlPort == port || gameplayPort < 1024 || gameplayPort > 65535 || gameplayPort == port || gameplayPort == controlPort) throw new ArgumentException("Invalid control port");
                control = new Player2Control(controlPort, session);
                server = new StreamServer(port, Json.Object("type", "hello", "protocolVersion", 1,
                    "bridgeVersion", "0.8.6", "sessionId", session, "unityVersion", Application.unityVersion,
                    "gameAssemblySha256", hash, "captureHz", hz, "gameplayPort", gameplayPort, "gameplayHz", gameplayHz, "readOnly", false, "controlPort", controlPort, "controlPlayer", 2), LogWorkerError);
                server.Start();
                gameplayServer = new StreamServer(gameplayPort, Json.Object("type", "hello", "protocolVersion", 1,
                    "bridgeVersion", "0.8.6", "sessionId", session, "stream", "gameplay", "captureHz", gameplayHz,
                    "fullSnapshotPort", port), LogWorkerError);
                gameplayServer.Start();
                Logger.LogInfo("Observer listening on 127.0.0.1:" + port + "; active rounds only; assembly=" + hash);
                StartCoroutine(CaptureLoop());
            }
            catch (Exception e) { Logger.LogError("Observer startup failed: " + e); if (server != null) server.Dispose(); if (gameplayServer != null) gameplayServer.Dispose(); if (control != null) control.Dispose(); control = null; server = null; if (worker != null) worker.Dispose(); }
        }

        private void FixedUpdate() { fixedTicks++; }
        private void Update()
        {
            if (control != null)
            {
                try { control.Tick(); }
                catch (Exception e) { Logger.LogError("Control disabled: " + e); control.Dispose(); control = null; }
            }
            // Unity logging is also kept off worker threads.
            string error = System.Threading.Interlocked.Exchange(ref lastError, null);
            if (error != null) Logger.LogWarning(error);
        }
        private void LogWorkerError(string error) { System.Threading.Interlocked.Exchange(ref lastError, error); }

        private IEnumerator CaptureLoop()
        {
            // Batchmode has no rendered end-of-frame coroutine boundary.
            object boundary = NativeSimulator.Enabled ? null : (object)new WaitForEndOfFrame();
            while (true)
            {
                yield return boundary;
                if (server == null) continue;
                float now = Time.realtimeSinceStartup;
                if (now < nextCapture && now < nextGameplay) continue;
                try
                {
                    KitchenGate gate = KitchenGate.Inspect();
                    if (gate.InRound)
                    {
                        bool fullWanted = server.HasClients, gameplayWanted = gameplayServer.HasClients;
                        // Gameplay still depends on full discovery/reconciliation.
                        // Control/navigation operate independently of both streams.
                        if ((fullWanted || gameplayWanted) && (now >= nextCapture || !capture.Matches(gate)))
                        {
                            CapturePacket packet = capture.Capture(gate, ++sequence, fixedTicks, server.CoalescedCaptures);
                            if (fullWanted) server.Publish(packet);
                            nextCapture = SampleSchedule.Next(nextCapture, now, interval);
                        }
                        if (gameplayWanted && now >= nextGameplay)
                        {
                            gameplayServer.Publish(capture.Gameplay.Capture(gate, capture.Epoch, ++gameplaySequence,
                                fixedTicks, gameplayServer.CoalescedCaptures));
                            nextGameplay = SampleSchedule.Next(nextGameplay, now, gameplayInterval);
                        }
                        if (!fullWanted && !gameplayWanted) nextCapture = now + interval;
                        if (!gameplayWanted) nextGameplay = now + gameplayInterval;
                    }
                    else
                    {
                        if (wasInRound) capture.EndRound();
                        nextCapture = now + interval; nextGameplay = now + gameplayInterval;
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
                    capture.EndRound();
                    nextCapture = now + interval; nextGameplay = now + gameplayInterval;
                    PublishStatus("capture_error", "unknown");
                }
            }
        }

        private void PublishStatus(string reason, string role)
        {
            PublishStatusTo(server, ++sequence, reason, role);
            PublishStatusTo(gameplayServer, ++gameplaySequence, reason, role);
        }

        private void PublishStatusTo(StreamServer target, long seq, string reason, string role)
        {
            if (target == null) return;
            target.Publish(new CapturePacket { Sequence = seq, Epoch = capture.Epoch,
                Frame = Json.Object("type", "status", "protocolVersion", 1, "sessionId", session,
                    "sequence", seq, "levelEpoch", capture.Epoch, "inRound", false, "reason", reason,
                    "role", role, "realtime", Time.realtimeSinceStartup) });
        }

        private void OnDestroy() { StopAllCoroutines(); if (worker != null) worker.Dispose(); if (gameplayServer != null) { gameplayServer.Dispose(); gameplayServer = null; } if (control != null) { control.Dispose(); control = null; } if (server != null) { server.Dispose(); server = null; } }
    }
}
