using WorkProgress.Models;

namespace WorkProgress.Services;

public class ColorAnalysisService
{
    private readonly CvProgressAnalyzer _cv = new(new CvAnalysisConfig
    {
        SaveDebug = true,
        DebugPath = "wwwroot/debug"
    });

    public ColorAnalysisResult Analyze(byte[] imageBytes)
    {
        return _cv.Analyze(imageBytes);
    }

    public ColorAnalysisResult Analyze(byte[] imageBytes, ColorProfile? profile)
    {
        return _cv.Analyze(imageBytes, profile);
    }

    public ColorAnalysisResult AnalyzeWithTemplate(byte[] imageBytes,
        string templateImagePath, string paintableMaskPath, ColorProfile? profile = null)
    {
        return _cv.AnalyzeWithTemplate(imageBytes, templateImagePath, paintableMaskPath, profile);
    }

    // ── Kept for DbService.SaveColorProfile ──
    public static (float H, float S, float V) HexToHsv(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);

        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;

        float v = max;
        float s = max == 0 ? 0 : delta / max;
        float h;

        if (delta == 0)
            h = 0;
        else if (max == rf)
            h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf)
            h = 60 * (((bf - rf) / delta) + 2);
        else
            h = 60 * (((rf - gf) / delta) + 4);

        if (h < 0) h += 360;
        return (h, s, v);
    }
}
