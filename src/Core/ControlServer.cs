using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SousChef.Core
{
    // Workers parse wire requests; Unity dispatches them in Pump(). At most one
    // outstanding request per connection and four connections bound the queue.
    public sealed class ControlServer : IDisposable
    {
        private sealed class Request
        {
            public int Client;
            public ControlCommand Command;
            public object Response;
            public bool Cancelled;
        }
        private readonly object sync = new object();
        private readonly Queue<Request> pending = new Queue<Request>();
        private readonly Queue<Request> priority = new Queue<Request>();
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly TcpListener listener;
        private readonly Action<int> disconnect;
        private readonly string hello;
        private volatile bool stopping;
        private int nextClient;
        private Thread accept;
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public ControlServer(int port, object greeting, Action<int> onDisconnect)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            hello = Json.Encode(greeting); disconnect = onDisconnect;
        }
        public void Start()
        {
            listener.Start(4); accept = new Thread(Accept) { IsBackground = true, Name = "SousChef control accept" }; accept.Start();
        }
        private void Accept()
        {
            while (!stopping)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient(); int id;
                    lock (sync)
                    {
                        if (stopping || clients.Count >= 4) { client.Close(); continue; }
                        clients.Add(client); id = ++nextClient;
                    }
                    new Thread(delegate() { Serve(client, id); }) { IsBackground = true, Name = "SousChef control client" }.Start();
                }
                catch (SocketException) { if (stopping) return; }
                catch (ObjectDisposedException) { return; }
            }
        }
        private void Serve(TcpClient client, int id)
        {
            try
            {
                client.NoDelay = true; client.ReceiveTimeout = 0; client.SendTimeout = 2000;
                NetworkStream stream = client.GetStream(); Write(stream, hello);
                while (!stopping)
                {
                    var line = new StringBuilder(); int b;
                    while ((b = stream.ReadByte()) != -1 && b != 10)
                    {
                        if (b < 32 || b > 126 || line.Length >= 2304) throw new IOException("invalid line");
                        line.Append((char)b);
                    }
                    if (b == -1) break;
                    ControlCommand command;
                    try { command = ControlCommand.Parse(line.ToString()); }
                    catch (ArgumentException e) { Write(stream, Json.Encode(Json.Object("type", "reply", "ok", false, "error", e.Message))); continue; }
                    var request = new Request { Client = id, Command = command };
                    lock (sync) { if (pending.Count + priority.Count >= 32) throw new IOException("control queue full"); if (command.Operation == "stop" || command.Operation == "cancel") priority.Enqueue(request); else pending.Enqueue(request); }
                    object response;
                    lock (request)
                    {
                        int waited = 0;
                        while (!stopping && request.Response == null)
                        {
                            Monitor.Wait(request, 100); waited += 100;
                            if (waited >= 2000 || (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0))
                            { request.Cancelled = true; throw new IOException("dispatch timeout or disconnect"); }
                        }
                        if (stopping) { request.Cancelled = true; break; }
                        response = request.Response;
                    }
                    byte[] encoded = WorkerHost.EncodeLine(response); stream.Write(encoded, 0, encoded.Length);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally { WorkerHost.CloseEncoder(); disconnect(id); client.Close(); lock (sync) clients.Remove(client); }
        }
        public void Pump(Func<int, ControlCommand, object> handle)
        {
            for (int i = 0; i < 8; i++)
            {
                Request request;
                lock (sync) { if (pending.Count + priority.Count == 0) break; request = priority.Count > 0 ? priority.Dequeue() : pending.Dequeue(); }
                lock (request)
                {
                    if (request.Cancelled) continue;
                    try { request.Response = handle(request.Client, request.Command); }
                    catch (Exception) { disconnect(request.Client); request.Response = Json.Object("type", "reply", "id", request.Command.Id, "ok", false, "error", "dispatch_error"); }
                    Monitor.PulseAll(request);
                }
            }
        }
        private static void Write(Stream stream, string value)
        {
            byte[] bytes = Json.Utf8Line(value); stream.Write(bytes, 0, bytes.Length);
        }
        public void Dispose()
        {
            stopping = true; listener.Stop();
            lock (sync) { foreach (var client in clients) client.Close(); }
            if (accept != null) accept.Join(1000);
        }
    }
}
