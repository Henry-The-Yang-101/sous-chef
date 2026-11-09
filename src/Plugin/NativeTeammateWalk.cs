using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin
{
    internal sealed partial class NativeSimulator
    {
        private sealed class TeammateWalk
        {
            public PlayerControls Human, Chef;
            public PlayerControls.ControlSchemeData Scheme;
            public ILogicalValue OldX, OldY;
            public NetworkLogicalValue X, Y;
            public Vector3 Direction;
            public float Armed, Started = -1, Duration;
            public readonly List<object> Samples = new List<object>();
        }
        private TeammateWalk teammateWalk;

        // Opt-in simulator input only. Arm before starting ordinary P2 navigation.
        // Starting when P2 first moves makes the encounter independent of search time.
        private void WalkTeammate(string arguments)
        {
            if (teammateWalk != null || !KitchenGate.Inspect().InRound)
                throw new InvalidOperationException("Expected a kitchen with no active teammate walk");
            string[] values = arguments.Split(' ');
            if (values.Length != 3) throw new ArgumentException("walk-teammate requires X Z DURATION_MS");
            var direction = new Vector3(Single.Parse(values[0], CultureInfo.InvariantCulture), 0,
                Single.Parse(values[1], CultureInfo.InvariantCulture));
            int ms = Int32.Parse(values[2], CultureInfo.InvariantCulture);
            if (!(direction.sqrMagnitude <= 1.001f) || ms < 50 || ms > 5000)
                throw new ArgumentException("Use a unit direction or zero and 50–5000 ms");
            var walk = new TeammateWalk { Armed = Time.realtimeSinceStartup, Direction = direction, Duration = ms / 1000f };
            foreach (var player in UnityEngine.Object.FindObjectsOfType<PlayerControls>())
            {
                if (player.PlayerIDProvider == null) continue;
                var id = player.PlayerIDProvider.GetID();
                if (id == PlayerInputLookup.Player.One) walk.Human = player;
                if (id == PlayerInputLookup.Player.Two) walk.Chef = player;
            }
            if (walk.Human == null || walk.Chef == null || walk.Human.ControlScheme == null ||
                !walk.Human.CanButtonBePressed() || walk.Human.Motion.GetVelocityXZ().magnitude > .1f ||
                walk.Chef.Motion.GetVelocityXZ().magnitude > .1f)
                throw new InvalidOperationException("Expected two stationary local chefs");
            File.Delete(Path.Combine(directory, "teammate-walk.json"));
            teammateWalk = walk;
        }

        private void TickTeammateWalk()
        {
            var walk = teammateWalk;
            if (walk == null) return;
            float now = Time.realtimeSinceStartup;
            if (walk.Human == null || walk.Chef == null || walk.Human.m_bRespawning || walk.Chef.m_bRespawning)
            { FinishTeammateWalk("chef_unavailable"); return; }
            if (walk.Started < 0)
            {
                if (now - walk.Armed > 10f) { FinishTeammateWalk("navigation_did_not_move"); return; }
                if (walk.Chef.Motion.GetVelocityXZ().sqrMagnitude <= .01f) return;
                walk.Scheme = walk.Human.ControlScheme;
                walk.OldX = walk.Scheme.m_moveX; walk.OldY = walk.Scheme.m_moveY;
                walk.X = new NetworkLogicalValue(walk.OldX, (PlayerInputLookup.LogicalValueID)0);
                walk.Y = new NetworkLogicalValue(walk.OldY, (PlayerInputLookup.LogicalValueID)0);
                walk.X.SetValue(walk.Direction.x); walk.Y.SetValue(walk.Direction.z);
                walk.Scheme.m_moveX = walk.X; walk.Scheme.m_moveY = walk.Y;
                walk.Started = now;
            }
            walk.Samples.Add(Json.Object("seconds", now - walk.Started,
                "p1", Values.Vector(walk.Human.transform.position), "p2", Values.Vector(walk.Chef.transform.position)));
            if (now - walk.Started >= walk.Duration) FinishTeammateWalk("completed");
        }

        private void FinishTeammateWalk(string result)
        {
            var walk = teammateWalk;
            if (walk == null) return;
            teammateWalk = null;
            if (walk.Scheme != null)
            {
                if (walk.Scheme.m_moveX == walk.X) walk.Scheme.m_moveX = walk.OldX;
                if (walk.Scheme.m_moveY == walk.Y) walk.Scheme.m_moveY = walk.OldY;
            }
            Write("teammate-walk.json", Json.Object("result", result, "direction", Values.Vector(walk.Direction), "samples", walk.Samples));
        }
    }
}
