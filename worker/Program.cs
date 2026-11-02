using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SousChef.Core;

internal static class Program
{
    private static int clients;
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("parent PID and token required");
        using var parent = Process.GetProcessById(Int32.Parse(args[0]));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(8);
        Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port); Console.Out.Flush();
        new Thread(() => { parent.WaitForExit(); Environment.Exit(0); }) { IsBackground = true }.Start();
        while (true)
        {
            var client = listener.AcceptTcpClient();
            if (Interlocked.Increment(ref clients) > 8) { Interlocked.Decrement(ref clients); client.Close(); continue; }
            new Thread(() => Serve(client, args[1])) { IsBackground = true }.Start();
        }
    }
    private static void Serve(TcpClient client, string token)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true; client.SendTimeout = 2000; client.ReceiveTimeout = 0;
                var stream = client.GetStream();
                var r = new BinaryReader(new BufferedStream(stream, 65536), Encoding.UTF8);
                var w = new BinaryWriter(new BufferedStream(stream, 65536), Encoding.UTF8);
                bool valid = r.ReadInt32() == WorkerWire.Version && r.ReadString() == token;
                w.Write(valid); w.Flush(); if (!valid) return;
                long generation = -1; WalkSearch search = null;
                var graph = new Dictionary<WalkCell, WalkEdge[]>(); var sent = new HashSet<WalkCell>();
                while (true)
                {
                    byte kind = r.ReadByte();
                    if (kind == 1) { byte[] bytes = Json.Utf8Line(Json.Encode(WorkerWire.ReadValue(r))); if (bytes.Length > WorkerWire.Limit) throw new IOException("Reply size limit"); w.Write(bytes.Length); w.Write(bytes); }
                    else if (kind == 2)
                    {
                        long next = r.ReadInt64(); WalkEdge[] starts = WorkerWire.Edges(r);
                        if (generation != next) { generation = next; search = null; graph.Clear(); sent.Clear(); }
                        int count = WorkerWire.Count(r, 128);
                        for (int i = 0; i < count; i++) graph[WorkerWire.Cell(r)] = WorkerWire.Edges(r);
                        if (graph.Count > 32000) throw new IOException("Graph limit");
                        Func<WalkCell, IEnumerable<WalkEdge>> expand = cell => graph.TryGetValue(cell, out var edges) ? edges : null;
                        if (search == null) search = new WalkSearch(starts, expand, 16000, () => true); else search.Continue(() => true);
                        var added = new List<WalkCell>(); foreach (var cell in search.Settled) if (sent.Add(cell)) added.Add(cell);
                        w.Write(generation); w.Write(search.Complete); w.Write(search.NodeLimitReached); w.Write(search.MinimumPendingDistance); w.Write(search.Distances.Count); w.Write(added.Count);
                        foreach (var cell in added)
                        { WorkerWire.Cell(w, cell); w.Write(search.Distances[cell]); bool has = search.Parent(cell, out var p); w.Write(has); if (has) WorkerWire.Cell(w, p); }
                    }
                    else if (kind == 3) FlightBatch.Run(r, w);
                    else throw new IOException("Unknown job");
                    w.Flush();
                }
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        finally { Interlocked.Decrement(ref clients); }
    }
}
