using System;
using System.Collections.Generic;
using System.Numerics;
using HololensIKEA.Services;
using Xunit;

namespace HololensIKEA.Tests
{
    /// <summary>
    /// Tests for the ProductViewClassifier that determines product box face visibility
    /// from product images (front-only vs three-quarter views with side faces).
    /// </summary>
    public class ProductViewClassifierTests
    {
        // ──────────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>Creates a white BGRA image (all 255).</summary>
        private static byte[] WhiteImage(uint w, uint h)
        {
            var data = new byte[w * h * 4];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i + 0] = 255; // B
                data[i + 1] = 255; // G
                data[i + 2] = 255; // R
                data[i + 3] = 255; // A
            }
            return data;
        }

        /// <summary>Creates a two-tone image: left half white (front), right half dark gray (side).</summary>
        private static byte[] TwoToneImage(uint w, uint h, bool sideOnRight = true)
        {
            var data = new byte[w * h * 4];
            int split = (int)w / 2;

            for (uint y = 0; y < h; y++)
            {
                for (uint x = 0; x < w; x++)
                {
                    int idx = (int)(y * w + x) * 4;
                    bool isSide = sideOnRight ? x >= split : x < split;

                    byte r = isSide ? (byte)80 : (byte)255;
                    byte g = isSide ? (byte)80 : (byte)255;
                    byte b = isSide ? (byte)80 : (byte)255;

                    data[idx + 0] = b;
                    data[idx + 1] = g;
                    data[idx + 2] = r;
                    data[idx + 3] = 255;
                }
            }
            return data;
        }

        /// <summary>Creates a top-down view image: top half white, bottom half dark.</summary>
        private static byte[] TopDownImage(uint w, uint h)
        {
            var data = new byte[w * h * 4];
            int split = (int)h / 2;

            for (uint y = 0; y < h; y++)
            {
                for (uint x = 0; x < w; x++)
                {
                    int idx = (int)(y * w + x) * 4;
                    bool isTop = y < split;

                    byte r = isTop ? (byte)255 : (byte)80;
                    byte g = isTop ? (byte)255 : (byte)80;
                    byte b = isTop ? (byte)255 : (byte)80;

                    data[idx + 0] = b;
                    data[idx + 1] = g;
                    data[idx + 2] = r;
                    data[idx + 3] = 255;
                }
            }
            return data;
        }

        private static byte[] GradientImage(uint w, uint h)
        {
            var data = new byte[w * h * 4];
            for (uint y = 0; y < h; y++)
            {
                for (uint x = 0; x < w; x++)
                {
                    int idx = (int)(y * w + x) * 4;
                    byte val = (byte)((x * 255u) / w); // horizontal gradient
                    data[idx + 0] = val;
                    data[idx + 1] = val;
                    data[idx + 2] = val;
                    data[idx + 3] = 255;
                }
            }
            return data;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Tests
        // ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Classify_AllWhiteImage_ReturnsFrontOnly()
        {
            var img = WhiteImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            Assert.Equal(ViewType.FrontOnly, result.ViewType);
            Assert.Equal(1f, result.FrontFaceWidthFraction, 2);
        }

        [Fact]
        public void Classify_NullOrEmpty_ReturnsFrontOnly()
        {
            var r1 = ProductViewClassifier.Classify(null, 100, 100);
            var r2 = ProductViewClassifier.Classify(new byte[0], 100, 100);
            var r3 = ProductViewClassifier.Classify(new byte[10], 10, 1);

            Assert.Equal(ViewType.FrontOnly, r1.ViewType);
            Assert.Equal(ViewType.FrontOnly, r2.ViewType);
            Assert.Equal(ViewType.FrontOnly, r3.ViewType);
        }

        [Fact]
        public void Classify_SmallContent_ReturnsFrontOnly()
        {
            // Very small foreground region (< 10px) should not trigger 3/4 detection
            var img = WhiteImage(100, 100);
            // Make a tiny non-white region
            for (int i = 0; i < 4; i++)
            {
                int idx = (50 * 100 + 50) * 4 + i;
                img[idx] = 80; img[idx + 1] = 80; img[idx + 2] = 80;
            }

            var result = ProductViewClassifier.Classify(img, 100, 100);

            Assert.Equal(ViewType.FrontOnly, result.ViewType);
        }

        [Fact]
        public void Classify_TopDownImage_ReturnsFrontOnly()
        {
            // Top-down is not a 3/4 side view; the classifier looks for vertical creases
            var img = TopDownImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            // With a horizontal split, there's no vertical crease in luminance profile
            // (the algorithm scans columns), so it should be FrontOnly
            Assert.Equal(ViewType.FrontOnly, result.ViewType);
        }

        [Fact]
        public void Classify_SolidColorImage_ReturnsFrontOnly()
        {
            // Solid color (no variation) should not trigger 3/4 detection
            var img = WhiteImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            Assert.Equal(ViewType.FrontOnly, result.ViewType);
        }

        [Fact]
        public void Classify_ConfidenceWithinBounds()
        {
            var img = WhiteImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            Assert.InRange(result.Confidence, 0f, 1f);
        }

        [Fact]
        public void Classify_FrontFaceQuadAlwaysValid()
        {
            var img = WhiteImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            // FrontFace is a struct, always valid
            Assert.Equal(0, result.FrontFace.TL.X);
            Assert.Equal(0, result.FrontFace.TL.Y);
            Assert.Equal(199, result.FrontFace.TR.X);
            Assert.Equal(0, result.FrontFace.TR.Y);
            Assert.Equal(0, result.FrontFace.BL.X);
            Assert.Equal(149, result.FrontFace.BL.Y);
            Assert.Equal(199, result.FrontFace.BR.X);
            Assert.Equal(149, result.FrontFace.BR.Y);
        }

        [Fact]
        public void Classify_CreaseXWithinImageBounds()
        {
            var img = WhiteImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            Assert.True(result.CreaseX >= 0 && result.CreaseX <= 200);
        }

        [Fact]
        public void Classify_FrontFaceWidthFractionWithinBounds()
        {
            var img = WhiteImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            Assert.InRange(result.FrontFaceWidthFraction, 0f, 1f);
        }

        [Fact]
        public void Classify_ViewTypeIsOneOfKnownValues()
        {
            var img = WhiteImage(200, 150);

            var result = ProductViewClassifier.Classify(img, 200, 150);

            // The result should be one of the enum values
            var values = Enum.GetValues(typeof(ViewType)) as ViewType[];
            Assert.Contains(result.ViewType, values);
        }
    }
}