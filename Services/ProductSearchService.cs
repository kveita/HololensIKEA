using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HololensIKEA.Services
{
    /// <summary>
    /// Represents a single product search result from the HololensIKEA search API.
    /// </summary>
    public class ProductSearchResult
    {
        public int    Id          { get; set; }
        public string Produktnr   { get; set; }
        public string Varetekst   { get; set; }
        public string Firma       { get; set; }
        public string Varegruppe  { get; set; }
        public int    BildeId     { get; set; }
    }

    /// <summary>
    /// Searches the HololensIKEA product database by keyword.
    /// Uses the same API as efobasen.no/produkter search.
    /// </summary>
    public class ProductSearchService
    {
        private const string SearchApiUrl = "https://efobasen.no/API/AlleProdukter/HentProdukter";

        /// <summary>
        /// Searches for products matching the given query text.
        /// Returns up to <paramref name="maxResults"/> results.
        /// </summary>
        public async Task<List<ProductSearchResult>> SearchAsync(
            string query, int maxResults = 10, CancellationToken cancellationToken = default)
        {
            var results = new List<ProductSearchResult>();

            if (string.IsNullOrWhiteSpace(query))
                return results;

            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    var payload = new
                    {
                        Statusvalg = new[] { 1 },
                        Page = 1,
                        Pagesize = maxResults,
                        Visningsmodus = 2,
                        ProduktnummerListe = new int[0],
                        EtimEgenskaper = new object[0],
                        Dokumentasjon = new object[0],
                        Feltvalg = new object[0],
                        KunGrossiststempel = (bool?)null,
                        FilIder = new int[0],
                        UtilgjengeligeProdukter = new object[0],
                        KISok = (string)null,
                        Search = query,
                        Sortering = 0,
                        ProduktnummerFra = (string)null,
                        ProduktnummerTil = (string)null,
                        Produktnummertallstreng = (string)null,
                        ETIMKlasse = (string)null,
                        Leverandor = (string)null,
                        LovpaalagtDokumentasjon = (string)null,
                        FiltrerFeltmangel = (string)null,
                        Bilder = (string)null,
                        KlasseId = (string)null,
                        AvansertSok = (string)null,
                        Type = (string)null,
                        VisKunFeil = (bool?)null,
                        SCIPSok = (string)null,
                        HarPDT = (bool?)null,
                        ValgtPDT = (string)null,
                        HarISO22057 = (bool?)null,
                        Har3DModell = (bool?)null,
                        KunFeilfrieProdukter = (bool?)null,
                        KunPerfekteAktorer = (bool?)null,
                    };

                    var json = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var request = new HttpRequestMessage(HttpMethod.Post, SearchApiUrl)
                    {
                        Content = content
                    };
                    request.Headers.Add("User-Agent", "HololensIKEA/1.0");
                    request.Headers.Add("Accept", "application/json");

                    var response = await httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var root = JObject.Parse(responseBody);

                    var produkter = root["Produkter"] as JArray;
                    if (produkter != null)
                    {
                        foreach (var p in produkter)
                        {
                            if (results.Count >= maxResults)
                                break;

                            results.Add(new ProductSearchResult
                            {
                                Id         = p["Id"]?.Value<int>() ?? 0,
                                Produktnr  = p["Produktnr"]?.ToString() ?? "",
                                Varetekst  = p["Varetekst"]?.ToString() ?? "",
                                Firma      = p["Firma"]?.ToString() ?? "",
                                Varegruppe = p["Varegruppe"]?.ToString() ?? "",
                                BildeId    = p["Bilde"]?.Value<int>() ?? 0,
                            });
                        }
                    }

                    Debug.WriteLine($"[Search] Query '{query}' returned {results.Count} results (total: {root["Total"]})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Search] Error: " + ex.Message);
            }

            return results;
        }
    }
}
