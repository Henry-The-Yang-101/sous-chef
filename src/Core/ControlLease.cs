using System;
using System.Globalization;

namespace SousChef.Core
{
    public sealed class ControlCommand
    {
        public string Operation, Id, Destination;
        public long Sequence;
        public int Milliseconds, Buttons;
        public float X, Y;

        // Small, bounded ASCII command grammar; replies are NDJSON.
        public static ControlCommand Parse(string line)
        {
            if (line == null || line.Length > 256) throw new ArgumentException("command_too_long");
            string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p[1].Length > 32) throw new ArgumentException("invalid_command");
            foreach (char c in p[1]) if (!Char.IsLetterOrDigit(c) && c != '-' && c != '_') throw new ArgumentException("invalid_id");
            var result = new ControlCommand { Operation = p[0], Id = p[1] };
            if (p[0] == "status" || p[0] == "claim" || p[0] == "release" || p[0] == "actions" || p[0] == "stop")
            {
                if (p.Length != 2) throw new ArgumentException("invalid_arguments");
                return result;
            }
            if (p[0] == "navigate_to")
            {
                if (p.Length != 3 || p[2].Length < 2 || p[2].Length > 32 || p[2][0] != 'd') throw new ArgumentException("invalid_destination");
                for (int i = 1; i < p[2].Length; i++) if (p[2][i] < '0' || p[2][i] > '9') throw new ArgumentException("invalid_destination");
                result.Destination = p[2]; return result;
            }
            if (p[0] != "input" || p.Length != 7) throw new ArgumentException("invalid_command");
            if (!Int64.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out result.Sequence) || result.Sequence < 1 ||
                !Int32.TryParse(p[3], out result.Milliseconds) || result.Milliseconds < 50 || result.Milliseconds > 1000 ||
                !Single.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out result.X) ||
                !Single.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out result.Y) ||
                Single.IsNaN(result.X) || Single.IsNaN(result.Y) || Math.Abs(result.X) > 1 || Math.Abs(result.Y) > 1 ||
                !Int32.TryParse(p[6], out result.Buttons) || result.Buttons < 0 || result.Buttons > 7)
                throw new ArgumentException("invalid_input");
            float length = (float)Math.Sqrt(result.X * result.X + result.Y * result.Y);
            if (length > 1) { result.X /= length; result.Y /= length; }
            return result;
        }
    }

    public struct ControlInput
    {
        public float X, Y;
        public int Buttons;
    }

    // Only primitive state crosses threads. Every clock value is monotonic seconds.
    public sealed class ControlLease
    {
        private readonly object sync = new object();
        private int owner;
        private long sequence;
        private double deadline;
        private ControlInput input;
        public string LastRelease { get; private set; }
        private void Clear(string reason) { owner = 0; input = new ControlInput(); LastRelease = reason; }
        private void Expire(double now) { if (owner != 0 && now >= deadline) Clear("expired"); }
        public bool Claim(int client, double now)
        {
            lock (sync)
            {
                Expire(now);
                if (owner != 0) return false;
                owner = client; sequence = 0; deadline = now + 0.5; input = new ControlInput(); return true;
            }
        }
        public string Set(int client, ControlCommand command, double now)
        {
            lock (sync)
            {
                Expire(now);
                if (owner != client || owner == 0) return "not_owner";
                if (command.Sequence <= sequence) return "stale_sequence";
                sequence = command.Sequence; deadline = now + command.Milliseconds / 1000.0;
                input = new ControlInput { X = command.X, Y = command.Y, Buttons = command.Buttons }; return null;
            }
        }
        public bool Release(int client, string reason)
        {
            lock (sync) { if (owner != client || owner == 0) return false; Clear(reason); return true; }
        }
        public void Revoke(string reason) { lock (sync) { if (owner != 0) Clear(reason); } }
        public int Owner(double now) { lock (sync) { Expire(now); return owner; } }
        public ControlInput Read(double now) { lock (sync) { Expire(now); return input; } }
    }
}
