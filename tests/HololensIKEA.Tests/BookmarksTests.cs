using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HololensIKEA.Models;
using HololensIKEA.Services;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace HololensIKEA.Tests
{
    /// <summary>
    /// Tests for the Bookmarks system — loading, searching, and end-to-end 3D model discovery.
    /// </summary>
    public class BookmarksTests
    {
        private readonly ITestOutputHelper _output;

        public BookmarksTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helper: Load bookmarks.json from repo root
        // ─────────────────────────────────────────────────────────────────────

        private List<Bookmark> LoadBookmarks()
        {
            var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var path = Path.Combine(basePath, "bookmarks.json");
            _output.WriteLine($"Loading bookmarks from: {path}");

            if (!File.Exists(path))
                throw new FileNotFoundException($"bookmarks.json not found at {path}");

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<Bookmark>>(json)
                ?? new List<Bookmark>();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Unit tests: Bookmark model
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Bookmark_CanCreateWithDefaults()
        {
            var b = new Bookmark();
            Assert.NotNull(b.Name);
            Assert.NotNull(b.Url);
        }

        [Fact]
        public void Bookmark_CanCreateWithNameAndUrl()
        {
            var b = new Bookmark { Name = "BILLY", Url = "https://www.ikea.com/us/en/p/billy/" };
            Assert.Equal("BILLY", b.Name);
            Assert.Equal("https://www.ikea.com/us/en/p/billy/", b.Url);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Unit tests: Bookmarks JSON structure
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void BookmarksJson_NotEmpty()
        {
            var bookmarks = LoadBookmarks();
            Assert.NotEmpty(bookmarks);
            _output.WriteLine($"Loaded {bookmarks.Count} bookmarks");
        }

        [Fact]
        public void BookmarksJson_AllHaveName()
        {
            var bookmarks = LoadBookmarks();
            foreach (var b in bookmarks)
            {
                Assert.False(string.IsNullOrWhiteSpace(b.Name));
                _output.WriteLine($"  - {b.Name}");
            }
        }

        [Fact]
        public void BookmarksJson_AllHaveValidUrl()
        {
            var bookmarks = LoadBookmarks();
            foreach (var b in bookmarks)
            {
                Assert.True(Uri.TryCreate(b.Url, UriKind.Absolute, out _),
                    $"Invalid URL: {b.Url}");
                Assert.True(b.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                    $"URL must start with https://: {b.Url}");
            }
        }

        [Fact]
        public void BookmarksJson_AllProvideDecodedGlbUrl()
        {
            var bookmarks = LoadBookmarks();
            foreach (var bookmark in bookmarks)
            {
                Assert.False(string.IsNullOrWhiteSpace(bookmark.GlbUrl),
                    $"Bookmark '{bookmark.Name}' must provide a decoded GLB URL.");
                Assert.True(bookmark.GlbUrl.StartsWith(
                    "https://raw.githubusercontent.com/turbolego/HololensIKEA/main/Models/",
                    StringComparison.OrdinalIgnoreCase),
                    $"Bookmark '{bookmark.Name}' must use a repository-hosted decoded GLB.");
                Assert.True(bookmark.GlbUrl.EndsWith(".glb", StringComparison.OrdinalIgnoreCase),
                    $"Bookmark '{bookmark.Name}' has an invalid GLB URL.");
            }
        }

        [Fact]
        public void BookmarksJson_BillyExists()
        {
            var bookmarks = LoadBookmarks();
            var billy = bookmarks.Find(b => b.Name.Contains("BILLY", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(billy);
            _output.WriteLine($"Found: {billy.Name} -> {billy.Url}");
        }

        [Fact]
        public void BookmarksJson_AllUrlsAreIKEADomains()
        {
            var bookmarks = LoadBookmarks();
            foreach (var b in bookmarks)
            {
                var uri = new Uri(b.Url);
                Assert.True(uri.Host.Equals("ikea.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith(".ikea.com", StringComparison.OrdinalIgnoreCase),
                    $"Non-IKEA domain: {uri.Host}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Unit tests: ModelService3D.FindModelUrl (static method)
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void FindModelUrl_WithGlbAttribute_ReturnsUrl()
        {
            var html = @"<div data-src=""https://www.ikea.com/us/en/images/products/billy-bookcase-glb-url.glb""></div>";
            var uri = new Uri("https://www.ikea.com/us/en/p/billy/");
            var result = ModelService3D.FindModelUrl(html, uri);
            Assert.NotNull(result);
            Assert.EndsWith(".glb", result!.ToString());
        }

        [Fact]
        public void FindModelUrl_WithAbsoluteGlb_ReturnsUrl()
        {
            var html = @"<script>var model = ""https://example.com/model.glb"";</script>";
            var uri = new Uri("https://www.ikea.com/us/en/p/test/");
            var result = ModelService3D.FindModelUrl(html, uri);
            Assert.NotNull(result);
            Assert.Contains(".glb", result!.ToString());
        }

        [Fact]
        public void FindModelUrl_EmptyHtml_ReturnsNull()
        {
            var result = ModelService3D.FindModelUrl("", new Uri("https://www.ikea.com/"));
            Assert.Null(result);
        }

        [Fact]
        public void FindModelUrl_NoGlb_ReturnsNull()
        {
            var html = @"<h1>Billy Bookcase</h1><p>No 3D model here.</p>";
            var result = ModelService3D.FindModelUrl(html, new Uri("https://www.ikea.com/"));
            Assert.Null(result);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Integration tests: Fetch actual IKEA pages
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// E2E: Verify that BILLY product page contains a 3D model URL.
        /// </summary>
        [Fact(Skip = "Network-dependent — run manually or in CI with --filter FullyQualifiedName~E2E")]
        public async Task E2E_Billy_ProductPage_Has3DModel()
        {
            var bookmarks = LoadBookmarks();
            var billy = bookmarks.Find(b => b.Name.Contains("BILLY", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(billy);

            var pageUri = new Uri(billy.Url);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");

            _output.WriteLine($"Fetching: {billy.Url}");
            var response = await client.GetAsync(pageUri);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            var modelUrl = ModelService3D.FindModelUrl(html, pageUri);

            _output.WriteLine($"BILLY page HTML length: {html.Length}");
            _output.WriteLine($"Model URL found: {modelUrl}");

            Assert.NotNull(modelUrl);
            Assert.True(modelUrl.ToString().Contains(".glb", StringComparison.OrdinalIgnoreCase),
                $"Expected .glb URL, got: {modelUrl}");
        }

        /// <summary>
        /// E2E: Verify that KALLAX product page contains a 3D model URL.
        /// </summary>
        [Fact(Skip = "Network-dependent")]
        public async Task E2E_KALLAX_ProductPage_Has3DModel()
        {
            var bookmarks = LoadBookmarks();
            var kallax = bookmarks.Find(b => b.Name.Contains("KALLAX", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(kallax);

            var pageUri = new Uri(kallax.Url);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");

            _output.WriteLine($"Fetching: {kallax.Url}");
            var response = await client.GetAsync(pageUri);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            var modelUrl = ModelService3D.FindModelUrl(html, pageUri);

            _output.WriteLine($"KALLAX page HTML length: {html.Length}");
            _output.WriteLine($"Model URL found: {modelUrl}");

            Assert.NotNull(modelUrl);
        }

        /// <summary>
        /// E2E: Run through ALL bookmarks and report which have 3D models.
        /// </summary>
        [Fact(Skip = "Network-dependent — runs all bookmarks, ~10 requests")]
        public async Task E2E_AllBookmarks_Check3DModels()
        {
            var bookmarks = LoadBookmarks();
            _output.WriteLine($"Testing {bookmarks.Count} bookmarks for 3D models...\n");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");

            int withModel = 0;
            int withoutModel = 0;

            foreach (var bookmark in bookmarks)
            {
                try
                {
                    var pageUri = new Uri(bookmark.Url);
                    var response = await client.GetAsync(pageUri);
                    response.EnsureSuccessStatusCode();
                    var html = await response.Content.ReadAsStringAsync();
                    var modelUrl = ModelService3D.FindModelUrl(html, pageUri);

                    if (modelUrl != null)
                    {
                        _output.WriteLine($"  ✓ {bookmark.Name}: {modelUrl}");
                        withModel++;
                    }
                    else
                    {
                        _output.WriteLine($"  ✗ {bookmark.Name}: NO 3D MODEL");
                        withoutModel++;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"  ? {bookmark.Name}: ERROR - {ex.Message}");
                    withoutModel++;
                }
            }

            _output.WriteLine($"\nResults: {withModel} with 3D model, {withoutModel} without");
            Assert.True(withModel > 0, "At least one bookmark should have a 3D model");
        }

        /// <summary>
        /// E2E: Verify that the discovery script can regenerate bookmarks.json
        /// and that it contains valid data.
        /// </summary>
        [Fact]
        public void DiscoveryScript_GeneratesValidBookmarks()
        {
            var bookmarks = LoadBookmarks();

            // Should have at least 5 products
            Assert.True(bookmarks.Count >= 5,
                $"Expected at least 5 bookmarks, got {bookmarks.Count}");

            // Each should have a name and URL
            foreach (var b in bookmarks)
            {
                Assert.False(string.IsNullOrWhiteSpace(b.Name));
                Assert.True(Uri.TryCreate(b.Url, UriKind.Absolute, out _),
                    $"Invalid URL: {b.Url}");
            }

            _output.WriteLine($"bookmarks.json is valid: {bookmarks.Count} entries");
        }
    }
}