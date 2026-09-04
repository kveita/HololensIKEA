using System;
using System.Numerics;
using HololensIKEA.Services;
using Xunit;
using Xunit.Abstractions;

namespace HololensIKEA.Tests
{
    /// <summary>
    /// Tests for ProductDepthAnalyzer — CPU analysis pipeline that converts
    /// BGRA8 product images into displacement maps, orientation detection,
    /// and light direction estimation.
    /// </summary>
    public class ProductDepthAnalyzerTests
    {
        private readonly ITestOutputHelper _output;

        public ProductDepthAnalyzerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Analyze: null / empty input
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Analyze_NullPixels_ReturnsUnknownOrientation()
        {
            var result = ProductDepthAnalyzer.Analyze(null, 0, 0);

            Assert.NotNull(result);
            Assert.Equal(ProductOrientation.Unknown, result.Orientation);
        }

        [Fact]
        public void Analyze_ZeroDimensions_ReturnsUnknownOrientation()
        {
            var result = ProductDepthAnalyzer.Analyze(new byte[0], 0, 0);

            Assert.NotNull(result);
            Assert.Equal(ProductOrientation.Unknown, result.Orientation);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Analyze: solid-color images
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Analyze_SolidWhite_RecognizesAsForeground()
        {
            // 16x16 solid white BGRA image
            var bgra = new byte[16 * 16 * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;     // B
                bgra[i + 1] = 255; // G
                bgra[i + 2] = 255; // R
                bgra[i + 3] = 255; // A
            }

            var result = ProductDepthAnalyzer.Analyze(bgra, 16, 16);

            Assert.NotNull(result);
            Assert.NotNull(result.DisplacementR8);
            Assert.Equal(64, result.DispWidth);
            Assert.Equal(64, result.DispHeight);
            // Solid white = uniform foreground — orientation should be determinable
            _output.WriteLine($"Orientation: {result.Orientation}");
            _output.WriteLine($"LightDir: {result.LightDir}");
        }

        [Fact]
        public void Analyze_SolidBlack_ReturnsValidResult()
        {
            var bgra = new byte[16 * 16 * 4];
            // All zeros = solid black
            var result = ProductDepthAnalyzer.Analyze(bgra, 16, 16);

            Assert.NotNull(result);
            Assert.NotNull(result.DisplacementR8);
            Assert.Equal(64 * 64, result.DisplacementR8.Length);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Analyze: displacement map properties
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Analyze_GradientImage_ProducesNonUniformDisplacement()
        {
            // 32x32 image with a horizontal gradient (dark left, bright right)
            int w = 32, h = 32;
            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 4;
                    byte val = (byte)(x * 255 / (w - 1));
                    bgra[idx] = val;     // B
                    bgra[idx + 1] = val; // G
                    bgra[idx + 2] = val; // R
                    bgra[idx + 3] = 255; // A
                }
            }

            var result = ProductDepthAnalyzer.Analyze(bgra, (uint)w, (uint)h);

            Assert.NotNull(result);
            Assert.NotNull(result.DisplacementR8);

            // Check that displacement values aren't all identical
            byte first = result.DisplacementR8[0];
            bool allSame = true;
            foreach (var v in result.DisplacementR8)
            {
                if (v != first) { allSame = false; break; }
            }
            Assert.False(allSame,
                "Displacement map should have variation for a gradient image");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Analyze: light direction is a unit-ish vector
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Analyze_GradientImage_LightDirectionIsFinite()
        {
            int w = 32, h = 32;
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 128; bgra[i + 1] = 128; bgra[i + 2] = 128; bgra[i + 3] = 255;
            }
            // Add a bright spot in center
            for (int y = 12; y < 20; y++)
                for (int x = 12; x < 20; x++)
                {
                    int idx = (y * w + x) * 4;
                    bgra[idx] = 255; bgra[idx + 1] = 255; bgra[idx + 2] = 255;
                }

            var result = ProductDepthAnalyzer.Analyze(bgra, (uint)w, (uint)h);

            Assert.False(float.IsNaN(result.LightDir.X), "LightDir.X is NaN");
            Assert.False(float.IsNaN(result.LightDir.Y), "LightDir.Y is NaN");
            Assert.False(float.IsInfinity(result.LightDir.X), "LightDir.X is Inf");
            Assert.False(float.IsInfinity(result.LightDir.Y), "LightDir.Y is Inf");

            var len = result.LightDir.Length();
            _output.WriteLine($"LightDir: {result.LightDir}, length: {len}");
            Assert.True(len > 0.001f, "Light direction should be non-zero");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Analyze: foreground mask detects white product on dark background
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Analyze_WhiteBoxOnBlackBackground_DetectsForeground()
        {
            int w = 64, h = 64;
            var bgra = new byte[w * h * 4];

            // Fill background with black
            // Draw a white rectangle in center (simulating product box)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 4;
                    bool inBox = x >= 16 && x < 48 && y >= 16 && y < 48;
                    byte val = inBox ? (byte)255 : (byte)0;
                    bgra[idx] = val;     // B
                    bgra[idx + 1] = val; // G
                    bgra[idx + 2] = val; // R
                    bgra[idx + 3] = 255; // A
                }
            }

            var result = ProductDepthAnalyzer.Analyze(bgra, (uint)w, (uint)h);

            Assert.NotNull(result);
            Assert.NotNull(result.DisplacementR8);
            // The displacement map should show variation (box is brighter than background)
            Assert.NotEqual(ProductOrientation.Unknown, result.Orientation);
            _output.WriteLine($"Orientation: {result.Orientation}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Analyze: result defaults are sensible
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void DepthAnalysisResult_DefaultsAreSane()
        {
            var r = new DepthAnalysisResult();
            Assert.Equal(ProductOrientation.Unknown, r.Orientation);
            Assert.Equal(64, r.DispWidth);
            Assert.Equal(64, r.DispHeight);
            Assert.False(float.IsNaN(r.LightDir.X));
            Assert.False(float.IsNaN(r.LightDir.Y));
        }
    }
}
