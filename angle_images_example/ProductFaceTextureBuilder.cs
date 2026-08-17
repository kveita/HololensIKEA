using System;
using System.Diagnostics;
using System.Numerics;

namespace HololensIKEA.Services
{
    /// <summary>
    /// Output of a single-face unwarp operation.
    /// </summary>
    public sealed class FaceTextureData
    {
        /// <summary>BGRA8 pixel data for this face, <see cref="Width"/> × <see cref="Height"/> pixels.</summary>
        public byte[] BgraPix { get; }
        public int    Width   { get; }
        public int    Height  { get; }

        public FaceTextureData(byte[] pix, int w, int h)
        { BgraPix = pix; Width = w; Height = h; }
    }

    /// <summary>
    /// All face textures extracted from one product image.
    /// </summary>
    public sealed class MultiFaceTextures
    {
        /// <summary>
        /// Perspective-corrected front-face texture.
        /// Always present; for FrontOnly images this is a tight crop of the whole product.
        /// </summary>
        public FaceTextureData Front { get; set; }

        /// <summary>
        /// Perspective-corrected side-face texture (right or left according to
        /// <see cref="ViewClassification.ViewType"/>).
        /// Null for FrontOnly images.
        /// </summary>
        public FaceTextureData Side  { get; set; }

        /// <summary>Which side the <see cref="Side"/> texture belongs to.</summary>
        public ViewType ViewType { get; set; } = ViewType.FrontOnly;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ProductFaceTextureBuilder
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs per-face CPU-side perspective correction on a product image.
    ///
    /// Given BGRA8 source pixels and a <see cref="ViewClassification"/> (from
    /// <see cref="ProductViewClassifier"/>), it unwarps each face region from its
    /// (possibly trapezoidal) shape in the photograph into a clean rectangle,
    /// ready to upload as a D3D11 texture for the corresponding box face.
    ///
    /// Perspective model:
    ///   We use a full projective (homographic) warp.  For each destination pixel
    ///   (u, v) in the output rectangle, we solve for the corresponding source
    ///   pixel via the inverse 3×3 homography H⁻¹, computed from the 4 known
    ///   corner correspondences.  We then bilinearly sample the source image at
    ///   that fractional coordinate.
    ///
    ///   This is mathematically exact for pinhole-camera perspective and handles
    ///   both the mild trapezoid of the front face and the steeper foreshortening
    ///   of the side face correctly.
    ///
    /// Side-face aspect correction:
    ///   The side face as captured in the photo is foreshortened in the horizontal
    ///   direction (the product's real depth is compressed).  We already "un-foreshorten"
    ///   it naturally by unwarping the detected side quad onto a destination rectangle
    ///   whose width/height ratio matches the product's real depth/height ratio
    ///   (supplied from JSON).  The renderer then stretches this onto the correct
    ///   physical box face, which has the same ratio — so proportions are preserved.
    ///
    /// Output resolution:
    ///   Front face: min(sourceWidth, MaxTexSize) × min(sourceHeight, MaxTexSize)
    ///               clamped to power-of-two for GPU compatibility (optional).
    ///   Side face:  height = same as front face height
    ///               width  = height × (productDepth / productHeight), clamped to MaxTexSize.
    /// </summary>
    public static class ProductFaceTextureBuilder
    {
        /// <summary>Maximum output texture dimension (each axis). Keep ≤ 1024 for HoloLens 1.</summary>
        public const int MaxTexSize = 512;

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds perspective-corrected textures for each visible face.
        /// </summary>
        /// <param name="srcBgra">Source BGRA8 pixels, width × height.</param>
        /// <param name="srcWidth">Source image width.</param>
        /// <param name="srcHeight">Source image height.</param>
        /// <param name="classification">Output of <see cref="ProductViewClassifier.Classify"/>.</param>
        /// <param name="productDepthM">Product physical depth in metres (from JSON). Used to
        ///   set the side-face texture's aspect ratio so it matches the real box face.</param>
        /// <param name="productHeightM">Product physical height in metres (from JSON).</param>
        public static MultiFaceTextures Build(
            byte[]             srcBgra,
            int                srcWidth,
            int                srcHeight,
            ViewClassification classification,
            float              productDepthM  = 0.1f,
            float              productHeightM = 0.1f)
        {
            var result = new MultiFaceTextures { ViewType = classification.ViewType };

            try
            {
                // ── Front face ────────────────────────────────────────────────
                int frontW = Math.Min(MaxTexSize, srcWidth);
                int frontH = Math.Min(MaxTexSize, srcHeight);

                // Maintain source aspect ratio of the front quad
                float frontAspect = classification.FrontFace.MidWidth
                                  / Math.Max(1f, classification.FrontFace.MidHeight);

                if (frontAspect >= 1f)
                    frontH = Math.Max(1, (int)(frontW / frontAspect));
                else
                    frontW = Math.Max(1, (int)(frontH * frontAspect));

                frontW = Math.Min(frontW, MaxTexSize);
                frontH = Math.Min(frontH, MaxTexSize);

                result.Front = UnwarpFace(
                    srcBgra, srcWidth, srcHeight,
                    classification.FrontFace,
                    frontW, frontH);

                // ── Side face (ThreeQuarter views only) ───────────────────────
                if (classification.ViewType == ViewType.ThreeQuarterRight ||
                    classification.ViewType == ViewType.ThreeQuarterLeft)
                {
                    // Determine output dimensions: use the same height as the front face,
                    // width set by the real depth:height ratio so the texture fits the box face.
                    float depthHeightRatio = productDepthM / Math.Max(0.001f, productHeightM);
                    int   sideH = frontH;
                    int   sideW = Math.Max(1, Math.Min(MaxTexSize, (int)(sideH * depthHeightRatio)));

                    result.Side = UnwarpFace(
                        srcBgra, srcWidth, srcHeight,
                        classification.SideFace,
                        sideW, sideH);

                    Debug.WriteLine(
                        $"[FaceTexBuilder] side {sideW}×{sideH}" +
                        $"  (depth:height={depthHeightRatio:F2})");
                }

                Debug.WriteLine(
                    $"[FaceTexBuilder] front {frontW}×{frontH}" +
                    $"  viewType={classification.ViewType}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FaceTexBuilder] " + ex.Message);
                // Fallback: crop entire image for the front face
                if (result.Front == null)
                    result.Front = CropEntireImage(srcBgra, srcWidth, srcHeight);
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core: perspective unwarp  (quad → rect)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Warps the source quad <paramref name="quad"/> (4 pixel-space corners)
        /// onto a destination rectangle of size <paramref name="dstW"/>×<paramref name="dstH"/>.
        ///
        /// Uses a full projective (homographic) inverse mapping:
        ///   For each destination pixel (u,v), compute the corresponding source
        ///   position via H⁻¹ (the inverse of the projective map quad→rect),
        ///   then bilinearly sample the source BGRA image.
        ///
        /// H is computed from the 4 corner correspondences by solving the
        /// standard 8-equation linear system (see <see cref="ComputeHomography"/>).
        /// </summary>
        private static FaceTextureData UnwarpFace(
            byte[] src, int srcW, int srcH,
            FaceQuad quad,
            int dstW, int dstH)
        {
            // Destination corners (normalised then scaled to dst pixels):
            // TL=(0,0), TR=(dstW-1,0), BL=(0,dstH-1), BR=(dstW-1,dstH-1)
            //
            // We want H_inv: maps (dstX, dstY) → (srcX, srcY)
            // Compute H: maps (srcX, srcY) → (dstX, dstY) from the 4 point pairs,
            // then invert it.

            // Source corners (from the detected quad)
            var srcPts = new Vector2[]
            {
                quad.TL, quad.TR, quad.BL, quad.BR
            };

            // Destination corners
            var dstPts = new Vector2[]
            {
                new Vector2(0,         0        ),
                new Vector2(dstW - 1,  0        ),
                new Vector2(0,         dstH - 1 ),
                new Vector2(dstW - 1,  dstH - 1 ),
            };

            // Compute H: src→dst.  Then invert to get H_inv: dst→src.
            double[] H = ComputeHomography(srcPts, dstPts);
            if (H == null)
            {
                // Degenerate quad — fall back to crop
                return CropQuad(src, srcW, srcH, quad, dstW, dstH);
            }
            double[] Hinv = Invert3x3(H);
            if (Hinv == null)
                return CropQuad(src, srcW, srcH, quad, dstW, dstH);

            // Rasterise the destination rectangle
            var dst = new byte[dstW * dstH * 4];

            for (int dy = 0; dy < dstH; ++dy)
            for (int dx = 0; dx < dstW; ++dx)
            {
                // Map destination pixel → source pixel via H_inv
                double w_  = Hinv[6] * dx + Hinv[7] * dy + Hinv[8];
                if (Math.Abs(w_) < 1e-9) continue;
                double sx = (Hinv[0] * dx + Hinv[1] * dy + Hinv[2]) / w_;
                double sy = (Hinv[3] * dx + Hinv[4] * dy + Hinv[5]) / w_;

                // Bilinear sample from source
                SampleBilinear(src, srcW, srcH, sx, sy,
                    out byte rb, out byte gb, out byte bb, out byte ab);

                int dstIdx = (dy * dstW + dx) * 4;
                dst[dstIdx + 0] = bb;
                dst[dstIdx + 1] = gb;
                dst[dstIdx + 2] = rb;
                dst[dstIdx + 3] = ab;
            }

            return new FaceTextureData(dst, dstW, dstH);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Homography computation  (8×8 DLT)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the 3×3 projective homography H (row-major, 9 elements)
        /// that maps each srcPt[i] to dstPt[i] (i = 0..3).
        ///
        /// Uses the standard Direct Linear Transform (DLT):
        ///   For each correspondence (x,y) → (u,v):
        ///     h11*x + h12*y + h13 − h31*x*u − h32*y*u = u
        ///     h21*x + h22*y + h23 − h31*x*v − h32*y*v = v
        ///   With h33 = 1 (normalisation), this gives 8 equations in 8 unknowns.
        ///   Solved via Gaussian elimination with partial pivoting.
        /// </summary>
        private static double[] ComputeHomography(Vector2[] src, Vector2[] dst)
        {
            // Build 8×8 matrix A and rhs vector b
            var A = new double[8, 8];
            var b = new double[8];

            for (int i = 0; i < 4; ++i)
            {
                double x = src[i].X, y = src[i].Y;
                double u = dst[i].X, v = dst[i].Y;

                // Row 2i:   h11*x + h12*y + h13 - h31*x*u - h32*y*u = u
                int r0 = i * 2;
                A[r0, 0] = x;  A[r0, 1] = y;  A[r0, 2] = 1;
                A[r0, 3] = 0;  A[r0, 4] = 0;  A[r0, 5] = 0;
                A[r0, 6] = -x * u; A[r0, 7] = -y * u;
                b[r0] = u;

                // Row 2i+1: h21*x + h22*y + h23 - h31*x*v - h32*y*v = v
                int r1 = r0 + 1;
                A[r1, 0] = 0;  A[r1, 1] = 0;  A[r1, 2] = 0;
                A[r1, 3] = x;  A[r1, 4] = y;  A[r1, 5] = 1;
                A[r1, 6] = -x * v; A[r1, 7] = -y * v;
                b[r1] = v;
            }

            // Gaussian elimination with partial pivoting
            if (!GaussianEliminate(A, b, 8, out double[] sol))
                return null;

            // H = [h11 h12 h13 h21 h22 h23 h31 h32 h33=1]
            return new double[]
            {
                sol[0], sol[1], sol[2],
                sol[3], sol[4], sol[5],
                sol[6], sol[7], 1.0
            };
        }

        private static bool GaussianEliminate(double[,] A, double[] b, int n, out double[] x)
        {
            x = new double[n];
            // Forward elimination
            for (int col = 0; col < n; ++col)
            {
                // Find pivot
                int pivot = col;
                for (int row = col + 1; row < n; ++row)
                    if (Math.Abs(A[row, col]) > Math.Abs(A[pivot, col]))
                        pivot = row;

                // Swap rows
                if (pivot != col)
                {
                    for (int k = 0; k < n; ++k)
                    { double tmp = A[col, k]; A[col, k] = A[pivot, k]; A[pivot, k] = tmp; }
                    { double tmp = b[col];    b[col]    = b[pivot];    b[pivot]    = tmp; }
                }

                if (Math.Abs(A[col, col]) < 1e-12) return false; // singular

                for (int row = col + 1; row < n; ++row)
                {
                    double f = A[row, col] / A[col, col];
                    for (int k = col; k < n; ++k) A[row, k] -= f * A[col, k];
                    b[row] -= f * b[col];
                }
            }
            // Back substitution
            for (int row = n - 1; row >= 0; --row)
            {
                x[row] = b[row];
                for (int k = row + 1; k < n; ++k) x[row] -= A[row, k] * x[k];
                x[row] /= A[row, row];
            }
            return true;
        }

        /// <summary>Inverts a 3×3 matrix stored row-major in a 9-element array.</summary>
        private static double[] Invert3x3(double[] m)
        {
            // Cofactor / adjugate method
            double a = m[0], b = m[1], c = m[2];
            double d = m[3], e = m[4], f = m[5];
            double g = m[6], h = m[7], k = m[8];

            double det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-12) return null;
            double inv = 1.0 / det;

            return new double[]
            {
                (e*k - f*h) * inv, (c*h - b*k) * inv, (b*f - c*e) * inv,
                (f*g - d*k) * inv, (a*k - c*g) * inv, (c*d - a*f) * inv,
                (d*h - e*g) * inv, (b*g - a*h) * inv, (a*e - b*d) * inv,
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bilinear sampler
        // ─────────────────────────────────────────────────────────────────────

        private static void SampleBilinear(byte[] src, int srcW, int srcH,
            double sx, double sy,
            out byte rb, out byte gb, out byte bb, out byte ab)
        {
            // Clamp to valid range
            sx = Math.Max(0, Math.Min(srcW - 1.001, sx));
            sy = Math.Max(0, Math.Min(srcH - 1.001, sy));

            int   x0 = (int)sx,   y0 = (int)sy;
            int   x1 = x0 + 1,    y1 = y0 + 1;
            float tx = (float)(sx - x0), ty = (float)(sy - y0);

            x1 = Math.Min(x1, srcW - 1);
            y1 = Math.Min(y1, srcH - 1);

            int i00 = (y0 * srcW + x0) * 4;
            int i10 = (y0 * srcW + x1) * 4;
            int i01 = (y1 * srcW + x0) * 4;
            int i11 = (y1 * srcW + x1) * 4;

            bb = Lerp2(src[i00], src[i10], src[i01], src[i11], tx, ty);
            gb = Lerp2(src[i00+1], src[i10+1], src[i01+1], src[i11+1], tx, ty);
            rb = Lerp2(src[i00+2], src[i10+2], src[i01+2], src[i11+2], tx, ty);
            ab = Lerp2(src[i00+3], src[i10+3], src[i01+3], src[i11+3], tx, ty);
        }

        private static byte Lerp2(byte c00, byte c10, byte c01, byte c11, float tx, float ty)
        {
            float v = c00 * (1 - tx) * (1 - ty)
                    + c10 * tx       * (1 - ty)
                    + c01 * (1 - tx) * ty
                    + c11 * tx       * ty;
            return (byte)Math.Max(0, Math.Min(255, (int)(v + 0.5f)));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fallbacks
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Simple axis-aligned crop of the bounding rect of a quad.</summary>
        private static FaceTextureData CropQuad(
            byte[] src, int srcW, int srcH,
            FaceQuad quad, int dstW, int dstH)
        {
            int x0 = (int)Math.Max(0, Math.Min(quad.TL.X, quad.BL.X));
            int y0 = (int)Math.Max(0, Math.Min(quad.TL.Y, quad.TR.Y));
            int x1 = (int)Math.Min(srcW - 1, Math.Max(quad.TR.X, quad.BR.X));
            int y1 = (int)Math.Min(srcH - 1, Math.Max(quad.BL.Y, quad.BR.Y));

            int cropW = Math.Max(1, x1 - x0 + 1);
            int cropH = Math.Max(1, y1 - y0 + 1);

            var cropped = new byte[cropW * cropH * 4];
            for (int cy = 0; cy < cropH; ++cy)
            for (int cx = 0; cx < cropW; ++cx)
            {
                int srcIdx = ((y0 + cy) * srcW + (x0 + cx)) * 4;
                int dstIdx = (cy * cropW + cx) * 4;
                cropped[dstIdx]     = src[srcIdx];
                cropped[dstIdx + 1] = src[srcIdx + 1];
                cropped[dstIdx + 2] = src[srcIdx + 2];
                cropped[dstIdx + 3] = src[srcIdx + 3];
            }

            // Nearest-neighbour resize to dstW×dstH
            return NNResize(cropped, cropW, cropH, dstW, dstH);
        }

        private static FaceTextureData CropEntireImage(byte[] src, int srcW, int srcH)
        {
            int w = Math.Min(srcW, MaxTexSize);
            int h = Math.Min(srcH, MaxTexSize);
            return NNResize(src, srcW, srcH, w, h);
        }

        private static FaceTextureData NNResize(byte[] src, int srcW, int srcH, int dstW, int dstH)
        {
            var dst = new byte[dstW * dstH * 4];
            float sx = srcW / (float)dstW, sy = srcH / (float)dstH;
            for (int dy = 0; dy < dstH; ++dy)
            for (int dx = 0; dx < dstW; ++dx)
            {
                int ox = Math.Min(srcW - 1, (int)(dx * sx));
                int oy = Math.Min(srcH - 1, (int)(dy * sy));
                int si = (oy * srcW + ox) * 4;
                int di = (dy * dstW + dx) * 4;
                dst[di] = src[si]; dst[di+1] = src[si+1];
                dst[di+2] = src[si+2]; dst[di+3] = src[si+3];
            }
            return new FaceTextureData(dst, dstW, dstH);
        }
    }
}
