using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using HololensIKEA.Services;
using Xunit;
using Xunit.Abstractions;

namespace HololensIKEA.Tests
{
    /// <summary>
    /// Integration tests for Draco-compressed GLB download and parsing.
    /// These tests download real IKEA models from the Rotera CDN and verify
    /// the full pipeline works end-to-end.
    ///
    /// Run manually or in CI with:
    ///   dotnet test --filter "FullyQualifiedName~DracoIntegrationTests"
    /// </summary>
    public class DracoIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public DracoIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Real IKEA product GLBs from Rotera CDN
        // ─────────────────────────────────────────────────────────────────────

        [Fact(Skip = "Network-dependent — run manually or in CI with --filter")]
        public async Task DownloadAndParse_BILLY_Bookcase_HasValidMesh()
        {
            // BILLY bookcase (50508652) — one of the most common IKEA products
            var glbUrl = "https://web-api.ikea.com/us/en/rotera/static/models/50508652-mini.glb";

            var glb = await DownloadGlbAsync(glbUrl);
            Assert.NotNull(glb);
            Assert.True(glb.Length > 100, $"GLB too small: {glb.Length} bytes");

            _output.WriteLine($"Downloaded {glb.Length} bytes from {glbUrl}");

            var mesh = ModelService3D.ParseGlb(glb);
            Assert.NotNull(mesh);

            // Verify positions are valid
            Assert.NotNull(mesh.Positions);
            Assert.True(mesh.Positions.Length > 0, "No positions in decoded mesh");
            foreach (var pos in mesh.Positions)
            {
                Assert.False(float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z),
                    $"NaN in position: ({pos.X}, {pos.Y}, {pos.Z})");
                Assert.False(float.IsInfinity(pos.X) || float.IsInfinity(pos.Y) || float.IsInfinity(pos.Z),
                    $"Infinity in position: ({pos.X}, {pos.Y}, {pos.Z})");
            }

            // Verify normals are valid
            Assert.NotNull(mesh.Normals);
            Assert.True(mesh.Normals.Length > 0, "No normals in decoded mesh");

            // Verify indices are valid
            Assert.NotNull(mesh.Indices);
            Assert.True(mesh.Indices.Length > 0, "No indices in decoded mesh");
            Assert.True(mesh.Indices.Length % 3 == 0, "Index count not multiple of 3 (not triangles)");

            // Verify bounds are realistic (not 1m³ placeholder cube)
            var maxDim = Math.Max(mesh.BoundsMeters.X,
                Math.Max(mesh.BoundsMeters.Y, mesh.BoundsMeters.Z));
            _output.WriteLine($"Bounds: {mesh.BoundsMeters} (max={maxDim}m)");
            Assert.False(maxDim >= 0.99f && maxDim <= 1.01f,
                $"Bounds look like the 1000x1000x1000mm placeholder cube: {mesh.BoundsMeters}");
            Assert.True(maxDim > 0.01f && maxDim < 5.0f,
                $"Bounds out of realistic range for furniture: {mesh.BoundsMeters}");
        }

        [Fact(Skip = "Network-dependent — run manually or in CI with --filter")]
        public async Task DownloadAndParse_KALLAX_Shelf_HasValidMesh()
        {
            // KALLAX shelf unit (702.758.93) — another common product
            var glbUrl = "https://web-api.ikea.com/us/en/rotera/static/models/70275893-mini.glb";

            var glb = await DownloadGlbAsync(glbUrl);
            Assert.NotNull(glb);

            var mesh = ModelService3D.ParseGlb(glb);
            Assert.NotNull(mesh);
            Assert.True(mesh.Positions.Length > 0, "No positions");

            var maxDim = Math.Max(mesh.BoundsMeters.X,
                Math.Max(mesh.BoundsMeters.Y, mesh.BoundsMeters.Z));
            _output.WriteLine($"KALLAX bounds: {mesh.BoundsMeters} (max={maxDim}m)");
            Assert.False(maxDim >= 0.99f && maxDim <= 1.01f,
                "Bounds look like placeholder cube");
        }

        [Fact(Skip = "Network-dependent — run manually or in CI with --filter")]
        public async Task DownloadAndParse_MALM_Dresser_HasValidMesh()
        {
            // MALM 6-drawer dresser (502.512.62)
            var glbUrl = "https://web-api.ikea.com/us/en/rotera/static/models/50251262-mini.glb";

            var glb = await DownloadGlbAsync(glbUrl);
            Assert.NotNull(glb);

            var mesh = ModelService3D.ParseGlb(glb);
            Assert.NotNull(mesh);
            Assert.True(mesh.Positions.Length > 0, "No positions");

            var maxDim = Math.Max(mesh.BoundsMeters.X,
                Math.Max(mesh.BoundsMeters.Y, mesh.BoundsMeters.Z));
            _output.WriteLine($"MALM bounds: {mesh.BoundsMeters} (max={maxDim}m)");
            Assert.False(maxDim >= 0.99f && maxDim <= 1.01f,
                "Bounds look like placeholder cube");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static async Task<byte[]> DownloadGlbAsync(string url)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");
                var bytes = await client.GetByteArrayAsync(url);
                return bytes;
            }
        }
    }
}
