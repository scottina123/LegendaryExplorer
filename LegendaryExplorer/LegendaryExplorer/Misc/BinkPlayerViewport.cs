using System;
using System.Drawing;

namespace LegendaryExplorer.Misc;

internal static class BinkPlayerViewport
{
    private const int NativeSliderHeight = 16;

    /// <summary>
    /// Matches RAD's centered, aspect-preserving video layout in the native client window.
    /// </summary>
    public static Rectangle CalculateVideoBounds(Size clientSize, Size movieSize)
    {
        if (clientSize.Width <= 0 || clientSize.Height <= 0
            || movieSize.Width <= 0 || movieSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        long heightConstrainedWidth = (long)clientSize.Height * movieSize.Width / movieSize.Height;
        int width;
        int height;
        if (heightConstrainedWidth <= clientSize.Width)
        {
            width = Math.Max(1, (int)heightConstrainedWidth);
            height = clientSize.Height;
        }
        else
        {
            width = clientSize.Width;
            height = Math.Max(1, (int)((long)clientSize.Width * movieSize.Height / movieSize.Width));
        }

        return new Rectangle((clientSize.Width - width) / 2, (clientSize.Height - height) / 2, width, height);
    }

    /// <summary>
    /// RAD's default Direct3D 11 player draws its private seek overlay within the bottom
    /// 16 client pixels of the displayed video, including when the video is letterboxed.
    /// </summary>
    public static Rectangle CalculateSliderBounds(Size clientSize, Size movieSize)
    {
        Rectangle videoBounds = CalculateVideoBounds(clientSize, movieSize);
        if (videoBounds.IsEmpty)
        {
            return Rectangle.Empty;
        }

        int height = Math.Min(NativeSliderHeight, videoBounds.Height);
        return new Rectangle(videoBounds.Left, videoBounds.Bottom - height, videoBounds.Width, height);
    }

    public static Point CalculateSeekPoint(Size clientSize, Size movieSize, double position)
    {
        Rectangle videoBounds = CalculateVideoBounds(clientSize, movieSize);
        if (videoBounds.IsEmpty)
        {
            return Point.Empty;
        }

        position = double.IsNaN(position) ? 0 : Math.Clamp(position, 0, 1);
        // The native slider rejects the first and last eight mapped client pixels.
        // Keep endpoint scrubs inside that hit area so they do not toggle playback.
        long scaledMargin = ((long)9 * videoBounds.Width + clientSize.Width - 1) / clientSize.Width;
        int margin = (int)Math.Min((videoBounds.Width - 1) / 2, scaledMargin);
        int videoX = Math.Clamp((int)Math.Round((videoBounds.Width - 1) * position),
            margin, videoBounds.Width - 1 - margin);
        int x = videoBounds.Left + videoX;

        // RAD scales the mouse Y coordinate by client width before testing the slider.
        // A very tall client needs a synthetic point beyond its bottom edge to pass that
        // test. Direct window messages support this without moving the desktop cursor.
        long scaledHeight = (long)(clientSize.Height - 1) * videoBounds.Height;
        long sliderY = videoBounds.Top + (scaledHeight + clientSize.Width - 1) / clientSize.Width;
        long y = Math.Max(clientSize.Height - 2, sliderY);
        return new Point(Math.Clamp(x, 0, short.MaxValue), (int)Math.Clamp(y, 0, short.MaxValue));
    }
}
