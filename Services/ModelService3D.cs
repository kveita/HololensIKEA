using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HololensIKEA.Services
{
    /// <summary>
    /// Parsed glTF mesh data ready for D3D11 rendering.
    /// </summary>
    public class GltfMeshData
    {
        /// <summary>Vertex positions in model space (converted to meters).</summary>
        public Vector3[] Positions { get; set; }
        /// <summary>Vertex normals.</summary>
        public Vector3[] Normals { get; set; }
        /// <summary>Triangle indices (3 per triangle).</summary>
        public ushort[] Indices { get; set; }
        /// <summary>Base color (RGBA) for the mesh.</summary>
        public Vector4 BaseColor { get; set; } = new Vector4(0.65f, 0.65f, 0.65f, 1f);
        /// <summary>Bounding box dimensions in meters (width, height, depth).</summary>
        public Vector3 BoundsMeters { get; set; }
        /// <summary>Center offset to apply (so model is centered at origin).</summary>
        public Vector3 CenterOffset { get; set; }
    }

    /// <summary>
    /// Fetches 3D model data from 3dfindit.com via GTIN search and parses embedded glTF.
    /// </summary>
    public class ModelService3D
    {
        private const string SearchUrl = "https://webapi.partcommunity.com/service/fulltextsearch";
        private const string PreviewUrl = "https://webapi.partcommunity.com/service/preview3d";
        private const float MmToMeters = 0.001f;

        /// <summary>
        /// Fetches and parses the 3D model for a product by its GTIN code.
        /// Returns null if the model cannot be fetched or parsed.
        /// </summary>
        public async Task<GltfMeshData> FetchModelAsync(string gtin, CancellationToken ct)
        {
            try
            {
                var json = await FetchPreview3dAsync(gtin, ct);
                if (string.IsNullOrEmpty(json))
                    return null;

                return ParseGltfResponse(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[3DFindit] Fetch failed: " + ex.Message);
                return null;
            }
        }

        private async Task<string> FetchPreview3dAsync(string gtin, CancellationToken ct)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);

                // Step 1: Search for the product path via fulltextsearch using GTIN
                var searchUrl = $"{SearchUrl}?query={Uri.EscapeDataString(gtin)}&language=english";
                var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchRequest.Headers.Add("Origin", "https://www.3dfindit.com");
                searchRequest.Headers.Add("Referer", "https://www.3dfindit.com/");

                var searchResponse = await client.SendAsync(searchRequest, ct);
                if (!searchResponse.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[3DFindit] Search HTTP {(int)searchResponse.StatusCode} for GTIN {gtin}");
                    return null;
                }

                var searchJson = await searchResponse.Content.ReadAsStringAsync();

                // Extract product path and part number from search result
                var pathMatch = Regex.Match(searchJson, "\"path\":\"([^\"]+\\.prj)\"");
                var nnMatch = Regex.Match(searchJson, "\"nn_name\":\"([^\"]+)\"");
                if (!pathMatch.Success)
                {
                    Debug.WriteLine($"[3DFindit] No product found for GTIN {gtin}");
                    return null;
                }

                var path = pathMatch.Groups[1].Value;
                var partNumber = nnMatch.Success ? nnMatch.Groups[1].Value : path.Split('/')[path.Split('/').Length - 1].Replace(".prj", "");

                Debug.WriteLine($"[3DFindit] Found path: {path}, part: {partNumber}");

                // Step 2: Build mident from the resolved path and fetch 3D preview
                var mident = Uri.EscapeDataString(
                    $"{{{path}}},013 {{LINEID=10}}  {{NB={partNumber}}},{{MANUID={partNumber}}}");

                var body = $"format=PARTJAVA3D&language=english&options=&path=&varsettransfer=" +
                           $"&mident={mident}" +
                           $"&preferformat=ASM_GLTF" +
                           $"&server_type=OEM_WEBSERVICE_webcomponentsdemo";

                var request = new HttpRequestMessage(HttpMethod.Post, PreviewUrl);
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
                request.Headers.Add("Origin", "https://www.3dfindit.com");
                request.Headers.Add("Referer", "https://www.3dfindit.com/");

                var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[3DFindit] Preview HTTP {(int)response.StatusCode} for {path}");
                    return null;
                }
                return await response.Content.ReadAsStringAsync();
            }
        }

        private GltfMeshData ParseGltfResponse(string json)
        {
            var root = JObject.Parse(json);
            var assembly = root["response"]?["assembly"];
            if (assembly == null) return null;

            // Get dimensions from globalTopoAttributes (in mm)
            var info = assembly["info"]?["globalTopoAttributes"];
            float xDim = 0, yDim = 0, zDim = 0;
            if (info != null)
            {
                xDim = info["xDim"]?.Value<float>() ?? 0;
                yDim = info["yDim"]?.Value<float>() ?? 0;
                zDim = info["zDim"]?.Value<float>() ?? 0;
            }

            // Find the main (non-cosmetic) part
            var parts = assembly["parts"] as JObject;
            if (parts == null) return null;

            JToken mainPart = null;
            Vector4 baseColor = new Vector4(0.65f, 0.65f, 0.65f, 1f);

            foreach (var kvp in parts)
            {
                var partObj = kvp.Value as JObject;
                if (partObj == null) continue;
                // Skip cosmetic parts (annotations, dimensions)
                var isCosm = partObj["isCosmetic"]?.Value<bool>() ?? false;
                if (isCosm) continue;

                mainPart = partObj;

                // Extract base color if available
                var bc = partObj["baseColor"] as JArray;
                if (bc != null && bc.Count >= 4)
                    baseColor = new Vector4(bc[0].Value<float>(), bc[1].Value<float>(), bc[2].Value<float>(), bc[3].Value<float>());
                break;
            }

            if (mainPart == null) return null;

            var content = mainPart["content"];
            if (content == null) return null;

            // Parse glTF embedded data
            return ParseGltfContent(content, baseColor, xDim, yDim, zDim);
        }

        private GltfMeshData ParseGltfContent(JToken content, Vector4 baseColor, float xDimMm, float yDimMm, float zDimMm)
        {
            var accessors = content["accessors"] as JArray;
            var bufferViews = content["bufferViews"] as JArray;
            var buffers = content["buffers"] as JArray;
            var meshes = content["meshes"] as JArray;

            if (accessors == null || bufferViews == null || buffers == null || meshes == null)
                return null;

            // Decode all buffer data (base64 data URIs)
            var bufferData = new List<byte[]>();
            foreach (var buf in buffers)
            {
                var uri = buf["uri"]?.ToString();
                if (uri != null && uri.StartsWith("data:"))
                {
                    var base64Start = uri.IndexOf(",", StringComparison.Ordinal);
                    if (base64Start >= 0)
                    {
                        var b64 = uri.Substring(base64Start + 1);
                        bufferData.Add(Convert.FromBase64String(b64));
                    }
                    else
                        bufferData.Add(new byte[0]);
                }
                else
                    bufferData.Add(new byte[0]);
            }

            // Combine all mesh primitives into a single vertex/index buffer
            var allPositions = new List<Vector3>();
            var allNormals = new List<Vector3>();
            var allIndices = new List<ushort>();

            foreach (var mesh in meshes)
            {
                var primitives = mesh["primitives"] as JArray;
                if (primitives == null) continue;

                foreach (var prim in primitives)
                {
                    var attrs = prim["attributes"];
                    if (attrs == null) continue;

                    var posIdx = attrs["POSITION"]?.Value<int>();
                    var normIdx = attrs["NORMAL"]?.Value<int>();
                    var indicesIdx = prim["indices"]?.Value<int>();

                    if (!posIdx.HasValue) continue;

                    var positions = ReadVec3Accessor(posIdx.Value, accessors, bufferViews, bufferData);
                    if (positions == null || positions.Length == 0) continue;

                    Vector3[] normals = null;
                    if (normIdx.HasValue)
                        normals = ReadVec3Accessor(normIdx.Value, accessors, bufferViews, bufferData);

                    // Default normals if missing
                    if (normals == null || normals.Length != positions.Length)
                    {
                        normals = new Vector3[positions.Length];
                        for (int i = 0; i < normals.Length; i++)
                            normals[i] = new Vector3(0, 1, 0);
                    }

                    ushort[] indices = null;
                    if (indicesIdx.HasValue)
                        indices = ReadUshortAccessor(indicesIdx.Value, accessors, bufferViews, bufferData);

                    // Generate indices if not present
                    if (indices == null)
                    {
                        indices = new ushort[positions.Length];
                        for (int i = 0; i < indices.Length; i++)
                            indices[i] = (ushort)i;
                    }

                    // Offset indices for the combined buffer
                    ushort baseVertex = (ushort)allPositions.Count;
                    foreach (var idx in indices)
                        allIndices.Add((ushort)(idx + baseVertex));

                    allPositions.AddRange(positions);
                    allNormals.AddRange(normals);
                }
            }

            if (allPositions.Count == 0)
                return null;

            // Convert positions to meters.
            // The 3dfindit API returns normalized positions in [-1, 1] range.
            // We scale by the globalTopoAttributes dimensions to get real-world size.
            var posArray = allPositions.ToArray();
            Vector3 min = posArray[0], max = posArray[0];
            for (int i = 1; i < posArray.Length; i++)
            {
                min = Vector3.Min(min, posArray[i]);
                max = Vector3.Max(max, posArray[i]);
            }
            Vector3 center = (min + max) * 0.5f;
            Vector3 range = max - min;

            if (xDimMm > 0 && yDimMm > 0 && zDimMm > 0)
            {
                // Scale normalized positions by actual dimensions
                Vector3 dimsMeters = new Vector3(xDimMm, yDimMm, zDimMm) * MmToMeters;
                Vector3 scaleFactor = new Vector3(
                    range.X > 0 ? dimsMeters.X / range.X : MmToMeters,
                    range.Y > 0 ? dimsMeters.Y / range.Y : MmToMeters,
                    range.Z > 0 ? dimsMeters.Z / range.Z : MmToMeters);
                for (int i = 0; i < posArray.Length; i++)
                    posArray[i] = (posArray[i] - center) * scaleFactor;
            }
            else
            {
                // Fallback: assume positions are in mm
                for (int i = 0; i < posArray.Length; i++)
                    posArray[i] = (posArray[i] - center) * MmToMeters;
            }

            Vector3 boundsMeters = (xDimMm > 0 && yDimMm > 0 && zDimMm > 0)
                ? new Vector3(xDimMm * MmToMeters, yDimMm * MmToMeters, zDimMm * MmToMeters)
                : (max - min) * MmToMeters;

            return new GltfMeshData
            {
                Positions = posArray,
                Normals = allNormals.ToArray(),
                Indices = allIndices.ToArray(),
                BaseColor = baseColor,
                BoundsMeters = boundsMeters,
                CenterOffset = center * MmToMeters
            };
        }

        private Vector3[] ReadVec3Accessor(int accessorIdx, JArray accessors, JArray bufferViews, List<byte[]> bufferData)
        {
            if (accessorIdx < 0 || accessorIdx >= accessors.Count) return null;
            var acc = accessors[accessorIdx];
            var count = acc["count"]?.Value<int>() ?? 0;
            var bvIdx = acc["bufferView"]?.Value<int>() ?? 0;
            var byteOffset = acc["byteOffset"]?.Value<int>() ?? 0;

            if (bvIdx < 0 || bvIdx >= bufferViews.Count) return null;
            var bv = bufferViews[bvIdx];
            var bufIdx = bv["buffer"]?.Value<int>() ?? 0;
            var bvOffset = bv["byteOffset"]?.Value<int>() ?? 0;
            var bvStride = bv["byteStride"]?.Value<int>() ?? 12; // vec3 = 12 bytes minimum

            if (bufIdx < 0 || bufIdx >= bufferData.Count) return null;
            var data = bufferData[bufIdx];

            var result = new Vector3[count];
            int offset = bvOffset + byteOffset;
            for (int i = 0; i < count; i++)
            {
                if (offset + 12 > data.Length) break;
                float x = BitConverter.ToSingle(data, offset);
                float y = BitConverter.ToSingle(data, offset + 4);
                float z = BitConverter.ToSingle(data, offset + 8);
                result[i] = new Vector3(x, y, z);
                offset += bvStride;
            }
            return result;
        }

        private ushort[] ReadUshortAccessor(int accessorIdx, JArray accessors, JArray bufferViews, List<byte[]> bufferData)
        {
            if (accessorIdx < 0 || accessorIdx >= accessors.Count) return null;
            var acc = accessors[accessorIdx];
            var count = acc["count"]?.Value<int>() ?? 0;
            var bvIdx = acc["bufferView"]?.Value<int>() ?? 0;
            var byteOffset = acc["byteOffset"]?.Value<int>() ?? 0;
            var componentType = acc["componentType"]?.Value<int>() ?? 5123; // UNSIGNED_SHORT

            if (bvIdx < 0 || bvIdx >= bufferViews.Count) return null;
            var bv = bufferViews[bvIdx];
            var bufIdx = bv["buffer"]?.Value<int>() ?? 0;
            var bvOffset = bv["byteOffset"]?.Value<int>() ?? 0;

            if (bufIdx < 0 || bufIdx >= bufferData.Count) return null;
            var data = bufferData[bufIdx];

            var result = new ushort[count];
            int offset = bvOffset + byteOffset;

            if (componentType == 5123) // UNSIGNED_SHORT
            {
                for (int i = 0; i < count; i++)
                {
                    if (offset + 2 > data.Length) break;
                    result[i] = BitConverter.ToUInt16(data, offset);
                    offset += 2;
                }
            }
            else if (componentType == 5125) // UNSIGNED_INT
            {
                for (int i = 0; i < count; i++)
                {
                    if (offset + 4 > data.Length) break;
                    result[i] = (ushort)BitConverter.ToUInt32(data, offset);
                    offset += 4;
                }
            }
            return result;
        }
    }
}
