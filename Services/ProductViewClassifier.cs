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
    ///   A. Build a foreground mask (white-distance threshold).
    ///   B. Find tight content bounding rect.
    ///   C. Build a column-luminance profile (upper 60% to avoid floor shadow).
    ///   D. Smooth the profile (box filter, half-window = 4).
    ///   E. Compute the discrete first derivative.
    ///   F. In the search zone [20%…82%], find the largest absolute delta with
    ///      adequate foreground density on both sides.
    ///   G. Classify: if delta ≥ MinLumDrop AND ≥ 12% of profile range → 3/4 view.
    ///   H. Determine side: negative delta → ThreeQuarterRight.
    ///   I. Trace foreground mask to find 4 corner coordinates per face.
    /// </summary>
    public static class ProductViewClassifier
    {
        private const float WhiteThreshold = 0.08f;
        private const float MinLumDiff     = 0.08f;   // min of top-half/bottom-half scores to accept crease
        private const float CreaseMinFrac  = 0.20f;
        private const float CreaseMaxFrac  = 0.80f;
        private const float MinSideDensity = 0.15f;   // required for the larger (front-face) region

        public static ViewClassification Classify(byte[] bgraPix, uint width, uint height)
        {
            var result = new ViewClassification();
            if (bgraPix == null || width == 0 || height == 0)
                return result;

            try
            {
                var mask = BuildMask(bgraPix, width, height);

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

                uint halfH = bh / 2;

                // Build luminance profiles for the TOP and BOTTOM halves of the bounding box.
                // A true face-to-face crease is a full-height phenomenon: it shows a consistent
                // luminance contrast in BOTH halves.  An isolated element (logo, handle) that
                // only exists in one vertical band cannot pass the consistency check below.
                var topProf = BuildColumnLumProfile(bgraPix, mask, width, height,
                    minX, maxX, minY,              minY + halfH);
                var botProf = BuildColumnLumProfile(bgraPix, mask, width, height,
                    minX, maxX, minY + halfH + 1u, maxY);

                int profLen = topProf.Length;

                // Precompute column foreground counts for O(1) density queries.
                var cumColFg = new int[profLen + 1];
                for (int ci = 0; ci < profLen; ++ci)
                {
                    uint x  = (uint)(minX + ci);
                    int  fg = 0;
                    for (uint y = minY; y <= maxY && y < height; ++y)
                        if (x < width) fg += mask[y * width + x];
                    cumColFg[ci + 1] = cumColFg[ci] + fg;
                }
                int fgRows = (int)(maxY - minY + 1);

                // Cumulative luminance sums — enable O(1) region average queries.
                var cumTopS = new double[profLen + 1]; var cumTopN = new int[profLen + 1];
                var cumBotS = new double[profLen + 1]; var cumBotN = new int[profLen + 1];
                for (int ci = 0; ci < profLen; ++ci)
                {
                    cumTopS[ci+1] = cumTopS[ci] + (topProf[ci] > 0f ? topProf[ci] : 0d);
                    cumTopN[ci+1] = cumTopN[ci] + (topProf[ci] > 0f ? 1 : 0);
                    cumBotS[ci+1] = cumBotS[ci] + (botProf[ci] > 0f ? botProf[ci] : 0d);
                    cumBotN[ci+1] = cumBotN[ci] + (botProf[ci] > 0f ? 1 : 0);
                }

                int searchStart = (int)(bw * CreaseMinFrac);
                int searchEnd   = (int)(bw * CreaseMaxFrac);

                float bestScore = 0f;
                int   bestCol   = -1;

                for (int ci = searchStart; ci < searchEnd && ci < profLen - 1; ++ci)
                {
                    int topLN = cumTopN[ci + 1],  topRN = cumTopN[profLen] - cumTopN[ci + 1];
                    int botLN = cumBotN[ci + 1],  botRN = cumBotN[profLen] - cumBotN[ci + 1];

                    float topLtAvg = topLN > 0 ? (float)(cumTopS[ci + 1] / topLN) : 0f;
                    float topRtAvg = topRN > 0 ? (float)((cumTopS[profLen] - cumTopS[ci + 1]) / topRN) : 0f;
                    float botLtAvg = botLN > 0 ? (float)(cumBotS[ci + 1] / botLN) : 0f;
                    float botRtAvg = botRN > 0 ? (float)((cumBotS[profLen] - cumBotS[ci + 1]) / botRN) : 0f;

                    float topScore = Math.Abs(topLtAvg - topRtAvg);
                    float botScore = Math.Abs(botLtAvg - botRtAvg);

                    // Consistency check: both halves must agree on which side is brighter.
                    // Skip this check if a half has too few foreground pixels to be meaningful.
                    if (topLN > 3 && topRN > 3 && botLN > 3 && botRN > 3)
                    {
                        if ((topLtAvg > topRtAvg) != (botLtAvg > botRtAvg))
                            continue;  // direction disagrees → isolated element, not a face boundary
                    }

                    // Score = MINIMUM of the two halves: both must individually show the contrast.
                    float score = Math.Min(topScore, botScore);

                    // Density: reject candidates where one side is almost completely empty.
                    // The larger (front-face) side must meet MinSideDensity; the smaller
                    // (side-panel, often off-white) only needs a bare minimum of 0.04f.
                    int   lFg  = cumColFg[ci + 1];
                    int   rFg  = cumColFg[profLen] - cumColFg[ci + 1];
                    float lD   = (ci + 1)          > 0 ? lFg / (float)((ci + 1)              * fgRows) : 0f;
                    float rD   = (profLen - ci - 1) > 0 ? rFg / (float)((profLen - ci - 1)   * fgRows) : 0f;

                    if (lD < 0.04f || rD < 0.04f) continue;
                    if (Math.Max(lD, rD) < MinSideDensity) continue;

                    if (score > bestScore) { bestScore = score; bestCol = ci; }
                }

                bool hasCrease = bestCol >= 0 && bestScore >= MinLumDiff;

                if (!hasCrease)
                {
                    result.ViewType   = ViewType.FrontOnly;
                    result.Confidence = 0.90f;
                    result.FrontFace  = ExtractFaceQuad(mask, width, minX, maxX, minY, maxY);
                    Debug.WriteLine("[ViewClassifier] FrontOnly (best score=" + bestScore.ToString("F3") + ")");
                    return result;
                }

                uint creasePixX = (uint)(minX + bestCol);
                result.CreaseX = creasePixX;

                // The side face is always the NARROWER region.
                // If the crease is in the right half → the right portion is smaller → side is on the right.
                // If the crease is in the left  half → the left  portion is smaller → side is on the left.
                bool sideIsRight = bestCol > profLen / 2;
                result.ViewType  = sideIsRight ? ViewType.ThreeQuarterRight : ViewType.ThreeQuarterLeft;

                result.Confidence             = Math.Min(1f, bestScore * 12f);
                result.FrontFaceWidthFraction = sideIsRight
                    ? bestCol / (float)bw           // front face fills the left portion
                    : 1f - bestCol / (float)bw;     // front face fills the right portion

                // Front face is always the LARGER region; side face is the smaller one.
                if (sideIsRight)
                {
                    result.FrontFace = ExtractFaceQuad(mask, width, minX,       creasePixX, minY, maxY);
                    result.SideFace  = ExtractFaceQuad(mask, width, creasePixX, maxX,       minY, maxY);
                }
                else
                {
                    result.SideFace  = ExtractFaceQuad(mask, width, minX,       creasePixX, minY, maxY);
                    result.FrontFace = ExtractFaceQuad(mask, width, creasePixX, maxX,       minY, maxY);
                }

                Debug.WriteLine(
                    "[ViewClassifier] " + result.ViewType +
                    "  crease=" + creasePixX + "px (" + result.FrontFaceWidthFraction.ToString("P0") + " front)" +
                    "  lumScore=" + bestScore.ToString("F3") +
                    "  conf=" + result.Confidence.ToString("F2"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ViewClassifier] Exception: " + ex.Message);
                result.ViewType  = ViewType.FrontOnly;
                result.FrontFace = WholeImageQuad(width, height);
            }

            return result;
        }

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

        private static FaceQuad ExtractFaceQuad(byte[] mask, uint w,
            uint x0, uint x1, uint y0, uint y1)
        {
            x1 = Math.Min(x1, w - 1);
            if (x1 <= x0) x1 = x0 + 1;
            uint faceW = x1 - x0;
            // Sample 5% of face width near each edge, clamped to [3, 20] pixels.
            uint edgeW = Math.Max(3u, Math.Min(20u, (uint)(faceW * 0.05f)));
            uint trX0  = x1 > edgeW ? x1 - edgeW : x0;

            float tlY = AverageTopY(mask, w, x0,    x0 + edgeW, y0, y1);
            float trY = AverageTopY(mask, w, trX0,  x1,         y0, y1);
            float blY = AverageBotY(mask, w, x0,    x0 + edgeW, y0, y1);
            float brY = AverageBotY(mask, w, trX0,  x1,         y0, y1);

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
