using OpenCvSharp;
using WorkProgress.Models;

namespace WorkProgress.Services;

public class CvAnalysisConfig
{
    public int PlanBlurSize { get; set; } = 5;
    public int PlanAdaptiveBlock { get; set; } = 51;
    public double PlanAdaptiveC { get; set; } = 5;
    public int PlanCloseKernel { get; set; } = 31;
    public double MinPlanFraction { get; set; } = 0.05;
    public double BorderTrimPct { get; set; } = 0.03;
    public double MinPlanRatio { get; set; } = 0.15;
    public double MaxPlanRatio { get; set; } = 0.95;

    public double ClaheClip { get; set; } = 3.0;
    public int PreBlurSize { get; set; } = 3;

    public int BlackMaxL { get; set; } = 90;
    public int BlackMaxSat { get; set; } = 110;

    public int GridThickRadius { get; set; } = 2;

    public int RedLowH1 { get; set; } = 0;
    public int RedHighH1 { get; set; } = 12;
    public int RedLowH2 { get; set; } = 168;
    public int RedHighH2 { get; set; } = 180;
    public int RedMinS { get; set; } = 60;
    public int RedMinV { get; set; } = 40;
    public int RedCloseSize { get; set; } = 5;

    public int BlackCloseSize { get; set; } = 5;

    public int MinComponentArea { get; set; } = 100;
    public int MinComponentDim { get; set; } = 8;
    public double MaxAspectRatio { get; set; } = 4.5;
    public double MedCompFraction { get; set; } = 0.005;

    public int AnalysisWidth { get; set; } = 1000;
    public double CompleteThreshold { get; set; } = 99.5;

    public bool SaveDebug { get; set; } = false;
    public string DebugPath { get; set; } = "wwwroot/debug";
}

public class CvProgressAnalyzer
{
    public const string CV_VERSION = "v6-effectiveMask-fill";
    private readonly CvAnalysisConfig _c;

    public CvProgressAnalyzer() : this(new CvAnalysisConfig()) { }
    public CvProgressAnalyzer(CvAnalysisConfig config) { _c = config; }

    public ColorAnalysisResult Analyze(byte[] imageBytes)
    {
        using var raw = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (raw.Empty()) return new ColorAnalysisResult();

        double scale = (double)_c.AnalysisWidth / raw.Cols;
        using var img = new Mat();
        Cv2.Resize(raw, img, new Size(_c.AnalysisWidth, (int)(raw.Rows * scale)),
                   0, 0, InterpolationFlags.Area);

        using var planMask = ExtractPlanMask(img);
        int planPx = Cv2.CountNonZero(planMask);
        if (planPx == 0) return new ColorAnalysisResult();

        using var norm = NormalizeLighting(img);

        // Subtract grid lines from denominator so painted / (plan − grid) ≈ 100%
        using var gridMask = BuildGridMask(img, planMask);
        using var notGrid = new Mat();
        Cv2.BitwiseNot(gridMask, notGrid);
        using var effectiveMask = new Mat();
        Cv2.BitwiseAnd(planMask, notGrid, effectiveMask);
        int effectivePx = Cv2.CountNonZero(effectiveMask);

        // Use effectiveMask for denominator, planMask for detection region
        var denominator = effectivePx > 0 ? effectiveMask : planMask;

        var (blackRaw, blackFiltered) = DetectBlack(norm, planMask, planPx);
        var (redRaw, redFiltered) = DetectRed(norm, planMask, planPx);

        using var grown = new Mat();
        Cv2.BitwiseOr(blackFiltered, redFiltered, grown);

        NearestSeedAssign(grown, blackFiltered, redFiltered, out var blackFinal, out var redFinal);

        var result = ComputePercent(blackFinal, redFinal, denominator);

        Console.WriteLine($"[CV] plan={planPx} effective={effectivePx} " +
            $"blackRaw={Cv2.CountNonZero(blackRaw)} blackFilt={Cv2.CountNonZero(blackFiltered)} " +
            $"blackFinal={Cv2.CountNonZero(blackFinal)} " +
            $"redRaw={Cv2.CountNonZero(redRaw)} redFilt={Cv2.CountNonZero(redFiltered)} " +
            $"redFinal={Cv2.CountNonZero(redFinal)} " +
            $"-> normal={result.NormalPercent}% ot={result.OtPercent}% total={result.TotalPercent}% " +
            $"isComplete={result.IsComplete}");

        if (_c.SaveDebug)
            SaveDebugImages(img, denominator, blackRaw, blackFiltered, blackFinal,
                           redRaw, redFiltered, redFinal, "default");

        blackRaw.Dispose(); blackFiltered.Dispose();
        redRaw.Dispose(); redFiltered.Dispose();
        blackFinal.Dispose(); redFinal.Dispose();
        return result;
    }

    // ════════════════════════════════════════════════════════════════
    //  AnalyzeWithTemplate  –  v6-effectiveMask-fill
    // ════════════════════════════════════════════════════════════════
    public ColorAnalysisResult AnalyzeWithTemplate(byte[] imageBytes,
        string templateImagePath, string paintableMaskPath, ColorProfile? profile = null)
    {
        Console.WriteLine("=== CV VERSION: effectiveMask+fill enabled ===");

        using var templateImg = Cv2.ImRead(templateImagePath, ImreadModes.Color);
        using var paintableMask = Cv2.ImRead(paintableMaskPath, ImreadModes.Grayscale);

        if (templateImg.Empty() || paintableMask.Empty())
        {
            Console.WriteLine("[CV-Template] Failed to load template files, falling back");
            return Analyze(imageBytes, profile);
        }

        using var raw = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (raw.Empty()) return new ColorAnalysisResult();

        using var resized = new Mat();
        Cv2.Resize(raw, resized, templateImg.Size(), 0, 0, InterpolationFlags.Area);

        using var aligned = AlignToTemplate(resized, templateImg);

        // ── 1) effectivePaintableMask = paintableMask − gridMask ──
        using var gridMask = BuildGridMaskFromTemplate(templateImg, paintableMask);
        using var notGrid = new Mat();
        Cv2.BitwiseNot(gridMask, notGrid);
        using var effectiveMask = new Mat();
        Cv2.BitwiseAnd(paintableMask, notGrid, effectiveMask);

        int paintPx = Cv2.CountNonZero(paintableMask);
        int gridPx = Cv2.CountNonZero(gridMask);
        int effectivePx = Cv2.CountNonZero(effectiveMask);

        Console.WriteLine($"[CV-Template] paintablePx={paintPx} gridPx={gridPx} " +
            $"effectivePx={effectivePx} gridRatio={gridPx / (double)Math.Max(1, paintPx):P1}");

        if (effectivePx == 0) return new ColorAnalysisResult();

        // ── 2) Detect colours within effectiveMask only ──
        using var norm = NormalizeLighting(aligned);

        Mat normalRaw, normalFiltered, otRaw, otFiltered;

        if (profile != null && profile.Colors.Count > 0)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(norm, hsv, ColorConversionCodes.BGR2HSV);

            float tol = 0.05f + (profile.Tolerance / 100f) * 0.95f;
            int hRange = Math.Max(8, (int)(tol * 40));
            int svRange = Math.Max(20, (int)(tol * 110));

            var normalColors = profile.Colors.Where(c => c.ColorGroup == "normal").ToList();
            var otColors = profile.Colors.Where(c => c.ColorGroup == "ot").ToList();

            normalRaw = BuildProfileMask(hsv, effectiveMask, normalColors, hRange, svRange, effectivePx);
            otRaw = BuildProfileMask(hsv, effectiveMask, otColors, hRange, svRange, effectivePx);
            normalFiltered = RemoveGridLines(normalRaw.Clone(), effectivePx);
            otFiltered = RemoveGridLines(otRaw.Clone(), effectivePx);
        }
        else
        {
            (normalRaw, normalFiltered) = DetectBlack(norm, effectiveMask, effectivePx);
            (otRaw, otFiltered) = DetectRed(norm, effectiveMask, effectivePx);
        }

        // ── 3) First NearestSeedAssign ──
        using var grown = new Mat();
        Cv2.BitwiseOr(normalFiltered, otFiltered, grown);
        NearestSeedAssign(grown, normalFiltered, otFiltered, out var normalFinal, out var otFinal);

        // ── 4) Fill small holes: close + dilate, clamp to effectiveMask ──
        using var painted = new Mat();
        Cv2.BitwiseOr(normalFinal, otFinal, painted);

        using var fillK = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(painted, painted, MorphTypes.Close, fillK);
        Cv2.Dilate(painted, painted, fillK, iterations: 1);
        Cv2.BitwiseAnd(painted, effectiveMask, painted);

        // Re-split filled area back to normal / ot
        NearestSeedAssign(painted, normalFiltered, otFiltered,
            out var normalFilled, out var otFilled);

        // ── 5) Compute percent using effectiveMask as denominator ──
        var result = ComputePercent(normalFilled, otFilled, effectiveMask);

        // ── 6) Logging ──
        Console.WriteLine($"[CV-Template] normalRaw={Cv2.CountNonZero(normalRaw)} " +
            $"normalFilt={Cv2.CountNonZero(normalFiltered)} " +
            $"normalFinal={Cv2.CountNonZero(normalFinal)} " +
            $"normalFilled={Cv2.CountNonZero(normalFilled)}");
        Console.WriteLine($"[CV-Template] otRaw={Cv2.CountNonZero(otRaw)} " +
            $"otFilt={Cv2.CountNonZero(otFiltered)} " +
            $"otFinal={Cv2.CountNonZero(otFinal)} " +
            $"otFilled={Cv2.CountNonZero(otFilled)}");
        Console.WriteLine($"[CV-Template] painted={Cv2.CountNonZero(painted)} " +
            $"effectivePx={effectivePx} " +
            $"-> normal={result.NormalPercent}% ot={result.OtPercent}% " +
            $"total={result.TotalPercent}% isComplete={result.IsComplete}");

        // ── 7) Debug images ──
        if (_c.SaveDebug)
        {
            SaveDebugImages(aligned, effectiveMask,
                normalRaw, normalFiltered, normalFilled,
                otRaw, otFiltered, otFilled, "template");
            try
            {
                Directory.CreateDirectory(_c.DebugPath);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                Cv2.ImWrite(Path.Combine(_c.DebugPath,
                    $"{ts}_template_0a_gridmask.png"), gridMask);
                Cv2.ImWrite(Path.Combine(_c.DebugPath,
                    $"{ts}_template_0b_effective_mask.png"), effectiveMask);
                Cv2.ImWrite(Path.Combine(_c.DebugPath,
                    $"{ts}_template_0c_painted_filled.png"), painted);

                // Overlay: aligned + template blend with effectiveMask contour
                using var alignOverlay = new Mat();
                Cv2.AddWeighted(templateImg, 0.4, aligned, 0.6, 0, alignOverlay);
                using var maskClone = effectiveMask.Clone();
                Cv2.FindContours(maskClone, out Point[][] cts, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                Cv2.DrawContours(alignOverlay, cts, -1, new Scalar(0, 255, 0), 2);
                Cv2.ImWrite(Path.Combine(_c.DebugPath,
                    $"{ts}_template_0d_align_overlay.jpg"), alignOverlay);
            }
            catch { }
        }

        // ── Dispose ──
        normalRaw.Dispose(); normalFiltered.Dispose();
        otRaw.Dispose(); otFiltered.Dispose();
        normalFinal.Dispose(); otFinal.Dispose();
        normalFilled.Dispose(); otFilled.Dispose();
        return result;
    }

    // ════════════════════════════════════════════════════════════════
    //  BuildGridMaskFromTemplate  –  Canny(50,150) + Dilate(3×3)
    // ════════════════════════════════════════════════════════════════
    private Mat BuildGridMaskFromTemplate(Mat templateBgr, Mat paintableMask)
    {
        using var gray = new Mat();
        Cv2.CvtColor(templateBgr, gray, ColorConversionCodes.BGR2GRAY);

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);

        using var dilK = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        var gridMask = new Mat();
        Cv2.Dilate(edges, gridMask, dilK, iterations: 1);

        // Restrict to paintable area
        Cv2.BitwiseAnd(gridMask, paintableMask, gridMask);

        int gridPx = Cv2.CountNonZero(gridMask);
        int maskPx = Cv2.CountNonZero(paintableMask);
        double ratio = maskPx > 0 ? (double)gridPx / maskPx : 0;

        Console.WriteLine($"[CV-Template] BuildGridMask: gridPx={gridPx} " +
            $"maskPx={maskPx} ratio={ratio:P1}");

        if (ratio > 0.50)
        {
            Console.WriteLine($"[CV-Template] Grid ratio too high ({ratio:P1}), disabling");
            gridMask.SetTo(new Scalar(0));
        }

        return gridMask;
    }

    private Mat AlignToTemplate(Mat currentImg, Mat templateImg)
    {
        using var grayTemplate = new Mat();
        using var grayCurrent = new Mat();
        Cv2.CvtColor(templateImg, grayTemplate, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(currentImg, grayCurrent, ColorConversionCodes.BGR2GRAY);

        using var orb = ORB.Create(5000);

        using var descTemplate = new Mat();
        using var descCurrent = new Mat();
        orb.DetectAndCompute(grayTemplate, null, out KeyPoint[] kpTemplate, descTemplate);
        orb.DetectAndCompute(grayCurrent, null, out KeyPoint[] kpCurrent, descCurrent);

        Console.WriteLine($"[CV-Align] keypoints: template={kpTemplate.Length} current={kpCurrent.Length}");

        if (kpTemplate.Length < 10 || kpCurrent.Length < 10 ||
            descTemplate.Empty() || descCurrent.Empty())
        {
            Console.WriteLine("[CV-Align] Insufficient keypoints, using resize-only");
            return currentImg.Clone();
        }

        using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
        var rawMatches = matcher.KnnMatch(descCurrent, descTemplate, 2);

        var goodMatches = new List<DMatch>();
        foreach (var pair in rawMatches)
        {
            if (pair.Length == 2 && pair[0].Distance < 0.75f * pair[1].Distance)
                goodMatches.Add(pair[0]);
        }

        Console.WriteLine($"[CV-Align] Good matches: {goodMatches.Count} / {rawMatches.Length}");

        if (goodMatches.Count < 10)
        {
            Console.WriteLine("[CV-Align] Too few matches, using resize-only");
            return currentImg.Clone();
        }

        var srcPts = goodMatches.Select(m =>
            new Point2d(kpCurrent[m.QueryIdx].Pt.X, kpCurrent[m.QueryIdx].Pt.Y)).ToArray();
        var dstPts = goodMatches.Select(m =>
            new Point2d(kpTemplate[m.TrainIdx].Pt.X, kpTemplate[m.TrainIdx].Pt.Y)).ToArray();

        using var homography = Cv2.FindHomography(srcPts, dstPts, HomographyMethods.Ransac, 5.0);

        if (homography.Empty() || homography.Rows != 3 || homography.Cols != 3)
        {
            Console.WriteLine("[CV-Align] Homography computation failed, using resize-only");
            return currentImg.Clone();
        }

        // --- Validate homography quality ---
        double h33 = homography.At<double>(2, 2);
        if (Math.Abs(h33) < 1e-6)
        {
            Console.WriteLine("[CV-Align] Degenerate homography (h33~0), using resize-only");
            return currentImg.Clone();
        }

        // Normalize by h33
        double h00 = homography.At<double>(0, 0) / h33;
        double h01 = homography.At<double>(0, 1) / h33;
        double h02 = homography.At<double>(0, 2) / h33;
        double h10 = homography.At<double>(1, 0) / h33;
        double h11 = homography.At<double>(1, 1) / h33;
        double h12 = homography.At<double>(1, 2) / h33;
        double h20 = homography.At<double>(2, 0) / h33;
        double h21 = homography.At<double>(2, 1) / h33;

        // Check perspective distortion (should be very small for factory camera setup)
        if (Math.Abs(h20) > 0.002 || Math.Abs(h21) > 0.002)
        {
            Console.WriteLine($"[CV-Align] Excessive perspective: h20={h20:E3} h21={h21:E3}, using resize-only");
            return currentImg.Clone();
        }

        // Check scale/rotation via determinant of upper-left 2x2
        double det = h00 * h11 - h01 * h10;
        if (det < 0.5 || det > 2.0)
        {
            Console.WriteLine($"[CV-Align] Bad determinant: {det:F3}, using resize-only");
            return currentImg.Clone();
        }

        // Check translation: shouldn't shift more than 20% of image dimensions
        double maxShift = Math.Max(templateImg.Cols, templateImg.Rows) * 0.2;
        if (Math.Abs(h02) > maxShift || Math.Abs(h12) > maxShift)
        {
            Console.WriteLine($"[CV-Align] Excessive translation: tx={h02:F1} ty={h12:F1}, using resize-only");
            return currentImg.Clone();
        }

        // Check rotation: off-diagonal elements should be small (< 0.3 ~ 17deg)
        if (Math.Abs(h01) > 0.3 || Math.Abs(h10) > 0.3)
        {
            Console.WriteLine($"[CV-Align] Excessive rotation: h01={h01:F3} h10={h10:F3}, using resize-only");
            return currentImg.Clone();
        }

        var warped = new Mat();
        Cv2.WarpPerspective(currentImg, warped, homography, templateImg.Size());

        Console.WriteLine($"[CV-Align] Alignment applied: det={det:F3} tx={h02:F1} ty={h12:F1} " +
            $"matches={goodMatches.Count}");
        return warped;
    }

    public ColorAnalysisResult Analyze(byte[] imageBytes, ColorProfile? profile)
    {
        if (profile == null || profile.Colors.Count == 0)
            return Analyze(imageBytes);

        using var raw = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (raw.Empty()) return new ColorAnalysisResult();

        double scale = (double)_c.AnalysisWidth / raw.Cols;
        using var img = new Mat();
        Cv2.Resize(raw, img, new Size(_c.AnalysisWidth, (int)(raw.Rows * scale)),
                   0, 0, InterpolationFlags.Area);

        using var planMask = ExtractPlanMask(img);
        int planPx = Cv2.CountNonZero(planMask);
        if (planPx == 0) return new ColorAnalysisResult();

        using var norm = NormalizeLighting(img);

        // Subtract grid lines from denominator
        using var gridMask = BuildGridMask(img, planMask);
        using var notGrid = new Mat();
        Cv2.BitwiseNot(gridMask, notGrid);
        using var effectiveMask = new Mat();
        Cv2.BitwiseAnd(planMask, notGrid, effectiveMask);
        int effectivePx = Cv2.CountNonZero(effectiveMask);

        var denominator = effectivePx > 0 ? effectiveMask : planMask;

        using var hsv = new Mat();
        Cv2.CvtColor(norm, hsv, ColorConversionCodes.BGR2HSV);

        float tol = 0.05f + (profile.Tolerance / 100f) * 0.95f;
        int hRange = Math.Max(8, (int)(tol * 40));
        int svRange = Math.Max(20, (int)(tol * 110));

        var normalColors = profile.Colors.Where(c => c.ColorGroup == "normal").ToList();
        var otColors = profile.Colors.Where(c => c.ColorGroup == "ot").ToList();

        using var normalRaw = BuildProfileMask(hsv, planMask, normalColors, hRange, svRange, planPx);
        using var otRaw = BuildProfileMask(hsv, planMask, otColors, hRange, svRange, planPx);

        var normalFiltered = RemoveGridLines(normalRaw.Clone(), planPx);
        var otFiltered = RemoveGridLines(otRaw.Clone(), planPx);

        using var grown = new Mat();
        Cv2.BitwiseOr(normalFiltered, otFiltered, grown);

        NearestSeedAssign(grown, normalFiltered, otFiltered, out var normalFinal, out var otFinal);

        var result = ComputePercent(normalFinal, otFinal, denominator);

        Console.WriteLine($"[CV-Profile] plan={planPx} effective={effectivePx} " +
            $"normalRaw={Cv2.CountNonZero(normalRaw)} normalFilt={Cv2.CountNonZero(normalFiltered)} " +
            $"normalFinal={Cv2.CountNonZero(normalFinal)} " +
            $"otRaw={Cv2.CountNonZero(otRaw)} otFilt={Cv2.CountNonZero(otFiltered)} " +
            $"otFinal={Cv2.CountNonZero(otFinal)} " +
            $"-> normal={result.NormalPercent}% ot={result.OtPercent}% total={result.TotalPercent}% " +
            $"isComplete={result.IsComplete}");

        if (_c.SaveDebug)
            SaveDebugImages(img, denominator, normalRaw, normalFiltered, normalFinal,
                           otRaw, otFiltered, otFinal, "profile");

        normalFiltered.Dispose(); otFiltered.Dispose();
        normalFinal.Dispose(); otFinal.Dispose();
        return result;
    }

    private static void NearestSeedAssign(Mat grown, Mat seedA, Mat seedB, out Mat outA, out Mat outB)
    {
        int seedACount = Cv2.CountNonZero(seedA);
        int seedBCount = Cv2.CountNonZero(seedB);

        if (seedACount == 0 && seedBCount == 0)
        {
            outA = new Mat(grown.Rows, grown.Cols, MatType.CV_8UC1, new Scalar(0));
            outB = new Mat(grown.Rows, grown.Cols, MatType.CV_8UC1, new Scalar(0));
            return;
        }

        if (seedACount == 0)
        {
            outA = new Mat(grown.Rows, grown.Cols, MatType.CV_8UC1, new Scalar(0));
            outB = grown.Clone();
            return;
        }

        if (seedBCount == 0)
        {
            outA = grown.Clone();
            outB = new Mat(grown.Rows, grown.Cols, MatType.CV_8UC1, new Scalar(0));
            return;
        }

        using var invA = new Mat();
        Cv2.BitwiseNot(seedA, invA);
        using var distA = new Mat();
        Cv2.DistanceTransform(invA, distA, DistanceTypes.L2, DistanceTransformMasks.Mask5);

        using var invB = new Mat();
        Cv2.BitwiseNot(seedB, invB);
        using var distB = new Mat();
        Cv2.DistanceTransform(invB, distB, DistanceTypes.L2, DistanceTransformMasks.Mask5);

        using var closerA = new Mat();
        Cv2.Compare(distA, distB, closerA, CmpType.LT);

        outA = new Mat();
        Cv2.BitwiseOr(closerA, seedA, outA);
        using var notSeedB = new Mat();
        Cv2.BitwiseNot(seedB, notSeedB);
        Cv2.BitwiseAnd(outA, notSeedB, outA);
        Cv2.BitwiseAnd(outA, grown, outA);

        using var notOutA = new Mat();
        Cv2.BitwiseNot(outA, notOutA);
        outB = new Mat();
        Cv2.BitwiseAnd(grown, notOutA, outB);
    }

    // ════════════════════════════════════════════════════════════════
    //  BuildGridMask  –  detect thin grid lines within the plan area
    //  using morphological top-hat + black-hat (same approach as
    //  TemplateMaskService). Returns a mask of grid pixels to exclude
    //  from the denominator so painted area / (plan − grid) ≈ 100%.
    // ════════════════════════════════════════════════════════════════
    private Mat BuildGridMask(Mat normalizedBgr, Mat planMask)
    {
        using var gray = new Mat();
        Cv2.CvtColor(normalizedBgr, gray, ColorConversionCodes.BGR2GRAY);

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

        using var gridInPlan = new Mat(normalizedBgr.Rows, normalizedBgr.Cols,
            MatType.CV_8UC1, new Scalar(0));
        gridIntensity.CopyTo(gridInPlan, planMask);

        var gridMask = new Mat();
        Cv2.Threshold(gridInPlan, gridMask, 0, 255,
            ThresholdTypes.Binary | ThresholdTypes.Otsu);

        using var dilK = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(gridMask, gridMask, dilK, iterations: 1);

        int planPx = Cv2.CountNonZero(planMask);
        int gridPx = Cv2.CountNonZero(gridMask);
        double gridRatio = planPx > 0 ? (double)gridPx / planPx : 0;

        Console.WriteLine($"[CV] BuildGridMask: gridPx={gridPx} planPx={planPx} " +
            $"gridRatio={gridRatio:P1}");

        if (gridRatio < 0.05 || gridRatio > 0.50)
        {
            Console.WriteLine($"[CV] Grid ratio {gridRatio:P1} outside [5%-50%], skipping");
            gridMask.SetTo(new Scalar(0));
        }

        return gridMask;
    }

    private Mat ExtractPlanMask(Mat bgr)
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

            var mask = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));

            if (contours.Length == 0)
            {
                Console.WriteLine("[CV] PlanMask: no contours after border kill");
                mask.SetTo(new Scalar(255));
                return mask;
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
                Console.WriteLine($"[CV] PlanMask: {rejectedEdge} edge-touching rejected, fallback to largest");
                for (int i = 0; i < contours.Length; i++)
                {
                    double area = Cv2.ContourArea(contours[i]);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestIdx = i;
                    }
                }
            }

            if (bestIdx < 0 || bestArea < imageArea * _c.MinPlanFraction)
            {
                Console.WriteLine("[CV] PlanMask: no qualifying contour found");
                mask.SetTo(new Scalar(255));
                return mask;
            }

            Cv2.DrawContours(mask, contours, bestIdx, new Scalar(255), -1);

            int planPx = Cv2.CountNonZero(mask);
            double planRatio = planPx / imageArea;

            if (planRatio < _c.MinPlanRatio || planRatio > _c.MaxPlanRatio)
            {
                Console.WriteLine($"[CV] WARNING: planRatio={planRatio:P1} outside range " +
                    $"[{_c.MinPlanRatio:P0}-{_c.MaxPlanRatio:P0}]");
            }

            Console.WriteLine($"[CV] PlanMask: bestArea={bestArea:F0} planPx={planPx} " +
                $"total={rows * cols} ratio={planRatio:P1} edgeRejected={rejectedEdge}");

            return mask;
        }
        finally
        {
            foreach (var c in hsvCh) c.Dispose();
        }
    }

    private Mat NormalizeLighting(Mat bgr)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

        Cv2.Split(lab, out Mat[] ch);
        using var clahe = Cv2.CreateCLAHE(_c.ClaheClip, new Size(8, 8));
        var lNorm = new Mat();
        clahe.Apply(ch[0], lNorm);
        ch[0].Dispose();
        ch[0] = lNorm;

        using var merged = new Mat();
        Cv2.Merge(ch, merged);
        foreach (var c in ch) c.Dispose();

        var result = new Mat();
        Cv2.CvtColor(merged, result, ColorConversionCodes.Lab2BGR);

        if (_c.PreBlurSize > 1)
            Cv2.GaussianBlur(result, result, new Size(_c.PreBlurSize, _c.PreBlurSize), 0);

        return result;
    }

    private (Mat raw, Mat filtered) DetectBlack(Mat normalizedBgr, Mat planMask, int planPx)
    {
        using var lab = new Mat();
        Cv2.CvtColor(normalizedBgr, lab, ColorConversionCodes.BGR2Lab);
        Cv2.Split(lab, out Mat[] labCh);

        using var hsv = new Mat();
        Cv2.CvtColor(normalizedBgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.Split(hsv, out Mat[] hsvCh);

        var lCh = labCh[0];
        var sCh = hsvCh[1];

        int threshold = ComputeOtsuInMask(lCh, planMask, planPx);
        threshold = Math.Min(threshold, _c.BlackMaxL);
        Console.WriteLine($"[CV] Otsu threshold={threshold} (capped at {_c.BlackMaxL})");

        using var darkMask = new Mat();
        Cv2.Threshold(lCh, darkMask, threshold, 255, ThresholdTypes.BinaryInv);

        using var satMask = new Mat();
        Cv2.Threshold(sCh, satMask, _c.BlackMaxSat, 255, ThresholdTypes.BinaryInv);

        var blackRaw = new Mat();
        Cv2.BitwiseAnd(darkMask, satMask, blackRaw);
        Cv2.BitwiseAnd(blackRaw, planMask, blackRaw);

        foreach (var c in labCh) c.Dispose();
        foreach (var c in hsvCh) c.Dispose();

        var blackFiltered = RemoveGridLines(blackRaw.Clone(), planPx);

        return (blackRaw, blackFiltered);
    }

    private static int ComputeOtsuInMask(Mat channel, Mat mask, int totalPixels)
    {
        using var hist = new Mat();
        Cv2.CalcHist(new[] { channel }, new[] { 0 }, mask, hist,
            1, new[] { 256 }, new[] { new Rangef(0, 256) });

        double sum = 0;
        for (int i = 0; i < 256; i++)
            sum += i * (double)hist.At<float>(i, 0);

        double sumB = 0;
        double wB = 0;
        double maxVar = 0;
        int bestThresh = 128;

        for (int t = 0; t < 256; t++)
        {
            double w = hist.At<float>(t, 0);
            wB += w;
            if (wB == 0) continue;
            double wF = totalPixels - wB;
            if (wF <= 0) break;

            sumB += t * w;
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;
            double varBetween = wB * wF * (mB - mF) * (mB - mF);

            if (varBetween > maxVar)
            {
                maxVar = varBetween;
                bestThresh = t;
            }
        }

        return bestThresh;
    }

    private Mat RemoveGridLines(Mat rawMask, int planPx)
    {
        int rows = rawMask.Rows, cols = rawMask.Cols;

        using var distF = new Mat();
        Cv2.DistanceTransform(rawMask, distF, DistanceTypes.L2, DistanceTransformMasks.Mask5);

        using var seedF = new Mat();
        Cv2.Threshold(distF, seedF, _c.GridThickRadius, 255, ThresholdTypes.Binary);
        using var seed = new Mat();
        seedF.ConvertTo(seed, MatType.CV_8UC1);

        int seedCount = Cv2.CountNonZero(seed);
        Console.WriteLine($"[CV] Grid removal: rawPx={Cv2.CountNonZero(rawMask)} seeds={seedCount}");

        if (seedCount == 0)
        {
            rawMask.Dispose();
            return new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));
        }

        var result = GeodesicReconstruct(seed, rawMask);
        rawMask.Dispose();

        using var closeK = Cv2.GetStructuringElement(MorphShapes.Ellipse,
            new Size(_c.BlackCloseSize, _c.BlackCloseSize));
        Cv2.MorphologyEx(result, result, MorphTypes.Close, closeK);

        result = FilterComponents(result, planPx);

        return result;
    }

    private static Mat GeodesicReconstruct(Mat marker, Mat mask)
    {
        var result = marker.Clone();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

        for (int i = 0; i < 500; i++)
        {
            using var prev = result.Clone();
            Cv2.Dilate(result, result, kernel);
            Cv2.BitwiseAnd(result, mask, result);

            using var diff = new Mat();
            Cv2.Absdiff(result, prev, diff);
            if (Cv2.CountNonZero(diff) == 0) break;
        }

        return result;
    }

    private (Mat raw, Mat filtered) DetectRed(Mat normalizedBgr, Mat planMask, int planPx)
    {
        using var hsvImg = new Mat();
        Cv2.CvtColor(normalizedBgr, hsvImg, ColorConversionCodes.BGR2HSV);

        using var rLow = new Mat();
        Cv2.InRange(hsvImg,
            new Scalar(_c.RedLowH1, _c.RedMinS, _c.RedMinV),
            new Scalar(_c.RedHighH1, 255, 255), rLow);

        using var rHigh = new Mat();
        Cv2.InRange(hsvImg,
            new Scalar(_c.RedLowH2, _c.RedMinS, _c.RedMinV),
            new Scalar(_c.RedHighH2, 255, 255), rHigh);

        var redRaw = new Mat();
        Cv2.BitwiseOr(rLow, rHigh, redRaw);
        Cv2.BitwiseAnd(redRaw, planMask, redRaw);

        using var closeK = Cv2.GetStructuringElement(MorphShapes.Ellipse,
            new Size(_c.RedCloseSize, _c.RedCloseSize));
        var redFiltered = new Mat();
        Cv2.MorphologyEx(redRaw, redFiltered, MorphTypes.Close, closeK);

        redFiltered = FilterComponents(redFiltered, planPx);

        return (redRaw, redFiltered);
    }

    private Mat FilterComponents(Mat inputMask, int planArea)
    {
        int rows = inputMask.Rows;
        int cols = inputMask.Cols;
        int medArea = Math.Max(50, (int)(planArea * _c.MedCompFraction));

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int nLabels = Cv2.ConnectedComponentsWithStats(inputMask, labels, stats, centroids);

        if (nLabels <= 1)
        {
            inputMask.Dispose();
            return new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));
        }

        var keep = new bool[nLabels];
        bool anyRemoved = false;

        for (int i = 1; i < nLabels; i++)
        {
            int area = stats.At<int>(i, 4);
            int width = stats.At<int>(i, 2);
            int height = stats.At<int>(i, 3);
            double aspect = (double)Math.Max(width, height) / Math.Max(1, Math.Min(width, height));

            if (area < _c.MinComponentArea)
            {
                anyRemoved = true;
            }
            else if (width < _c.MinComponentDim || height < _c.MinComponentDim)
            {
                anyRemoved = true;
            }
            else if (area < medArea && aspect > _c.MaxAspectRatio)
            {
                anyRemoved = true;
            }
            else
            {
                keep[i] = true;
            }
        }

        if (!anyRemoved) return inputMask;

        var result = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));
        for (int y = 0; y < labels.Rows; y++)
            for (int x = 0; x < labels.Cols; x++)
            {
                int lbl = labels.At<int>(y, x);
                if (lbl > 0 && keep[lbl])
                    result.Set(y, x, (byte)255);
            }

        inputMask.Dispose();
        return result;
    }

    public ColorAnalysisResult ComputePercent(Mat blackMask, Mat redMask, Mat planMask)
    {
        int planPx = Cv2.CountNonZero(planMask);
        if (planPx == 0) return new ColorAnalysisResult();

        Console.WriteLine($"[CV] ComputePercent: planPx={planPx}");

        using var bInPlan = new Mat();
        Cv2.BitwiseAnd(blackMask, planMask, bInPlan);
        using var rInPlan = new Mat();
        Cv2.BitwiseAnd(redMask, planMask, rInPlan);

        int blackPx = Cv2.CountNonZero(bInPlan);
        int redPx = Cv2.CountNonZero(rInPlan);

        decimal bPct = Math.Round((decimal)blackPx / planPx * 100, 2);
        decimal rPct = Math.Round((decimal)redPx / planPx * 100, 2);
        decimal tPct = Math.Min(bPct + rPct, 100m);

        bool isComplete = (double)tPct >= _c.CompleteThreshold;

        return new ColorAnalysisResult
        {
            NormalPercent = bPct,
            OtPercent = rPct,
            TotalPercent = Math.Round(tPct, 2),
            IsComplete = isComplete
        };
    }

    private Mat BuildProfileMask(Mat hsv, Mat planMask,
        List<ColorProfileColor> colors, int hRange, int svRange, int planPx)
    {
        var combined = new Mat(hsv.Rows, hsv.Cols, MatType.CV_8UC1, new Scalar(0));

        foreach (var color in colors)
        {
            int tH = (int)(color.HsvH / 2.0);
            int tS = (int)(color.HsvS * 255);
            int tV = (int)(color.HsvV * 255);

            int hLo, hHi, sLo, sHi, vLo, vHi;

            if (tV < 80)
            {
                hLo = 0; hHi = 180;
                sLo = 0; sHi = 255;
                vLo = 0; vHi = Math.Min(255, Math.Max(100, tV + svRange * 3));
            }
            else
            {
                hLo = Math.Max(0, tH - hRange); hHi = Math.Min(180, tH + hRange);
                sLo = Math.Max(0, tS - svRange); sHi = Math.Min(255, tS + svRange);
                vLo = Math.Max(0, tV - svRange); vHi = Math.Min(255, tV + svRange);
            }

            using var m = new Mat();
            Cv2.InRange(hsv, new Scalar(hLo, sLo, vLo), new Scalar(hHi, sHi, vHi), m);

            if (tH < hRange)
            {
                using var w = new Mat();
                Cv2.InRange(hsv, new Scalar(180 - (hRange - tH), sLo, vLo),
                                 new Scalar(180, sHi, vHi), w);
                Cv2.BitwiseOr(m, w, m);
            }
            else if (tH > 180 - hRange)
            {
                using var w = new Mat();
                Cv2.InRange(hsv, new Scalar(0, sLo, vLo),
                                 new Scalar(hRange - (180 - tH), sHi, vHi), w);
                Cv2.BitwiseOr(m, w, m);
            }

            Cv2.BitwiseOr(combined, m, combined);
        }

        using var planned = new Mat();
        Cv2.BitwiseAnd(combined, planMask, planned);
        combined.Dispose();

        using var kC = Cv2.GetStructuringElement(MorphShapes.Ellipse,
            new Size(_c.RedCloseSize, _c.RedCloseSize));
        var result = new Mat();
        Cv2.MorphologyEx(planned, result, MorphTypes.Close, kC);

        return FilterComponents(result, planPx);
    }

    private void SaveDebugImages(Mat img, Mat planMask,
        Mat normalRaw, Mat normalFiltered, Mat normalFinal,
        Mat otRaw, Mat otFiltered, Mat otFinal,
        string prefix)
    {
        try
        {
            Directory.CreateDirectory(_c.DebugPath);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string p = _c.DebugPath;

            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_1_plan_mask.png"), planMask);
            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_2a_normal_raw.png"), normalRaw);
            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_2b_normal_filtered.png"), normalFiltered);
            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_2c_normal_final.png"), normalFinal);
            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_3a_ot_raw.png"), otRaw);
            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_3b_ot_filtered.png"), otFiltered);
            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_3c_ot_final.png"), otFinal);

            using var overlay = img.Clone();
            using var blueColor = new Mat(img.Rows, img.Cols, MatType.CV_8UC3, new Scalar(220, 130, 0));
            blueColor.CopyTo(overlay, normalFinal);
            using var redColor = new Mat(img.Rows, img.Cols, MatType.CV_8UC3, new Scalar(0, 0, 230));
            redColor.CopyTo(overlay, otFinal);

            using var blended = new Mat();
            Cv2.AddWeighted(img, 0.45, overlay, 0.55, 0, blended);

            using var planClone = planMask.Clone();
            Cv2.FindContours(planClone, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            Cv2.DrawContours(blended, contours, -1, new Scalar(0, 255, 0), 2);

            Cv2.ImWrite(Path.Combine(p, $"{ts}_{prefix}_5_overlay.png"), blended);
        }
        catch { }
    }
}
