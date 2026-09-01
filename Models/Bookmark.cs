namespace HololensIKEA.Models
{
    /// <summary>
    /// Represents a bookmarked IKEA product with a 3D model.
    /// Loaded from bookmarks.json.
    /// </summary>
    public class Bookmark
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";

        /// <summary>
        /// Optional direct .glb model URL. IKEA renders the "View in 3D"
        /// model-viewer element client-side (via JavaScript), so the model
        /// URL cannot be scraped from the raw product page HTML fetched by
        /// this app. Populate this by running the model-viewer's src through
        /// a tool such as https://github.com/apinanaivot/IKEA-3D-Model-Download-Button
        /// in a desktop browser and copying the resulting .glb URL here.
        /// When set, this is used directly instead of the (unreliable)
        /// HTML-scraping fallback in ModelService3D.
        /// </summary>
        public string GlbUrl { get; set; } = "";

        /// <summary>Original IKEA GLB URL used by the bookmark-refresh workflow.</summary>
        public string SourceGlbUrl { get; set; } = "";
    }
}