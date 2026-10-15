using System;
using System.Linq;

namespace NAudio.WaveFormRenderer
{
    public class AveragePeakProvider : PeakProvider
    {
        private readonly float scale;

        public AveragePeakProvider(float scale)
        {
            this.scale = scale;
        }

        public override PeakInfo GetNextPeak()
        {
            var samplesRead = Provider.Read(ReadBuffer, 0, ReadBuffer.Length);
            if (samplesRead == 0)
                return new PeakInfo(0, 0);

            var sum = ReadBuffer.Take(samplesRead).Select(s => Math.Abs(s)).Sum();
            var average = sum/samplesRead;
            
            return new PeakInfo(average * (0 - scale), average * scale);
        }
    }
}
