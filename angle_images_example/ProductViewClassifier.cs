using System;
using System.Diagnostics;
using System.Numerics;

namespace HololensIKEA.Services
{
    // ─────────────────────────────────────────────────────────────────────────
    // Data types
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Which product box faces are visible in the image.</summary>
    public enum ViewType
    {
        FrontOnly,         // Orthographic-ish front shot — front face fills the frame
        ThreeQuarterRight, // Front + right side face visible (side panel darker, on right)
        ThreeQuarterLeft,  // Front + left  side face visible (mirrored shot)
        TopDown,           // Bird's-eye — top face dominant
    }

    /// <summary>
    /// 4 pixel-coordinate corners of one face region in the source image.
    /// Order: TopLeft, TopRight, BottomLeft, BottomRight.
    /// </summary>
    public readonly struct FaceQuad
    {
        public readonly Vector2 TL, TR, BL, BR;

        public FaceQuad(Vector2 tl, Vector2 tr, Vector2 bl, Vector2 br)
        { TL = tl; TR = tr; BL = bl; BR = br; }

        /// <summary>Approximate width at mid-height of the quad.</summary>
        public float MidWidth  => 0.5f * ((TR.X - TL.X) + (BR.X - BL.X));
        /// <summary>Approximate height at mid-width of the quad.</summary>
        public float MidHeight => 0.5f * ((BL.Y - TL.Y) + (BR.Y - TR.Y));
    }

    /// <summary>Complete view-classification result for one product image.</summary>
    public sealed class ViewClassification
    {
        public ViewType ViewType { get; set; } = ViewType.FrontOnly;

        /// <summary>Pixel-coordinate quad for the front face region of the image.</summary>
        public FaceQuad FrontFace { get; set; }
        /// <summary>Pixel-coordinate quad for the side face region (right or left per ViewType).</summary>
        public FaceQuad SideFace  { get; set; }

        /// <summary>
        /// Fraction of the bounding-box width that belongs to the front face.
        /// e.g. 0.62 means 62 % front face, 38 % side face.
        /// </summary>
        public float FrontFaceWidthFraction { get; set; } = 1f;

        /// <summary>Pixel X coordinate of the front/side crease.</summary>
        public float CreaseX { get; set; }

        /// <summary>Detection confidence (0–1).</summary>
        public float Confidence { get; set; } = 1f;

        public bool HasRightSide => ViewType == ViewType.ThreeQuarterRight;
        public bool HasLeftSide  => ViewType == ViewType.ThreeQuarterLeft;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ProductViewClassifier
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Analyses a decoded BGRA8 product image to determine the shooting angle
    /// and locate the pixel boundary between visible box faces.
    ///
    /// Algorithm:
    ///
    ///   A. Build a foreground mask (white-distance threshold).
    ///   B. Find tight content bounding rect.
    ///   C. Build a column-luminance profile: for each column, compute the mean
    ///      Rec.709 luminance of foreground pixels in the upper 60 % of the
    ///      bounding rect (avoids cast shadows from feet / bases).
    ///   D. Smooth the profile (box filter, half-window = 4) to suppress JPEG noise.
    ///   E. Compute the discrete first derivative of the smoothed profile.
    ///   F. In the search zone [20 %…82 %] of the bounding-box width, find the
    ///      column with the largest absolute delta that also has adequate foreground
    ///      density on BOTH sides (so we don't pick a shadow or background gap).
    ///   G. If the winning delta is strong enough (≥ MinLumDrop AND ≥ 12 % of the
    ///      profile's full luminance range) → 3/4 view; otherwise → FrontOnly.
    ///   H. Determine side: negative delta = luminance drops going right
    ///      → bright front face is left, dark side face is right → ThreeQuarterRight.
    ///   I. Trace the foreground mask along each column range to find the 4 precise
    ///      corner coordinates of each visible face region.
    ///
    /// For the AC unit (Image 1):     → FrontOnly   (no strong internal crease)
    /// For the ABB switch box (Image 2): → ThreeQuarterRight  (strong drop ~col 62 %)
    /// </summary>
    public static class ProductViewClassifier
    {
        // ── Tuneable constants ────────────────────────────────────────────────

        private const float WhiteThreshold    = 0.08f;  // RGB distance from white
        private const float MinLumDrop        = 0.045f; // absolute luminance step needed
        private const float CreaseMinFrac     = 0.20f;  // crease must be inside this band
        private const float CreaseMaxFrac     = 0.82f;
        private const float MinSideDensity    = 0.35f;  // foreground fraction needed on each side
        private const int   ProfileSmooth    = 4;       // box-filter half-window (columns)
        private const float MinCreaseFraction = 0.12f;  // delta / profileRange threshold

        // ─────────────────────────────────────────────────────────────────────

        /// <param name="bgraPix">BGRA8 premultiplied bytes, row-major.</param>
        public static ViewClassification Classify(byte[] bgraPix, uint width, uint height)
        {
            var result = new ViewClassification();
            if (bgraPix == null || width == 0 || height == 0)
                return result;

            try
            {
                // ── A. Foreground mask ────────────────────────────────────────
                var mask = BuildMask(bgraPix, width, height);

                // ── B. Tight content bounding rect ────────────────────────────
                if (!FindBoundingRect(mask, width, height,
                    out uint minX, out uint maxX, out uint minY, out uint maxY))
                {
                    result.FrontFace = WholeImageQuad(width, height);
                    return result;
                }

                uint bw = maxX - minX;
                uint bh = maxY - minY;

                if (bw < 10 || bh < 10)
                {
                    result.FrontFace = WholeImageQuad(width, height);
                    return result;
                }

                // ── C. Column luminance profile (upper 60 % to avoid floor shadow) ─
                uint scanTop = minY;
                uint scanBot = minY + (uint)(bh * 0.60f);
                var  profile = BuildColumnLumProfile(bgraPix, mask, width, height,
                                                      minX, maxX, scanTop, scanBot);

                // ── D. Smooth ─────────────────────────────────────────────────
                SmoothProfile(profile, ProfileSmooth);

                // ── E. First derivative ───────────────────────────────────────
                var delta = new float[profile.Length];
                for (int i = 1; i < delta.Length; ++i)
                    delta[i] = profile[i] - profile[i - 1];

                // Profile range for relative threshold
                float profMin = float.MaxValue, profMax = float.MinValue;
                foreach (var v in profile)
                {
                    if (v < profMin) profMin = v;
                    if (v > profMax) profMax = v;
                }
                float profRange = Math.Max(1e-4f, profMax - profMin);

                // ── F. Find crease candidate in search zone ───────────────────
                int searchStart = (int)(bw * CreaseMinFrac);
                int searchEnd   = (int)(bw * CreaseMaxFrac);

                float bestDelta = 0f;
                int   bestCol   = -1;

                for (int ci = searchStart; ci <= searchEnd && ci < delta.Length; ++ci)
                {
                    float d = delta[ci];
                    if (Math.Abs(d) <= Math.Abs(bestDelta))
                        continue;

                    // Both sides must have sufficient foreground coverage
                    uint leftX1  = (uint)(minX + ci);
                    uint rightX0 = leftX1 + 1;
                    float leftDens  = ColumnDensity(mask, width, minX,    leftX1,  minY, maxY);
                    float rightDens = ColumnDensity(mask, width, rightX0, maxX,    minY, maxY);

                    if (leftDens > MinSideDensity && rightDens > MinSideDensity)
                    {
                        bestDelta = d;
                        bestCol   = ci;
                    }
                }

                // ── G. Classify ───────────────────────────────────────────────
                bool hasCrease =
                    bestCol >= 0 &&
                    Math.Abs(bestDelta) >= MinLumDrop &&
                    Math.Abs(bestDelta) / profRange  >= MinCreaseFraction;

                if (!hasCrease)
                {
                    result.ViewType   = ViewType.FrontOnly;
                    result.Confidence = 0.90f;
                    result.FrontFace  = ExtractFaceQuad(mask, width, minX, maxX, minY, maxY);
                    Debug.WriteLine("[ViewClassifier] FrontOnly (no crease found)");
                    return result;
                }

                uint creasePixX = (uint)(minX + bestCol);
                result.CreaseX = creasePixX;

                // ── H. Side direction ─────────────────────────────────────────
                // Negative delta: luminance drops left→right → dark side is on the right
                result.ViewType = bestDelta < 0f
                    ? ViewType.ThreeQuarterRight
                    : ViewType.ThreeQuarterLeft;

                result.FrontFaceWidthFraction = bestCol / (float)bw;
                result.Confidence = Math.Min(1f, Math.Abs(bestDelta) / profRange * 5f);

                // ── I. Per-face quad corners ──────────────────────────────────
                result.FrontFace = ExtractFaceQuad(mask, width, minX, creasePixX, minY, maxY);
                result.SideFace  = ExtractFaceQuad(mask, width, creasePixX, maxX, minY, maxY);

                Debug.WriteLine(
                    $"[ViewClassifier] {result.ViewType}" +
                    $"  crease={creasePixX}px ({result.FrontFaceWidthFraction:P0} front)" +
                    $"  Δlum={bestDelta:+0.000;-0.000}" +
                    $"  conf={result.Confidence:F2}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ViewClassifier] Exception: " + ex.Message);
                result.ViewType  = ViewType.FrontOnly;
                result.FrontFace = WholeImageQuad(width, height);
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers — mask & bounding rect
        // ─────────────────────────────────────────────────────────────────────

        private static byte[] BuildMask(byte[] pix, uint w, uint h)
        {
            var mask = new byte[w * h];
            for (uint i = 0; i < w * h; ++i)
            {
                float b  = pix[i * 4 + 0] / 255f;
                float g  = pix[i * 4 + 1] / 255f;
                float r  = pix[i * 4 + 2] / 255f;
                float dr = 1f - r, dg = 1f - g, db = 1f - b;
                float dist = (float)Math.Sqrt(dr * dr + dg * dg + db * db) / 1.732050808f;
                mask[i] = dist > WhiteThreshold ? (byte)1 : (byte)0;
            }
            return mask;
        }

        private static bool FindBoundingRect(byte[] mask, uint w, uint h,
            out uint minX, out uint maxX, out uint minY, out uint maxY)
        {
            minX = w; maxX = 0; minY = h; maxY = 0;
            for (uint y = 0; y < h; ++y)
            for (uint x = 0; x < w; ++x)
            {
                if (mask[y * w + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            return maxX > minX && maxY > minY;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers — luminance profile
        // ─────────────────────────────────────────────────────────────────────

        private static float[] BuildColumnLumProfile(
            byte[] pix, byte[] mask, uint w, uint h,
            uint minX, uint maxX, uint scanTop, uint scanBot)
        {
            int cols = (int)(maxX - minX + 1);
            var profile = new float[cols];

            for (int ci = 0; ci < cols; ++ci)
            {
                uint x = (uint)(minX + ci);
                if (x >= w) break;

                float sum = 0f; int cnt = 0;
                for (uint y = scanTop; y <= scanBot && y < h; ++y)
                {
                    uint idx = y * w + x;
                    if (mask[idx] == 0) continue;
                    float r = pix[idx * 4 + 2] / 255f;
                    float g = pix[idx * 4 + 1] / 255f;
                    float b = pix[idx * 4 + 0] / 255f;
                    sum += 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    ++cnt;
                }
                profile[ci] = cnt > 0 ? sum / cnt : 0f;
            }
            return profile;
        }

        private static void SmoothProfile(float[] profile, int halfWindow)
        {
            var copy = (float[])profile.Clone();
            for (int i = 0; i < profile.Length; ++i)
            {
                float s = 0f; int n = 0;
                for (int k = -halfWindow; k <= halfWindow; ++k)
                {
                    int j = i + k;
                    if (j < 0 || j >= copy.Length) continue;
                    s += copy[j]; ++n;
                }
                profile[i] = n > 0 ? s / n : copy[i];
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers — foreground density
        // ─────────────────────────────────────────────────────────────────────

        private static float ColumnDensity(byte[] mask, uint w,
            uint x0, uint x1, uint y0, uint y1)
        {
            if (x0 >= x1) return 0f;
            int fg = 0, total = 0;
            for (uint y = y0; y <= y1; ++y)
            for (uint x = x0; x <= x1 && x < w; ++x)
            { fg += mask[y * w + x]; ++total; }
            return total == 0 ? 0f : fg / (float)total;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers — quad corner tracing
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// For the column range [x0, x1], scans the foreground mask to find the
        /// topmost and bottommost foreground Y at the leftmost and rightmost columns,
        /// giving the 4 corners of the face region.
        ///
        /// Corners are averaged over a 3-column strip to reduce single-pixel noise.
        /// This captures the slight trapezoid shape of a front face in a 3/4 perspective
        /// shot (top edge is typically shorter than the bottom due to upward convergence).
        /// </summary>
        private static FaceQuad ExtractFaceQuad(byte[] mask, uint w,
            uint x0, uint x1, uint y0, uint y1)
        {
            x1 = Math.Min(x1, w - 1);
            uint avgW = Math.Min(3u, (x1 > x0 ? x1 - x0 : 1u));

            float tlY = AverageTopY(mask, w, x0, x0 + avgW, y0, y1);
            float trY = AverageTopY(mask, w, x1 > avgW ? x1 - avgW : x0, x1, y0, y1);
            float blY = AverageBotY(mask, w, x0, x0 + avgW, y0, y1);
            float brY = AverageBotY(mask, w, x1 > avgW ? x1 - avgW : x0, x1, y0, y1);

            return new FaceQuad(
                new Vector2(x0, tlY),
                new Vector2(x1, trY),
                new Vector2(x0, blY),
                new Vector2(x1, brY));
        }

        private static float AverageTopY(byte[] mask, uint w,
            uint x0, uint x1, uint y0, uint y1)
        {
            float s = 0f; int n = 0;
            for (uint x = x0; x <= x1 && x < w; ++x)
            {
                for (uint y = y0; y <= y1; ++y)
                    if (mask[y * w + x] != 0) { s += y; ++n; break; }
            }
            return n > 0 ? s / n : y0;
        }

        private static float AverageBotY(byte[] mask, uint w,
            uint x0, uint x1, uint y0, uint y1)
        {
            float s = 0f; int n = 0;
            for (uint x = x0; x <= x1 && x < w; ++x)
            {
                for (uint y = y1; y >= y0 && y <= y1; --y)
                    if (mask[y * w + x] != 0) { s += y; ++n; break; }
            }
            return n > 0 ? s / n : y1;
        }

        private static FaceQuad WholeImageQuad(uint w, uint h) =>
            new FaceQuad(
                new Vector2(0,     0),
                new Vector2(w - 1, 0),
                new Vector2(0,     h - 1),
                new Vector2(w - 1, h - 1));
    }
}
