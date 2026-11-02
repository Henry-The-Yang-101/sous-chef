using System;
using System.IO;

namespace SousChef.Core
{
    // Calm-flight endpoint integration, same scalar operation order as ThrowFlight.
    // Wind membership and all collision/receiver checks remain native.
    public static class FlightBatch
    {
        public static float[] Ends(float[][] inputs)
        {
            var ends = new float[inputs.Length * 3];
            for (int n = 0; n < inputs.Length; n++)
            {
                float[] p = inputs[n]; if (p.Length != 12) throw new IOException("Flight input shape");
                foreach (float f in p) if (Single.IsNaN(f) || Single.IsInfinity(f)) throw new IOException("Nonfinite flight input");
                float time = p[10], step = p[11];
                if (time < 0 || time > 4 || step < .001f || step > .1f || p[9] < 0) throw new IOException("Flight bounds");
                int count = (int)Math.Ceiling(time / step); if (count < 1 || count > 4000) throw new IOException("Flight duration");
                float x = p[0], y = p[1], z = p[2], vx = p[3], vy = p[4], vz = p[5];
                float damping = Math.Max(0, 1 - p[9] * step), elapsed = 0;
                for (int i = 0; i < count; i++)
                {
                    float duration = Math.Min(step, time - elapsed); if (duration <= 0) break;
                    vx = (vx + p[6] * step) * damping; vy = (vy + p[7] * step) * damping; vz = (vz + p[8] * step) * damping;
                    x += vx * duration; y += vy * duration; z += vz * duration; elapsed += duration;
                }
                ends[n * 3] = x; ends[n * 3 + 1] = y; ends[n * 3 + 2] = z;
            }
            return ends;
        }
        public static void Run(BinaryReader r, BinaryWriter w)
        {
            var inputs = new float[WorkerWire.Count(r, 36)][];
            for (int i = 0; i < inputs.Length; i++) { inputs[i] = new float[12]; for (int j = 0; j < 12; j++) inputs[i][j] = r.ReadSingle(); }
            float[] ends = Ends(inputs); w.Write(ends.Length); foreach (float f in ends) w.Write(f);
        }
    }
}
