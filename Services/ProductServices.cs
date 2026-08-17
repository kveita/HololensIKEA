using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HololensIKEA.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HololensIKEA.Services
{
    /// <summary>
    /// Fetches raw JSON from HololensIKEA API.
    /// Creates a new HttpClient per request to avoid static init issues in UWP.
    /// </summary>
    public class EfobasenApiService
    {
        private const string ApiUrl = "https://efobasen.no/API/VisProdukt/HentProduktinfo";

        public async Task<string> FetchProductJsonAsync(int elnummer, CancellationToken cancellationToken)
        {
            using (var httpClient = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
                try
                {
                    var payload = JsonConvert.SerializeObject(new { Elnummer = elnummer });
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    request.Headers.Add("User-Agent", "HololensIKEA/1.0");

                    var response = await httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
                finally
                {
                    request?.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Parses HololensIKEA JSON and converts to RenderableProduct.
    /// Handles unit normalization, field name variations, and missing data.
    /// </summary>
    public class ProductParser
    {
        private const float MillimetersToMeters = 0.001f;
        private const float CentimetersToMeters = 0.01f;

        public RenderableProduct ParseToRenderableProduct(string json, int productId)
        {
            var product = new RenderableProduct { ProductId = productId };
            var root = JObject.Parse(json);

            var produktinfo = root["Skjema"]?["Produktinfo"];

            var varetekst = produktinfo?["Varetekst"]?.ToString();
            if (!string.IsNullOrEmpty(varetekst))
                product.ProductName = varetekst;

            // 3D model availability
            var har3d = produktinfo?["Har3DModell"];
            if (har3d != null && har3d.Type == JTokenType.Boolean)
                product.Has3DModel = har3d.Value<bool>();

            // Extract GTIN from nested Felter (field named "gtin.nummer")
            if (product.Has3DModel)
            {
                var gtin = ExtractGtin(root);
                if (!string.IsNullOrEmpty(gtin))
                    product.Gtin = gtin;
            }

            var dimensionMappings = new Dictionary<string, Action<RenderableProduct, float>>(StringComparer.OrdinalIgnoreCase)
            {
                // Product dimension fields (authoritative)
                ["bredde på produktet"]      = (p, v) => p.WidthMeters  = v,
                ["høyde på produktet"]       = (p, v) => p.HeightMeters = v,
                ["lengde på produktet"]      = (p, v) => p.DepthMeters  = v,
                ["bredde innendørs enhet"]   = (p, v) => p.WidthMeters  = v,
                ["høyde innendørs enhet"]    = (p, v) => p.HeightMeters = v,
                ["dybde innendørsenhet"]     = (p, v) => p.DepthMeters  = v,
                ["bredde utendørs enhet"]    = (p, v) => p.WidthMeters  = v,
                ["høyde utendørs enhet"]     = (p, v) => p.HeightMeters = v,
                ["dybde utendørs enhet"]     = (p, v) => p.DepthMeters  = v,
                // F-pak packaging fields — only use as fallback when product dims are missing
                ["f-pak bredde"]             = (p, v) => { if (p.WidthMeters  <= 0) p.WidthMeters  = v; },
                ["f-pak høyde"]              = (p, v) => { if (p.HeightMeters <= 0) p.HeightMeters = v; },
                ["f-pak lengde"]             = (p, v) => { if (p.DepthMeters  <= 0) p.DepthMeters  = v; },
            };

            var grupper = root["Skjema"]?["Skjema"]?["Grupper"] as JArray;
            if (grupper != null)
                foreach (var gruppe in grupper)
                    ParseGroupForDimensions(gruppe as JObject, dimensionMappings, product);

            var imageUrl = TryExtractImageUrl(root);
            if (!string.IsNullOrEmpty(imageUrl))
                product.ImageUrl = imageUrl;

            ApplyDefaultDimensions(product);
            return product;
        }

        private void ParseGroupForDimensions(
            JObject gruppe,
            Dictionary<string, Action<RenderableProduct, float>> mappings,
            RenderableProduct product)
        {
            if (gruppe == null) return;

            var felter = gruppe["Felter"] as JArray;
            if (felter != null)
                foreach (var felt in felter)
                    ParseFieldForDimension(felt as JObject, mappings, product);

            var subGrupper = gruppe["Grupper"] as JArray;
            if (subGrupper != null)
                foreach (var subGruppe in subGrupper)
                    ParseGroupForDimensions(subGruppe as JObject, mappings, product);
        }

        private void ParseFieldForDimension(
            JObject felt,
            Dictionary<string, Action<RenderableProduct, float>> mappings,
            RenderableProduct product)
        {
            if (felt == null) return;

            var navn      = felt["Navn"]?.ToString()?.Trim();
            var verdi     = felt["Verdi"];
            var maleenhet = felt["Maleenhet"]?.ToString() ?? "";
            var etimKode  = felt["ETIMKode"]?.ToString()?.ToUpperInvariant();

            if (verdi == null) return;

            Action<RenderableProduct, float> setter = null;

            // Name-based matching (F-pak and legacy field names)
            if (!string.IsNullOrEmpty(navn))
                mappings.TryGetValue(navn, out setter);

            // ETIMKode matching for standardised ETIM dimension fields.
            // Only overwrite if not already set by a name-based match (F-pak wins over ETIM).
            if (setter == null && !string.IsNullOrEmpty(etimKode))
            {
                switch (etimKode)
                {
                    case "EF000008": setter = (p, v) => { if (p.WidthMeters  <= 0) p.WidthMeters  = v; }; break; // Bredde
                    case "EF000040": setter = (p, v) => { if (p.HeightMeters <= 0) p.HeightMeters = v; }; break; // Høyde
                    case "EF000049": setter = (p, v) => { if (p.DepthMeters  <= 0) p.DepthMeters  = v; }; break; // Dybde
                }
            }

            if (setter == null) return;

            var rawValue = verdi.ToString();
            var parsed   = ParseDimension(rawValue);
            if (parsed.HasValue && parsed.Value > 0)
                setter(product, (float)NormalizeToMeters(parsed.Value, maleenhet));
        }

        private double? ParseDimension(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue)) return null;
            try
            {
                var match = Regex.Match(rawValue, @"[-+]?[0-9]*[.,]?[0-9]+");
                if (!match.Success) return null;
                var normalized = match.Value.Replace(",", ".");
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                    return result;
            }
            catch { }
            return null;
        }

        private double NormalizeToMeters(double value, string raw)
        {
            if (!string.IsNullOrEmpty(raw))
            {
                var lower = raw.ToLowerInvariant();
                if (lower.Contains("cm"))  return value * CentimetersToMeters;
                if (lower.Contains("mm"))  return value * MillimetersToMeters;
                if (lower.Contains("m") && !lower.Contains("mm") && !lower.Contains("cm"))
                    return value;
            }
            return value * MillimetersToMeters;
        }

        /// <summary>
        /// Extracts the product image URL from the HololensIKEA JSON response.
        /// The API stores images as integer file IDs in Skjema.Produktinfo.Bilder[].
        /// The image is served at: https://efobasen.no/API/Produktfiler/Skalert/bilde.jpg?id={fileId}&amp;w=1000&amp;h=1000&amp;m=5
        /// </summary>
        private string TryExtractImageUrl(JObject root)
        {
            const string ImageBaseUrl = "https://efobasen.no/API/Produktfiler/Skalert/bilde.jpg";

            // Path 1: Skjema.Produktinfo.Bilder — array of integer file IDs (primary API shape)
            var bilder = root["Skjema"]?["Produktinfo"]?["Bilder"] as JArray;
            if (bilder != null && bilder.Count > 0)
            {
                var first = bilder[0];
                // Integer file ID (most common) — construct the URL
                if (first?.Type == JTokenType.Integer)
                {
                    var fileId = first.Value<long>();
                    return $"{ImageBaseUrl}?id={fileId}&w=1000&h=1000&m=5";
                }
                // Object with Url / BildeUrl / Src sub-field
                if (first?.Type == JTokenType.Object)
                {
                    var url = first["Url"]?.ToString()
                           ?? first["BildeUrl"]?.ToString()
                           ?? first["Src"]?.ToString();
                    if (!string.IsNullOrEmpty(url)) return url;

                    // Object with integer Id sub-field
                    var idToken = first["Id"] ?? first["FilId"];
                    if (idToken?.Type == JTokenType.Integer)
                        return $"{ImageBaseUrl}?id={idToken.Value<long>()}&w=1000&h=1000&m=5";
                }
            }

            // Path 2: Scan schema groups for a field of Type=="Bilde" whose Verdi is an int array
            // (the "Bilde og media" group stores images this way)
            var schemaGrupper = root["Skjema"]?["Skjema"]?["Grupper"] as JArray;
            if (schemaGrupper != null)
            {
                var fileId = FindBildeFieldId(schemaGrupper);
                if (fileId.HasValue)
                    return $"{ImageBaseUrl}?id={fileId.Value}&w=1000&h=1000&m=5";
            }

            // Path 3: Skjema.Produktinfo direct URL fields (legacy products)
            var bildeurl = root["Skjema"]?["Produktinfo"]?["Bildeurl"]?.ToString()
                        ?? root["Skjema"]?["Produktinfo"]?["BildeUrl"]?.ToString()
                        ?? root["Skjema"]?["Produktinfo"]?["Bilde"]?.ToString();
            if (!string.IsNullOrEmpty(bildeurl) && bildeurl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return bildeurl;

            return null;
        }

        /// <summary>
        /// Recursively searches schema groups for the first field with Type=="Bilde"
        /// and returns the first integer file ID from its Verdi array.
        /// </summary>
        private long? FindBildeFieldId(JArray grupper)
        {
            if (grupper == null) return null;
            foreach (var gruppe in grupper)
            {
                var felter = gruppe["Felter"] as JArray;
                if (felter != null)
                {
                    foreach (var felt in felter)
                    {
                        if (felt?["Type"]?.ToString() == "Bilde")
                        {
                            var verdi = felt["Verdi"];
                            if (verdi?.Type == JTokenType.Integer)
                                return verdi.Value<long>();
                            if (verdi is JArray arr && arr.Count > 0 && arr[0].Type == JTokenType.Integer)
                                return arr[0].Value<long>();
                        }
                    }
                }
                var sub = gruppe["Grupper"] as JArray;
                var found = FindBildeFieldId(sub);
                if (found.HasValue) return found;
            }
            return null;
        }

        private void ApplyDefaultDimensions(RenderableProduct product)
        {
            const float defaultSize = 0.5f;
            if (product.WidthMeters  <= 0) product.WidthMeters  = defaultSize;
            if (product.HeightMeters <= 0) product.HeightMeters = defaultSize;
            if (product.DepthMeters  <= 0) product.DepthMeters  = defaultSize;
        }

        /// <summary>
        /// Extracts the GTIN code from nested schema fields.
        /// The GTIN is stored in a field with Navn == "gtin.nummer" inside the Grupper hierarchy.
        /// </summary>
        private string ExtractGtin(JObject root)
        {
            var grupper = root["Skjema"]?["Skjema"]?["Grupper"] as JArray;
            if (grupper == null) return null;
            return FindGtinInGroups(grupper);
        }

        private string FindGtinInGroups(JArray grupper)
        {
            if (grupper == null) return null;
            foreach (var gruppe in grupper)
            {
                var felter = gruppe["Felter"] as JArray;
                if (felter != null)
                {
                    foreach (var felt in felter)
                    {
                        var navn = felt?["Navn"]?.ToString();
                        if (string.Equals(navn, "gtin.nummer", StringComparison.OrdinalIgnoreCase))
                        {
                            var verdi = felt["Verdi"]?.ToString();
                            if (!string.IsNullOrEmpty(verdi))
                                return verdi;
                        }
                    }
                }
                var sub = gruppe["Grupper"] as JArray;
                var found = FindGtinInGroups(sub);
                if (found != null) return found;
            }
            return null;
        }
    }

    /// <summary>
    /// Orchestrates product loading with in-memory caching.
    /// </summary>
    public class ProductRepository
    {
        private readonly EfobasenApiService              _apiService = new EfobasenApiService();
        private readonly ProductParser                   _parser     = new ProductParser();
        private readonly Dictionary<int, RenderableProduct> _cache  = new Dictionary<int, RenderableProduct>();

        public async Task<RenderableProduct> GetProductAsync(int elnummer, CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(elnummer, out var cached))
                return cached;

            var json    = await _apiService.FetchProductJsonAsync(elnummer, cancellationToken);
            var product = _parser.ParseToRenderableProduct(json, elnummer);
            _cache[elnummer] = product;
            return product;
        }

        public void ClearCache() => _cache.Clear();
    }
}
