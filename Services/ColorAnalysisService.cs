using WorkProgress.Models;
using SkiaSharp;

namespace WorkProgress.Services;

public class ColorAnalysisService
{
    public ColorAnalysisResult Analyze(byte[] imageBytes)
    {
        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            return new ColorAnalysisResult();

        int carpetPixels = 0;
        int blackPixels = 0;
        int redPixels = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                if (pixel.Alpha < 128) continue;

                float h, s, v;
                RgbToHsv(pixel.Red, pixel.Green, pixel.Blue, out h, out s, out v);

                // Skip white/near-white background pixels
                if (v > 0.85f && s < 0.15f)
                    continue;

                carpetPixels++;

                if (IsRedColor(h, s, v))
                {
                    redPixels++;
                }
                else if (v < 0.4f && s < 0.5f)
                {
                    blackPixels++;
                }
            }
        }

        if (carpetPixels == 0)
            return new ColorAnalysisResult();

        decimal normalPct = Math.Round((decimal)blackPixels / carpetPixels * 100, 2);
        decimal otPct = Math.Round((decimal)redPixels / carpetPixels * 100, 2);

        return new ColorAnalysisResult
        {
            NormalPercent = normalPct,
            OtPercent = otPct,
            TotalPercent = Math.Round(normalPct + otPct, 2)
        };
    }

    private bool IsRedColor(float h, float s, float v)
    {
        return (h < 25 || h > 335) && s > 0.25f && v > 0.1f;
    }

    private void RgbToHsv(byte r, byte g, byte b, out float h, out float s, out float v)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0)
        {
            h = 0;
        }
        else if (max == rf)
        {
            h = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            h = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            h = 60 * (((rf - gf) / delta) + 4);
        }

        if (h < 0) h += 360;
    }
}
