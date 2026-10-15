using System;
using System.Drawing;
using NAudio.Wave;

namespace NAudio.WaveFormRenderer
{
    public class WaveFormRenderer
    {
        public Image Render(WaveStream waveStream, WaveFormRendererSettings settings)
        {
            return Render(waveStream, new MaxPeakProvider(), settings);
        }        

        public Image Render(WaveStream waveStream, IPeakProvider peakProvider, WaveFormRendererSettings settings)
        {
            var stepSize = settings.PixelsPerPeak + settings.SpacerPixels;
            var peakCount = (settings.Width - 1) / stepSize + 1;
            var frames = waveStream.Length / waveStream.WaveFormat.BlockAlign;
            // Sample providers read interleaved channels, and the underlying WAV reader requires
            // complete frames. Also keep one frame per peak when the clip is shorter than the image.
            var framesPerPeak = Math.Max(1L, (frames + peakCount - 1) / peakCount);
            var samplesPerPeak = checked((int)(framesPerPeak * waveStream.WaveFormat.Channels));
            peakProvider.Init(waveStream.ToSampleProvider(), samplesPerPeak);
            return Render(peakProvider, settings);
        }

        private static Image Render(IPeakProvider peakProvider, WaveFormRendererSettings settings)
        {
            if (settings.DecibelScale)
                peakProvider = new DecibelPeakProvider(peakProvider, 48);

            var b = new Bitmap(settings.Width, settings.TopHeight + settings.BottomHeight);
            if (settings.BackgroundColor == Color.Transparent)
            {
                b.MakeTransparent();
            }
            using (var g = Graphics.FromImage(b))
            {
                g.FillRectangle(settings.BackgroundBrush, 0,0,b.Width,b.Height);
                var midPoint = settings.TopHeight;

                int x = 0;
                var currentPeak = peakProvider.GetNextPeak();
                while (x < settings.Width)
                {
                    var nextPeak = peakProvider.GetNextPeak();
                    
                    for (int n = 0; n < settings.PixelsPerPeak; n++)
                    {
                        var lineHeight = settings.TopHeight * currentPeak.Max;
                        g.DrawLine(settings.TopPeakPen, x, midPoint, x, midPoint - lineHeight);
                        lineHeight = settings.BottomHeight * currentPeak.Min;
                        g.DrawLine(settings.BottomPeakPen, x, midPoint, x, midPoint - lineHeight);
                        x++;
                    }

                    for (int n = 0; n < settings.SpacerPixels; n++)
                    {
                        // spacer bars are always the lower of the 
                        var max = Math.Min(currentPeak.Max, nextPeak.Max);
                        var min = Math.Max(currentPeak.Min, nextPeak.Min);

                        var lineHeight = settings.TopHeight * max;
                        g.DrawLine(settings.TopSpacerPen, x, midPoint, x, midPoint - lineHeight);
                        lineHeight = settings.BottomHeight * min;
                        g.DrawLine(settings.BottomSpacerPen, x, midPoint, x, midPoint - lineHeight); 
                        x++;
                    }
                    currentPeak = nextPeak;
                }
            }
            return b;
        }

    }
}
