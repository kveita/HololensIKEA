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

        /// <summary>True if the product has a 3D model available on 3dfindit.com.</summary>
        public bool   Has3DModel { get; set; }
        /// <summary>GTIN (EAN) code used to search for the 3D model on 3dfindit.com.</summary>
        public string Gtin       { get; set; }
    }
}
