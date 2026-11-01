using System;

namespace SousChef.Core
{
    // Fixed-inclination native launch: horizontal force, vertical force*tan(angle).
    // Pure ballistic helpers; the plugin separately checks native eligibility,
    // live collision sweeps, receiving volumes and navigation to release stances.
    public static class ThrowTrajectory
    {
        // Independent implementation of the free-flight integration described in
        // NVIDIA PhysX 3.4 DyBodyCoreIntegrator.h: gravity, damping, then position.
        // Fractional times interpolate along the next actual fixed-step segment.
        public static float Displacement(float velocity, float acceleration, float drag, float time, float step)
        {
            if (!Finite(velocity) || !Finite(acceleration) || !Finite(drag) || !Finite(time) || !Finite(step) || drag < 0 || time < 0 || step <= 0) return Single.NaN;
            double count = Math.Floor(time / step + 1e-7), fraction = Math.Max(0, time / step - count);
            double q = Math.Max(0, 1 - drag * step), next, sum;
            if (drag * step < 1e-6)
            {
                sum = velocity * count + acceleration * step * count * (count + 1) * .5;
                next = velocity + acceleration * step * (count + 1);
            }
            else
            {
                double power = Math.Pow(q, count), series = q * (1 - power) / (1 - q);
                sum = velocity * series + acceleration * step * q / (1 - q) * (count - series);
                next = (velocity * power + acceleration * step * series + acceleration * step) * q;
            }
            return (float)((sum + next * fraction) * step);
        }
        public static bool FlightTime(float height, float verticalSpeed, float gravity, float drag, float step, out float time)
        {
            time = 0;
            if (!Finite(height) || !Finite(verticalSpeed) || !Finite(gravity) || !Finite(drag) || !Finite(step) || gravity >= 0 || drag < 0 || step < .001f || step > .1f) return false;
            float previous = Displacement(verticalSpeed, gravity, drag, .1f, step) - height;
            for (float t = .1f + step; t <= 4; t += step)
            {
                float next = Displacement(verticalSpeed, gravity, drag, t, step) - height;
                if (previous >= 0 && next <= 0)
                {
                    float low = t - step, high = t;
                    for (int i = 0; i < 20; i++) { float middle = (low + high) * .5f; if (Displacement(verticalSpeed, gravity, drag, middle, step) > height) low = middle; else high = middle; }
                    time = (low + high) * .5f; return true;
                }
                previous = next;
            }
            return false;
        }
        public static bool Intercept(float dx, float dz, float vx, float vz, float speed, float drag, float step, out float time)
        {
            time = 0;
            if (!Finite(dx) || !Finite(dz) || !Finite(vx) || !Finite(vz) || !Finite(speed) || !Finite(drag) || !Finite(step) || speed <= 0 || drag < 0 || step < .001f || step > .1f) return false;
            float low = .1f;
            double initialX = dx + vx * low, initialZ = dz + vz * low;
            if (Math.Sqrt(initialX * initialX + initialZ * initialZ) <= Displacement(speed, 0, drag, low, step)) return false;
            for (float high = .1f + step; high <= 4; high += step)
            {
                double x = dx + vx * high, z = dz + vz * high;
                if (Math.Sqrt(x * x + z * z) > Displacement(speed, 0, drag, high, step)) { low = high; continue; }
                for (int i = 0; i < 20; i++)
                {
                    float middle = (low + high) * .5f; x = dx + vx * middle; z = dz + vz * middle;
                    if (Math.Sqrt(x * x + z * z) > Displacement(speed, 0, drag, middle, step)) low = middle; else high = middle;
                }
                time = (low + high) * .5f; return true;
            }
            return false;
        }
        public static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
    }
}
