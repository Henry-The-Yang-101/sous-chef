using System;

namespace SousChef.Core
{
    public static class SampleSchedule
    {
        // Preserve cadence across frame quantization without catch-up bursts.
        public static float Next(float previous, float now, float period)
        {
            if (period <= 0 || Single.IsNaN(period) || Single.IsInfinity(period))
                throw new ArgumentOutOfRangeException("period");
            return previous + Math.Max(1, (int)Math.Floor((now - previous) / period) + 1) * period;
        }
    }
}
