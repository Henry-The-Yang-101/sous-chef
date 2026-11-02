using System;
using System.Collections.Generic;
using System.Threading;

namespace SousChef.Core
{
    public sealed class FlightTask
    {
        internal float[][] Inputs;
        public float[] Ends;
        public volatile bool Complete, Failed;
    }
    // One connection services bounded throw batches across all preview planners.
    public sealed class RemoteFlight : IDisposable
    {
        private readonly WorkerHost host;
        private readonly Queue<FlightTask> pending = new Queue<FlightTask>();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private volatile bool stopping;
        private WorkerConnection connection;
        private long completedBatches;
        public long CompletedBatches { get { return Interlocked.Read(ref completedBatches); } }
        public RemoteFlight(WorkerHost owner)
        { host = owner; new Thread(Loop) { IsBackground = true, Name = "SousChef throw IPC" }.Start(); }
        public FlightTask Submit(float[][] inputs)
        {
            lock (pending)
            {
                if (stopping || !host.Ready || pending.Count >= 32) return null;
                var task = new FlightTask { Inputs = inputs }; pending.Enqueue(task); signal.Set(); return task;
            }
        }
        private void Loop()
        {
            while (!stopping)
            {
                signal.WaitOne(250, false);
                while (!stopping)
                {
                    FlightTask task;
                    lock (pending) { if (pending.Count == 0) break; task = pending.Dequeue(); }
                    try
                    {
                        if (connection == null) connection = host.Connect();
                        connection.Writer.Write((byte)3); connection.Writer.Write(task.Inputs.Length);
                        foreach (float[] input in task.Inputs) foreach (float f in input) connection.Writer.Write(f);
                        connection.Writer.Flush();
                        var ends = new float[WorkerWire.Count(connection.Reader, 108)];
                        if (ends.Length != task.Inputs.Length * 3) throw new System.IO.IOException("Flight reply shape");
                        for (int i = 0; i < ends.Length; i++) ends[i] = connection.Reader.ReadSingle(); task.Ends = ends; Interlocked.Increment(ref completedBatches);
                    }
                    catch (Exception) { task.Failed = true; if (connection != null) connection.Dispose(); connection = null; }
                    finally { task.Inputs = null; task.Complete = true; }
                }
            }
        }
        public void Dispose()
        {
            stopping = true; signal.Set(); if (connection != null) connection.Dispose();
            lock (pending) { foreach (var task in pending) { task.Failed = task.Complete = true; } pending.Clear(); }
        }
    }
}
