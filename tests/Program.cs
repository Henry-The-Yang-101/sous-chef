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
        TestJson(); TestIdentity(); TestStream();
        if (args.Length > 0) Audit(args[0]);
        Console.WriteLine("PASS: " + checks + " assertions");
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
