namespace SeewoAssistant.Core.Services.VirtualCamera;

/// <summary>
/// A BGRA32 pixel buffer with a known stride. Kept free of any UI or OS dependency
/// so the drawing and scaling code is unit testable on any platform.
/// </summary>
public sealed class FrameBuffer
{
    public FrameBuffer(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
        Stride = width * 4;
        Pixels = new byte[Stride * height];
    }

    public FrameBuffer(int width, int height, byte[] pixels, int stride)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    /// <summary>Writes a BGRA pixel, ignoring out-of-range coordinates.</summary>
    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a = 255)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }

        var offset = (y * Stride) + (x * 4);
        Pixels[offset + 0] = b;
        Pixels[offset + 1] = g;
        Pixels[offset + 2] = r;
        Pixels[offset + 3] = a;
    }

    public void Fill(byte b, byte g, byte r, byte a = 255)
    {
        for (var y = 0; y < Height; y++)
        {
            var rowStart = y * Stride;
            for (var x = 0; x < Width; x++)
            {
                var offset = rowStart + (x * 4);
                Pixels[offset + 0] = b;
                Pixels[offset + 1] = g;
                Pixels[offset + 2] = r;
                Pixels[offset + 3] = a;
            }
        }
    }

    public void FillRect(int x, int y, int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(Width, x + width);
        var y1 = Math.Min(Height, y + height);

        for (var py = y0; py < y1; py++)
        {
            var rowStart = py * Stride;
            for (var px = x0; px < x1; px++)
            {
                var offset = rowStart + (px * 4);
                Pixels[offset + 0] = b;
                Pixels[offset + 1] = g;
                Pixels[offset + 2] = r;
                Pixels[offset + 3] = a;
            }
        }
    }

    /// <summary>
    /// Scales this buffer to the requested size using bilinear sampling with
    /// edge clamping. Correct for both upscaling and downscaling.
    /// </summary>
    public FrameBuffer Scale(int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }

        var result = new FrameBuffer(targetWidth, targetHeight);

        if (targetWidth == Width && targetHeight == Height)
        {
            Buffer.BlockCopy(Pixels, 0, result.Pixels, 0, Pixels.Length);
            return result;
        }

        // Map destination pixel centres back into source space. Sampling at the
        // centre avoids the half-pixel shift that makes naive scalers look soft.
        var xRatio = (double)Width / targetWidth;
        var yRatio = (double)Height / targetHeight;

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = ((y + 0.5) * yRatio) - 0.5;
            if (sourceY < 0)
            {
                sourceY = 0;
            }

            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(y0 + 1, Height - 1);
            var yWeight = sourceY - y0;
            if (y0 >= Height)
            {
                y0 = Height - 1;
                y1 = Height - 1;
                yWeight = 0;
            }

            var destinationRow = y * result.Stride;

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = ((x + 0.5) * xRatio) - 0.5;
                if (sourceX < 0)
                {
                    sourceX = 0;
                }

                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(x0 + 1, Width - 1);
                var xWeight = sourceX - x0;
                if (x0 >= Width)
                {
                    x0 = Width - 1;
                    x1 = Width - 1;
                    xWeight = 0;
                }

                var topLeft = (y0 * Stride) + (x0 * 4);
                var topRight = (y0 * Stride) + (x1 * 4);
                var bottomLeft = (y1 * Stride) + (x0 * 4);
                var bottomRight = (y1 * Stride) + (x1 * 4);
                var destination = destinationRow + (x * 4);

                for (var channel = 0; channel < 4; channel++)
                {
                    var top = (Pixels[topLeft + channel] * (1 - xWeight)) + (Pixels[topRight + channel] * xWeight);
                    var bottom = (Pixels[bottomLeft + channel] * (1 - xWeight)) + (Pixels[bottomRight + channel] * xWeight);
                    var value = (top * (1 - yWeight)) + (bottom * yWeight);
                    result.Pixels[destination + channel] = (byte)Math.Clamp(Math.Round(value), 0, 255);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Scales to fit inside the target box while preserving aspect ratio, centring
    /// the result on a solid background. Avoids the distortion that stretching to
    /// 16:9 would cause for a non-16:9 source.
    /// </summary>
    public FrameBuffer FitInto(int targetWidth, int targetHeight, byte backgroundB = 0, byte backgroundG = 0, byte backgroundR = 0)
    {
        var result = new FrameBuffer(targetWidth, targetHeight);
        result.Fill(backgroundB, backgroundG, backgroundR);

        var scale = Math.Min((double)targetWidth / Width, (double)targetHeight / Height);
        var scaledWidth = Math.Max(1, (int)Math.Round(Width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(Height * scale));

        var scaled = Scale(scaledWidth, scaledHeight);
        var offsetX = (targetWidth - scaledWidth) / 2;
        var offsetY = (targetHeight - scaledHeight) / 2;

        for (var y = 0; y < scaledHeight; y++)
        {
            var sourceRow = y * scaled.Stride;
            var destinationRow = ((y + offsetY) * result.Stride) + (offsetX * 4);
            Buffer.BlockCopy(scaled.Pixels, sourceRow, result.Pixels, destinationRow, scaled.Stride);
        }

        return result;
    }
}
