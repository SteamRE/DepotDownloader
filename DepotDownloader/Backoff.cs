// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Threading.Tasks;

namespace DepotDownloader
{
    class Backoff
    {
        public readonly double ScalingFactor;
        public readonly double MinDelayS;
        public readonly double MaxDelayS;
        public int Attempts = 0;

        public Backoff(double MinDelayS = 1.0, double MaxDelayS = 120.0, double ScalingFactor = 1.3)
        {
            this.MinDelayS = MinDelayS;
            this.MaxDelayS = MaxDelayS;
            this.ScalingFactor = ScalingFactor;
        }

        public void RecordAttempt()
        {
            this.Attempts += 1;
        }

        public Task Sleep()
        {
            if (Attempts == 0)
            {
                return Task.Delay(0);
            }
            var sleepTimeS = MinDelayS * Math.Pow(ScalingFactor, Attempts - 1);
            if (sleepTimeS > MaxDelayS)
            {
                sleepTimeS = MaxDelayS;
            }
            var sleepTimeMS = (int)Math.Round(sleepTimeS * 1000.0);
            Console.WriteLine($"Waiting {sleepTimeS:F2} seconds");
            return Task.Delay(sleepTimeMS);
        }
    }
}
