using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using HololensIKEA.Common;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Renders dimension labels (in millimeters) on each visible edge of the product box.
    /// 
    /// Three labels are rendered as textured quads:
    ///   - Width  (X-axis) — displayed along the bottom front edge
    ///   - Height (Y-axis) — displayed along the left front edge  
    ///   - Depth  (Z-axis) — displayed along the bottom side edge
    /// 
    /// Labels use Direct2D/DirectWrite to render text to a texture, then display it
    /// as a small billboard facing the user.
    /// </summary>
    internal sealed class ProductDimensionLabels : Disposer
    {
        // ─────────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────────

        private const int   LABEL_TEX_W = 256;     // Texture width per label
        private const int   LABEL_TEX_H = 64;      // Texture height per label
        private const float LABEL_SCALE = 0.08f;   // World-space height of label (meters)
        private const float LABEL_OFFSET = 0.02f;  // Offset from box edge (meters)

        // ─────────────────────────────────────────────────────────────────────
        // D3D objects
        // ─────────────────────────────────────────────────────────────────────

        private readonly DeviceResources _dr;

        private InputLayout    _inputLayout;
        private VertexShader   _vs;
        private GeometryShader _gs;
        private PixelShader    _ps;
        private SamplerState   _sampler;
        private BlendState     _blendState;
        private DepthStencilState _depthState;

        // Per-label resources (width, height, depth)
        private SharpDX.Direct3D11.Buffer _cbModel;
        private Texture2D[]               _labelTextures = new Texture2D[3];
        private ShaderResourceView[]      _labelSrvs     = new ShaderResourceView[3];
        private SharpDX.Direct2D1.RenderTarget[] _d2dTargets = new SharpDX.Direct2D1.RenderTarget[3];

        // Quad geometry (shared by all labels)
        private SharpDX.Direct3D11.Buffer _vb;
        private SharpDX.Direct3D11.Buffer _ib;

        // DirectWrite resources
        private SharpDX.DirectWrite.TextFormat _textFormat;
        private SharpDX.Direct2D1.SolidColorBrush[] _brushes = new SharpDX.Direct2D1.SolidColorBrush[3];

        // State
        private bool       _loadingComplete = false;
        private bool       _usingVprt = false;
        private bool       _visible = true;
        private float      _widthMm, _heightMm, _depthMm;
        private Vector3    _position;
        private Vector3    _dims;
        private Quaternion _rotation = Quaternion.Identity;
        private bool       _labelsDirty = true;

        // Label colors
        private static readonly SharpDX.Mathematics.Interop.RawColor4 ColorWidth  = new SharpDX.Mathematics.Interop.RawColor4(1.0f, 0.4f, 0.4f, 1.0f);  // Red-ish
        private static readonly SharpDX.Mathematics.Interop.RawColor4 ColorHeight = new SharpDX.Mathematics.Interop.RawColor4(0.4f, 1.0f, 0.4f, 1.0f);  // Green-ish
        private static readonly SharpDX.Mathematics.Interop.RawColor4 ColorDepth  = new SharpDX.Mathematics.Interop.RawColor4(0.4f, 0.6f, 1.0f, 1.0f);  // Blue-ish

        // ─────────────────────────────────────────────────────────────────────
        // Vertex layout
        // ─────────────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct VertexPosUV
        {
            public Vector3 Position;
            public Vector2 UV;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construction
        // ─────────────────────────────────────────────────────────────────────

        public ProductDimensionLabels(DeviceResources deviceResources)
        {
            _dr = deviceResources;
            CreateDeviceDependentResourcesAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Gets or sets whether the dimension labels are visible.</summary>
        public bool IsVisible
        {
            get => _visible;
            set => _visible = value;
        }

        /// <summary>Updates the dimensions and triggers label texture regeneration.</summary>
        public void SetDimensions(float widthM, float heightM, float depthM)
        {
            float wMm = widthM * 1000f;
            float hMm = heightM * 1000f;
            float dMm = depthM * 1000f;

            if (Math.Abs(_widthMm - wMm) > 0.1f ||
                Math.Abs(_heightMm - hMm) > 0.1f ||
                Math.Abs(_depthMm - dMm) > 0.1f)
            {
                _widthMm = wMm;
                _heightMm = hMm;
                _depthMm = dMm;
                _dims = new Vector3(widthM, heightM, depthM);
                _labelsDirty = true;
            }
        }

        /// <summary>Sets the world position of the product box center.</summary>
        public void SetPosition(Vector3 pos) => _position = pos;

        /// <summary>Sets the rotation of the product box.</summary>
        public void SetRotation(Quaternion rot) => _rotation = rot;

        /// <summary>Must be called once per frame before Render().</summary>
        public void Update()
        {
            if (!_loadingComplete || !_visible)
                return;

            if (_labelsDirty)
            {
                RenderLabelsToTextures();
                _labelsDirty = false;
            }
        }

        /// <summary>Renders all three dimension labels.</summary>
        public void Render()
        {
            if (!_loadingComplete || !_visible)
                return;

            var ctx = _dr.D3DDeviceContext;

            // Set up shared state
            ctx.OutputMerger.SetBlendState(_blendState);
            ctx.OutputMerger.SetDepthStencilState(_depthState);

            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            ctx.InputAssembler.InputLayout = _inputLayout;

            int stride = Marshal.SizeOf<VertexPosUV>();
            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vb, stride, 0));
            ctx.InputAssembler.SetIndexBuffer(_ib, Format.R16_UInt, 0);

            ctx.VertexShader.SetShader(_vs, null, 0);
            ctx.VertexShader.SetConstantBuffers(0, _cbModel);

            if (!_usingVprt)
                ctx.GeometryShader.SetShader(_gs, null, 0);
            else
                ctx.GeometryShader.SetShader(null, null, 0);

            ctx.PixelShader.SetShader(_ps, null, 0);
            ctx.PixelShader.SetSampler(0, _sampler);

            // Render each label with its transform
            RenderLabel(ctx, 0, GetWidthLabelTransform());   // Width label
            RenderLabel(ctx, 1, GetHeightLabelTransform());  // Height label
            RenderLabel(ctx, 2, GetDepthLabelTransform());   // Depth label

            // Restore state
            ctx.OutputMerger.SetBlendState(null);
            ctx.OutputMerger.SetDepthStencilState(null);
            ctx.GeometryShader.SetShader(null, null, 0);
        }

        private void RenderLabel(DeviceContext ctx, int index, Matrix4x4 transform)
        {
            if (_labelSrvs[index] == null)
                return;

            // Update model constant buffer
            var cbData = new ModelConstantBuffer { model = Matrix4x4.Transpose(transform) };
            ctx.UpdateSubresource(ref cbData, _cbModel);

            ctx.PixelShader.SetShaderResource(0, _labelSrvs[index]);
            ctx.DrawIndexedInstanced(6, 2, 0, 0, 0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Label transforms - position labels on box edges
        // ─────────────────────────────────────────────────────────────────────

        private Matrix4x4 GetWidthLabelTransform()
        {
            // Width label: centered on bottom front edge, pointing forward
            float aspect = (float)LABEL_TEX_W / LABEL_TEX_H;
            float labelW = LABEL_SCALE * aspect;
            float labelH = LABEL_SCALE;

            // Position: below the box, centered on front face
            var localOffset = new Vector3(0, -_dims.Y * 0.5f - LABEL_OFFSET - labelH * 0.5f, _dims.Z * 0.5f + 0.01f);
            var worldOffset = Vector3.Transform(localOffset, _rotation);

            return Matrix4x4.CreateScale(labelW, labelH, 1f) *
                   Matrix4x4.CreateFromQuaternion(_rotation) *
                   Matrix4x4.CreateTranslation(_position + worldOffset);
        }

        private Matrix4x4 GetHeightLabelTransform()
        {
            // Height label: on left side, rotated 90° to read vertically
            float aspect = (float)LABEL_TEX_W / LABEL_TEX_H;
            float labelW = LABEL_SCALE * aspect;
            float labelH = LABEL_SCALE;

            // Position: left of the box, centered vertically on front face
            var localOffset = new Vector3(-_dims.X * 0.5f - LABEL_OFFSET - labelH * 0.5f, 0, _dims.Z * 0.5f + 0.01f);
            var worldOffset = Vector3.Transform(localOffset, _rotation);

            // Rotate 90° CCW so text reads bottom-to-top
            var labelRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));

            return Matrix4x4.CreateScale(labelW, labelH, 1f) *
                   Matrix4x4.CreateFromQuaternion(labelRot) *
                   Matrix4x4.CreateFromQuaternion(_rotation) *
                   Matrix4x4.CreateTranslation(_position + worldOffset);
        }

        private Matrix4x4 GetDepthLabelTransform()
        {
            // Depth label: on bottom side edge
            float aspect = (float)LABEL_TEX_W / LABEL_TEX_H;
            float labelW = LABEL_SCALE * aspect;
            float labelH = LABEL_SCALE;

            // Position: below and to the right, showing depth
            var localOffset = new Vector3(_dims.X * 0.5f + LABEL_OFFSET + labelH * 0.5f, -_dims.Y * 0.5f - LABEL_OFFSET, 0);
            var worldOffset = Vector3.Transform(localOffset, _rotation);

            // Rotate -90° around Y to face the side
            var labelRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(-Math.PI / 2));

            return Matrix4x4.CreateScale(labelW, labelH, 1f) *
                   Matrix4x4.CreateFromQuaternion(labelRot) *
                   Matrix4x4.CreateFromQuaternion(_rotation) *
                   Matrix4x4.CreateTranslation(_position + worldOffset);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Device resources
        // ─────────────────────────────────────────────────────────────────────

        public async void CreateDeviceDependentResourcesAsync()
        {
            ReleaseDeviceDependentResources();

            _usingVprt = _dr.D3DDeviceSupportsVprt;
            var device = _dr.D3DDevice;
            var folder = Windows.ApplicationModel.Package.Current.InstalledLocation;

            try
            {
                // Load shaders (reuse texture shaders)
                var vsFile = _usingVprt
                    ? "Content\\Shaders\\TextureVertexShader.cso"
                    : "Content\\Shaders\\TextureVertexShaderNoVPRT.cso";
                var vsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync(vsFile));
                _vs = this.ToDispose(new VertexShader(device, vsBytes));

                var elements = new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
                    new InputElement("TEXCOORD", 0, Format.R32G32_Float, 12, 0, InputClassification.PerVertexData, 0),
                };
                _inputLayout = this.ToDispose(new InputLayout(device, vsBytes, elements));

                if (!_usingVprt)
                {
                    var gsBytes = await DirectXHelper.ReadDataAsync(
                        await folder.GetFileAsync("Content\\Shaders\\TextureGeometryShader.cso"));
                    _gs = this.ToDispose(new GeometryShader(device, gsBytes));
                }

                var psBytes = await DirectXHelper.ReadDataAsync(
                    await folder.GetFileAsync("Content\\Shaders\\TexturePixelShader.cso"));
                _ps = this.ToDispose(new PixelShader(device, psBytes));

                // Create quad geometry (unit quad centered at origin)
                var verts = new VertexPosUV[]
                {
                    new VertexPosUV { Position = new Vector3(-0.5f, -0.5f, 0), UV = new Vector2(0, 1) },
                    new VertexPosUV { Position = new Vector3( 0.5f, -0.5f, 0), UV = new Vector2(1, 1) },
                    new VertexPosUV { Position = new Vector3(-0.5f,  0.5f, 0), UV = new Vector2(0, 0) },
                    new VertexPosUV { Position = new Vector3( 0.5f,  0.5f, 0), UV = new Vector2(1, 0) },
                };
                var indices = new ushort[] { 0, 2, 3, 0, 3, 1 };

                _vb = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, verts));
                _ib = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, indices));

                // Model constant buffer
                var cbInit = new ModelConstantBuffer { model = Matrix4x4.Identity };
                _cbModel = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(device, BindFlags.ConstantBuffer, ref cbInit));

                // Sampler
                var samplerDesc = new SamplerStateDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    ComparisonFunction = Comparison.Never,
                    MinimumLod = 0,
                    MaximumLod = float.MaxValue,
                };
                _sampler = this.ToDispose(new SamplerState(device, samplerDesc));

                // Blend state (alpha blending)
                var blendDesc = new BlendStateDescription();
                blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = true,
                    SourceBlend = BlendOption.SourceAlpha,
                    DestinationBlend = BlendOption.InverseSourceAlpha,
                    BlendOperation = BlendOperation.Add,
                    SourceAlphaBlend = BlendOption.One,
                    DestinationAlphaBlend = BlendOption.Zero,
                    AlphaBlendOperation = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteMaskFlags.All,
                };
                _blendState = this.ToDispose(new BlendState(device, blendDesc));

                // Depth state (read but don't write)
                var depthDesc = new DepthStencilStateDescription
                {
                    IsDepthEnabled = true,
                    DepthWriteMask = DepthWriteMask.Zero,
                    DepthComparison = Comparison.LessEqual,
                };
                _depthState = this.ToDispose(new DepthStencilState(device, depthDesc));

                // Create label textures and D2D targets
                CreateLabelTextures(device);

                // Create DirectWrite resources
                _textFormat = this.ToDispose(new SharpDX.DirectWrite.TextFormat(
                    _dr.DWriteFactory, "Segoe UI",
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 32f));
                _textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                _textFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                _loadingComplete = true;
                _labelsDirty = true;
                Debug.WriteLine("[DimLabels] Resources created (VPRT=" + _usingVprt + ")");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DimLabels] Init failed: " + ex.Message);
            }
        }

        private void CreateLabelTextures(SharpDX.Direct3D11.Device device)
        {
            var colors = new[] { ColorWidth, ColorHeight, ColorDepth };

            for (int i = 0; i < 3; i++)
            {
                var texDesc = new Texture2DDescription
                {
                    Width = LABEL_TEX_W,
                    Height = LABEL_TEX_H,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                };
                _labelTextures[i] = this.ToDispose(new Texture2D(device, texDesc));
                _labelSrvs[i] = this.ToDispose(new ShaderResourceView(device, _labelTextures[i]));

                using (var surf = _labelTextures[i].QueryInterface<SharpDX.DXGI.Surface>())
                {
                    var rtProps = new SharpDX.Direct2D1.RenderTargetProperties(
                        SharpDX.Direct2D1.RenderTargetType.Default,
                        new SharpDX.Direct2D1.PixelFormat(Format.Unknown, SharpDX.Direct2D1.AlphaMode.Premultiplied),
                        96f, 96f,
                        SharpDX.Direct2D1.RenderTargetUsage.None,
                        SharpDX.Direct2D1.FeatureLevel.Level_DEFAULT);
                    _d2dTargets[i] = this.ToDispose(
                        new SharpDX.Direct2D1.RenderTarget(_dr.D2DFactory, surf, rtProps));
                }

                _brushes[i] = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(_d2dTargets[i], colors[i]));
            }
        }

        private void RenderLabelsToTextures()
        {
            var labels = new[] 
            { 
                $"{_widthMm:F0} mm",   // Width
                $"{_heightMm:F0} mm",  // Height
                $"{_depthMm:F0} mm"    // Depth
            };

            var bgColor = new SharpDX.Mathematics.Interop.RawColor4(0.1f, 0.1f, 0.15f, 0.85f);
            var rect = new SharpDX.Mathematics.Interop.RawRectangleF(0, 0, LABEL_TEX_W, LABEL_TEX_H);

            for (int i = 0; i < 3; i++)
            {
                if (_d2dTargets[i] == null || _brushes[i] == null)
                    continue;

                _d2dTargets[i].BeginDraw();
                _d2dTargets[i].Clear(bgColor);
                _d2dTargets[i].DrawText(labels[i], _textFormat, rect, _brushes[i]);
                _d2dTargets[i].EndDraw();
            }

            Debug.WriteLine($"[DimLabels] Rendered: W={_widthMm:F0} H={_heightMm:F0} D={_depthMm:F0} mm");
        }

        public void ReleaseDeviceDependentResources()
        {
            _loadingComplete = false;
            this.RemoveAndDispose(ref _vs);
            this.RemoveAndDispose(ref _gs);
            this.RemoveAndDispose(ref _ps);
            this.RemoveAndDispose(ref _inputLayout);
            this.RemoveAndDispose(ref _vb);
            this.RemoveAndDispose(ref _ib);
            this.RemoveAndDispose(ref _cbModel);
            this.RemoveAndDispose(ref _sampler);
            this.RemoveAndDispose(ref _blendState);
            this.RemoveAndDispose(ref _depthState);
            this.RemoveAndDispose(ref _textFormat);

            for (int i = 0; i < 3; i++)
            {
                _brushes[i]?.Dispose();
                _brushes[i] = null;
                _d2dTargets[i]?.Dispose();
                _d2dTargets[i] = null;
                _labelSrvs[i]?.Dispose();
                _labelSrvs[i] = null;
                _labelTextures[i]?.Dispose();
                _labelTextures[i] = null;
            }
        }
    }
}
