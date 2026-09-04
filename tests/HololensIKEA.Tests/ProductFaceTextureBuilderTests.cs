using System;
using System.Numerics;
using HololensIKEA.Services;
using Xunit;
using Xunit.Abstractions;

namespace HololensIKEA.Tests
{
    /// <summary>
    /// Tests for ProductFaceTextureBuilder — per-face CPU-side perspective
    /// correction that unwarps detected quad regions into clean rectangles.
    /// </summary>
    public class ProductFaceTextureBuilderTests
    {
        private readonly ITestOutputHelper _output;

        public ProductFaceTextureBuilderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: null / empty input
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_NullPixels_ReturnsEmptyResult()
        {
            var classification = MakeFrontOnlyClassification();
            var result = ProductFaceTextureBuilder.Build(null, 0, 0, classification);

            Assert.NotNull(result);
            Assert.Null(result.Front);
            Assert.Null(result.Side);
        }

        [Fact]
        public void Build_ZeroDimensions_ReturnsEmptyResult()
        {
            var classification = MakeFrontOnlyClassification();
            var result = ProductFaceTextureBuilder.Build(new byte[0], 0, 0, classification);

            Assert.NotNull(result);
            Assert.Null(result.Front);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: FrontOnly classification
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_FrontOnly_ProducesFrontTexture()
        {
            int w = 64, h = 64;
            var bgra = CreateSolidBgra(w, h, 200, 200, 200);
            var classification = MakeFrontOnlyClassification(
                frontQuad: MakeFullImageQuad(w, h));

            var result = ProductFaceTextureBuilder.Build(
                bgra, w, h, classification);

            Assert.NotNull(result.Front);
            Assert.True(result.Front.Width > 0, "Front width must be > 0");
            Assert.True(result.Front.Height > 0, "Front height must be > 0");
            Assert.Null(result.Side);
            Assert.Equal(ViewType.FrontOnly, result.ViewType);
            _output.WriteLine($"Front texture: {result.Front.Width}x{result.Front.Height}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: ThreeQuarterRight produces front + side
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_ThreeQuarterRight_ProducesFrontAndSide()
        {
            int w = 128, h = 64;
            var bgra = CreateSolidBgra(w, h, 180, 180, 180);
            var classification = MakeThreeQuarterClassification(
                ViewType.ThreeQuarterRight, w, h);

            var result = ProductFaceTextureBuilder.Build(
                bgra, w, h, classification, productDepthM: 0.2f, productHeightM: 0.3f);

            Assert.NotNull(result.Front);
            Assert.NotNull(result.Side);
            Assert.Equal(ViewType.ThreeQuarterRight, result.ViewType);
            _output.WriteLine($"Front: {result.Front.Width}x{result.Front.Height}");
            _output.WriteLine($"Side: {result.Side.Width}x{result.Side.Height}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: ThreeQuarterLeft produces front + side
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_ThreeQuarterLeft_ProducesFrontAndSide()
        {
            int w = 128, h = 64;
            var bgra = CreateSolidBgra(w, h, 180, 180, 180);
            var classification = MakeThreeQuarterClassification(
                ViewType.ThreeQuarterLeft, w, h);

            var result = ProductFaceTextureBuilder.Build(
                bgra, w, h, classification, productDepthM: 0.15f, productHeightM: 0.25f);

            Assert.NotNull(result.Front);
            Assert.NotNull(result.Side);
            Assert.Equal(ViewType.ThreeQuarterLeft, result.ViewType);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: output dimensions are clamped to MaxTexSize
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_LargeInput_OutputClampedToMaxTexSize()
        {
            int w = 1024, h = 1024;
            var bgra = CreateSolidBgra(w, h, 150, 150, 150);
            var classification = MakeFrontOnlyClassification(
                frontQuad: MakeFullImageQuad(w, h));

            var result = ProductFaceTextureBuilder.Build(
                bgra, w, h, classification);

            Assert.NotNull(result.Front);
            Assert.True(result.Front.Width <= ProductFaceTextureBuilder.MaxTexSize,
                $"Front width {result.Front.Width} exceeds MaxTexSize {ProductFaceTextureBuilder.MaxTexSize}");
            Assert.True(result.Front.Height <= ProductFaceTextureBuilder.MaxTexSize,
                $"Front height {result.Front.Height} exceeds MaxTexSize {ProductFaceTextureBuilder.MaxTexSize}");
            _output.WriteLine($"Clamped: {result.Front.Width}x{result.Front.Height}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: output pixel count matches dimensions
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_OutputPixelCountMatchesDimensions()
        {
            int w = 48, h = 48;
            var bgra = CreateSolidBgra(w, h, 100, 100, 100);
            var classification = MakeFrontOnlyClassification(
                frontQuad: MakeFullImageQuad(w, h));

            var result = ProductFaceTextureBuilder.Build(
                bgra, w, h, classification);

            Assert.NotNull(result.Front);
            int expectedPixels = result.Front.Width * result.Front.Height * 4; // BGRA
            Assert.Equal(expectedPixels, result.Front.BgraPix.Length);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build: solid color input produces uniform-ish output
        //  (perspective correction of a flat color should stay flat)
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_SolidColorInput_ProducesUniformOutput()
        {
            int w = 48, h = 48;
            byte r = 120, g = 140, b = 160;
            var bgra = CreateSolidBgra(w, h, b, g, r);
            var classification = MakeFrontOnlyClassification(
                frontQuad: MakeFullImageQuad(w, h));

            var result = ProductFaceTextureBuilder.Build(
                bgra, w, h, classification);

            Assert.NotNull(result.Front);
            Assert.NotNull(result.Front.BgraPix);

            // Check that most pixels are close to the input color
            // (some variation at edges due to bilinear sampling is OK)
            int closeCount = 0;
            for (int i = 0; i < result.Front.BgraPix.Length; i += 4)
            {
                if (Math.Abs(result.Front.BgraPix[i] - b) <= 5 &&
                    Math.Abs(result.Front.BgraPix[i + 1] - g) <= 5 &&
                    Math.Abs(result.Front.BgraPix[i + 2] - r) <= 5)
                    closeCount++;
            }
            int totalPixels = result.Front.BgraPix.Length / 4;
            float fraction = (float)closeCount / totalPixels;
            _output.WriteLine($"{closeCount}/{totalPixels} pixels within tolerance ({fraction:P1})");
            Assert.True(fraction > 0.8f,
                $"Expected >80% of pixels to match input color, got {fraction:P1}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  FaceTextureData: constructor sets properties correctly
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void FaceTextureData_Constructor_SetsProperties()
        {
            var pix = new byte[16];
            var data = new FaceTextureData(pix, 4, 4);

            Assert.Equal(pix, data.BgraPix);
            Assert.Equal(4, data.Width);
            Assert.Equal(4, data.Height);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  MultiFaceTextures: default ViewType is FrontOnly
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void MultiFaceTextures_DefaultViewTypeIsFrontOnly()
        {
            var mft = new MultiFaceTextures();
            Assert.Equal(ViewType.FrontOnly, mft.ViewType);
            Assert.Null(mft.Front);
            Assert.Null(mft.Side);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static byte[] CreateSolidBgra(int w, int h, byte b, byte g, byte r)
        {
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = 255;
            }
            return bgra;
        }

        private static ViewClassification MakeFrontOnlyClassification(
            FaceQuad? frontQuad = null)
        {
            return new ViewClassification
            {
                ViewType = ViewType.FrontOnly,
                FrontFace = frontQuad ?? MakeFullImageQuad(64, 64),
                SideFace = default
            };
        }

        private static ViewClassification MakeThreeQuarterClassification(
            ViewType viewType, int w, int h)
        {
            // Front face is the left portion, side face is the right portion
            return new ViewClassification
            {
                ViewType = viewType,
                FrontFace = new FaceQuad(
                    new Vector2(0, 0),
                    new Vector2(w * 0.55f, 0),
                    new Vector2(0, h),
                    new Vector2(w * 0.55f, h)),
                SideFace = new FaceQuad(
                    new Vector2(w * 0.55f, 0),
                    new Vector2(w, 0),
                    new Vector2(w * 0.55f, h),
                    new Vector2(w, h))
            };
        }

        private static FaceQuad MakeFullImageQuad(int w, int h)
        {
            return new FaceQuad(
                new Vector2(0, 0),
                new Vector2(w, 0),
                new Vector2(0, h),
                new Vector2(w, h));
        }
    }
}
