using System;
using System.Globalization;
using System.Text;

namespace SousChef.Core
{
    public sealed class ControlCommand
    {
        public string Operation, Id, Destination, Source, Selector, Condition, DesiredState;
        public long Sequence;
        public int Milliseconds, Buttons;
        public float X, Y;
        public bool Indefinite;
        // Internal task handoff mode; ordinary throw_to_player keeps catch semantics.
        public bool GroundHandoff;
        // Internal place_at mode: use the native floor drop at the current pose.
        public bool GroundDrop;
        public string GroundDropAwayFrom;
        public TaskRequest Task;

        // Small, bounded ASCII command grammar; replies are NDJSON.
        public static ControlCommand Parse(string line)
        {
            if (line == null || line.Length > (line.StartsWith("task ", StringComparison.Ordinal) ? 2048 : 256)) throw new ArgumentException("command_too_long");
            string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p[1].Length > 32) throw new ArgumentException("invalid_command");
            foreach (char c in p[1]) if (!Char.IsLetterOrDigit(c) && c != '-' && c != '_') throw new ArgumentException("invalid_id");
            var result = new ControlCommand { Operation = p[0], Id = p[1] };
            if (p[0] == "task") { result.Task = TaskRequest.Parse(p); result.Indefinite = true; return result; }
            if (Array.IndexOf(ActionRun.LogisticsNames, p[0]) >= 0)
            {
                if (p[0] == "get_plate" || p[0] == "return_utensil")
                {
                    if (p.Length != 3 || !DestinationId(p[2])) throw new ArgumentException("invalid_destination");
                    if (p[0] == "get_plate") result.Destination = p[2]; else result.Source = p[2];
                }
                else
                {
                    if (p.Length != 4 || !DestinationId(p[3])) throw new ArgumentException("invalid_destination");
                    result.Destination = p[3];
                    if (p[0] == "fetch_ingredient" || p[0] == "get_utensil") result.Selector = DecodeSelector(p[2]);
                    else { if (!DestinationId(p[2])) throw new ArgumentException("invalid_source"); result.Source = p[2]; }
                }
                return result;
            }
            if (p[0] == "status" || p[0] == "claim" || p[0] == "release" || p[0] == "actions" || p[0] == "stop")
            {
                if (p.Length != 2) throw new ArgumentException("invalid_arguments");
                return result;
            }
            if (p[0] == "navigate_to" || p[0] == "pick_up" || p[0] == "place_at" ||
                p[0] == "add_ingredient" || p[0] == "collect_contents" || p[0] == "discard" || p[0] == "throw_to" ||
                p[0] == "work_at" || p[0] == "activate" || p[0] == "wait_until")
            {
                int count = p[0] == "work_at" || p[0] == "wait_until" ? 5 : p[0] == "activate" ? 4 : 3;
                if (p.Length != count || p[2].Length < 2 || p[2].Length > 32 || p[2][0] != 'd') throw new ArgumentException("invalid_destination");
                for (int i = 1; i < p[2].Length; i++) if (p[2][i] < '0' || p[2][i] > '9') throw new ArgumentException("invalid_destination");
                result.Destination = p[2];
                if (p[0] == "activate")
                {
                    if (p[3] != "on" && p[3] != "off" && p[3] != "trigger") throw new ArgumentException("invalid_state");
                    result.DesiredState = p[3];
                }
                if (count == 5)
                {
                    string[] conditions = p[0] == "work_at" ? ActionRun.WorkConditions : ActionRun.WaitConditions;
                    if (Array.IndexOf(conditions, p[3]) < 0) throw new ArgumentException("invalid_condition");
                    if (!Int32.TryParse(p[4], NumberStyles.None, CultureInfo.InvariantCulture, out result.Milliseconds) ||
                        result.Milliseconds < 100 || result.Milliseconds > 120000) throw new ArgumentException("invalid_timeout");
                    result.Condition = p[3];
                }
                return result;
            }
            if (p[0] == "throw_to_player")
            {
                if (p.Length != 3 || (p[2] != "1" && p[2] != "3" && p[2] != "4")) throw new ArgumentException("invalid_player");
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
        private static bool DestinationId(string value)
        {
            if (value.Length < 2 || value.Length > 32 || value[0] != 'd') return false;
            for (int i = 1; i < value.Length; i++) if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }
        internal static string DecodeSelector(string token)
        {
            if (token.Length < 2 || token.Length > 129 || token[0] != 's') throw new ArgumentException("invalid_selector");
            string encoded = token.Substring(1);
            foreach (char c in encoded) if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_') throw new ArgumentException("invalid_selector");
            try
            {
                encoded = encoded.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                string value = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                if (value.Trim().Length == 0 || value.Length > 64) throw new ArgumentException("invalid_selector");
                foreach (char c in value) if (Char.IsControl(c)) throw new ArgumentException("invalid_selector");
                return value;
            }
            catch (FormatException) { throw new ArgumentException("invalid_selector"); }
            catch (DecoderFallbackException) { throw new ArgumentException("invalid_selector"); }
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
        private bool persistent;
        private bool suspended;
        private double suspendedAt;
        private long sequence;
        private double deadline, inputDeadline;
        private ControlInput input;
        public string LastRelease { get; private set; }
        private void Clear(string reason) { owner = 0; persistent = suspended = false; input = new ControlInput(); inputDeadline = 0; LastRelease = reason; }
        private void Expire(double now) { if (!suspended && owner != 0 && now >= deadline) Clear("expired"); }
        public void Suspend(double now)
        {
            lock (sync)
            {
                Expire(now);
                if (owner != 0 && !suspended) { suspended = true; suspendedAt = now; }
                input = new ControlInput(); inputDeadline = now;
            }
        }
        public void Resume(double now)
        {
            lock (sync)
            {
                if (suspended) { deadline += Math.Max(0, now - suspendedAt); suspended = false; }
                input = new ControlInput(); inputDeadline = now;
            }
        }
        public bool Claim(int client, double now, bool retainOwnership = false)
        {
            lock (sync)
            {
                Expire(now);
                if (owner != 0) return false;
                owner = client; persistent = retainOwnership; sequence = 0; deadline = persistent ? Double.PositiveInfinity : now + 0.5; inputDeadline = now; input = new ControlInput(); return true;
            }
        }
        public string Set(int client, ControlCommand command, double now)
        { return Set(client, command, now, command.Milliseconds); }
        public string Set(int client, ControlCommand command, double now, int inputMilliseconds)
        {
            lock (sync)
            {
                Expire(now);
                if (owner != client || owner == 0) return "not_owner";
                if (inputMilliseconds < 1 || inputMilliseconds > command.Milliseconds) return "invalid_input_window";
                if (command.Sequence <= sequence) return "stale_sequence";
                sequence = command.Sequence; deadline = persistent ? Double.PositiveInfinity : now + command.Milliseconds / 1000.0;
                inputDeadline = now + inputMilliseconds / 1000.0;
                input = new ControlInput { X = command.X, Y = command.Y, Buttons = command.Buttons }; return null;
            }
        }
        public bool Release(int client, string reason)
        {
            lock (sync) { if (owner != client || owner == 0) return false; Clear(reason); return true; }
        }
        public void Disconnect(int client)
        {
            // Autonomous work renews its own bounded inputs on the game thread.
            // Losing its observer must not cancel the accepted request. Manual
            // input has no executor and must still neutralize immediately.
            lock (sync) { if (owner == client && owner != 0 && !persistent) Clear("disconnected"); }
        }
        public void Revoke(string reason) { lock (sync) { if (owner != 0) Clear(reason); } }
        public int Owner(double now) { lock (sync) { Expire(now); return owner; } }
        public ControlInput Read(double now)
        {
            lock (sync)
            {
                Expire(now);
                if (suspended || now >= inputDeadline) input = new ControlInput();
                return input;
            }
        }
    }
}
