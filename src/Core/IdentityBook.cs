using System;
using System.Collections.Generic;

namespace SousChef.Core
{
    public sealed class IdentityBook
    {
        private sealed class Entry
        {
            public object Token;
            public string Id;
        }
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private readonly string session;
        private long next;
        public int Epoch { get; private set; }

        public IdentityBook(string sessionId) { session = sessionId; }
        public void NewEpoch() { Epoch++; entries.Clear(); }

        public string Get(int instance, object token)
        {
            Entry entry;
            if (!entries.TryGetValue(instance, out entry) || !Object.ReferenceEquals(entry.Token, token))
            {
                entry = new Entry { Token = token, Id = session + ":" + Epoch + ":" + (++next) };
                entries[instance] = entry;
            }
            return entry.Id;
        }

        public void Retain(HashSet<int> live)
        {
            var removed = new List<int>();
            foreach (int key in entries.Keys) if (!live.Contains(key)) removed.Add(key);
            foreach (int key in removed) entries.Remove(key);
        }
    }
}
