using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SousChef.Core
{
    // Owns only its child process. Start/handshake/socket IO never run in Unity Update.
    public sealed class WorkerHost : IDisposable
    {
        public static WorkerHost Current;
        private readonly string runtime, assembly, token = Guid.NewGuid().ToString("N");
        private Process process;
        private volatile bool stopping, ready;
        private int port;
        public string Error { get; private set; }
        public RemoteFlight Flights { get; private set; }
        public bool Ready { get { return ready && !stopping; } }
        public int ProcessId { get; private set; }
        public WorkerHost(string dotnet, string worker) { runtime = dotnet; assembly = worker; }
        public void Start()
        {
            new Thread(delegate()
            {
                try
                {
                    var start = new ProcessStartInfo(runtime, Quote(assembly) + " " + Process.GetCurrentProcess().Id + " " + token) {
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    var child = Process.Start(start);
                    lock (this) { process = child; if (stopping) { child.Kill(); return; } }
                    // Drain errors so a full stderr pipe cannot hang the worker.
                    child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Error = e.Data; };
                    child.BeginErrorReadLine(); ProcessId = child.Id;
                    string greeting = child.StandardOutput.ReadLine();
                    int selected;
                    if (greeting == null || !Int32.TryParse(greeting, out selected) || selected < 1024 || selected > 65535) throw new IOException("Worker handshake failed");
                    port = selected; ready = !stopping;
                    if (!stopping) Flights = new RemoteFlight(this);
                    child.WaitForExit(); ready = false;
                    if (!stopping) Error = "Worker exited: " + child.ExitCode;
                }
                catch (Exception e) { ready = false; Error = e.Message; }
            }) { IsBackground = true, Name = "SousChef worker lifecycle" }.Start();
        }
        private static string Quote(string value) { return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        public WorkerConnection Connect()
        {
            if (!Ready) throw new IOException("Worker unavailable");
            var client = new TcpClient();
            try
            {
                client.NoDelay = true; client.SendTimeout = 2000; client.ReceiveTimeout = 2000;
                client.Connect(IPAddress.Loopback, port);
                var result = new WorkerConnection(client);
                result.Writer.Write(WorkerWire.Version); result.Writer.Write(token); result.Writer.Flush();
                if (!result.Reader.ReadBoolean()) throw new IOException("Worker protocol mismatch");
                return result;
            }
            catch { client.Close(); throw; }
        }
        [ThreadStatic] private static WorkerConnection encoding;
        [ThreadStatic] private static WorkerHost encodingHost;
        public static string Encode(object frozen)
        {
            WorkerHost host = Current;
            if (host == null || !host.Ready) return Json.Encode(frozen);
            try
            {
                if (encoding == null || encodingHost != host)
                { if (encoding != null) encoding.Dispose(); encoding = host.Connect(); encodingHost = host; }
                encoding.Writer.Write((byte)1); WorkerWire.WriteValue(encoding.Writer, frozen); encoding.Writer.Flush();
                return WorkerWire.ReadString(encoding.Reader);
            }
            catch (Exception e)
            {
                host.Error = e.Message;
                if (encoding != null) encoding.Dispose(); encoding = null;
                return Json.Encode(frozen);
            }
        }
        public void Dispose()
        {
            stopping = true; ready = false;
            if (Flights != null) Flights.Dispose();
            lock (this) { if (process != null) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } } }
            if (Current == this) Current = null;
        }
    }
    public sealed class WorkerConnection : IDisposable
    {
        private readonly TcpClient client;
        public readonly BinaryReader Reader;
        public readonly BinaryWriter Writer;
        public WorkerConnection(TcpClient socket)
        { client = socket; var stream = socket.GetStream(); Reader = new BinaryReader(new BufferedStream(stream, 65536), Encoding.UTF8); Writer = new BinaryWriter(new BufferedStream(stream, 65536), Encoding.UTF8); }
        public void Dispose() { client.Close(); }
    }
    public interface IWorkerJob { object Run(WorkerConnection connection); }
    // At most one immutable job and one response; never wait in a caller's Update.
    public sealed class WorkerMailbox : IDisposable
    {
        private readonly WorkerHost host;
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly object sync = new object();
        private IWorkerJob pending;
        private object result;
        private bool busy;
        private volatile bool stopping, failed;
        private WorkerConnection connection;
        public bool Failed { get { return failed; } }
        public WorkerMailbox(WorkerHost owner)
        {
            host = owner;
            new Thread(Loop) { IsBackground = true, Name = "SousChef planning IPC" }.Start();
        }
        public bool Submit(IWorkerJob job)
        { lock (sync) { if (stopping || failed || busy || result != null) return false; pending = job; busy = true; } signal.Set(); return true; }
        public object Take() { lock (sync) { object value = result; result = null; return value; } }
        public bool Idle { get { lock (sync) return !busy && result == null && !failed && !stopping; } }
        private void Loop()
        {
            try
            {
                while (!stopping)
                {
                    signal.WaitOne(250, false); IWorkerJob job;
                    lock (sync) { job = pending; pending = null; }
                    if (job == null) continue;
                    if (connection == null) connection = host.Connect();
                    object reply = job.Run(connection);
                    lock (sync) { if (!stopping) result = reply; busy = false; }
                }
            }
            catch (Exception) { failed = true; }
            finally { if (connection != null) connection.Dispose(); }
        }
        public void Dispose() { stopping = true; signal.Set(); if (connection != null) connection.Dispose(); }
    }
}
