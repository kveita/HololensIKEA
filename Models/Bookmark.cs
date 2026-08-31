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
    }
}