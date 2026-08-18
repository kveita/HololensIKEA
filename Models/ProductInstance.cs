using System;
using System.Numerics;
using HololensIKEA.Models;
using HololensIKEA.Services;

namespace HololensIKEA.Models
{
    /// <summary>
    /// Represents a single product instance in the scene with its transform state.
    /// Used for multi-product support where each product can be positioned independently.
    /// </summary>
    public class ProductInstance
    {
        /// <summary>Unique identifier for this instance.</summary>
        public Guid InstanceId { get; } = Guid.NewGuid();

        /// <summary>The underlying product data.</summary>
        public RenderableProduct Product { get; set; }

        /// <summary>World-space position of the product center.</summary>
        public Vector3 Position { get; set; } = new Vector3(0f, 0f, -2f);

        /// <summary>World-space rotation.</summary>
        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        /// <summary>Dimensions in meters (cached from Product for quick access, or set directly).</summary>
        public Vector3 Dimensions
        {
            get => _dimensions ?? (Product != null
                ? new Vector3(Product.WidthMeters, Product.HeightMeters, Product.DepthMeters)
                : new Vector3(0.1f, 0.1f, 0.1f));
            set => _dimensions = value;
        }
        private Vector3? _dimensions;

        /// <summary>Half-extents for collision/hit testing.</summary>
        public Vector3 HalfExtents => Dimensions * 0.5f;

        /// <summary>Whether this product is currently selected (being manipulated).</summary>
        public bool IsSelected { get; set; } = false;

        /// <summary>Whether image/textures have been loaded.</summary>
        public bool TexturesLoaded { get; set; } = false;

        /// <summary>The main texture SRV (front face image).</summary>
        public SharpDX.Direct3D11.ShaderResourceView TextureSRV { get; set; }

        /// <summary>The displacement map SRV (for pseudo-3D effect).</summary>
        public SharpDX.Direct3D11.ShaderResourceView DisplacementSRV { get; set; }

        /// <summary>The side face texture SRV (for 3/4 view products).</summary>
        public SharpDX.Direct3D11.ShaderResourceView SideFaceSRV { get; set; }

        /// <summary>View type classification (FrontOnly, ThreeQuarterLeft, etc.).</summary>
        public ViewType ViewType { get; set; } = ViewType.FrontOnly;

        /// <summary>Parsed 3D mesh data from an IKEA product-page GLB (null if unavailable).</summary>
        public GltfMeshData MeshData { get; set; }

        /// <summary>Independent position for the 3D mesh model.</summary>
        public Vector3 MeshPosition { get; set; } = new Vector3(0f, 0f, -2f);

        /// <summary>Independent rotation for the 3D mesh model.</summary>
        public Quaternion MeshRotation { get; set; } = Quaternion.Identity;

        /// <summary>True if this product should render as a 3D mesh instead of a box+sprite.</summary>
        public bool Has3DModel => MeshData != null;

        /// <summary>Content bounds (minU, minV, maxU, maxV) for the product image.</summary>
        public ContentBounds Bounds { get; set; } = new ContentBounds();

        /// <summary>Content bounds as Vector4 for direct sprite renderer use.</summary>
        public Vector4 ContentBoundsVec { get; set; } = new Vector4(0f, 0f, 1f, 1f);

        /// <summary>Timestamp when this instance was created.</summary>
        public DateTime CreatedAt { get; } = DateTime.Now;

        /// <summary>
        /// Creates a new empty product instance (for object initializer usage).
        /// </summary>
        public ProductInstance() { }

        /// <summary>
        /// Creates a new product instance at the specified position.
        /// </summary>
        public ProductInstance(RenderableProduct product, Vector3 position)
        {
            Product = product;
            Position = position;
        }

        /// <summary>
        /// Tests if a ray intersects this product's bounding box.
        /// </summary>
        /// <param name="rayOrigin">Ray start point in world space.</param>
        /// <param name="rayDir">Normalized ray direction.</param>
        /// <param name="maxDist">Maximum ray distance.</param>
        /// <param name="hitDist">Output: distance to hit point if intersection found.</param>
        /// <returns>True if the ray hits this product.</returns>
        public bool RayIntersects(Vector3 rayOrigin, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = 0f;
            var halfExt = HalfExtents;

            // Slab-method ray vs AABB test (axis-aligned in world space for simplicity)
            // For rotated boxes, we transform the ray into local space first
            var invRot = Quaternion.Inverse(Rotation);
            var localOrigin = Vector3.Transform(rayOrigin - Position, invRot);
            var localDir = Vector3.Transform(rayDir, invRot);

            float tMin = 0f;
            float tMax = maxDist;

            // X axis
            if (Math.Abs(localDir.X) < 1e-8f)
            {
                if (localOrigin.X < -halfExt.X || localOrigin.X > halfExt.X)
                    return false;
            }
            else
            {
                float invD = 1f / localDir.X;
                float t0 = (-halfExt.X - localOrigin.X) * invD;
                float t1 = (halfExt.X - localOrigin.X) * invD;
                if (t0 > t1) { var tmp = t0; t0 = t1; t1 = tmp; }
                tMin = Math.Max(tMin, t0);
                tMax = Math.Min(tMax, t1);
                if (tMin > tMax) return false;
            }

            // Y axis
            if (Math.Abs(localDir.Y) < 1e-8f)
            {
                if (localOrigin.Y < -halfExt.Y || localOrigin.Y > halfExt.Y)
                    return false;
            }
            else
            {
                float invD = 1f / localDir.Y;
                float t0 = (-halfExt.Y - localOrigin.Y) * invD;
                float t1 = (halfExt.Y - localOrigin.Y) * invD;
                if (t0 > t1) { var tmp = t0; t0 = t1; t1 = tmp; }
                tMin = Math.Max(tMin, t0);
                tMax = Math.Min(tMax, t1);
                if (tMin > tMax) return false;
            }

            // Z axis
            if (Math.Abs(localDir.Z) < 1e-8f)
            {
                if (localOrigin.Z < -halfExt.Z || localOrigin.Z > halfExt.Z)
                    return false;
            }
            else
            {
                float invD = 1f / localDir.Z;
                float t0 = (-halfExt.Z - localOrigin.Z) * invD;
                float t1 = (halfExt.Z - localOrigin.Z) * invD;
                if (t0 > t1) { var tmp = t0; t0 = t1; t1 = tmp; }
                tMin = Math.Max(tMin, t0);
                tMax = Math.Min(tMax, t1);
                if (tMin > tMax) return false;
            }

            hitDist = tMin >= 0 ? tMin : tMax;
            return hitDist >= 0 && hitDist <= maxDist;
        }

        /// <summary>
        /// Disposes texture resources.
        /// </summary>
        public void DisposeTextures()
        {
            TextureSRV?.Dispose();
            TextureSRV = null;
            DisplacementSRV?.Dispose();
            DisplacementSRV = null;
            SideFaceSRV?.Dispose();
            SideFaceSRV = null;
            TexturesLoaded = false;
        }
    }
}
