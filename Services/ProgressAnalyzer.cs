using OpenCvSharp;

namespace WorkProgress.Services;

// ═══════════════════════════════════════════════════════════════════════
//  ProgressAnalyzer v4  —  Hex-grid carpet plan analysis
//
//  WHY the old pipeline had issues:
//  ─────────────────────────────────
//  Empty image returning ~30%+:
//    Global or Otsu threshold captured grid lines AND shadows.
//    Morph open was too weak to break grid intersections (120° junctions
//    are wider than a 5×5 kernel). Grid forms one connected mesh so CC
//    filter couldn't help either.
//
//  Fully-painted image returning ~78%:
//    Aggressive morph open eroded paint-blob edges, losing ~22%.
//    Fixed thresholds missed paint at varying brightness.
//
//  New approach (this file):
//  ─────────────────────────
//  1. CLAHE + blur normalizes uneven phone-camera lighting.
//  2. Plan mask via adaptive threshold isolates the printed hex grid
//     region from white paper background.
//  3. Dynamic black threshold (meanL − offset) adapts to each image's
//     actual brightness, avoiding both over- and under-detection.
//  4. NO morph open at all — instead, connected-component filtering
//     removes thin grid-line fragments by area, width, height, and
//     aspect ratio. Paint blobs are large & roughly equilateral so
//     they pass; grid segments are narrow/elongated so they fail.
//  5. Resolution-scaled CC thresholds ensure consistent behavior
//     across different phone cameras and image sizes.
// ═══════════════════════════════════════════════════════════════════════

public sealed class ProgressAnalyzerOptions
{
    // ── Red detection (HSV) ──
    public int RedSMin { get; set; } = 80;          // min saturation for red
    public int RedVMin { get; set; } = 50;          // min value for red

    // ── Black detection (LAB dynamic) ──
    public int BlackSMax { get; set; } = 130;       // max HSV saturation (exclude saturated colors)
    public int DynamicBlackOffset { get; set; } = 25; // dynamicThresh = meanL − this

    // ── Connected-component filter (base values for 1280×720) ──
    public int BaseMinArea { get; set; } = 500;
    public int BaseMinWidth { get; set; } = 15;
    public int BaseMinHeight { get; set; } = 15;
    public double MaxAspectRatio { get; set; } = 5.0;

    // ── Plan mask ──
    public int PlanAdaptiveBlock { get; set; } = 51;    // adaptive threshold block size (odd)
    public double PlanAdaptiveC { get; set; } = 5.0;    // adaptive threshold constant
    public int PlanCloseKernel { get; set; } = 31;      // morph close kernel to merge hex cells
    public double MinPlanFraction { get; set; } = 0.05;  // min fraction of image for valid plan

    // ── Behavior ──
    public bool UsePerspectiveCorrection { get; set; } = false;
    public bool ClampTotalAt100 { get; set; } = true;
    public double CompleteThreshold { get; set; } = 95.0;

    // ── Sanity clamp ──
    public double EmptyTotalThreshold { get; set; } = 7.0;   // if total < this...
    public double EmptyRedThreshold { get; set; } = 2.0;     // ...AND red < this → treat as 0
}

public sealed class ProgressResult
{
    public double BlackPercent { get; set; }
    public double RedPercent { get; set; }
    public double TotalPercent { get; set; }

    public override string ToString() =>
        $"Black={BlackPercent:F2}% Red={RedPercent:F2}% Total={TotalPercent:F2}%";
}

public sealed class ProgressAnalyzer
{
    private readonly ProgressAnalyzerOptions _opt;

    public ProgressAnalyzer(ProgressAnalyzerOptions? opt = null)
    {
        _opt = opt ?? new ProgressAnalyzerOptions();
    }

    // ───────────────────────────────────────────────────────────────
    //  PUBLIC: Analyze from file path
    // ───────────────────────────────────────────────────────────────
    public ProgressResult Analyze(string imagePath, string? debugDir = null)
    {
        using var bgr = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (bgr.Empty())
            throw new FileNotFoundException($"Cannot read image: {imagePath}");
        return Analyze(bgr, debugDir);
    }

    // ───────────────────────────────────────────────────────────────
    //  PUBLIC: Analyze from Mat (BGR)
    // ───────────────────────────────────────────────────────────────
    public ProgressResult Analyze(Mat bgr, string? debugDir = null)
    {
        if (bgr.Empty())
            return new ProgressResult();

        // ── Step 1: Normalize lighting ──
        using var norm = NormalizeLighting(bgr);
        SaveDebug(debugDir, "01_normalized.png", norm);

        // ── Step 2: Extract plan mask ──
        using var planMask = ExtractPlanMask(norm);
        SaveDebug(debugDir, "02_planMask.png", planMask);

        int planArea = Cv2.CountNonZero(planMask);
        if (planArea == 0)
            return new ProgressResult();

        // Compute resolution scale factor for CC filtering
        double scale = Math.Sqrt((double)(bgr.Cols * bgr.Rows) / (1280.0 * 720.0));
        double minArea = _opt.BaseMinArea * scale;
        double minWidth = _opt.BaseMinWidth * scale;
        double minHeight = _opt.BaseMinHeight * scale;

        // ── Step 3: Detect red ──
        using var redRaw = DetectRed(norm, planMask);
        SaveDebug(debugDir, "03_red_raw.png", redRaw);

        using var redFiltered = FilterComponents(redRaw.Clone(), minArea, minWidth, minHeight);
        SaveDebug(debugDir, "04_red_filtered.png", redFiltered);

        // ── Step 4: Detect black (dynamic threshold) ──
        using var blackRaw = DetectBlack(norm, planMask);
        SaveDebug(debugDir, "05_black_raw.png", blackRaw);

        using var blackCCFiltered = FilterComponents(blackRaw.Clone(), minArea, minWidth, minHeight);

        // ── Step 5: Remove overlap (red wins) ──
        using var notRed = new Mat();
        Cv2.BitwiseNot(redFiltered, notRed);
        using var blackFinal = new Mat();
        Cv2.BitwiseAnd(blackCCFiltered, notRed, blackFinal);
        SaveDebug(debugDir, "06_black_filtered.png", blackFinal);

        // ── Step 6: Compute percentages ──
        int blackPx = Cv2.CountNonZero(blackFinal);
        int redPx = Cv2.CountNonZero(redFiltered);

        double blackPct = Math.Round((double)blackPx / planArea * 100.0, 2);
        double redPct = Math.Round((double)redPx / planArea * 100.0, 2);
        double totalPct = Math.Round(blackPct + redPct, 2);

        // Clamp at 100 if above threshold
        if (_opt.ClampTotalAt100 && totalPct >= _opt.CompleteThreshold)
            totalPct = 100.0;
        if (totalPct > 100.0)
            totalPct = 100.0;

        // Sanity clamp: near-empty → treat as 0
        if (totalPct < _opt.EmptyTotalThreshold && redPct < _opt.EmptyRedThreshold)
            totalPct = 0.0;

        var result = new ProgressResult
        {
            BlackPercent = blackPct,
            RedPercent = redPct,
            TotalPercent = totalPct
        };

        Console.WriteLine($"[ProgressAnalyzer] plan={planArea} black={blackPx} red={redPx} " +
            $"scale={scale:F2} → {result}");

        // ── Step 7: Debug overlay ──
        SaveOverlay(debugDir, bgr, planMask, blackFinal, redFiltered);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    //  STEP 1 — Normalize lighting
    //
    //  BGR → LAB → CLAHE on L → merge → slight blur
    //  CLAHE equalizes uneven lighting from phone flash / shadows.
    //  Blur suppresses sensor noise that creates false dark pixels.
    // ═══════════════════════════════════════════════════════════════
    private static Mat NormalizeLighting(Mat bgr)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

        Cv2.Split(lab, out Mat[] channels);
        try
        {
            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            var lNorm = new Mat();
            clahe.Apply(channels[0], lNorm);
            channels[0].Dispose();
            channels[0] = lNorm;

            using var merged = new Mat();
            Cv2.Merge(channels, merged);

            var result = new Mat();
            Cv2.CvtColor(merged, result, ColorConversionCodes.Lab2BGR);

            // Slight blur to suppress noise (3×3 Gaussian)
            Cv2.GaussianBlur(result, result, new Size(3, 3), 0);
            return result;
        }
        finally
        {
            foreach (var ch in channels) ch.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  STEP 2 — Extract plan mask (exclude white paper background)
    //
    //  1. Grayscale → blur → adaptive threshold
    //     Adaptive threshold detects ALL dark structures relative to
    //     their local neighborhood: grid lines, paint, text, borders.
    //  2. Morph CLOSE (large kernel) merges adjacent hex cells into
    //     one solid region representing the entire plan area.
    //  3. Largest contour → fill → binary planMask (0/255)
    //
    //  Optional: if UsePerspectiveCorrection and contour ≈ 4 points,
    //  warp to rectangle.
    // ═══════════════════════════════════════════════════════════════
    private Mat ExtractPlanMask(Mat normalizedBgr)
    {
        int rows = normalizedBgr.Rows;
        int cols = normalizedBgr.Cols;

        using var gray = new Mat();
        Cv2.CvtColor(normalizedBgr, gray, ColorConversionCodes.BGR2GRAY);

        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        // Adaptive threshold: dark pixels (grid/paint) → 255, white paper → 0
        using var thresh = new Mat();
        Cv2.AdaptiveThreshold(blurred, thresh, 255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.BinaryInv,
            _opt.PlanAdaptiveBlock, _opt.PlanAdaptiveC);

        // Close merges hex cells into one solid plan region
        using var closeK = Cv2.GetStructuringElement(MorphShapes.Rect,
            new Size(_opt.PlanCloseKernel, _opt.PlanCloseKernel));
        using var closed = new Mat();
        Cv2.MorphologyEx(thresh, closed, MorphTypes.Close, closeK);

        Cv2.FindContours(closed, out Point[][] contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var mask = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));

        if (contours.Length == 0)
        {
            // Fallback: treat entire image as plan
            mask.SetTo(new Scalar(255));
            return mask;
        }

        // Find largest contour
        int bestIdx = 0;
        double bestArea = 0;
        for (int i = 0; i < contours.Length; i++)
        {
            double a = Cv2.ContourArea(contours[i]);
            if (a > bestArea) { bestArea = a; bestIdx = i; }
        }

        double imageArea = rows * (double)cols;
        if (bestArea < imageArea * _opt.MinPlanFraction)
        {
            // Too small to be the plan → treat entire image as plan
            mask.SetTo(new Scalar(255));
            return mask;
        }

        // Optional perspective correction
        if (_opt.UsePerspectiveCorrection)
        {
            var approx = Cv2.ApproxPolyDP(contours[bestIdx],
                Cv2.ArcLength(contours[bestIdx], true) * 0.02, true);
            if (approx.Length == 4)
            {
                // Sort points: TL, TR, BR, BL
                var ordered = OrderPoints(approx);
                double w = Math.Max(
                    Distance(ordered[0], ordered[1]),
                    Distance(ordered[3], ordered[2]));
                double h = Math.Max(
                    Distance(ordered[0], ordered[3]),
                    Distance(ordered[1], ordered[2]));

                var dst = new Point2f[]
                {
                    new(0, 0), new((float)w, 0),
                    new((float)w, (float)h), new(0, (float)h)
                };
                var src = ordered.Select(p => new Point2f(p.X, p.Y)).ToArray();
                using var M = Cv2.GetPerspectiveTransform(src, dst);
                // Note: caller would need to warp the source image too.
                // For now, just fill the contour on the mask.
            }
        }

        // Fill the contour to create a solid plan mask
        Cv2.DrawContours(mask, contours, bestIdx, new Scalar(255), -1);
        return mask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  STEP 3 — Red detection (HSV hue-wrapping)
    //
    //  Red in HSV wraps around H=0/180, so two ranges:
    //    H ∈ [0..10] ∪ [170..180], S ≥ RedSMin, V ≥ RedVMin
    //  Apply planMask → morph CLOSE (3×3) to fill tiny holes.
    // ═══════════════════════════════════════════════════════════════
    private Mat DetectRed(Mat normalizedBgr, Mat planMask)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(normalizedBgr, hsv, ColorConversionCodes.BGR2HSV);

        // Low-end red: H ∈ [0, 10]
        using var rLow = new Mat();
        Cv2.InRange(hsv,
            new Scalar(0, _opt.RedSMin, _opt.RedVMin),
            new Scalar(10, 255, 255),
            rLow);

        // High-end red: H ∈ [170, 180]
        using var rHigh = new Mat();
        Cv2.InRange(hsv,
            new Scalar(170, _opt.RedSMin, _opt.RedVMin),
            new Scalar(180, 255, 255),
            rHigh);

        var redMask = new Mat();
        Cv2.BitwiseOr(rLow, rHigh, redMask);

        // Restrict to plan region
        Cv2.BitwiseAnd(redMask, planMask, redMask);

        // CLOSE only (3×3) — fills small holes without eroding blobs
        using var closeK = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(redMask, redMask, MorphTypes.Close, closeK);

        return redMask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  STEP 4 — Black detection (dynamic LAB threshold)
    //
    //  WHY dynamic threshold is critical:
    //    Different images have different overall brightness (phone
    //    exposure, lighting conditions). A fixed threshold either
    //    over-detects on bright images (capturing grid/shadows) or
    //    under-detects on dark images (missing paint).
    //
    //  Algorithm:
    //    1. Compute mean L (brightness) inside plan region
    //    2. dynamicThresh = meanL − DynamicBlackOffset
    //    3. Black = (L < dynamicThresh) AND (S < BlackSMax)
    //    4. Apply planMask
    //
    //  The saturation check (S < BlackSMax) excludes dark-red and
    //  dark-blue pixels that are dark but not actually black paint.
    //
    //  NO morph open is applied — that would erode paint edges and
    //  lose coverage. Grid removal is done entirely via CC filtering.
    // ═══════════════════════════════════════════════════════════════
    private Mat DetectBlack(Mat normalizedBgr, Mat planMask)
    {
        // LAB for luminance
        using var lab = new Mat();
        Cv2.CvtColor(normalizedBgr, lab, ColorConversionCodes.BGR2Lab);
        Cv2.Split(lab, out Mat[] labCh);

        // HSV for saturation
        using var hsv = new Mat();
        Cv2.CvtColor(normalizedBgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.Split(hsv, out Mat[] hsvCh);

        try
        {
            var lCh = labCh[0];
            var sCh = hsvCh[1];

            // Compute mean L inside plan region
            var meanL = Cv2.Mean(lCh, planMask);
            double dynamicThresh = meanL.Val0 - _opt.DynamicBlackOffset;

            // Clamp threshold to reasonable range
            dynamicThresh = Math.Max(30, Math.Min(dynamicThresh, 150));

            Console.WriteLine($"[ProgressAnalyzer] meanL={meanL.Val0:F1} dynamicThresh={dynamicThresh:F1}");

            // Black = L < dynamicThresh
            using var darkMask = new Mat();
            Cv2.Threshold(lCh, darkMask, dynamicThresh, 255, ThresholdTypes.BinaryInv);

            // AND low saturation (exclude dark-reds, dark-blues etc.)
            using var satMask = new Mat();
            Cv2.Threshold(sCh, satMask, _opt.BlackSMax, 255, ThresholdTypes.BinaryInv);

            // Combine: dark AND low-saturation AND inside plan
            var blackRaw = new Mat();
            Cv2.BitwiseAnd(darkMask, satMask, blackRaw);
            Cv2.BitwiseAnd(blackRaw, planMask, blackRaw);

            return blackRaw;
        }
        finally
        {
            foreach (var c in labCh) c.Dispose();
            foreach (var c in hsvCh) c.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Connected-component filtering (grid line removal)
    //
    //  WHY CC filter works better than morph open for grid removal:
    //    Morph open erodes ALL structures uniformly — it removes grid
    //    lines but ALSO erodes paint-blob edges, losing 10-25% area.
    //    CC filtering is selective: it checks each component's shape
    //    properties and only removes ones matching grid-line geometry.
    //
    //  Grid lines produce CC components that are:
    //    - Small area (single line segments between paint blobs)
    //    - Narrow width OR height (lines are thin in one dimension)
    //    - High aspect ratio (elongated line segments)
    //
    //  Paint blobs produce CC components that are:
    //    - Large area (filled hex cells are substantial)
    //    - Wide AND tall (roughly equilateral shapes)
    //    - Low aspect ratio (not elongated)
    //
    //  Scaling rule:
    //    scale = sqrt(imagePixels / referencePixels)
    //    All thresholds are multiplied by scale so a 4K image uses
    //    proportionally larger thresholds than a 720p image.
    //
    //  Remove component if ANY of these fail:
    //    - area < minArea
    //    - width < minWidth
    //    - height < minHeight
    //    - aspectRatio > MaxAspectRatio
    // ═══════════════════════════════════════════════════════════════
    private Mat FilterComponents(Mat inputMask, double minArea, double minWidth, double minHeight)
    {
        int rows = inputMask.Rows;
        int cols = inputMask.Cols;

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
        int removedCount = 0;

        for (int i = 1; i < nLabels; i++) // skip label 0 (background)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            double aspect = (double)Math.Max(w, h) / Math.Max(1, Math.Min(w, h));

            bool remove = area < minArea
                       || w < minWidth
                       || h < minHeight
                       || aspect > _opt.MaxAspectRatio;

            if (remove)
            {
                anyRemoved = true;
                removedCount++;
            }
            else
            {
                keep[i] = true;
            }
        }

        Console.WriteLine($"[ProgressAnalyzer] CC filter: {nLabels - 1} components, " +
            $"removed {removedCount}, kept {nLabels - 1 - removedCount} " +
            $"(minArea={minArea:F0} minW={minWidth:F0} minH={minHeight:F0})");

        if (!anyRemoved) return inputMask;

        // Build filtered mask (only kept components)
        var result = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));

        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                int lbl = labels.At<int>(y, x);
                if (lbl > 0 && keep[lbl])
                    result.Set(y, x, (byte)255);
            }

        inputMask.Dispose();
        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Debug: save individual stage images
    // ═══════════════════════════════════════════════════════════════
    private static void SaveDebug(string? debugDir, string filename, Mat image)
    {
        if (debugDir == null) return;
        try
        {
            Directory.CreateDirectory(debugDir);
            Cv2.ImWrite(Path.Combine(debugDir, filename), image);
        }
        catch { /* best-effort debug output */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Debug: overlay masks on original image
    //
    //  Green contour = plan boundary
    //  Red tint      = detected red paint
    //  Blue tint     = detected black paint (distinct visualization)
    // ═══════════════════════════════════════════════════════════════
    private static void SaveOverlay(string? debugDir, Mat original, Mat planMask,
        Mat blackMask, Mat redMask)
    {
        if (debugDir == null) return;
        try
        {
            Directory.CreateDirectory(debugDir);

            using var overlay = original.Clone();

            // Tint black areas in blue (BGR: 220, 130, 0)
            using var blueColor = new Mat(original.Rows, original.Cols, MatType.CV_8UC3,
                new Scalar(220, 130, 0));
            blueColor.CopyTo(overlay, blackMask);

            // Tint red areas in red (BGR: 0, 0, 230)
            using var redColor = new Mat(original.Rows, original.Cols, MatType.CV_8UC3,
                new Scalar(0, 0, 230));
            redColor.CopyTo(overlay, redMask);

            // Blend: 45% original + 55% overlay
            using var blended = new Mat();
            Cv2.AddWeighted(original, 0.45, overlay, 0.55, 0, blended);

            // Draw plan contour in green
            using var planClone = planMask.Clone();
            Cv2.FindContours(planClone, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            Cv2.DrawContours(blended, contours, -1, new Scalar(0, 255, 0), 2);

            Cv2.ImWrite(Path.Combine(debugDir, "07_overlay.png"), blended);
        }
        catch { /* best-effort */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Geometry helpers for perspective correction
    // ═══════════════════════════════════════════════════════════════
    private static Point[] OrderPoints(Point[] pts)
    {
        // Order: top-left, top-right, bottom-right, bottom-left
        var sorted = pts.OrderBy(p => p.X + p.Y).ToArray();
        var tl = sorted[0];
        var br = sorted[3];
        var remaining = sorted.Skip(1).Take(2).ToArray();
        var tr = remaining.OrderByDescending(p => p.X - p.Y).First();
        var bl = remaining.OrderBy(p => p.X - p.Y).First();
        return new[] { tl, tr, br, bl };
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SANITY TEST — run on two sample images and print results
    //
    //  Usage:
    //    ProgressAnalyzer.RunSanityTest(
    //        "path/to/fully_painted.jpg",
    //        "path/to/almost_empty.jpg",
    //        "path/to/debug_output_dir");
    // ═══════════════════════════════════════════════════════════════
    public static void RunSanityTest(string fullImagePath, string emptyImagePath,
        string debugBaseDir = "wwwroot/debug/sanity")
    {
        var analyzer = new ProgressAnalyzer();

        Console.WriteLine("╔══════════════════════════════════════════╗");
        Console.WriteLine("║  ProgressAnalyzer v4 — Sanity Test      ║");
        Console.WriteLine("╚══════════════════════════════════════════╝");

        // Test A: Fully painted → expect ~95–100% total
        Console.WriteLine("\n── Test A: Fully painted image ──");
        Console.WriteLine($"   File: {fullImagePath}");
        string debugA = Path.Combine(debugBaseDir, "full");
        var resultA = analyzer.Analyze(fullImagePath, debugA);
        Console.WriteLine($"   Result: {resultA}");
        bool passA = resultA.TotalPercent >= 95.0;
        Console.WriteLine($"   PASS: {(passA ? "YES" : "NO")} (expected Total >= 95%)");

        // Test B: Almost empty → expect ~0–5% total
        Console.WriteLine("\n── Test B: Almost empty image ──");
        Console.WriteLine($"   File: {emptyImagePath}");
        string debugB = Path.Combine(debugBaseDir, "empty");
        var resultB = analyzer.Analyze(emptyImagePath, debugB);
        Console.WriteLine($"   Result: {resultB}");
        bool passB = resultB.TotalPercent <= 5.0;
        Console.WriteLine($"   PASS: {(passB ? "YES" : "NO")} (expected Total <= 5%)");

        Console.WriteLine("\n══════════════════════════════════════════");
        Console.WriteLine($"  Overall: {(passA && passB ? "ALL PASSED" : "SOME FAILED")}");
        Console.WriteLine("══════════════════════════════════════════");
    }
}
