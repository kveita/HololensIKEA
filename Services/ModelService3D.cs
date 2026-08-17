using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HololensIKEA.Services
{
    public class GltfMeshData
    {
        public Vector3[] Positions { get; set; }
        public Vector3[] Normals { get; set; }
        public ushort[] Indices { get; set; }
        public Vector4 BaseColor { get; set; } = new Vector4(0.72f, 0.72f, 0.72f, 1f);
        public Vector3 BoundsMeters { get; set; }
        public Vector3 CenterOffset { get; set; }
    }

    /// <summary>
    /// Resolves an IKEA product page to the GLB URL exposed by IKEA's
    /// model-viewer/gltf-model markup, following the discovery strategy of
    /// apinanaivot/IKEA-3D-Model-Download-Button, then parses the GLB.
    /// </summary>
    public sealed class ModelService3D
    {
        private static readonly Regex ModelAttribute = new Regex(
            @"(?:src|gltf-model)\s*=\s*[\"']([^\"']*(?:\.glb|glb_draco)[^\"']*)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AbsoluteModelUrl = new Regex(
            @"https?://[^\"'<>\s]+(?:\.glb|glb_draco)[^\"'<>\s]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<GltfMeshData> FetchModelAsync(string productPageUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(productPageUrl)) return null;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) })
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 HoloLensIKEA/1.0");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                    var pageUri = new Uri(productPageUrl, UriKind.Absolute);
                    var html = await client.GetStringAsync(pageUri);
                    var modelUrl = FindModelUrl(html, pageUri);
                    if (modelUrl == null)
                    {
                        Debug.WriteLine("[IKEA] No GLB URL found in product page HTML.");
                        return null;
                    }
                    Debug.WriteLine("[IKEA] Downloading GLB: " + modelUrl);
                    var bytes = await client.GetByteArrayAsync(modelUrl);
                    return ParseGlb(bytes);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[IKEA] Model fetch failed: " + ex.Message);
                return null;
            }
        }

        public static Uri FindModelUrl(string html, Uri pageUri)
        {
            if (string.IsNullOrEmpty(html)) return null;
            html = html.Replace("&amp;", "&").Replace("\\/", "/").Replace("\\u0026", "&");
            string raw = null;
            var match = ModelAttribute.Match(html);
            if (match.Success) raw = match.Groups[1].Value;
            if (string.IsNullOrEmpty(raw))
            {
                match = AbsoluteModelUrl.Match(html);
                if (match.Success) raw = match.Value;
            }
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Replace("&quot;", "\"").Replace("&#x2F;", "/");
            if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute)) return absolute;
            if (Uri.TryCreate(pageUri, raw, out var relative)) return relative;
            return null;
        }

        private static GltfMeshData ParseGlb(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20) return null;
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2) return null;
                reader.ReadUInt32();
                JObject gltf = null;
                byte[] bin = null;
                while (stream.Position + 8 <= stream.Length)
                {
                    var length = reader.ReadUInt32();
                    var type = reader.ReadUInt32();
                    if (length > stream.Length - stream.Position) return null;
                    var chunk = reader.ReadBytes((int)length);
                    if (type == 0x4E4F534A) gltf = JObject.Parse(Encoding.UTF8.GetString(chunk).TrimEnd('\0', ' ', '\n', '\r'));
                    else if (type == 0x004E4942) bin = chunk;
                }
                return gltf == null || bin == null ? null : ParseGltf(gltf, bin);
            }
        }

        private static GltfMeshData ParseGltf(JObject root, byte[] bin)
        {
            var accessors = root["accessors"] as JArray;
            var views = root["bufferViews"] as JArray;
            var meshes = root["meshes"] as JArray;
            if (accessors == null || views == null || meshes == null) return null;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<ushort>();
            foreach (var mesh in meshes)
            {
                foreach (var primitive in (mesh["primitives"] as JArray) ?? new JArray())
                {
                    var attrs = primitive["attributes"];
                    var posIndex = attrs?["POSITION"]?.Value<int>();
                    if (!posIndex.HasValue) continue;
                    var localPositions = ReadVec3(posIndex.Value, accessors, views, bin);
                    if (localPositions == null || localPositions.Length == 0) continue;
                    var normIndex = attrs?["NORMAL"]?.Value<int>();
                    var localNormals = normIndex.HasValue ? ReadVec3(normIndex.Value, accessors, views, bin) : null;
                    if (localNormals == null || localNormals.Length != localPositions.Length)
                    {
                        localNormals = new Vector3[localPositions.Length];
                        for (var i = 0; i < localNormals.Length; i++) localNormals[i] = Vector3.UnitY;
                    }
                    var indexIndex = primitive["indices"]?.Value<int>();
                    var localIndices = indexIndex.HasValue ? ReadIndices(indexIndex.Value, accessors, views, bin) : null;
                    if (localIndices == null)
                    {
                        localIndices = new uint[localPositions.Length];
                        for (var i = 0; i < localIndices.Length; i++) localIndices[i] = (uint)i;
                    }
                    var baseVertex = positions.Count;
                    positions.AddRange(localPositions);
                    normals.AddRange(localNormals);
                    foreach (var index in localIndices)
                    {
                        var absolute = (ulong)baseVertex + index;
                        if (absolute <= ushort.MaxValue) indices.Add((ushort)absolute);
                    }
                }
            }
            if (positions.Count == 0 || indices.Count < 3) return null;
            var min = positions[0];
            var max = positions[0];
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var center = (min + max) * 0.5f;
            var range = max - min;
            var maxRange = Math.Max(range.X, Math.Max(range.Y, range.Z));
            var scale = maxRange > 20f ? 0.001f : 1f;
            for (var i = 0; i < positions.Count; i++) positions[i] = (positions[i] - center) * scale;
            return new GltfMeshData { Positions = positions.ToArray(), Normals = normals.ToArray(), Indices = indices.ToArray(), BoundsMeters = range * scale, CenterOffset = center * scale };
        }

        private static Vector3[] ReadVec3(int accessorIndex, JArray accessors, JArray views, byte[] bin)
        {
            var accessor = accessors[accessorIndex];
            var count = accessor["count"]?.Value<int>() ?? 0;
            var viewIndex = accessor["bufferView"]?.Value<int>() ?? -1;
            if (viewIndex < 0 || viewIndex >= views.Count) return null;
            var view = views[viewIndex];
            var start = (view["byteOffset"]?.Value<int>() ?? 0) + (accessor["byteOffset"]?.Value<int>() ?? 0);
            var stride = view["byteStride"]?.Value<int>() ?? 12;
            var result = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var offset = start + i * stride;
                if (offset + 12 > bin.Length) return null;
                result[i] = new Vector3(BitConverter.ToSingle(bin, offset), BitConverter.ToSingle(bin, offset + 4), BitConverter.ToSingle(bin, offset + 8));
            }
            return result;
        }

        private static uint[] ReadIndices(int accessorIndex, JArray accessors, JArray views, byte[] bin)
        {
            var accessor = accessors[accessorIndex];
            var count = accessor["count"]?.Value<int>() ?? 0;
            var viewIndex = accessor["bufferView"]?.Value<int>() ?? -1;
            if (viewIndex < 0 || viewIndex >= views.Count) return null;
            var view = views[viewIndex];
            var componentType = accessor["componentType"]?.Value<int>() ?? 5123;
            var start = (view["byteOffset"]?.Value<int>() ?? 0) + (accessor["byteOffset"]?.Value<int>() ?? 0);
            var elementSize = componentType == 5125 ? 4 : componentType == 5121 ? 1 : 2;
            var stride = view["byteStride"]?.Value<int>() ?? elementSize;
            var result = new uint[count];
            for (var i = 0; i < count; i++)
            {
                var offset = start + i * stride;
                if (offset + elementSize > bin.Length) return null;
                result[i] = componentType == 5125 ? BitConverter.ToUInt32(bin, offset) : componentType == 5121 ? bin[offset] : BitConverter.ToUInt16(bin, offset);
            }
            return result;
        }
    }
}
