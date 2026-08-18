using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HololensIKEA.Services
{
    /// <summary>Normalized bounds of non-white content (minU, minV, maxU, maxV).</summary>
    public struct ContentBounds
    {
        public float MinU { get; }
        public float MinV { get; }
        public float MaxU { get; }
        public float MaxV { get; }

        public ContentBounds(float minU, float minV, float maxU, float maxV)
        {
            MinU = minU; MinV = minV; MaxU = maxU; MaxV = maxV;
        }
    }

    internal sealed class ImageLoadResult
    {
        public ShaderResourceView Srv       { get; }
        public byte[]             TightBgra { get; }   // BGRA8, tightly packed (no row padding)
        public uint               Width     { get; }
        public uint               Height    { get; }
        public ContentBounds      Bounds    { get; }   // Normalized (0-1) bounds of non-white content

        public ImageLoadResult(ShaderResourceView srv, byte[] tight, uint w, uint h, ContentBounds bounds)
        {
            Srv       = srv;
            TightBgra = tight;
            Width     = w;
            Height    = h;
            Bounds    = bounds;
        }
    }

    /// <summary>
    /// Downloads a product image from a URL, decodes it to BGRA8 via Windows.Graphics.Imaging,
    /// and uploads it to a D3D11 ShaderResourceView.  Results are cached by URL.
    /// While loading, GetPlaceholder() returns a 2×2 grey checkerboard SRV.
    /// </summary>
    internal sealed class ProductImageLoader : IDisposable
    {
        private readonly SharpDX.Direct3D11.Device               _device;
        private readonly Dictionary<string, ShaderResourceView>  _cache = new Dictionary<string, ShaderResourceView>();
        private readonly object                                   _cacheLock = new object();
        private ShaderResourceView                               _placeholder;
        private bool                                             _disposed;

        public ProductImageLoader(SharpDX.Direct3D11.Device device)
        {
            _device     = device;
            _placeholder = CreateCheckerboard(device);
        }

        /// <summary>A 2×2 grey checkerboard SRV shown while the real texture is still loading.</summary>
        public ShaderResourceView GetPlaceholder() => _placeholder;

        /// <summary>
        /// Download and decode an image from <paramref name="url"/>, upload to GPU.
        /// Returns the cached SRV if the URL was already loaded.
        /// Returns null on failure.
        /// </summary>
        public async Task<ShaderResourceView> LoadFromUrlAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(url, out var cached))
                    return cached;
            }

            try
            {
                var result = await DownloadAndDecodeAsync(url, ct).ConfigureAwait(false);
                if (result == null) return null;

                lock (_cacheLock)
                    _cache[url] = result.Srv;

                return result.Srv;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("[ImageLoader] " + url + " failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Download, decode, and return both the GPU SRV and the raw CPU pixel data.
        /// Used when depth analysis also needs the pixel bytes.
        /// Returns null on failure.
        /// </summary>
        public async Task<ImageLoadResult> DownloadAndDecodeAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                byte[] imageBytes;
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        imageBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
                ct.ThrowIfCancellationRequested();
                var result = await DecodeAndUploadAsync(imageBytes).ConfigureAwait(false);
                GetLastResult = result;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("[ImageLoader] Download failed " + url + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Upload a 64×64 R8 displacement map (output of ProductDepthAnalyzer) as an SRV.
        /// </summary>
        public ShaderResourceView UploadDisplacementMap(byte[] r8Data, int width, int height)
        {
            var desc = new Texture2DDescription
            {
                Width             = width,
                Height            = height,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Immutable,
                BindFlags         = BindFlags.ShaderResource,
            };

            unsafe
            {
                fixed (byte* ptr = r8Data)
                {
                    var dataBox = new DataBox((IntPtr)ptr, width, 0);
                    using (var tex = new Texture2D(_device, desc, new[] { dataBox }))
                    {
                        var srvDesc = new ShaderResourceViewDescription
                        {
                            Format    = Format.R8_UNorm,
                            Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D,
                            Texture2D = new ShaderResourceViewDescription.Texture2DResource
                            {
                                MipLevels       = 1,
                                MostDetailedMip = 0,
                            },
                        };
                        return new ShaderResourceView(_device, tex, srvDesc);
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private async Task<ImageLoadResult> DecodeAndUploadAsync(byte[] imageBytes)
        {
            using (var stream = new InMemoryRandomAccessStream())
            {
                // Write bytes into WinRT stream
                await stream.WriteAsync(imageBytes.AsBuffer());
                stream.Seek(0);

                // Decode to BGRA8 premultiplied using BitmapDecoder
                var decoder = await BitmapDecoder.CreateAsync(stream);

                uint w = decoder.PixelWidth;
                uint h = decoder.PixelHeight;

                // GetPixelDataAsync returns tightly-packed pixels (no stride padding).
                var pixelProvider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] tightBgra = pixelProvider.DetachPixelData();

                var srv = UploadBGRA(tightBgra, w, h);
                var bounds = ComputeContentBounds(tightBgra, w, h, whiteThreshold: 0.08f);
                return new ImageLoadResult(srv, tightBgra, w, h, bounds);
            }
        }

        /// <summary>
        /// Scans BGRA8 pixels to find the bounding box of non-white content.
        /// Returns normalized (0-1) bounds: (minU, minV, maxU, maxV).
        /// White is detected as distance from (1,1,1,1) &lt; threshold.
        /// </summary>
        private static ContentBounds ComputeContentBounds(byte[] tightBgra, uint width, uint height, float whiteThreshold)
        {
            if (tightBgra == null || width == 0 || height == 0)
                return new ContentBounds(0f, 0f, 1f, 1f);

            int minX = (int)width, maxX = -1;
            int minY = (int)height, maxY = -1;

            // Scan all pixels; find min/max X,Y of non-white pixels
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * (int)width + x) * 4;  // BGRA, 4 bytes per pixel
                    byte b = tightBgra[idx];
                    byte g = tightBgra[idx + 1];
                    byte r = tightBgra[idx + 2];
                    byte a = tightBgra[idx + 3];

                    // Convert to float [0,1]
                    float bf = b / 255f;
                    float gf = g / 255f;
                    float rf = r / 255f;
                    float af = a / 255f;

                    // Distance from white (1,1,1,1) in RGB space (ignore alpha for white detection)
                    float distFromWhite = 1f - ((bf + gf + rf) / 3f);

                    // If this pixel is not-white (distance > threshold), it's content
                    if (distFromWhite > whiteThreshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            // If no content found, return full quad
            if (minX > maxX || minY > maxY)
                return new ContentBounds(0f, 0f, 1f, 1f);

            // Normalize bounds to [0,1] and add 1-pixel padding
            float pad = 1f;
            float minUNorm = Math.Max(0f, (minX - pad) / width);
            float maxUNorm = Math.Min(1f, (maxX + 1f + pad) / width);
            float minVNorm = Math.Max(0f, (minY - pad) / height);
            float maxVNorm = Math.Min(1f, (maxY + 1f + pad) / height);

            return new ContentBounds(minUNorm, minVNorm, maxUNorm, maxVNorm);
        }

        internal ShaderResourceView UploadBGRA(byte[] pixels, uint width, uint height)
        {
            var desc = new Texture2DDescription
            {
                Width             = (int)width,
                Height            = (int)height,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Immutable,
                BindFlags         = BindFlags.ShaderResource,
            };

            unsafe
            {
                fixed (byte* ptr = pixels)
                {
                    var dataBox = new DataBox((IntPtr)ptr, (int)width * 4, 0);
                    using (var tex = new Texture2D(_device, desc, new[] { dataBox }))
                        return new ShaderResourceView(_device, tex);
                }
            }
        }

        private static ShaderResourceView CreateCheckerboard(SharpDX.Direct3D11.Device device)
        {
            // 2×2 BGRA8: alternating light-grey / dark-grey
            byte[] pix =
            {
                180, 180, 180, 255,  100, 100, 100, 255,
                100, 100, 100, 255,  180, 180, 180, 255,
            };

            var desc = new Texture2DDescription
            {
                Width = 2, Height = 2, MipLevels = 1, ArraySize = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Immutable,
                BindFlags         = BindFlags.ShaderResource,
            };

            unsafe
            {
                fixed (byte* ptr = pix)
                {
                    var dataBox = new DataBox((IntPtr)ptr, 8, 0);
                    using (var tex = new Texture2D(device, desc, new[] { dataBox }))
                        return new ShaderResourceView(device, tex);
                }
            }
        }

        /// <summary>Returns DownloadAndDecodeAsync result, or null on failure. Bounds are included.</summary>
        public ImageLoadResult GetLastResult { get; private set; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _placeholder?.Dispose();
            _placeholder = null;

            lock (_cacheLock)
            {
                foreach (var srv in _cache.Values)
                    srv?.Dispose();
                _cache.Clear();
            }
        }
    }
}
