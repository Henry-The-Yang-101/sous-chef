using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SousChef.Core
{
    public sealed class CapturePacket
    {
        public long Sequence;
        public int Epoch;
        public object Frame;
        // The list and its values must be frozen after publication.
        public Dictionary<string, object> Assets = new Dictionary<string, object>();
    }

    // A single pending capture and a single latest encoded frame bound memory.
    // Every client writes on its own thread. No socket/JSON work runs in Unity.
    public sealed class StreamServer : IDisposable
    {
        private sealed class Encoded
        {
            public long Sequence;
            public int Epoch;
            public byte[] Frame;
            public Dictionary<string, byte[]> Assets;
        }
        private readonly object sync = new object();
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly AutoResetEvent pendingSignal = new AutoResetEvent(false);
        private readonly Action<string> report;
        private readonly Func<object, byte[]> encode;
        private readonly byte[] hello;
        private readonly TcpListener listener;
        private Thread encoderThread, acceptThread;
        private CapturePacket pending;
        private int audience, pendingAudience;
        private Encoded latest;
        private volatile bool stopping;
        private long coalesced;
        public long CoalescedCaptures { get { return Interlocked.Read(ref coalesced); } }
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public bool HasClients { get { lock (sync) return clients.Count > 0; } }

        public StreamServer(int port, object greeting, Action<string> log, Func<object, byte[]> encode = null)
        {
            report = log;
            this.encode = encode ?? WorkerHost.EncodeLine;
            hello = this.encode(greeting);
            listener = new TcpListener(IPAddress.Loopback, port);
        }

        public void Start()
        {
            listener.Start(4);
            encoderThread = Background(EncodeLoop, "SousChef encoder");
            acceptThread = Background(AcceptLoop, "SousChef accept");
        }

        public void Publish(CapturePacket packet)
        {
            lock (sync)
            {
                if (stopping) return;
                if (pending != null) Interlocked.Increment(ref coalesced);
                pending = packet;
                pendingAudience = audience;
            }
            pendingSignal.Set();
        }

        private void EncodeLoop()
        {
            var assetCache = new Dictionary<string, byte[]>(); int epoch = -1;
            while (!stopping)
            {
                pendingSignal.WaitOne(250, false);
                CapturePacket packet; int generation;
                lock (sync) { packet = pending; generation = pendingAudience; pending = null; }
                if (packet == null) continue;
                try
                {
                    if (epoch != packet.Epoch) { assetCache.Clear(); epoch = packet.Epoch; }
                    var assets = new Dictionary<string, byte[]>();
                    foreach (var asset in packet.Assets)
                    {
                        byte[] bytes;
                        if (!assetCache.TryGetValue(asset.Key, out bytes))
                        {
                            bytes = encode(asset.Value); assetCache.Add(asset.Key, bytes);
                        }
                        assets.Add(asset.Key, bytes);
                    }
                    var encoded = new Encoded { Sequence = packet.Sequence, Epoch = packet.Epoch,
                        Frame = encode(packet.Frame), Assets = assets };
                    assetCache = assets;
                    lock (sync)
                    {
                        // A new first subscriber must never receive a capture left
                        // over from before demand stopped (including in-flight JSON).
                        if (generation == audience) { latest = encoded; Monitor.PulseAll(sync); }
                    }
                }
                catch (Exception e) { report("Serialization failed: " + e.Message); }
            }
            WorkerHost.CloseEncoder();
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    client.NoDelay = true; client.SendTimeout = 2000;
                    client.SendBufferSize = 65536;
                    lock (sync)
                    {
                        if (stopping || clients.Count >= 4) { client.Close(); continue; }
                        if (clients.Count == 0) { audience++; latest = null; pending = null; }
                        clients.Add(client);
                    }
                    Background(delegate { SendLoop(client); }, "SousChef client");
                }
                catch (SocketException e) { if (!stopping) report("Accept failed: " + e.Message); }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void SendLoop(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream(); stream.Write(hello, 0, hello.Length);
                long last = -1; int epoch = -1;
                var sent = new HashSet<string>();
                while (!stopping)
                {
                    Encoded frame;
                    lock (sync)
                    {
                        while (!stopping && (latest == null || latest.Sequence == last))
                        {
                            // Observation clients send no input. Detect a closed
                            // reader even while capture is idle, so it cannot keep
                            // main-thread work enabled indefinitely.
                            if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0) return;
                            Monitor.Wait(sync, 250);
                        }
                        if (stopping) break;
                        frame = latest;
                    }
                    if (epoch != frame.Epoch) { sent.Clear(); epoch = frame.Epoch; }
                    foreach (var asset in frame.Assets)
                        if (sent.Add(asset.Key)) stream.Write(asset.Value, 0, asset.Value.Length);
                    if (last >= 0 && frame.Sequence > last + 1)
                    {
                        byte[] gap = encode(Json.Object("type", "gap", "afterSequence", last,
                            "beforeSequence", frame.Sequence, "reason", "latest-state backpressure"));
                        stream.Write(gap, 0, gap.Length);
                    }
                    stream.Write(frame.Frame, 0, frame.Frame.Length); last = frame.Sequence;
                    // Assets can disappear and later reappear within the same epoch.
                    // Match the client's current-frame cache, including empty status frames.
                    sent = new HashSet<string>(frame.Assets.Keys);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                WorkerHost.CloseEncoder(); client.Close();
                lock (sync)
                {
                    clients.Remove(client);
                    if (clients.Count == 0) { audience++; latest = null; pending = null; }
                }
            }
        }

        public void Dispose()
        {
            stopping = true; listener.Stop(); pendingSignal.Set();
            lock (sync)
            {
                foreach (TcpClient client in clients) client.Close();
                Monitor.PulseAll(sync);
            }
            if (encoderThread != null) encoderThread.Join(2500);
            if (acceptThread != null) acceptThread.Join(2500);
            // Client threads are background threads and sockets are closed above.
        }

        private static Thread Background(ThreadStart action, string name)
        {
            var thread = new Thread(action) { IsBackground = true, Name = name }; thread.Start(); return thread;
        }
    }
}
