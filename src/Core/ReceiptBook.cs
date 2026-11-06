using System;
using System.Collections.Generic;

namespace SousChef.Core
{
    // Game-thread-only, bounded receipts. Eviction means unknown, never success.
    public sealed class ReceiptBook
    {
        public sealed class Entry
        {
            public string Id, Payload, Epoch, State, Reason;
            public object Evidence;
            public bool Terminal { get { return State != "running" && State != "waiting" && State != "suspended" && State != "unknown"; } }
            public object Describe() { return Json.Object("id", Id, "epoch", Epoch, "state", State, "reason", Reason, "evidence", Evidence); }
        }
        private readonly int capacity;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly Queue<string> order = new Queue<string>();
        public ReceiptBook(int limit = 256) { if (limit < 2) throw new ArgumentException("receipt_capacity"); capacity = limit; }
        public Entry Find(string id) { Entry entry; return entries.TryGetValue(id, out entry) ? entry : null; }
        public Entry Add(string id, string payload, string epoch)
        {
            if (entries.ContainsKey(id)) throw new ArgumentException("duplicate_receipt");
            int inspected = 0;
            while (entries.Count >= capacity)
            {
                if (++inspected > capacity) throw new InvalidOperationException("receipt_capacity_busy");
                string oldest = order.Dequeue();
                if (!entries[oldest].Terminal) { order.Enqueue(oldest); continue; }
                entries.Remove(oldest);
            }
            var entry = new Entry { Id = id, Payload = payload, Epoch = epoch, State = "unknown" };
            entries.Add(id, entry); order.Enqueue(id); return entry;
        }
        public void Clear() { entries.Clear(); order.Clear(); }
    }
}
