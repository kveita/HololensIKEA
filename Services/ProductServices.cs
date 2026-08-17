using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HololensIKEA.Models;

namespace HololensIKEA.Services
{
    /// <summary>Loads IKEA product pages and creates the renderable scene metadata.</summary>
    public sealed class IkeaProductRepository
    {
        private readonly Dictionary<string, RenderableProduct> cache =
            new Dictionary<string, RenderableProduct>(StringComparer.OrdinalIgnoreCase);

        public async Task<RenderableProduct> GetProductAsync(string productPageUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(productPageUrl))
                throw new ArgumentException("An IKEA product URL is required.", nameof(productPageUrl));
            if (!productPageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !productPageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The value must be an HTTP(S) URL.", nameof(productPageUrl));
            if (cache.TryGetValue(productPageUrl, out var cached)) return cached;

            var pageUri = new Uri(productPageUrl, UriKind.Absolute);
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");
                var html = await client.GetStringAsync(pageUri);
                var product = new RenderableProduct
                {
                    ProductId = productPageUrl.GetHashCode(),
                    ProductName = ExtractTitle(html) ?? "IKEA product",
                    // IKEA product-page dimensions are not needed for model rendering;
                    // the GLB bounds replace these defaults when it arrives.
                    WidthMeters = 1.0f,
                    HeightMeters = 1.0f,
                    DepthMeters = 1.0f,
                    Has3DModel = ModelService3D.FindModelUrl(html, pageUri) != null,
                    ModelUrl = productPageUrl
                };
                cache[productPageUrl] = product;
                return product;
            }
        }

        private static string ExtractTitle(string html)
        {
            var match = Regex.Match(html ?? string.Empty,
                @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return null;
            var title = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            title = Regex.Replace(title, @"\s+", " ").Trim();
            var separator = title.IndexOf(" - IKEA", StringComparison.OrdinalIgnoreCase);
            return separator > 0 ? title.Substring(0, separator).Trim() : title;
        }
    }
}
