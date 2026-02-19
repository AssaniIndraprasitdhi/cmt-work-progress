using OpenCvSharp;
using WorkProgress.Models;

namespace WorkProgress.Services;

public class TemplateMaskService
{
    private readonly CvAnalysisConfig _c;
    private readonly IWebHostEnvironment _env;
    private readonly DbService _db;

    public TemplateMaskService(IWebHostEnvironment env, DbService db)
    {
        _c = new CvAnalysisConfig { SaveDebug = true };
        _env = env;
        _db = db;
    }

    public async Task<OrderTemplate> CreateTemplate(string orderNo, byte[] templateImageBytes)
    {
        using var raw = Cv2.ImDecode(templateImageBytes, ImreadModes.Color);
        if (raw.Empty()) throw new ArgumentException("Invalid image");

        double scale = (double)_c.AnalysisWidth / raw.Cols;
        using var img = new Mat();
        Cv2.Resize(raw, img, new Size(_c.AnalysisWidth, (int)(raw.Rows * scale)),
                   0, 0, InterpolationFlags.Area);

        using var paintableMask = GeneratePaintableMask(img);
        int paintablePixels = Cv2.CountNonZero(paintableMask);

        var templatesDir = Path.Combine(_env.WebRootPath, "templates");
        Directory.CreateDirectory(templatesDir);

        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safe = orderNo.Trim().Replace("/", "_").Replace(" ", "_").Replace("\\", "_");

        var imgName = $"{ts}_{safe}_template.jpg";
        var maskName = $"{ts}_{safe}_paintable.png";

        Cv2.ImWrite(Path.Combine(templatesDir, imgName), img);
        Cv2.ImWrite(Path.Combine(templatesDir, maskName), paintableMask);

        if (_c.SaveDebug)
        {
            var debugDir = _c.DebugPath;
            Directory.CreateDirectory(debugDir);
            Cv2.ImWrite(Path.Combine(debugDir, $"{ts}_template_input.jpg"), img);
            Cv2.ImWrite(Path.Combine(debugDir, $"{ts}_template_paintable.png"), paintableMask);

            using var overlay = img.Clone();
            using var green = new Mat(img.Rows, img.Cols, MatType.CV_8UC3, new Scalar(0, 200, 0));
            green.CopyTo(overlay, paintableMask);
            using var blended = new Mat();
            Cv2.AddWeighted(img, 0.5, overlay, 0.5, 0, blended);
            Cv2.ImWrite(Path.Combine(debugDir, $"{ts}_template_overlay.jpg"), blended);
        }

        var template = new OrderTemplate
        {
            OrderNo = orderNo.Trim(),
            TemplateImagePath = $"/templates/{imgName}",
            PaintableMaskPath = $"/templates/{maskName}",
            PaintablePixels = paintablePixels,
            TemplateWidth = img.Cols,
            TemplateHeight = img.Rows
        };

        Console.WriteLine($"[Template] Created for {orderNo}: " +
            $"size={img.Cols}x{img.Rows} paintablePx={paintablePixels} " +
            $"ratio={paintablePixels / (double)(img.Cols * img.Rows):P1}");

        return await _db.SaveTemplate(template);
    }

    private Mat GeneratePaintableMask(Mat bgr)
    {
        int rows = bgr.Rows, cols = bgr.Cols;
        double imageArea = rows * (double)cols;
        int borderPx = Math.Max(2, (int)(Math.Min(rows, cols) * _c.BorderTrimPct));

        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.Split(hsv, out Mat[] hsvCh);

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        try
        {
            using var coloredMask = new Mat();
            Cv2.Threshold(hsvCh[1], coloredMask, 25, 255, ThresholdTypes.Binary);

            using var darkMask = new Mat();
            Cv2.Threshold(hsvCh[2], darkMask, 200, 255, ThresholdTypes.BinaryInv);

            using var nonWhite = new Mat();
            Cv2.BitwiseOr(coloredMask, darkMask, nonWhite);

            using var blurred = new Mat();
            Cv2.GaussianBlur(gray, blurred, new Size(_c.PlanBlurSize, _c.PlanBlurSize), 0);
            using var adaptiveThresh = new Mat();
            Cv2.AdaptiveThreshold(blurred, adaptiveThresh, 255,
                AdaptiveThresholdTypes.GaussianC,
                ThresholdTypes.BinaryInv,
                _c.PlanAdaptiveBlock, _c.PlanAdaptiveC);

            using var combined = new Mat();
            Cv2.BitwiseOr(nonWhite, adaptiveThresh, combined);

            using var closeK = Cv2.GetStructuringElement(MorphShapes.Rect,
                new Size(_c.PlanCloseKernel, _c.PlanCloseKernel));
            using var closed = new Mat();
            Cv2.MorphologyEx(combined, closed, MorphTypes.Close, closeK);

            using var erodeK = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            using var eroded = new Mat();
            Cv2.Erode(closed, eroded, erodeK, iterations: 2);

            Cv2.Rectangle(eroded, new Rect(0, 0, cols, borderPx), new Scalar(0), -1);
            Cv2.Rectangle(eroded, new Rect(0, rows - borderPx, cols, borderPx), new Scalar(0), -1);
            Cv2.Rectangle(eroded, new Rect(0, 0, borderPx, rows), new Scalar(0), -1);
            Cv2.Rectangle(eroded, new Rect(cols - borderPx, 0, borderPx, rows), new Scalar(0), -1);

            Cv2.FindContours(eroded, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            var carpetMask = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));

            if (contours.Length == 0)
            {
                Console.WriteLine("[Template] No contours found");
                return carpetMask;
            }

            int bestIdx = -1;
            double bestArea = 0;
            int rejectedEdge = 0;

            for (int i = 0; i < contours.Length; i++)
            {
                double area = Cv2.ContourArea(contours[i]);
                if (area < imageArea * _c.MinPlanFraction) continue;

                var bbox = Cv2.BoundingRect(contours[i]);
                bool touchesEdge = bbox.X <= 0 || bbox.Y <= 0 ||
                                   bbox.X + bbox.Width >= cols ||
                                   bbox.Y + bbox.Height >= rows;
                if (touchesEdge)
                {
                    rejectedEdge++;
                    continue;
                }

                if (area > bestArea)
                {
                    bestArea = area;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0 && rejectedEdge > 0)
            {
                for (int i = 0; i < contours.Length; i++)
                {
                    double area = Cv2.ContourArea(contours[i]);
                    if (area > bestArea) { bestArea = area; bestIdx = i; }
                }
            }

            if (bestIdx < 0) return carpetMask;

            Cv2.DrawContours(carpetMask, contours, bestIdx, new Scalar(255), -1);

            int carpetPx = Cv2.CountNonZero(carpetMask);
            if (carpetPx == 0) return carpetMask;

            Console.WriteLine($"[Template] CarpetRegion: carpetPx={carpetPx} " +
                $"ratio={carpetPx / imageArea:P1} edgeRejected={rejectedEdge}");

            // --- Step 2: Detect grid lines using morphological top-hat + black-hat ---
            // Top-hat detects bright thin features, black-hat detects dark thin features.
            // The kernel size must be larger than the grid line width so that
            // opening/closing removes the grid lines while preserving block interiors.
            using var blurGray = new Mat();
            Cv2.GaussianBlur(gray, blurGray, new Size(3, 3), 0);

            int openSize = 2 * (2 * _c.GridThickRadius + 1) + 1;
            using var hatK = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                new Size(openSize, openSize));

            using var morphOpened = new Mat();
            using var morphClosed = new Mat();
            Cv2.MorphologyEx(blurGray, morphOpened, MorphTypes.Open, hatK);
            Cv2.MorphologyEx(blurGray, morphClosed, MorphTypes.Close, hatK);

            using var topHat = new Mat();
            Cv2.Subtract(blurGray, morphOpened, topHat);
            using var blackHat = new Mat();
            Cv2.Subtract(morphClosed, blurGray, blackHat);

            using var gridIntensity = new Mat();
            Cv2.Add(topHat, blackHat, gridIntensity);

            using var gridInCarpet = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));
            gridIntensity.CopyTo(gridInCarpet, carpetMask);

            using var gridMask = new Mat();
            Cv2.Threshold(gridInCarpet, gridMask, 0, 255,
                ThresholdTypes.Binary | ThresholdTypes.Otsu);

            using var gridDilK = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Dilate(gridMask, gridMask, gridDilK, iterations: 1);

            using var noGrid = new Mat();
            Cv2.BitwiseNot(gridMask, noGrid);
            var paintable = new Mat();
            Cv2.BitwiseAnd(carpetMask, noGrid, paintable);

            using var cleanK = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
            Cv2.MorphologyEx(paintable, paintable, MorphTypes.Open, cleanK);
            Cv2.MorphologyEx(paintable, paintable, MorphTypes.Close, cleanK);

            int paintablePx = Cv2.CountNonZero(paintable);
            int gridPx = carpetPx - paintablePx;
            double gridRatio = (double)gridPx / carpetPx;

            Console.WriteLine($"[Template] GridDetection: openSize={openSize} " +
                $"gridPx={gridPx} paintablePx={paintablePx} " +
                $"paintableRatio={paintablePx / (double)carpetPx:P1} " +
                $"gridRatio={gridRatio:P1}");

            if (gridRatio < 0.05 || gridRatio > 0.50)
            {
                Console.WriteLine($"[Template] Grid ratio {gridRatio:P1} outside [5%-50%], " +
                    $"using carpet mask directly");
                paintable.Dispose();
                return carpetMask;
            }

            carpetMask.Dispose();
            return paintable;
        }
        finally
        {
            foreach (var c in hsvCh) c.Dispose();
        }
    }

}
