using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using SousChef.Core;
using SousChef.Plugin.Adapters;

internal static class Program
{
    private static int checks;
    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++;
    }
    private static void Main(string[] args)
    {
        checks += NavigationTests.Run();
        TestSchedule(); TestJson(); TestUtf8Line(); TestIdentity(); TestStream(); TestStreamDemand(); TestControl();
        if (args.Length > 0) Audit(args[0]);
        Console.WriteLine("PASS: " + checks + " assertions");
    }

    private static void TestSchedule()
    {
        float deadline = 0; int samples = 0;
        // Slightly inexact 60 Hz render boundaries must still deliver ~20 Hz.
        for (int frame = 0; frame < 600; frame++)
        {
            float now = frame / 60f;
            if (now < deadline) continue;
            samples++; deadline = SampleSchedule.Next(deadline, now, .05f);
            Assert(deadline > now, "no duplicate capture/catch-up on same frame");
        }
        Assert(samples >= 199 && samples <= 201, "20 Hz cadence without frame-quantization drift");
        Assert(SampleSchedule.Next(.05f, 10f, .05f) > 10f, "skip missed samples after stall");
    }

    private static void TestJson()
    {
        string sample = "quotes \" slash \\ newline\n\u0001 emoji 🥘";
        string wire = Json.Encode(Json.Object("s", sample, "null", null, "nan", Double.NaN,
            "infinity", Double.PositiveInfinity, "items", new object[] { -1, 1.25, false, "" }));
        using (JsonDocument document = JsonDocument.Parse(wire))
        {
            Assert(document.RootElement.GetProperty("s").GetString() == sample, "JSON Unicode/control roundtrip");
            Assert(document.RootElement.GetProperty("nan").ValueKind == JsonValueKind.Null, "non-finite JSON");
            Assert(document.RootElement.GetProperty("items")[1].GetDouble() == 1.25, "number precision");
        }
        bool rejected = false;
        try { Json.Encode(new object()); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "must not serialize opaque game objects on worker");
        var record = Json.Object("id", "original", "nested", Json.Object("value", 1));
        record["id"] = "updated";
        for (int i = 0; i < 20; i++) record["field" + i] = i;
        record["floatArray"] = new[] { .1f, Single.NaN, Single.PositiveInfinity, -2f };
        record["indices"] = new[] { Int32.MinValue, 0, Int32.MaxValue };
        using (var document = JsonDocument.Parse(Json.Encode(record)))
        {
            var root = document.RootElement;
            Assert(root.GetProperty("id").GetString() == "updated", "record update replaces existing key");
            Assert(root.EnumerateObject().Count() == 24, "record growth does not serialize spare capacity or duplicate keys");
            Assert(root.GetProperty("field19").GetInt32() == 19, "record survives repeated growth");
            Assert(root.GetProperty("nested").GetProperty("value").GetInt32() == 1, "nested record preserved");
            Assert(root.GetProperty("floatArray")[0].GetDouble() == (double).1f, "float array preserves original double wire precision");
            Assert(root.GetProperty("floatArray")[1].ValueKind == JsonValueKind.Null &&
                root.GetProperty("floatArray")[2].ValueKind == JsonValueKind.Null, "array nonfinite values remain null");
            Assert(root.GetProperty("indices")[0].GetInt32() == Int32.MinValue &&
                root.GetProperty("indices")[2].GetInt32() == Int32.MaxValue, "mesh indices retain full integer range");
        }
        Assert((string)record["id"] == "updated", "record lookup supports catalogue sorting");
        // Preserve wire output across the previous dictionary representation,
        // including dynamic adapter maps mixed into compact records.
        var legacy = new Dictionary<string, object> { { "x", new object[] { (double).1f, null, -2d } },
            { "adapter", new Dictionary<string, object> { { "field", "value" } } } };
        Assert(Json.Encode(Json.Object("x", new[] { .1f, Single.NaN, -2f }, "adapter", legacy["adapter"])) ==
            Json.Encode(legacy), "compact records and typed arrays preserve JSON semantics");
    }

    private static void TestUtf8Line()
    {
        foreach (string value in new[] { "", "{}", "café 中文 🥘", "\ud800", Json.Encode(Json.Object("s", "\n\r\"")) })
            Assert(Json.Utf8Line(value).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(value + "\n")),
                "NDJSON framing preserves every byte, including Unicode");
        // Measure the actual old/new framing paths on the same large payload.
        // This is an allocation regression check, not a rendering FPS benchmark.
        string large = new string('x', 1024 * 1024);
        Json.Utf8Line(large); System.Text.Encoding.UTF8.GetBytes(large + "\n");
        long start = GC.GetAllocatedBytesForCurrentThread();
        byte[] previous = System.Text.Encoding.UTF8.GetBytes(large + "\n");
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        byte[] current = Json.Utf8Line(large);
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(current.SequenceEqual(previous), "large frame bytes unchanged");
        Assert(oldBytes - newBytes >= large.Length * 2, "framing avoids a full UTF-16 copy");
        Console.WriteLine("1 MiB line framing allocated " + oldBytes + " -> " + newBytes + " bytes");
    }

    private static void TestStreamDemand()
    {
        using (var server = new StreamServer(0, Json.Object("type", "hello"), e => { throw new Exception(e); }))
        {
            server.Start();
            Assert(!server.HasClients, "no capture demand before subscription");
            using (var client = new TcpClient("127.0.0.1", server.Port))
            {
                client.ReceiveTimeout = 4000;
                var reader = new StreamReader(client.GetStream()); Read(reader);
                Assert(server.HasClients, "hello establishes capture demand");
                server.Publish(Packet(1)); Read(reader); Read(reader);
            }
            for (int i = 0; i < 200 && server.HasClients; i++) Thread.Sleep(5);
            Assert(!server.HasClients, "disconnect clears demand without another publication");
            // Simulate a capture racing with the final disconnect, including a
            // potentially in-flight encoder. A later subscriber needs fresh data.
            server.Publish(Packet(2));
            using (var client = new TcpClient("127.0.0.1", server.Port))
            {
                client.ReceiveTimeout = 4000;
                var reader = new StreamReader(client.GetStream());
                Assert(Read(reader).GetProperty("type").GetString() == "hello", "resubscription hello");
                server.Publish(Packet(3));
                Assert(Read(reader).GetProperty("type").GetString() == "asset", "resubscription restores assets");
                Assert(Read(reader).GetProperty("sequence").GetInt64() == 3, "resubscription never replays stale kitchen");
            }
        }
    }

    private static void TestIdentity()
    {
        var book = new IdentityBook("test"); book.NewEpoch();
        var a = new object(); var b = new object();
        string first = book.Get(7, a);
        Assert(first == book.Get(7, a), "identity stable");
        Assert(first != book.Get(7, b), "Unity instance reuse does not alias");
        book.Retain(new HashSet<int>());
        Assert(first != book.Get(7, a), "retired object not resurrected");
        string before = book.Get(7, a); book.NewEpoch();
        Assert(before != book.Get(7, a), "restart has new epoch");
    }

    private static JsonElement Read(StreamReader reader)
    {
        string line = reader.ReadLine();
        Assert(line != null, "stream unexpectedly closed");
        return JsonDocument.Parse(line).RootElement.Clone();
    }
    private static CapturePacket Packet(long n, int epoch = 1, bool assets = true)
    {
        var packet = new CapturePacket { Sequence = n, Epoch = epoch,
            Frame = Json.Object("type", "snapshot", "sequence", n, "levelEpoch", epoch) };
        if (assets) packet.Assets["mesh:1"] = Json.Object("type", "asset", "id", "mesh:1");
        return packet;
    }
    private static void TestStream()
    {
        var failures = new List<string>();
        using (var server = new StreamServer(0, Json.Object("type", "hello", "protocolVersion", 1),
            delegate(string e) { lock (failures) failures.Add(e); }))
        {
            server.Start();
            using (var client = new TcpClient("127.0.0.1", server.Port))
            {
                client.ReceiveTimeout = 4000;
                var reader = new StreamReader(client.GetStream());
                Assert(Read(reader).GetProperty("type").GetString() == "hello", "hello first");
                server.Publish(Packet(1));
                Assert(Read(reader).GetProperty("type").GetString() == "asset", "asset precedes reference");
                Assert(Read(reader).GetProperty("sequence").GetInt64() == 1, "frame 1");
                server.Publish(Packet(4));
                Assert(Read(reader).GetProperty("type").GetString() == "gap", "sequence gap explicit");
                Assert(Read(reader).GetProperty("sequence").GetInt64() == 4, "full state after gap");
                server.Publish(Packet(5, 2));
                Assert(Read(reader).GetProperty("type").GetString() == "asset", "epoch resets assets");
                Assert(Read(reader).GetProperty("sequence").GetInt64() == 5, "epoch state");
                using (var reconnect = new TcpClient("127.0.0.1", server.Port))
                {
                    reconnect.ReceiveTimeout = 4000; var fresh = new StreamReader(reconnect.GetStream());
                    Assert(Read(fresh).GetProperty("type").GetString() == "hello", "reconnect hello");
                    Assert(Read(fresh).GetProperty("type").GetString() == "asset", "reconnect assets");
                    Assert(Read(fresh).GetProperty("sequence").GetInt64() == 5, "reconnect latest snapshot");
                }
                server.Publish(new CapturePacket { Sequence = 6, Epoch = 2,
                    Frame = Json.Object("type", "status", "inRound", false, "sequence", 6) });
                Assert(Read(reader).GetProperty("type").GetString() == "status", "exit invalidates kitchen");
                server.Publish(Packet(7, 2));
                Assert(Read(reader).GetProperty("type").GetString() == "asset", "assets resent after status in same epoch");
                Assert(Read(reader).GetProperty("sequence").GetInt64() == 7, "recovered snapshot");
                server.Publish(Packet(8, 2, false));
                Assert(Read(reader).GetProperty("sequence").GetInt64() == 8, "asset disappeared");
                server.Publish(Packet(9, 2));
                Assert(Read(reader).GetProperty("type").GetString() == "asset", "asset reappears without unresolved reference");
                Assert(Read(reader).GetProperty("sequence").GetInt64() == 9, "reappearance snapshot");
            }
            // Slow clients cannot delay publication or retain an unbounded queue.
            using (var slow = new TcpClient("127.0.0.1", server.Port))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int n = 10; n < 1010; n++) server.Publish(Packet(n));
                Assert(watch.ElapsedMilliseconds < 1500, "publish is nonblocking");
                Assert(server.CoalescedCaptures > 0, "bounded mailbox coalesces captures");
            }
            lock (failures) Assert(failures.Count == 0, "background errors: " + String.Join(",", failures));
        }
    }

    private static void TestControl()
    {
        var command = ControlCommand.Parse("input r1 1 300 1 1 3");
        Assert(Math.Abs(command.X * command.X + command.Y * command.Y - 1) < 0.001, "diagonal normalized");
        foreach (string invalid in new[] { "input x 1 300 NaN 0 0", "input x 1 300 Infinity 0 0",
            "input x 1 300 2 0 0", "input x 1 1001 0 0 0", "input x 1 300 0 0 8",
            "input x 0 300 0 0 0", "claim x extra", "join x", "input x 1 300 0 0", "claim a/b" })
        {
            bool rejected = false;
            try { ControlCommand.Parse(invalid); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "reject malformed control: " + invalid);
        }
        var lease = new ControlLease();
        Assert(lease.Set(1, command, 0) == "not_owner", "no unclaimed input");
        Assert(lease.Claim(1, 0) && !lease.Claim(2, 0), "exclusive ownership");
        Assert(!lease.Claim(1, 0), "claim cannot reset sequence on active owner");
        Assert(lease.Set(2, command, 0) == "not_owner", "other client cannot drive P2");
        Assert(lease.Set(1, command, 0) == null, "owned input accepted");
        Assert(lease.Set(1, command, 0.1) == "stale_sequence", "duplicate input rejected");
        Assert(!lease.Release(2, "disconnect"), "other client's disconnect cannot release owner");
        Assert(lease.Read(0.299).Buttons == 3, "held input remains live");
        Assert(lease.Read(0.3).Buttons == 0 && lease.Owner(0.3) == 0, "deadline neutralizes and revokes");
        Assert(lease.Set(1, command, 0.4) == "not_owner", "expired input cannot resume");
        Assert(lease.Claim(2, 1), "new owner after expiry");
        lease.Revoke("paused");
        Assert(lease.Owner(1) == 0 && lease.LastRelease == "paused", "pause revokes ownership");
        Assert(lease.Claim(3, 2) && lease.Release(3, "disconnected"), "disconnect releases");
        Assert(lease.Read(2).X == 0, "disconnect neutralizes movement");

        int disconnected = 0;
        using (var server = new ControlServer(0, Json.Object("type", "hello"), id => Interlocked.Increment(ref disconnected)))
        using (var client = new TcpClient())
        {
            server.Start(); client.Connect("127.0.0.1", server.Port); client.ReceiveTimeout = 3000;
            var reader = new StreamReader(client.GetStream());
            Assert(Read(reader).GetProperty("type").GetString() == "hello", "control hello");
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes("status request1\n");
            client.GetStream().Write(bytes, 0, bytes.Length);
            bool dispatched = false; int mainThread = Thread.CurrentThread.ManagedThreadId;
            for (int i = 0; i < 100 && !dispatched; i++)
            {
                server.Pump((id, request) => {
                    Assert(Thread.CurrentThread.ManagedThreadId == mainThread, "control dispatch stays on caller thread");
                    dispatched = true; return Json.Object("id", request.Id, "ok", true);
                });
                Thread.Sleep(5);
            }
            Assert(dispatched && Read(reader).GetProperty("id").GetString() == "request1", "control roundtrip");
            client.Close();
            for (int i = 0; i < 100 && disconnected == 0; i++) Thread.Sleep(5);
            Assert(disconnected == 1, "socket disconnect callback");
        }
    }

    private static void Audit(string managed)
    {
        AssemblyLoadContext.Default.Resolving += delegate(AssemblyLoadContext context, AssemblyName name)
        {
            string file = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
        };
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Assembly-CSharp.dll"));
        Type fastList = assembly.GetType("System.Collections.Generic.FastList`1");
        Assert(fastList != null && fastList.GetField("_items") != null, "FastList backing array contract");
        Assert(fastList.GetField("_size", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType == typeof(int),
            "FastList live size contract");
        var errors = new List<string>();
        foreach (AdapterSpec spec in AdapterCatalog.Specs.Values)
        {
            Type type = assembly.GetType(spec.TypeName);
            if (type == null) { errors.Add("Missing type " + spec.TypeName); continue; }
            foreach (string name in spec.Fields)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field == null) errors.Add("Missing field " + spec.TypeName + "." + name);
                else checks++;
            }
        }
        foreach (string error in errors) Console.Error.WriteLine(error);
        Assert(errors.Count == 0, "Installed-game adapter contract audit failed");
        Console.WriteLine("Audited " + AdapterCatalog.Specs.Count + " adapter types against installed assembly.");
    }
}
