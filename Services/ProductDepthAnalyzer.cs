using System;
using System.Diagnostics;
using System.Numerics;

namespace HololensIKEA.Services
{
    /// <summary>Which face(s) of the product box are primarily visible in the image.</summary>
    public enum ProductOrientation
    {
        FrontOnly,
        FrontAndRightSide,
        FrontAndLeftSide,
        FrontAndTop,
        FrontTopRight,
        FrontTopLeft,
        TopDown,
        Unknown,
    }

    /// <summary>
    /// Result of ProductDepthAnalyzer.Analyze().
    /// </summary>
    public sealed class DepthAnalysisResult
    {
        /// <summary>Detected orientation of the product in the image.</summary>
        public ProductOrientation Orientation { get; set; } = ProductOrientation.Unknown;

        /// <summary>Estimated dominant light direction (2-D, normalised).</summary>
        public Vector2 LightDir { get; set; } = new Vector2(-0.707f, -0.707f);

        /// <summary>64×64 displacement map as R8 bytes (0 = most recessed, 255 = most protruding).</summary>
        public byte[] DisplacementR8 { get; set; }

        public int DispWidth  { get; set; } = 64;
        public int DispHeight { get; set; } = 64;
    }

    /// <summary>
    /// CPU analysis pipeline that converts a decoded BGRA8 product image into:
    ///   1. A 64×64 R8 displacement / depth map (brightness = relative protrusion).
    ///   2. Orientation detection (front-only, 3/4 view, top-down, etc.)
    ///   3. Estimated light direction for shading consistency.
    ///
    /// Pipeline per image:
    ///   BGRA pixels
    ///     → white-threshold mask (isolates foreground pixels)
    ///     → Rec.709 luminance
    ///     → subtract planar ambient-light gradient (least-squares plane fit)
    ///     → Gaussian blur r=3 (kill JPEG noise)
    ///     → unsharp-mask edge enhance ×1.2
    ///     → normalise 0–1
    ///     → orientation from bounding-rect density analysis
    ///     → light direction from Sobel gradient field
    ///     → box-filter downsample to 64×64
    ///     → convert to R8
    /// </summary>
    internal static class ProductDepthAnalyzer
    {
        private const float WhiteDistThreshold = 0.10f; // foreground mask cutoff

        public static DepthAnalysisResult Analyze(byte[] bgraPix, uint width, uint height)
        {
            var result = new DepthAnalysisResult();
            if (bgraPix == null || width == 0 || height == 0)
                return result;

            try
            {
                var mask = BuildMask(bgraPix, width, height);
                var lum  = ComputeLuminance(bgraPix, width, height, mask);

                SubtractAmbientGradient(lum, width, height, mask);
                GaussianBlur(lum, width, height, 3);
                UnsharpMask(lum, width, height, 1.2f);
                NormaliseRange(lum, width, height, mask);

                result.Orientation = DetectOrientation(mask, width, height);
                result.LightDir    = EstimateLightDirection(lum, width, height, mask);

                var disp64 = Downsample(lum, width, height, 64, 64);
                result.DisplacementR8 = ToR8(disp64);
                result.DispWidth      = 64;
                result.DispHeight     = 64;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DepthAnalyzer] " + ex.Message);
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Stage 1 – foreground mask
        // ─────────────────────────────────────────────────────────────────────

        private static byte[] BuildMask(byte[] pix, uint w, uint h)
        {
            var mask = new byte[w * h];
            for (uint i = 0; i < w * h; ++i)
            {
                float b = pix[i * 4 + 0] / 255f;
                float g = pix[i * 4 + 1] / 255f;
                float r = pix[i * 4 + 2] / 255f;
                float dr = 1f - r, dg = 1f - g, db = 1f - b;
                float dist = (float)Math.Sqrt(dr * dr + dg * dg + db * db) / 1.732050808f; // / sqrt(3)
                mask[i] = dist > WhiteDistThreshold ? (byte)1 : (byte)0;
            }
            return mask;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Stage 2 – luminance
        // ─────────────────────────────────────────────────────────────────────

        private static float[] ComputeLuminance(byte[] pix, uint w, uint h, byte[] mask)
        {
            var lum = new float[w * h];
            for (uint i = 0; i < w * h; ++i)
            {
                if (mask[i] == 0) { lum[i] = 0.5f; continue; }
                float r = pix[i * 4 + 2] / 255f;
                float g = pix[i * 4 + 1] / 255f;
                float b = pix[i * 4 + 0] / 255f;
                lum[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            }
            return lum;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Stage 3 – subtract ambient planar gradient  (least-squares plane)
        // Minimises sum((ax + by + c − lum)²) over masked foreground pixels.
        // Removing the broad studio-light ramp leaves only surface-relief detail.
        // ─────────────────────────────────────────────────────────────────────

        private static void SubtractAmbientGradient(float[] lum, uint w, uint h, byte[] mask)
        {
            // Accumulate normal-equation coefficients for  [sxx sxy sx ][a]   [sxl]
            //                                              [sxy syy sy ][b] = [syl]
            //                                              [sx  sy  sn ][c]   [sl ]
            double sxx = 0, sxy = 0, sxz = 0;
            double syy = 0, syz = 0;
            double sx  = 0, sy  = 0, sz  = 0, sn = 0;

            for (uint y = 0; y < h; ++y)
            for (uint x = 0; x < w; ++x)
            {
                uint i = y * w + x;
                if (mask[i] == 0) continue;
                double xn = x / (double)w;
                double yn = y / (double)h;
                double zn = lum[i];
                sxx += xn * xn; sxy += xn * yn; sxz += xn * zn;
                syy += yn * yn; syz += yn * zn;
                sx  += xn;      sy  += yn;      sz  += zn; sn += 1;
            }

            if (sn < 3) return;

            // Cramer's rule for 3×3
            double det = sxx * (syy * sn - sy * sy)
                       - sxy * (sxy * sn - sy * sx)
                       + sx  * (sxy * sy - syy * sx);
            if (Math.Abs(det) < 1e-12) return;

            double a = (sxz * (syy * sn - sy * sy) - sxy * (syz * sn - sy * sz) + sx  * (syz * sy - syy * sz)) / det;
            double b = (sxx * (syz * sn - sy * sz) - sxz * (sxy * sn - sy * sx) + sx  * (sxy * sz - sxz * sx)) / det;
            double c = (sxx * (syy * sz - syz * sy) - sxy * (sxy * sz - syz * sx) + sxz * (sxy * sy - syy * sx)) / det;

            for (uint y = 0; y < h; ++y)
            for (uint x = 0; x < w; ++x)
            {
                uint i = y * w + x;
                if (mask[i] == 0) continue;
                lum[i] = (float)(lum[i] - (a * (x / (double)w) + b * (y / (double)h) + c));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Stage 4 – separable Gaussian blur
        // ─────────────────────────────────────────────────────────────────────

        private static void GaussianBlur(float[] img, uint w, uint h, int radius)
        {
            if (radius <= 0) return;

            int   kSize  = 2 * radius + 1;
            float sigma  = radius / 2f;
            var   kernel = new float[kSize];
            float kSum   = 0;

            for (int k = -radius; k <= radius; ++k)
            {
                float v = (float)Math.Exp(-k * k / (2f * sigma * sigma));
                kernel[k + radius] = v;
                kSum += v;
            }
            for (int k = 0; k < kSize; ++k) kernel[k] /= kSum;

            var tmp = new float[w * h];

            // Horizontal pass
            for (uint y = 0; y < h; ++y)
            for (uint x = 0; x < w; ++x)
            {
                float v = 0, ws = 0;
                for (int k = -radius; k <= radius; ++k)
                {
                    int xi = (int)x + k;
                    if (xi < 0 || xi >= (int)w) continue;
                    float kw = kernel[k + radius];
                    v  += img[y * w + (uint)xi] * kw;
                    ws += kw;
                }
                tmp[y * w + x] = ws > 1e-6f ? v / ws : 0f;
            }

            // Vertical pass
            for (uint y = 0; y < h; ++y)
            for (uint x = 0; x < w; ++x)
            {
                float v = 0, ws = 0;
                for (int k = -radius; k <= radius; ++k)
                {
                    int yi = (int)y + k;
                    if (yi < 0 || yi >= (int)h) continue;
                    float kw = kernel[k + radius];
                    v  += tmp[(uint)yi * w + x] * kw;
                    ws += kw;
                }
                img[y * w + x] = ws > 1e-6f ? v / ws : 0f;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Stage 5 – unsharp mask edge enhance
        // ─────────────────────────────────────────────────────────────────────

        private static void UnsharpMask(float[] img, uint w, uint h, float strength)
        {
            var blurred = new float[w * h];
            Array.Copy(img, blurred, img.Length);
            GaussianBlur(blurred, w, h, 2);
            for (uint i = 0; i < w * h; ++i)
                img[i] = img[i] + strength * (img[i] - blurred[i]);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Stage 6 – normalise to 0–1 over foreground pixels
        // ─────────────────────────────────────────────────────────────────────

        private static void NormaliseRange(float[] img, uint w, uint h, byte[] mask)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (uint i = 0; i < w * h; ++i)
            {
                if (mask[i] == 0) continue;
                if (img[i] < min) min = img[i];
                if (img[i] > max) max = img[i];
            }

            if (max - min < 1e-6f)
            {
                for (uint i = 0; i < w * h; ++i) img[i] = 0.5f;
                return;
            }

            for (uint i = 0; i < w * h; ++i)
                img[i] = mask[i] == 0 ? 0.5f : Clamp01((img[i] - min) / (max - min));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Orientation detection
        //
        // Strategy:
        //   1. Find the tight bounding rect of foreground pixels.
        //   2. Analyse left / right / top edge strips for partial fill density
        //      (a side face receding from the primary face has sparser density
        //       at the outer edge because it tapers away).
        //   3. Use bounding-rect aspect ratio to detect top-down views.
        // ─────────────────────────────────────────────────────────────────────

        private static ProductOrientation DetectOrientation(byte[] mask, uint w, uint h)
        {
            uint minX = w, maxX = 0, minY = h, maxY = 0;
            for (uint y = 0; y < h; ++y)
            for (uint x = 0; x < w; ++x)
            {
                if (mask[y * w + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            if (maxX <= minX || maxY <= minY) return ProductOrientation.Unknown;

            uint bw = maxX - minX;
            uint bh = maxY - minY;
            float aspect = bw / (float)bh;

            // Very wide bounding box relative to height = top-down orientation.
            if (aspect < 0.5f) return ProductOrientation.TopDown;

            int stripW = (int)(bw * 0.12f + 1);
            int stripH = (int)(bh * 0.12f + 1);

            float leftDensity  = ColumnStripDensity(mask, w, h, minX,             minY, stripW, (int)bh);
            float rightDensity = ColumnStripDensity(mask, w, h, maxX - (uint)stripW + 1, minY, stripW, (int)bh);
            float topDensity   = RowStripDensity   (mask, w, h, minX,             minY, (int)bw, stripH);

            // A partial side-face has density well below 1.0 but above background noise.
            bool hasRight = rightDensity < 0.70f && rightDensity > 0.12f;
            bool hasLeft  = leftDensity  < 0.70f && leftDensity  > 0.12f;
            bool hasTop   = topDensity   < 0.70f && topDensity   > 0.12f;

            if (hasRight && hasTop) return ProductOrientation.FrontTopRight;
            if (hasLeft  && hasTop) return ProductOrientation.FrontTopLeft;
            if (hasRight)           return ProductOrientation.FrontAndRightSide;
            if (hasLeft)            return ProductOrientation.FrontAndLeftSide;
            if (hasTop)             return ProductOrientation.FrontAndTop;

            return ProductOrientation.FrontOnly;
        }

        private static float ColumnStripDensity(byte[] mask, uint w, uint h,
                                                 uint x0, uint y0, int stripW, int stripH)
        {
            int count = 0, total = 0;
            for (int dy = 0; dy < stripH; ++dy)
            for (int dx = 0; dx < stripW; ++dx)
            {
                uint xi = x0 + (uint)dx;
                uint yi = y0 + (uint)dy;
                if (xi >= w || yi >= h) continue;
                count += mask[yi * w + xi];
                ++total;
            }
            return total == 0 ? 0f : count / (float)total;
        }

        private static float RowStripDensity(byte[] mask, uint w, uint h,
                                              uint x0, uint y0, int stripW, int stripH)
        {
            int count = 0, total = 0;
            for (int dy = 0; dy < stripH; ++dy)
            for (int dx = 0; dx < stripW; ++dx)
            {
                uint xi = x0 + (uint)dx;
                uint yi = y0 + (uint)dy;
                if (xi >= w || yi >= h) continue;
                count += mask[yi * w + xi];
                ++total;
            }
            return total == 0 ? 0f : count / (float)total;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Light-direction estimation (dominant Sobel gradient direction)
        // ─────────────────────────────────────────────────────────────────────

        private static Vector2 EstimateLightDirection(float[] lum, uint w, uint h, byte[] mask)
        {
            double gx = 0, gy = 0;
            for (uint y = 1; y < h - 1; ++y)
            for (uint x = 1; x < w - 1; ++x)
            {
                if (mask[y * w + x] == 0) continue;
                double dx = lum[y * w + (x + 1)] - lum[y * w + (x - 1)];
                double dy = lum[(y + 1) * w + x]  - lum[(y - 1) * w + x];
                double mag = Math.Sqrt(dx * dx + dy * dy);
                gx += dx * mag;
                gy += dy * mag;
            }
            double len = Math.Sqrt(gx * gx + gy * gy);
            if (len < 1e-6)
                return new Vector2(-0.707f, -0.707f);
            return new Vector2((float)(gx / len), (float)(gy / len));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Downsample (box filter)
        // ─────────────────────────────────────────────────────────────────────

        private static float[] Downsample(float[] src, uint sw, uint sh, int dw, int dh)
        {
            var dst    = new float[dw * dh];
            float scaleX = sw / (float)dw;
            float scaleY = sh / (float)dh;

            for (int dy = 0; dy < dh; ++dy)
            for (int dx = 0; dx < dw; ++dx)
            {
                int x0 = (int)(dx       * scaleX);
                int x1 = (int)((dx + 1) * scaleX);
                int y0 = (int)(dy       * scaleY);
                int y1 = (int)((dy + 1) * scaleY);

                float sum = 0; int cnt = 0;
                for (int y = y0; y < y1 && y < (int)sh; ++y)
                for (int x = x0; x < x1 && x < (int)sw; ++x)
                {
                    sum += src[y * sw + x];
                    ++cnt;
                }
                dst[dy * dw + dx] = cnt > 0 ? sum / cnt : 0.5f;
            }
            return dst;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Convert float[] 0–1 to R8 byte[]
        // ─────────────────────────────────────────────────────────────────────

        private static byte[] ToR8(float[] data)
        {
            var r8 = new byte[data.Length];
            for (int i = 0; i < data.Length; ++i)
                r8[i] = (byte)(Clamp01(data[i]) * 255f);
            return r8;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
