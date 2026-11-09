using System;
using BepInEx.Configuration;
using InControl;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Control
{
    internal sealed class PushToTalk : IDisposable
    {
        private readonly ConfigEntry<Key> binding;
        private readonly StreamServer stream;
        private readonly PushToTalkMenu menu;
        private Key key;
        private KeyCombo combo;
        private bool ready;
        private long sequence, turn;
        private string phase = "idle";
        private float nextHeartbeat;

        public int Port { get { return stream.Port; } }
        public string BindingLabel { get { return key == Key.None ? "" : combo.ToString(); } }

        public PushToTalk(ConfigFile config, string session, Action<string> log)
        {
            binding = config.Bind("Voice", "PushToTalk", Key.None, "Keyboard shortcut for the local harness microphone.");
            SetKey(binding.Value);
            stream = new StreamServer(0, Json.Object("type", "hello", "protocolVersion", 1,
                "sessionId", session, "stream", "voice"), log, value => Json.Utf8Line(Json.Encode(value)));
            try
            {
                stream.Start();
                menu = new PushToTalkMenu(this, log);
            }
            catch { stream.Dispose(); throw; }
        }

        public void Rebind(Key value)
        {
            binding.Value = value;
            SetKey(value);
            Publish();
        }

        private void SetKey(Key value)
        {
            Cancel();
            key = value < Key.Mouse0 && value != Key.Escape && value != Key.Backspace &&
                Enum.IsDefined(typeof(Key), value) ? value : Key.None;
            combo = key == Key.None ? default(KeyCombo) : new KeyCombo(key);
        }

        public void Tick()
        {
            menu.Tick();
            if (key != Key.None && Application.isFocused && !menu.IsOpen && !TimeManager.IsPaused(TimeManager.PauseLayer.Main))
            {
                bool held = combo.IsPressed;
                if (!held)
                {
                    ready = true;
                    if (phase == "held") SetPhase("released");
                }
                else if (ready && phase != "held")
                {
                    turn++;
                    SetPhase("held");
                }
            }
            else Cancel();

            if (stream.HasClients && Time.realtimeSinceStartup >= nextHeartbeat) Publish();
        }

        public void Cancel()
        {
            ready = false;
            if (phase == "held") SetPhase("cancelled");
        }

        public void FocusLost()
        {
            Cancel();
            menu.CancelCapture();
        }

        private void SetPhase(string value)
        {
            phase = value;
            Publish();
        }

        private void Publish()
        {
            nextHeartbeat = Time.realtimeSinceStartup + .25f;
            long seq = ++sequence;
            stream.Publish(new CapturePacket { Sequence = seq, Frame = Json.Object(
                "type", "pushToTalk", "sequence", seq, "turn", turn, "phase", phase, "key", BindingLabel) });
        }

        public void Dispose()
        {
            menu.Dispose();
            stream.Dispose();
        }
    }
}
