namespace HololensIKEA.Models
{
    public class RenderableProduct
    {
        public int    ProductId   { get; set; }
        public string ProductName { get; set; } = "Unknown Product";
        public float  WidthMeters  { get; set; }
        public float  HeightMeters { get; set; }
        public float  DepthMeters  { get; set; }
        /// <summary>URL of the product image (JPEG/PNG). May be null if unavailable.</summary>
        public string ImageUrl    { get; set; }

        /// <summary>True if the product page exposes an IKEA 3D model.</summary>
        public bool   Has3DModel { get; set; }
        /// <summary>Original IKEA product page used to resolve the GLB model URL.</summary>
        public string ModelUrl   { get; set; }
        /// <summary>Legacy identifier retained for compatibility with shared renderers.</summary>
        public string Gtin       { get; set; }
    }
}
