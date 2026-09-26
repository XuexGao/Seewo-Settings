using SeewoAssistant.Core.Abstractions;

namespace SeewoAssistant.Core.Services.VirtualCamera;

/// <summary>
/// Builds the BGRA frames that get pushed into the virtual camera.
/// </summary>
/// <remarks>
/// Every generator produces a full-size buffer at the requested resolution. The
/// channel's maximum is 1920x1080, so larger sources are scaled down with
/// <see cref="FrameBuffer.FitInto"/> rather than rejected.
/// </remarks>
public sealed class FrameSourceFactory
{
    private readonly IAppLogger _logger;
    private int _testPatternPhase;

    public FrameSourceFactory(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;

    /// <summary>A single flat colour. <paramref name="color"/> is <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public FrameBuffer CreateSolidColor(string color, int width = DefaultWidth, int height = DefaultHeight)
    {
        var (a, r, g, b) = ParseColor(color);
        var frame = new FrameBuffer(width, height);
        frame.Fill(b, g, r, a);
        return frame;
    }

    /// <summary>
    /// An animated pattern used to confirm the camera is live and to eyeball
    /// latency: colour bars plus a sweep bar that moves one step per call.
    /// </summary>
    public FrameBuffer CreateTestPattern(int width = DefaultWidth, int height = DefaultHeight)
    {
        var frame = new FrameBuffer(width, height);

        // Standard 75% colour bars, top two thirds.
        (byte R, byte G, byte B)[] bars =
        [
            (192, 192, 192), // grey
            (192, 192, 0),   // yellow
            (0, 192, 192),   // cyan
            (0, 192, 0),     // green
            (192, 0, 192),   // magenta
            (192, 0, 0),     // red
            (0, 0, 192),     // blue
            (16, 16, 16),    // near black
        ];

        var barAreaHeight = (int)(height * 0.66);
        var barWidth = (double)width / bars.Length;

        for (var y = 0; y < barAreaHeight; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = Math.Min(bars.Length - 1, (int)(x / barWidth));
                var (r, g, b) = bars[index];
                frame.SetPixel(x, y, b, g, r);
            }
        }

        // Greyscale ramp along the bottom third, so banding and colour shifts in
        // the RGB to NV12 conversion are visible.
        for (var y = barAreaHeight; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var level = (byte)(x * 255 / Math.Max(1, width - 1));
                frame.SetPixel(x, y, level, level, level);
            }
        }

        // Sweep bar: advances on every call, so a frozen camera is obvious.
        var phase = Interlocked.Increment(ref _testPatternPhase);
        var sweepX = (int)(phase * (width / 60.0)) % width;
        var sweepWidth = Math.Max(4, width / 160);
        frame.FillRect(sweepX, 0, sweepWidth, height, 0, 0, 255);

        // A block that walks vertically as well, making frame drops visible.
        var blockY = (int)((phase * 7.0) % Math.Max(1, height - 80));
        frame.FillRect(width / 2 - 40, blockY, 80, 80, 255, 255, 255);

        return frame;
    }

    /// <summary>
    /// Loads an image file and scales it to fit the frame, letterboxing as needed.
    /// Returns null when the file cannot be decoded; the caller falls back to the
    /// test pattern so the camera never stops producing video.
    /// </summary>
    public FrameBuffer? TryCreateFromImage(string path, int width = DefaultWidth, int height = DefaultHeight)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            _logger.Warn($"Virtual camera image not found: {path}");
            return null;
        }

        try
        {
            var decoded = ImageDecoder.DecodeToBgra(path);
            if (decoded is null)
            {
                _logger.Warn($"Could not decode image: {path}");
                return null;
            }

            return decoded.FitInto(width, height);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load virtual camera image '{path}'.", ex);
            return null;
        }
    }

    /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>. Falls back to black.</summary>
    public static (byte A, byte R, byte G, byte B) ParseColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return (255, 0, 0, 0);
        }

        var text = color.Trim().TrimStart('#');

        try
        {
            switch (text.Length)
            {
                case 3:
                {
                    var r = (byte)(Convert.ToInt32(new string(text[0], 2), 16));
                    var g = (byte)(Convert.ToInt32(new string(text[1], 2), 16));
                    var b = (byte)(Convert.ToInt32(new string(text[2], 2), 16));
                    return (255, r, g, b);
                }

                case 6:
                {
                    var value = Convert.ToInt32(text, 16);
                    return (255, (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
                }

                case 8:
                {
                    var value = Convert.ToUInt32(text, 16);
                    return ((byte)((value >> 24) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
                }

                default:
                    return (255, 0, 0, 0);
            }
        }
        catch (FormatException)
        {
            return (255, 0, 0, 0);
        }
        catch (OverflowException)
        {
            return (255, 0, 0, 0);
        }
    }
}
