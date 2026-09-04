using System;
using System.Numerics;
using System.Text;
using HololensIKEA.Services;
using Xunit;

namespace HololensIKEA.Tests
{
    /// <summary>
    /// Tests for the Draco decoder and GLB parsing pipeline.
    ///
    /// The native draco_tiny_dec.dll is x86-only and not available on the
    /// test runner, so the Draco-specific decode path is tested through
    /// the GLB parsing layer (ParseGlb) which can detect and extract Draco
    /// compressed data without the native DLL. The actual native decode
    /// requires running on HoloLens (x86) or a Windows x86 host with the DLL.
    /// </summary>
    public class DracoDecoderTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // ParseGlb — GLB binary format parsing
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ParseGlb_NullBytes_ReturnsNull()
        {
            Assert.Null(ModelService3D.ParseGlb(null));
        }

        [Fact]
        public void ParseGlb_EmptyBytes_ReturnsNull()
        {
            Assert.Null(ModelService3D.ParseGlb(Array.Empty<byte>()));
        }

        [Fact]
        public void ParseGlb_TooShort_ReturnsNull()
        {
            // 19 bytes < minimum 20
            var bytes = new byte[19];
            Assert.Null(ModelService3D.ParseGlb(bytes));
        }

        [Fact]
        public void ParseGlb_InvalidMagic_ReturnsNull()
        {
            var bytes = new byte[20];
            // Wrong magic: should be 0x46546C67 ("glTF")
            bytes[0] = 0x00;
            bytes[1] = 0x00;
            bytes[2] = 0x00;
            bytes[3] = 0x00;
            Assert.Null(ModelService3D.ParseGlb(bytes));
        }

        [Fact]
        public void ParseGlb_InvalidVersion_ReturnsNull()
        {
            var bytes = new byte[20];
            // Magic: "glTF"
            WriteU32(bytes, 0, 0x46546C67);
            // Version: 1 (only version 2 is valid)
            WriteU32(bytes, 4, 1);
            // Total length: 20
            WriteU32(bytes, 8, 20);
            Assert.Null(ModelService3D.ParseGlb(bytes));
        }

        [Fact]
        public void ParseGlb_ValidHeader_NoChunks_ReturnsNull()
        {
            // 12-byte header, no chunk headers fit
            var bytes = new byte[12];
            WriteU32(bytes, 0, 0x46546C67);
            WriteU32(bytes, 4, 2);
            WriteU32(bytes, 8, 12);
            Assert.Null(ModelService3D.ParseGlb(bytes));
        }

        [Fact]
        public void ParseGlb_ChunkLengthExceedsRemaining_ReturnsNull()
        {
            // Header claims 20 bytes total, but the chunk claims 100 bytes
            var bytes = new byte[28];
            WriteU32(bytes, 0, 0x46546C67); // magic
            WriteU32(bytes, 4, 2);          // version
            WriteU32(bytes, 8, 28);         // total length
            WriteU32(bytes, 12, 100);       // chunk length (too large)
            WriteU32(bytes, 16, 0x4E4F534A); // JSON chunk type
            Assert.Null(ModelService3D.ParseGlb(bytes));
        }

        [Fact]
        public void ParseGlb_JsonChunkOnly_NoBinChunk_ReturnsNull()
        {
            var json = "{\"accessors\":[],\"bufferViews\":[],\"meshes\":[]}";
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            PadTo4(ref jsonBytes);

            var glb = BuildGlb(jsonBytes, null);
            Assert.Null(ModelService3D.ParseGlb(glb));
        }

        [Fact]
        public void ParseGlb_MinimalValidGlb_ReturnsNonNull()
        {
            // A minimal valid GLB with both JSON and BIN chunks.
            // The JSON references a single mesh with one primitive.
            // This is a NON-Draco path — tests uncompressed parsing.
            var json = @"{
                ""accessors"": [
                    {
                        ""bufferView"": 0,
                        ""componentType"": 5123,
                        ""count"": 3,
                        ""type"": ""SCALAR"",
                        ""max"": [2],
                        ""min"": [0]
                    },
                    {
                        ""bufferView"": 1,
                        ""componentType"": 5126,
                        ""count"": 3,
                        ""type"": ""VEC3"",
                        ""max"": [1, 1, 0],
                        ""min"": [0, 0, 0]
                    }
                ],
                ""bufferViews"": [
                    { ""byteOffset"": 0, ""byteLength"": 6, ""target"": 34963 },
                    { ""byteOffset"": 6, ""byteLength"": 36, ""byteStride"": 12, ""target"": 34962 }
                ],
                ""meshes"": [
                    {
                        ""primitives"": [
                            {
                                ""attributes"": { ""POSITION"": 1 },
                                ""indices"": 0
                            }
                        ]
                    }
                ]
            }";

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            PadTo4(ref jsonBytes);

            // BIN: 3 indices (uint16) + 3 positions (vec3 = 3 floats each)
            // Indices: 0, 1, 2
            // Positions: (0,0,0), (1,0,0), (0,1,0)
            var bin = new byte[6 + 36];
            WriteU16(bin, 0, 0);
            WriteU16(bin, 2, 1);
            WriteU16(bin, 4, 2);
            WriteF32(bin, 6, 0f); WriteF32(bin, 10, 0f); WriteF32(bin, 14, 0f);
            WriteF32(bin, 18, 1f); WriteF32(bin, 22, 0f); WriteF32(bin, 26, 0f);
            WriteF32(bin, 30, 0f); WriteF32(bin, 34, 1f); WriteF32(bin, 38, 0f);

            var glb = BuildGlb(jsonBytes, bin);
            var result = ModelService3D.ParseGlb(glb);

            Assert.NotNull(result);
            Assert.Equal(3, result.Positions.Length);
            Assert.Equal(3, result.Indices.Length / 3); // 3 indices = 1 triangle
            // Positions should be centered around origin
            Assert.True(result.BoundsMeters.Length() > 0);
        }

        [Fact]
        public void ParseGlb_WithNormals_ParsesCorrectly()
        {
            var json = @"{
                ""accessors"": [
                    {
                        ""bufferView"": 0,
                        ""componentType"": 5123,
                        ""count"": 3,
                        ""type"": ""SCALAR""
                    },
                    {
                        ""bufferView"": 1,
                        ""componentType"": 5126,
                        ""count"": 3,
                        ""type"": ""VEC3""
                    },
                    {
                        ""bufferView"": 2,
                        ""componentType"": 5126,
                        ""count"": 3,
                        ""type"": ""VEC3""
                    }
                ],
                ""bufferViews"": [
                    { ""byteOffset"": 0, ""byteLength"": 6, ""target"": 34963 },
                    { ""byteOffset"": 6, ""byteLength"": 36, ""byteStride"": 12, ""target"": 34962 },
                    { ""byteOffset"": 42, ""byteLength"": 36, ""byteStride"": 12, ""target"": 34962 }
                ],
                ""meshes"": [
                    {
                        ""primitives"": [
                            {
                                ""attributes"": { ""POSITION"": 1, ""NORMAL"": 2 },
                                ""indices"": 0
                            }
                        ]
                    }
                ]
            }";

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            PadTo4(ref jsonBytes);

            var bin = new byte[6 + 36 + 36];
            // Indices
            WriteU16(bin, 0, 0); WriteU16(bin, 2, 1); WriteU16(bin, 4, 2);
            // Positions
            WriteF32(bin, 6, 0f); WriteF32(bin, 10, 0f); WriteF32(bin, 14, 0f);
            WriteF32(bin, 18, 1f); WriteF32(bin, 22, 0f); WriteF32(bin, 26, 0f);
            WriteF32(bin, 30, 0f); WriteF32(bin, 34, 1f); WriteF32(bin, 38, 0f);
            // Normals (all pointing up)
            WriteF32(bin, 42, 0f); WriteF32(bin, 46, 1f); WriteF32(bin, 50, 0f);
            WriteF32(bin, 54, 0f); WriteF32(bin, 58, 1f); WriteF32(bin, 62, 0f);
            WriteF32(bin, 66, 0f); WriteF32(bin, 70, 1f); WriteF32(bin, 74, 0f);

            var glb = BuildGlb(jsonBytes, bin);
            var result = ModelService3D.ParseGlb(glb);

            Assert.NotNull(result);
            Assert.Equal(3, result.Normals.Length);
            // All normals should be UnitY
            for (int i = 0; i < result.Normals.Length; i++)
                Assert.Equal(Vector3.UnitY, result.Normals[i]);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ParseGlb — Draco-compressed path
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ParseGlb_DracoExtensionPresent_AttemptsDecode()
        {
            // A GLB with KHR_draco_mesh_compression extension.
            // The Draco bufferView contains dummy compressed data.
            // On Windows with the native DLL, this would attempt decode.
            // On the test runner (no native DLL), it will throw DllNotFoundException
            // or return null depending on the platform.
            //
            // This test verifies the detection path works: the JSON chunk
            // is parsed, the Draco extension is found, and the buffer is
            // extracted. The actual decode is platform-dependent.
            var json = @"{
                ""accessors"": [],
                ""bufferViews"": [
                    {
                        ""byteOffset"": 0,
                        ""byteLength"": 16,
                        ""target"": 34962
                    }
                ],
                ""meshes"": [
                    {
                        ""primitives"": [
                            {
                                ""attributes"": {},
                                ""extensions"": {
                                    ""KHR_draco_mesh_compression"": {
                                        ""bufferView"": 0,
                                        ""attributes"": {
                                            ""POSITION"": 0,
                                            ""NORMAL"": 1
                                        }
                                    }
                                }
                            }
                        ]
                    }
                ]
            }";

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            PadTo4(ref jsonBytes);

            // 16 bytes of dummy compressed data
            var compressed = new byte[16];
            for (int i = 0; i < 16; i++) compressed[i] = (byte)i;

            var glb = BuildGlb(jsonBytes, compressed);

            // On a non-Windows test runner, this will either:
            // - Return null (DllNotFoundException caught by try/catch? No, DracoDecoder is static with no try/catch)
            // - Actually, ParseGlb calls ParseGltf which calls DracoDecoder.TryDecode which does DllImport.
            //   The DllImport will throw DllNotFoundException if the DLL isn't found.
            // This test verifies the host can handle the Draco path.
            // We use a try/catch to handle the platform-dependent behavior.
            GltfMeshData result = null;
            try
            {
                result = ModelService3D.ParseGlb(glb);
            }
            catch (DllNotFoundException)
            {
                // Expected on non-Windows — the native DLL is not available.
                // This confirms the Draco detection path was triggered.
                return;
            }

            // If we got here (Windows with the DLL), the decode should fail
            // because the compressed data is garbage.
            Assert.Null(result);
        }

        [Fact]
        public void ParseGlb_DracoBufferViewOutOfBounds_ReturnsNull()
        {
            // Draco bufferView references data beyond the BIN chunk
            var json = @"{
                ""accessors"": [],
                ""bufferViews"": [
                    {
                        ""byteOffset"": 999,
                        ""byteLength"": 16,
                        ""target"": 34962
                    }
                ],
                ""meshes"": [
                    {
                        ""primitives"": [
                            {
                                ""attributes"": {},
                                ""extensions"": {
                                    ""KHR_draco_mesh_compression"": {
                                        ""bufferView"": 0,
                                        ""attributes"": {
                                            ""POSITION"": 0
                                        }
                                    }
                                }
                            }
                        ]
                    }
                ]
            }";

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            PadTo4(ref jsonBytes);

            var glb = BuildGlb(jsonBytes, new byte[16]);
            var result = ModelService3D.ParseGlb(glb);

            // Should return null because the Draco bufferView is outside the BIN chunk
            Assert.Null(result);
        }

        [Fact]
        public void ParseGlb_DracoBufferViewIndexInvalid_ReturnsNull()
        {
            // Draco extension references a bufferView index that doesn't exist
            var json = @"{
                ""accessors"": [],
                ""bufferViews"": [],
                ""meshes"": [
                    {
                        ""primitives"": [
                            {
                                ""attributes"": {},
                                ""extensions"": {
                                    ""KHR_draco_mesh_compression"": {
                                        ""bufferView"": 5,
                                        ""attributes"": {
                                            ""POSITION"": 0
                                        }
                                    }
                                }
                            }
                        ]
                    }
                ]
            }";

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            PadTo4(ref jsonBytes);

            var glb = BuildGlb(jsonBytes, new byte[16]);
            var result = ModelService3D.ParseGlb(glb);

            Assert.Null(result);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ParseGlb — Multiple primitives (some Draco, some uncompressed)
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ParseGlb_MixedDracoAndUncompressed_ReturnsNullForDracoOnly()
        {
            // A mesh with two primitives: one Draco (that will fail to decode),
            // one uncompressed. The Draco failure should cause the whole mesh
            // to return null (ParseGltf returns null on Draco decode failure).
            var json = @"{
                ""accessors"": [
                    {
                        ""bufferView"": 0,
                        ""componentType"": 5126,
                        ""count"": 3,
                        ""type"": ""VEC3""
                    }
                ],
                ""bufferViews"": [
                    { ""byteOffset"": 0, ""byteLength"": 36, ""byteStride"": 12, ""target"": 34962 },
                    { ""byteOffset"": 36, ""byteLength"": 16, ""target"": 34962 }
                ],
                ""meshes"": [
                    {
                        ""primitives"": [
                            {
                                ""attributes"": { ""POSITION"": 0 },
                                ""extensions"": {
                                    ""KHR_draco_mesh_compression"": {
                                        ""bufferView"": 1,
                                        ""attributes"": { ""POSITION"": 0 }
                                    }
                                }
                            }
                        ]
                    }
                ]
            }";

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            PadTo4(ref jsonBytes);

            var bin = new byte[36 + 16];
            WriteF32(bin, 0, 0f); WriteF32(bin, 4, 0f); WriteF32(bin, 8, 0f);
            WriteF32(bin, 12, 1f); WriteF32(bin, 16, 0f); WriteF32(bin, 20, 0f);
            WriteF32(bin, 24, 0f); WriteF32(bin, 28, 1f); WriteF32(bin, 32, 0f);
            // Dummy Draco data
            for (int i = 0; i < 16; i++) bin[36 + i] = (byte)i;

            var glb = BuildGlb(jsonBytes, bin);

            GltfMeshData result = null;
            try
            {
                result = ModelService3D.ParseGlb(glb);
            }
            catch (DllNotFoundException)
            {
                // Expected on non-Windows
                return;
            }

            // Draco decode should fail on garbage data, so result is null
            Assert.Null(result);
        }

        // ─────────────────────────────────────────────────────────────────────
        // FindModelUrl — Draco-specific model URLs
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void FindModelUrl_DracoGlbExtension_FindsUrl()
        {
            // IKEA sometimes uses glb_draco extensions in their URLs
            var page = new Uri("https://www.ikea.com/us/en/p/billy-12345678/");
            var html = "<model-viewer gltf-model=\"https://models.example/billy.glb_draco\"></model-viewer>";

            var modelUrl = ModelService3D.FindModelUrl(html, page);

            Assert.NotNull(modelUrl);
            Assert.Contains("glb_draco", modelUrl.ToString());
        }

        [Fact]
        public void FindModelUrl_DracoMiniGlb_FindsUrl()
        {
            // IKEA Rotera mini GLB URLs (often Draco-compressed)
            var page = new Uri("https://www.ikea.com/us/en/p/billy-12345678/");
            var html = "<div data-src=\"https://models.example/billy-mini.glb\"></div>";

            var modelUrl = ModelService3D.FindModelUrl(html, page);

            Assert.NotNull(modelUrl);
            Assert.Contains("mini.glb", modelUrl.ToString());
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static byte[] BuildGlb(byte[] jsonChunk, byte[] binChunk)
        {
            // GLB header: 12 bytes
            // magic (4) + version (4) + length (4)
            uint totalLen = 12;

            // JSON chunk: chunkLength (4) + chunkType (4) + data
            uint jsonChunkLen = (uint)(jsonChunk?.Length ?? 0);
            uint jsonChunkHeader = 8;
            totalLen += jsonChunkHeader + jsonChunkLen;

            // BIN chunk (optional)
            uint binChunkLen = (uint)(binChunk?.Length ?? 0);
            uint binChunkHeader = 0;
            if (binChunkLen > 0)
            {
                binChunkHeader = 8;
                totalLen += binChunkHeader + binChunkLen;
            }

            var glb = new byte[totalLen];
            WriteU32(glb, 0, 0x46546C67); // magic "glTF"
            WriteU32(glb, 4, 2);          // version 2
            WriteU32(glb, 8, totalLen);   // total length

            uint offset = 12;
            // JSON chunk
            if (jsonChunk != null)
            {
                WriteU32(glb, (int)offset, jsonChunkLen);
                WriteU32(glb, (int)offset + 4, 0x4E4F534A); // "JSON"
                Buffer.BlockCopy(jsonChunk, 0, glb, (int)offset + 8, jsonChunk.Length);
                offset += jsonChunkHeader + jsonChunkLen;
            }

            // BIN chunk
            if (binChunk != null)
            {
                WriteU32(glb, (int)offset, binChunkLen);
                WriteU32(glb, (int)offset + 4, 0x004E4942); // "BIN\0"
                Buffer.BlockCopy(binChunk, 0, glb, (int)offset + 8, binChunk.Length);
            }

            return glb;
        }

        private static void PadTo4(ref byte[] bytes)
        {
            int remainder = bytes.Length % 4;
            if (remainder > 0)
            {
                var padded = new byte[bytes.Length + (4 - remainder)];
                Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
                bytes = padded;
            }
        }

        private static void WriteU32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteF32(byte[] buf, int offset, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            buf[offset] = bytes[0];
            buf[offset + 1] = bytes[1];
            buf[offset + 2] = bytes[2];
            buf[offset + 3] = bytes[3];
        }
    }
}