using OpenCvSharp;

namespace WorkProgress.Services;

// ═══════════════════════════════════════════════════════════════════════════
//  QrPerspectiveCorrector — QR-based plan rectification
//
//  WHY QR-based warp is more stable than contour-based detection:
//  ──────────────────────────────────────────────────────────────
//  Contour-based approach finds the plan boundary by edge/threshold,
//  but fails when:
//    1. Paint bleeds over the plan border → contour shape distorted
//    2. Shadows or folds create false edges → wrong quadrilateral
//    3. Paper curls → convex hull ≠ actual plan rectangle
//    4. Multiple dark regions confuse "largest contour" heuristic
//    5. The plan has no clear rectangular border at all
//
//  QR codes solve all of these because:
//    1. Machine-readable → no heuristic corner guessing
//    2. Self-identifying → each corner knows its role (TL/TR/BR/BL)
//    3. Error-corrected → readable even at angles, partial occlusion
//    4. Position-independent → order doesn't depend on orientation
//    5. Sub-pixel precise → finder patterns give very stable corners
//
//  QR size & placement guidelines:
//  ─────────────────────────────────
//  • Size: 15–25mm printed. Must be ≥ 30×30 pixels in the captured photo.
//    At typical phone distance (~40cm), 20mm QR → ~100px. Safe margin.
//  • Placement: Print QR codes at the four corners of the plan area,
//    just OUTSIDE the hex grid boundary. The QR's "inward" corner
//    (the one facing the plan center) defines the plan corner.
//  • Quiet zone: Leave ≥ 4 modules of white space around each QR.
//  • Content: Encode exactly "TL", "TR", "BR", or "BL" (no quotes).
//  • Orientation: QRs can be at any rotation; the decoder handles it.
//
//  NuGet packages (already in project):
//    OpenCvSharp4              4.10.0.20241108
//    OpenCvSharp4.Extensions   4.10.0.20241108
//    OpenCvSharp4.runtime.win  4.10.0.20241108
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Which plan corner this QR code marks.</summary>
public enum CornerTag
{
    TL, // top-left
    TR, // top-right
    BR, // bottom-right
    BL  // bottom-left
}

/// <summary>A single detected QR code with its tag and corner polygon.</summary>
public sealed class DetectedQr
{
    public CornerTag Tag { get; init; }
    public string RawText { get; init; } = "";
    public Point2f[] Polygon { get; init; } = Array.Empty<Point2f>(); // 4 corners from detector
    public Point2f PlanCorner { get; set; } // the extreme point used for the warp quad
}

public sealed class QrPerspectiveCorrector
{
    // Minimum quad area as a fraction of the image area.
    // If the detected quad is smaller than this, something is wrong.
    private const double MinQuadAreaFraction = 0.02;

    // Maximum allowed ratio between longest and shortest side.
    // Catches degenerate quads from misdetected QR positions.
    private const double MaxSideRatio = 10.0;

    // ───────────────────────────────────────────────────────────────
    //  PUBLIC API
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects 4 QR codes (TL/TR/BR/BL), builds a perspective quad,
    /// and warps the input to a front-facing rectangle.
    /// </summary>
    /// <param name="input">BGR image (CV_8UC3) from a smartphone camera.</param>
    /// <param name="outputWidth">Desired width of the rectified output.</param>
    /// <param name="outputHeight">Desired height of the rectified output.</param>
    /// <param name="debugDir">If non-null, saves debug images to this directory.</param>
    /// <returns>A new Mat (CV_8UC3) containing the warped plan. Caller must dispose.</returns>
    public Mat WarpByQrCorners(Mat input, int outputWidth, int outputHeight, string? debugDir = null)
    {
        if (input.Empty())
            throw new ArgumentException("Input image is empty.");
        if (input.Type() != MatType.CV_8UC3)
            throw new ArgumentException($"Expected CV_8UC3 input, got {input.Type()}.");

        SaveDebug(debugDir, "01_input.png", input);

        // ── Step 1: Detect QR codes ──
        var detections = DetectQrCodes(input);

        Console.WriteLine($"[QrWarp] Detected {detections.Count} QR codes: " +
            string.Join(", ", detections.Select(d => $"{d.Tag}({d.RawText})")));

        // ── Step 2: Validate we have all 4 corners (or estimate the 4th) ──
        var quad = ResolveQuad(detections);

        // ── Step 3: Select the extreme plan-corner point from each QR ──
        foreach (var qr in quad.Values)
            qr.PlanCorner = SelectExtremeCorner(qr.Tag, qr.Polygon);

        // ── Step 4: Build source quad [TL, TR, BR, BL] ──
        var srcPoints = new Point2f[]
        {
            quad[CornerTag.TL].PlanCorner,
            quad[CornerTag.TR].PlanCorner,
            quad[CornerTag.BR].PlanCorner,
            quad[CornerTag.BL].PlanCorner
        };

        // ── Step 5: Validate the quad ──
        ValidateQuad(srcPoints, input.Cols, input.Rows);

        // ── Step 6: Debug overlay ──
        SaveQrOverlay(debugDir, input, quad);

        // ── Step 7: Perspective warp ──
        var dstPoints = new Point2f[]
        {
            new(0, 0),
            new(outputWidth - 1, 0),
            new(outputWidth - 1, outputHeight - 1),
            new(0, outputHeight - 1)
        };

        using var transform = Cv2.GetPerspectiveTransform(srcPoints, dstPoints);
        var warped = new Mat();
        Cv2.WarpPerspective(input, warped, transform,
            new Size(outputWidth, outputHeight),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            new Scalar(0, 0, 0));

        SaveDebug(debugDir, "03_warped.png", warped);

        Console.WriteLine($"[QrWarp] Warped {input.Cols}x{input.Rows} → {outputWidth}x{outputHeight}");
        return warped;
    }

    // ═══════════════════════════════════════════════════════════════
    //  QR DETECTION
    //
    //  Uses iterative DetectAndDecode + masking to find all QR codes.
    // ═══════════════════════════════════════════════════════════════

    private static List<DetectedQr> DetectQrCodes(Mat input)
    {
        // Use iterative single detection with masking
        // (DetectAndDecodeMulti is not available in OpenCvSharp4 4.10.x)
        return TryDetectIterative(input);
    }

    /// <summary>
    /// Fallback: detect one QR at a time, mask it out, repeat.
    /// Handles older OpenCvSharp builds where Multi may not work.
    /// </summary>
    private static List<DetectedQr> TryDetectIterative(Mat input)
    {
        var results = new List<DetectedQr>();
        var seen = new HashSet<CornerTag>();

        // Work on a clone so we can paint over detected QRs
        using var work = input.Clone();
        using var detector = new QRCodeDetector();

        for (int attempt = 0; attempt < 8; attempt++) // max 8 attempts (4 QRs + retries)
        {
            string decoded = detector.DetectAndDecode(work, out Point2f[] points);

            if (string.IsNullOrWhiteSpace(decoded) || points == null || points.Length < 4)
                break;

            string text = decoded.Trim();
            if (TryParseTag(text, out var tag) && !seen.Contains(tag))
            {
                var polygon = points.Take(4).ToArray();
                if (PolygonArea(polygon) >= 10)
                {
                    results.Add(new DetectedQr
                    {
                        Tag = tag,
                        RawText = text,
                        Polygon = polygon
                    });
                    seen.Add(tag);
                }
            }

            // Mask out the detected QR region so the next iteration finds a different one
            var intPoints = points.Take(4)
                .Select(p => new Point((int)p.X, (int)p.Y))
                .ToArray();
            // Expand the mask slightly to fully cover the QR quiet zone
            var hull = Cv2.ConvexHull(intPoints);
            Cv2.FillConvexPoly(work, hull, new Scalar(255, 255, 255));

            if (seen.Count >= 4) break;
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════════════
    //  TAG PARSING
    // ═══════════════════════════════════════════════════════════════

    private static bool TryParseTag(string text, out CornerTag tag)
    {
        tag = CornerTag.TL;
        if (string.IsNullOrWhiteSpace(text)) return false;

        return Enum.TryParse(text.Trim().ToUpperInvariant(), out tag)
            && Enum.IsDefined(typeof(CornerTag), tag);
    }

    // ═══════════════════════════════════════════════════════════════
    //  QUAD RESOLUTION (handles 3-corner fallback)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensures we have exactly 4 unique corners. If only 3 are found,
    /// estimates the 4th via parallelogram assumption.
    /// </summary>
    private static Dictionary<CornerTag, DetectedQr> ResolveQuad(List<DetectedQr> detections)
    {
        // Deduplicate: keep the first occurrence of each tag
        var byTag = new Dictionary<CornerTag, DetectedQr>();
        foreach (var d in detections)
        {
            if (!byTag.ContainsKey(d.Tag))
                byTag[d.Tag] = d;
        }

        if (byTag.Count >= 4)
            return byTag;

        if (byTag.Count == 3)
        {
            // Estimate the missing 4th corner using parallelogram assumption:
            //   TL + BR = TR + BL  (diagonals bisect each other)
            //   missing = opposite1 + opposite2 - diagonal_partner
            var allTags = new[] { CornerTag.TL, CornerTag.TR, CornerTag.BR, CornerTag.BL };
            var missing = allTags.First(t => !byTag.ContainsKey(t));
            var present = allTags.Where(t => byTag.ContainsKey(t)).ToArray();

            // Temporarily compute plan corners for the 3 detected QRs
            foreach (var qr in byTag.Values)
                qr.PlanCorner = SelectExtremeCorner(qr.Tag, qr.Polygon);

            var estimated = EstimateMissingCorner(missing, byTag);

            Console.WriteLine($"[QrWarp] Only 3 QRs found. Estimated {missing} at ({estimated.X:F0},{estimated.Y:F0})");

            // Create a synthetic QR entry with the estimated point as all 4 polygon corners
            byTag[missing] = new DetectedQr
            {
                Tag = missing,
                RawText = $"{missing}(estimated)",
                Polygon = new[] { estimated, estimated, estimated, estimated },
                PlanCorner = estimated
            };

            return byTag;
        }

        // Less than 3 corners → cannot form a meaningful quad
        string found = byTag.Count == 0
            ? "none"
            : string.Join(", ", byTag.Keys);
        throw new InvalidOperationException(
            $"Need at least 3 QR corners to build a perspective quad. Found: {found}. " +
            $"Ensure the plan sheet has QR codes labeled TL, TR, BR, BL at the four corners.");
    }

    /// <summary>
    /// Parallelogram estimation: in a parallelogram ABCD,
    /// A + C = B + D (diagonals share the same midpoint).
    /// Given 3 corners, solve for the 4th.
    /// </summary>
    private static Point2f EstimateMissingCorner(
        CornerTag missing, Dictionary<CornerTag, DetectedQr> known)
    {
        // Diagonal pairs: (TL,BR) and (TR,BL)
        // TL + BR = TR + BL  →  any missing = sum_of_diagonal_pair - diagonal_partner
        //
        // Rearranging for each missing corner:
        //   missing TL = TR + BL - BR
        //   missing TR = TL + BR - BL
        //   missing BR = TR + BL - TL
        //   missing BL = TL + BR - TR

        Point2f Get(CornerTag t) => known[t].PlanCorner;

        return missing switch
        {
            CornerTag.TL => new Point2f(
                Get(CornerTag.TR).X + Get(CornerTag.BL).X - Get(CornerTag.BR).X,
                Get(CornerTag.TR).Y + Get(CornerTag.BL).Y - Get(CornerTag.BR).Y),
            CornerTag.TR => new Point2f(
                Get(CornerTag.TL).X + Get(CornerTag.BR).X - Get(CornerTag.BL).X,
                Get(CornerTag.TL).Y + Get(CornerTag.BR).Y - Get(CornerTag.BL).Y),
            CornerTag.BR => new Point2f(
                Get(CornerTag.TR).X + Get(CornerTag.BL).X - Get(CornerTag.TL).X,
                Get(CornerTag.TR).Y + Get(CornerTag.BL).Y - Get(CornerTag.TL).Y),
            CornerTag.BL => new Point2f(
                Get(CornerTag.TL).X + Get(CornerTag.BR).X - Get(CornerTag.TR).X,
                Get(CornerTag.TL).Y + Get(CornerTag.BR).Y - Get(CornerTag.TR).Y),
            _ => throw new ArgumentOutOfRangeException(nameof(missing))
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  CORNER SELECTION STRATEGY
    //
    //  Each QR code has 4 corner points from the detector.
    //  We pick the one that is most "extreme" toward the plan corner
    //  it represents. This gives us the actual plan boundary point.
    //
    //  Geometric intuition (for a roughly upright image):
    //    TL corner → closest to top-left  → minimize (x + y)
    //    TR corner → closest to top-right → minimize (-x + y) = maximize (x - y)
    //    BR corner → closest to bot-right → maximize (x + y)
    //    BL corner → closest to bot-left  → minimize (x - y)
    //
    //  This works regardless of QR code rotation because we are
    //  choosing among the 4 detected polygon vertices, not among
    //  fixed-orientation positions within the QR pattern.
    // ═══════════════════════════════════════════════════════════════

    internal static Point2f SelectExtremeCorner(CornerTag tag, Point2f[] polygon)
    {
        if (polygon.Length < 4)
            throw new ArgumentException($"QR polygon has {polygon.Length} points, expected 4.");

        return tag switch
        {
            // TL → the point with the smallest (x + y)
            CornerTag.TL => polygon.OrderBy(p => p.X + p.Y).First(),

            // TR → the point with the smallest (-x + y), i.e. largest (x - y)
            CornerTag.TR => polygon.OrderByDescending(p => p.X - p.Y).First(),

            // BR → the point with the largest (x + y)
            CornerTag.BR => polygon.OrderByDescending(p => p.X + p.Y).First(),

            // BL → the point with the smallest (x - y)
            CornerTag.BL => polygon.OrderBy(p => p.X - p.Y).First(),

            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  QUAD VALIDATION
    //
    //  Ensures the 4 source points form a valid, convex quadrilateral
    //  with reasonable size and proportions.
    // ═══════════════════════════════════════════════════════════════

    private static void ValidateQuad(Point2f[] srcPoints, int imgWidth, int imgHeight)
    {
        // 1. Convexity check via cross products of consecutive edges
        if (!IsConvex(srcPoints))
            throw new InvalidOperationException(
                "Detected QR corners form a non-convex quadrilateral. " +
                "This usually means one QR code was misidentified or misplaced. " +
                $"Corners: TL=({srcPoints[0].X:F0},{srcPoints[0].Y:F0}) " +
                $"TR=({srcPoints[1].X:F0},{srcPoints[1].Y:F0}) " +
                $"BR=({srcPoints[2].X:F0},{srcPoints[2].Y:F0}) " +
                $"BL=({srcPoints[3].X:F0},{srcPoints[3].Y:F0})");

        // 2. Area check
        double quadArea = PolygonArea(srcPoints);
        double imageArea = imgWidth * (double)imgHeight;
        if (quadArea < imageArea * MinQuadAreaFraction)
            throw new InvalidOperationException(
                $"Detected plan quad is too small ({quadArea:F0} px², " +
                $"{quadArea / imageArea * 100:F1}% of image). " +
                $"Minimum is {MinQuadAreaFraction * 100}%. " +
                "Ensure the plan fills a reasonable portion of the photo.");

        // 3. Extreme skew check (side ratio)
        double[] sides = new double[4];
        for (int i = 0; i < 4; i++)
        {
            var a = srcPoints[i];
            var b = srcPoints[(i + 1) % 4];
            sides[i] = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }
        double maxSide = sides.Max();
        double minSide = sides.Min();
        if (minSide < 1 || maxSide / minSide > MaxSideRatio)
            throw new InvalidOperationException(
                $"Extreme skew detected: side lengths [{string.Join(", ", sides.Select(s => $"{s:F0}"))}]. " +
                $"Ratio {maxSide / minSide:F1} exceeds maximum {MaxSideRatio}. " +
                "Check that QR codes are placed at the actual plan corners.");
    }

    /// <summary>
    /// Checks convexity by verifying all cross products of consecutive
    /// edges have the same sign (all CW or all CCW).
    /// Points must be ordered: [TL, TR, BR, BL].
    /// </summary>
    private static bool IsConvex(Point2f[] pts)
    {
        if (pts.Length < 4) return false;

        int n = pts.Length;
        bool? positive = null;

        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            var c = pts[(i + 2) % n];

            // Cross product of vectors (a→b) × (b→c)
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);

            if (Math.Abs(cross) < 1e-6) continue; // collinear edge, skip

            if (positive == null)
                positive = cross > 0;
            else if ((cross > 0) != positive.Value)
                return false;
        }

        return positive != null; // at least one non-degenerate turn
    }

    /// <summary>
    /// Shoelace formula for polygon area.
    /// Works for any simple (non-self-intersecting) polygon.
    /// </summary>
    private static double PolygonArea(Point2f[] pts)
    {
        double area = 0;
        int n = pts.Length;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) / 2.0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  DEBUG OUTPUT
    // ═══════════════════════════════════════════════════════════════

    private static void SaveDebug(string? debugDir, string filename, Mat image)
    {
        if (debugDir == null) return;
        try
        {
            Directory.CreateDirectory(debugDir);
            Cv2.ImWrite(Path.Combine(debugDir, filename), image);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Draws QR polygons (green), chosen plan-corner points (red),
    /// and tag labels on the input image.
    /// </summary>
    private static void SaveQrOverlay(string? debugDir, Mat input,
        Dictionary<CornerTag, DetectedQr> quad)
    {
        if (debugDir == null) return;
        try
        {
            Directory.CreateDirectory(debugDir);
            using var overlay = input.Clone();

            foreach (var (tag, qr) in quad)
            {
                // Draw QR polygon in green
                if (qr.Polygon.Length >= 4)
                {
                    var intPoly = qr.Polygon
                        .Select(p => new Point((int)p.X, (int)p.Y))
                        .ToArray();
                    for (int i = 0; i < intPoly.Length; i++)
                    {
                        var a = intPoly[i];
                        var b = intPoly[(i + 1) % intPoly.Length];
                        Cv2.Line(overlay, a, b, new Scalar(0, 255, 0), 2);
                    }
                }

                // Draw chosen plan-corner point in red (large circle)
                var cp = new Point((int)qr.PlanCorner.X, (int)qr.PlanCorner.Y);
                Cv2.Circle(overlay, cp, 8, new Scalar(0, 0, 255), -1); // filled red dot

                // Label the tag
                Cv2.PutText(overlay, tag.ToString(),
                    new Point(cp.X + 12, cp.Y - 12),
                    HersheyFonts.HersheySimplex, 1.2,
                    new Scalar(0, 255, 255), 2); // yellow text
            }

            // Draw the quad edges connecting the 4 plan-corner points
            var corners = new[] { CornerTag.TL, CornerTag.TR, CornerTag.BR, CornerTag.BL };
            for (int i = 0; i < 4; i++)
            {
                var a = quad[corners[i]].PlanCorner;
                var b = quad[corners[(i + 1) % 4]].PlanCorner;
                Cv2.Line(overlay,
                    new Point((int)a.X, (int)a.Y),
                    new Point((int)b.X, (int)b.Y),
                    new Scalar(255, 0, 255), 2); // magenta quad outline
            }

            Cv2.ImWrite(Path.Combine(debugDir, "02_detected_qr_overlay.png"), overlay);
        }
        catch { /* best-effort */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  CONVENIENCE: file-path overload
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads an image from disk, warps it, and returns the result.
    /// </summary>
    public Mat WarpByQrCorners(string imagePath, int outputWidth, int outputHeight,
        string? debugDir = null)
    {
        using var input = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (input.Empty())
            throw new FileNotFoundException($"Cannot read image: {imagePath}");
        return WarpByQrCorners(input, outputWidth, outputHeight, debugDir);
    }

    // ═══════════════════════════════════════════════════════════════
    //  INTEGRATION: use with ProgressAnalyzer
    //
    //  Usage example:
    //
    //    var corrector = new QrPerspectiveCorrector();
    //    using var warped = corrector.WarpByQrCorners(
    //        "photo.jpg", 1280, 720, "debug/qr");
    //
    //    var analyzer = new ProgressAnalyzer();
    //    var result = analyzer.Analyze(warped, "debug/analysis");
    //    Console.WriteLine(result);
    // ═══════════════════════════════════════════════════════════════
}
