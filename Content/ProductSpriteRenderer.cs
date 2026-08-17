using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using HololensIKEA.Common;
using HololensIKEA.Services;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Renders the product image texture as an overlay on the front face (+Z) of the product box.
    ///
    /// Two modes, selected automatically:
    ///   BASIC  – flat quad at z=+0.501 (local space), uses white-removal pixel shader.
    ///            Active immediately when a texture SRV is set.
    ///   DISPLACED – 65×65 subdivided grid; each vertex displaced in Z by sampling a
    ///               64×64 R8 displacement map (from ProductDepthAnalyzer).
    ///               Activated when SetDisplacementTexture() is called.
    ///
    /// Rendering is always done AFTER ProductBoxRenderer so the transparent areas of the
    /// sprite reveal the solid box geometry underneath.
    /// </summary>
    internal sealed class ProductSpriteRenderer : Disposer
    {
        // ─────────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────────

        private const int  GridN           = 64;    // grid subdivision (64×64 = 65×65 vertices)
        private const float FrontFaceZ     = 0.51f;  // local Z; box front face is at 0.500 — extra clearance for 16-bit depth

        // ─────────────────────────────────────────────────────────────────────
        // D3D11 pipeline objects
        // ─────────────────────────────────────────────────────────────────────

        private readonly DeviceResources _dr;

        private InputLayout    _inputLayout;

        // Basic (flat quad) pipeline
        private VertexShader   _vsBasic;
        private GeometryShader _gsBasic;      // null on VPRT devices
        private PixelShader    _psBasic;

        // Displaced (grid) pipeline
        private VertexShader   _vsDisplaced;
        private GeometryShader _gsDisplaced;  // null on VPRT devices
        private PixelShader    _psDisplaced;

        // Constant buffers
        private SharpDX.Direct3D11.Buffer _modelCB;           // b0: float4x4 model
        private SharpDX.Direct3D11.Buffer _spriteCB;          // b2: sprite params

        // Samplers + states
        private SamplerState      _linearSampler;
        private SamplerState      _pointSampler;
        private BlendState        _blendState;
        private DepthStencilState _depthState;
        private RasterizerState   _rasterizerState;

        // Geometry
        private SharpDX.Direct3D11.Buffer _vbQuad;    // 4 vertices
        private SharpDX.Direct3D11.Buffer _ibQuad;    // 6 indices (R16_UInt)
        private SharpDX.Direct3D11.Buffer _vbGrid;    // (GridN+1)² vertices
        private SharpDX.Direct3D11.Buffer _ibGrid;    // GridN² × 6 indices (R16_UInt)

        // Textures
        private ShaderResourceView _srvTexture;
        private ShaderResourceView _srvDisplacement;
        private ShaderResourceView _srvSideFace;

        // Side-face geometry (right or left box face)
        private SharpDX.Direct3D11.Buffer _vbSide;
        private SharpDX.Direct3D11.Buffer _ibSide;
        private SharpDX.Direct3D11.Buffer _sideCB;
        private ViewType                   _viewType             = ViewType.FrontOnly;
        private float                     _lastDepthForSideGeom = -1f;

        // State
        private bool  _loadingComplete = false;
        private bool  _usingVprt       = false;
        private bool  _hasDisplacement = false;

        // Parameters
        private Vector3    _position = new Vector3(0f, 0f, -2f);
        private float      _width    = 0.5f;
        private float      _height   = 0.5f;
        private float      _depth    = 0.5f;
        private Quaternion _rotation = Quaternion.Identity;
        private float      _whiteThreshold = 0.08f;
        private float   _whiteSoftness  = 0.12f;
        private Vector4 _contentBounds  = new Vector4(0f, 0f, 1f, 1f);  // (minU, minV, maxU, maxV)

        // ─────────────────────────────────────────────────────────────────────
        // Shader constant-buffer structs
        // ─────────────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct ModelCbData
        {
            public Matrix4x4 Model;   // 64 bytes, matches cbuffer b0
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpriteCbData
        {
            public float WhiteThreshold;
            public float WhiteSoftness;
            public float Opacity;
            public float DepthScale;
            public Vector4 ContentBounds;  // (minU, minV, maxU, maxV) — normalized bounds of non-white content
        }

        // ─────────────────────────────────────────────────────────────────────
        // Vertex layout (matches both VS families)
        // ─────────────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct VertexPosUV
        {
            public Vector3 Position;
            public Vector2 UV;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construction / disposal
        // ─────────────────────────────────────────────────────────────────────

        public ProductSpriteRenderer(DeviceResources deviceResources)
        {
            _dr = deviceResources;
            CreateDeviceDependentResourcesAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        public void SetPosition(Vector3 pos)        => _position = pos;
        public void SetDimensions(float w, float h, float d) { _width = w; _height = h; _depth = d; }
        public void SetRotation(Quaternion rot) => _rotation = rot;

        /// <summary>
        /// Swaps in all per-instance rendering state WITHOUT disposing any SRVs.
        /// Used to render saved product instances that own their own textures.
        /// </summary>
        public void ApplyInstanceState(Vector3 position, float w, float h, float d,
                                       Quaternion rotation,
                                       ShaderResourceView textureSrv,
                                       ShaderResourceView displacementSrv,
                                       ShaderResourceView sideFaceSrv,
                                       Vector4 contentBounds,
                                       ViewType viewType)
        {
            _position = position;
            _width = w; _height = h; _depth = d;
            _rotation = rotation;
            _srvTexture = textureSrv;
            _srvDisplacement = displacementSrv;
            _hasDisplacement = displacementSrv != null;
            _srvSideFace = sideFaceSrv;
            _contentBounds = contentBounds;
            _viewType = viewType;
        }

        /// <summary>
        /// Detaches the current texture SRVs and state so they can be saved externally.
        /// After this call the renderer holds null SRVs and will not dispose the old ones.
        /// </summary>
        public void DetachState(out ShaderResourceView textureSrv,
                                out ShaderResourceView displacementSrv,
                                out ShaderResourceView sideFaceSrv,
                                out Vector4 contentBounds,
                                out ViewType viewType)
        {
            textureSrv      = _srvTexture;
            displacementSrv = _srvDisplacement;
            sideFaceSrv     = _srvSideFace;
            contentBounds   = _contentBounds;
            viewType        = _viewType;

            // Null out so SetTexture() won't dispose them
            _srvTexture      = null;
            _srvDisplacement = null;
            _hasDisplacement = false;
            _srvSideFace     = null;
        }

        public void SetTexture(ShaderResourceView srv)
        {
            if (!ReferenceEquals(srv, _srvTexture))
            {
                _srvTexture?.Dispose();
                _srvTexture = srv;
            }
        }

        public void SetDisplacementTexture(ShaderResourceView srv)
        {
            if (!ReferenceEquals(srv, _srvDisplacement))
            {
                _srvDisplacement?.Dispose();
                _srvDisplacement = srv;
            }
            _hasDisplacement = srv != null;
        }

        public void SetWhiteRemovalParams(float threshold, float softness)
        {
            _whiteThreshold = threshold;
            _whiteSoftness  = softness;
        }

        public void SetContentBounds(float minU, float minV, float maxU, float maxV)
        {
            _contentBounds = new Vector4(minU, minV, maxU, maxV);
        }

        public void SetSideFaceTexture(ShaderResourceView srv, ViewType viewType)
        {
            if (!ReferenceEquals(srv, _srvSideFace))
            {
                _srvSideFace?.Dispose();
                _srvSideFace = srv;
            }
            _viewType = viewType;
            _lastDepthForSideGeom = -1f;  // force geometry rebuild
        }

        public void ClearSideFaceTexture()
        {
            _srvSideFace?.Dispose();
            _srvSideFace = null;
            _viewType    = ViewType.FrontOnly;
        }

        /// <summary>Updates the model constant buffer. Must be called once per frame before Render().</summary>
        public void Update(StepTimer timer)
        {
            if (!_loadingComplete) return;

            // Same transform as ProductBoxRenderer – scale unit cube, rotate, then translate.
            var model = Matrix4x4.Transpose(
                Matrix4x4.CreateScale(_width, _height, _depth) *
                Matrix4x4.CreateFromQuaternion(_rotation) *
                Matrix4x4.CreateTranslation(_position));

            var modelData = new ModelCbData { Model = model };
            _dr.D3DDeviceContext.UpdateSubresource(ref modelData, _modelCB);

            // Rebuild side-face geometry when depth or view type changes
            if (_loadingComplete && _srvSideFace != null &&
                Math.Abs(_depth - _lastDepthForSideGeom) > 0.0001f)
            {
                BuildSideFaceGeometry(_dr.D3DDevice);
                _lastDepthForSideGeom = _depth;
            }
        }

        /// <summary>
        /// Renders the sprite. Call after ProductBoxRenderer.Render() so transparent
        /// sprite areas show the underlying box geometry.
        /// </summary>
        public void Render()
        {
            if (!_loadingComplete || _srvTexture == null) return;

            var ctx = _dr.D3DDeviceContext;

            bool useDisplaced = _hasDisplacement && _srvDisplacement != null
                                                 && _vbGrid != null;

            // ── Update sprite constant buffer ─────────────────────────────
            var sd = new SpriteCbData
            {
                WhiteThreshold = _whiteThreshold,
                WhiteSoftness  = _whiteSoftness,
                Opacity        = 1.0f,
                DepthScale     = _depth,
                ContentBounds  = _contentBounds,
            };
            ctx.UpdateSubresource(ref sd, _spriteCB);

            // ── Pipeline state ────────────────────────────────────────────
            ctx.OutputMerger.SetBlendState(_blendState);
            ctx.OutputMerger.SetDepthStencilState(_depthState);
            ctx.Rasterizer.State = _rasterizerState;
            ctx.InputAssembler.PrimitiveTopology =
                SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            ctx.InputAssembler.InputLayout = _inputLayout;

            // ── Vertex / index buffers ────────────────────────────────────
            int stride = Marshal.SizeOf<VertexPosUV>();

            if (useDisplaced)
            {
                ctx.InputAssembler.SetVertexBuffers(
                    0, new VertexBufferBinding(_vbGrid, stride, 0));
                ctx.InputAssembler.SetIndexBuffer(_ibGrid, Format.R16_UInt, 0);
            }
            else
            {
                ctx.InputAssembler.SetVertexBuffers(
                    0, new VertexBufferBinding(_vbQuad, stride, 0));
                ctx.InputAssembler.SetIndexBuffer(_ibQuad, Format.R16_UInt, 0);
            }

            // ── Vertex shader ─────────────────────────────────────────────
            if (useDisplaced)
            {
                ctx.VertexShader.SetShader(_vsDisplaced, null, 0);
                ctx.VertexShader.SetShaderResource(1, _srvDisplacement);
                ctx.VertexShader.SetSampler(1, _pointSampler);
            }
            else
            {
                ctx.VertexShader.SetShader(_vsBasic, null, 0);
                ctx.VertexShader.SetShaderResource(1, null);
            }
            ctx.VertexShader.SetConstantBuffers(0, _modelCB);
            ctx.VertexShader.SetConstantBuffers(2, _spriteCB);

            // ── Geometry shader (non-VPRT only) ───────────────────────────
            if (!_usingVprt)
            {
                var gs = useDisplaced ? _gsDisplaced : _gsBasic;
                ctx.GeometryShader.SetShader(gs, null, 0);
            }
            else
            {
                ctx.GeometryShader.SetShader(null, null, 0);
            }

            // ── Pixel shader ──────────────────────────────────────────────
            var ps = useDisplaced ? _psDisplaced : _psBasic;
            ctx.PixelShader.SetShader(ps, null, 0);
            ctx.PixelShader.SetShaderResource(0, _srvTexture);
            ctx.PixelShader.SetSampler(0, _linearSampler);
            ctx.PixelShader.SetConstantBuffers(2, _spriteCB);

            // ── Draw ──────────────────────────────────────────────────────
            int indexCount = useDisplaced ? GridN * GridN * 6 : 6;
            ctx.DrawIndexedInstanced(indexCount, 2, 0, 0, 0);

            // ── Side face draw (3/4 view) ─────────────────────────────────
            if (_srvSideFace != null && _vbSide != null)
                RenderSideFace(ctx);

            // ── Restore default render state ──────────────────────────────
            ctx.OutputMerger.SetBlendState(null);
            ctx.OutputMerger.SetDepthStencilState(null);
            ctx.Rasterizer.State = null;
            ctx.GeometryShader.SetShader(null, null, 0);
            ctx.VertexShader.SetShaderResource(1, null);
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
                // ── Vertex shaders ────────────────────────────────────────
                // Basic: reuse the existing texture VS (already compiled).
                string vsBasicFile = _usingVprt
                    ? "Content\\Shaders\\TextureVertexShader.cso"
                    : "Content\\Shaders\\TextureVertexShaderNoVPRT.cso";

                var vsBasicBytes = await DirectXHelper.ReadDataAsync(
                    await folder.GetFileAsync(vsBasicFile));
                _vsBasic = this.ToDispose(new VertexShader(device, vsBasicBytes));

                // Displaced VS (new shaders).
                string vsDispFile = _usingVprt
                    ? "Content\\Shaders\\SpriteDisplacedVertexShader.cso"
                    : "Content\\Shaders\\SpriteDisplacedVertexShaderNoVPRT.cso";

                var vsDispBytes = await DirectXHelper.ReadDataAsync(
                    await folder.GetFileAsync(vsDispFile));
                _vsDisplaced = this.ToDispose(new VertexShader(device, vsDispBytes));

                // ── Input layout (from displaced VS bytes; identical for both VS) ──
                var elements = new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float,  0, 0,
                        InputClassification.PerVertexData, 0),
                    new InputElement("TEXCOORD", 0, Format.R32G32_Float,    12, 0,
                        InputClassification.PerVertexData, 0),
                };
                _inputLayout = this.ToDispose(
                    new InputLayout(device, vsDispBytes, elements));

                // ── Geometry shaders (non-VPRT only) ─────────────────────
                if (!_usingVprt)
                {
                    var gsBasicBytes = await DirectXHelper.ReadDataAsync(
                        await folder.GetFileAsync("Content\\Shaders\\TextureGeometryShader.cso"));
                    _gsBasic = this.ToDispose(new GeometryShader(device, gsBasicBytes));

                    var gsDispBytes = await DirectXHelper.ReadDataAsync(
                        await folder.GetFileAsync("Content\\Shaders\\SpriteDisplacedGeometryShader.cso"));
                    _gsDisplaced = this.ToDispose(new GeometryShader(device, gsDispBytes));
                }

                // ── Pixel shaders ─────────────────────────────────────────
                var psBasicBytes = await DirectXHelper.ReadDataAsync(
                    await folder.GetFileAsync("Content\\Shaders\\SpritePixelShader.cso"));
                _psBasic = this.ToDispose(new PixelShader(device, psBasicBytes));

                var psDispBytes = await DirectXHelper.ReadDataAsync(
                    await folder.GetFileAsync("Content\\Shaders\\SpriteDisplacedPixelShader.cso"));
                _psDisplaced = this.ToDispose(new PixelShader(device, psDispBytes));

                // ── Constant buffers ──────────────────────────────────────
                var modelData  = new ModelCbData();
                var spriteData = new SpriteCbData
                {
                    WhiteThreshold = _whiteThreshold,
                    WhiteSoftness  = _whiteSoftness,
                    Opacity        = 1.0f,
                    DepthScale     = _depth,
                };

                _modelCB  = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                    device, BindFlags.ConstantBuffer, ref modelData));
                _spriteCB = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                    device, BindFlags.ConstantBuffer, ref spriteData));

                // ── Samplers ──────────────────────────────────────────────
                var linearDesc = new SamplerStateDescription
                {
                    Filter             = Filter.MinMagMipLinear,
                    AddressU           = TextureAddressMode.Clamp,
                    AddressV           = TextureAddressMode.Clamp,
                    AddressW           = TextureAddressMode.Clamp,
                    MaximumAnisotropy  = 1,
                    MinimumLod         = 0,
                    MaximumLod         = float.MaxValue,
                    MipLodBias         = 0,
                    BorderColor        = new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0),
                    ComparisonFunction = Comparison.Never,
                };
                _linearSampler = this.ToDispose(new SamplerState(device, linearDesc));

                var pointDesc = linearDesc;
                pointDesc.Filter = Filter.MinMagMipPoint;
                _pointSampler = this.ToDispose(new SamplerState(device, pointDesc));

                // ── Blend state (premultiplied alpha) ─────────────────────
                var blendDesc = new BlendStateDescription();
                blendDesc.RenderTarget[0].IsBlendEnabled        = true;
                blendDesc.RenderTarget[0].SourceBlend           = BlendOption.One;        // premultiplied src
                blendDesc.RenderTarget[0].DestinationBlend      = BlendOption.InverseSourceAlpha;
                blendDesc.RenderTarget[0].BlendOperation        = BlendOperation.Add;
                blendDesc.RenderTarget[0].SourceAlphaBlend      = BlendOption.One;
                blendDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
                blendDesc.RenderTarget[0].AlphaBlendOperation   = BlendOperation.Add;
                blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
                _blendState = this.ToDispose(new BlendState(device, blendDesc));

                // ── Depth stencil state (read-only; no depth write for overlay) ──
                var dsDesc = new DepthStencilStateDescription
                {
                    IsDepthEnabled  = true,
                    DepthWriteMask  = DepthWriteMask.Zero,          // don't overwrite depth
                    DepthComparison = Comparison.LessEqual,
                    IsStencilEnabled = false,
                };
                _depthState = this.ToDispose(new DepthStencilState(device, dsDesc));

                // ── Rasterizer state (no culling — winding is reliable from user's viewing angle) ──
                var rsDesc = new RasterizerStateDescription
                {
                    CullMode                 = CullMode.None,
                    FillMode                 = FillMode.Solid,
                    IsFrontCounterClockwise  = false,
                    IsDepthClipEnabled       = true,
                    IsMultisampleEnabled     = false,
                    IsAntialiasedLineEnabled = false,
                    IsScissorEnabled         = false,
                    DepthBias                = 0,
                    DepthBiasClamp           = 0f,
                    SlopeScaledDepthBias     = 0f,
                };
                _rasterizerState = this.ToDispose(new RasterizerState(device, rsDesc));

                // ── Geometry ──────────────────────────────────────────────
                BuildQuadGeometry(device);
                BuildGridGeometry(device);

                _loadingComplete = true;
                Debug.WriteLine("[Sprite] Device resources ready (VPRT=" + _usingVprt + ")");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Sprite] CreateDeviceDependentResources failed: " + ex.Message);
            }
        }

        public void ReleaseDeviceDependentResources()
        {
            _loadingComplete = false;
            this.RemoveAndDispose(ref _vsBasic);
            this.RemoveAndDispose(ref _vsDisplaced);
            this.RemoveAndDispose(ref _gsBasic);
            this.RemoveAndDispose(ref _gsDisplaced);
            this.RemoveAndDispose(ref _psBasic);
            this.RemoveAndDispose(ref _psDisplaced);
            this.RemoveAndDispose(ref _inputLayout);
            this.RemoveAndDispose(ref _modelCB);
            this.RemoveAndDispose(ref _spriteCB);
            this.RemoveAndDispose(ref _linearSampler);
            this.RemoveAndDispose(ref _pointSampler);
            this.RemoveAndDispose(ref _blendState);
            this.RemoveAndDispose(ref _rasterizerState);
            this.RemoveAndDispose(ref _depthState);
            this.RemoveAndDispose(ref _vbQuad);
            this.RemoveAndDispose(ref _ibQuad);
            this.RemoveAndDispose(ref _vbGrid);
            this.RemoveAndDispose(ref _ibGrid);
            _vbSide?.Dispose(); _vbSide = null;
            _ibSide?.Dispose(); _ibSide = null;
            _sideCB?.Dispose(); _sideCB = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Geometry builders
        // ─────────────────────────────────────────────────────────────────────

        private void BuildQuadGeometry(SharpDX.Direct3D11.Device device)
        {
            float z = FrontFaceZ;
            var verts = new[]
            {
                new VertexPosUV { Position = new Vector3(-0.5f, -0.5f, z), UV = new Vector2(0f, 1f) }, // 0 BL
                new VertexPosUV { Position = new Vector3( 0.5f, -0.5f, z), UV = new Vector2(1f, 1f) }, // 1 BR
                new VertexPosUV { Position = new Vector3(-0.5f,  0.5f, z), UV = new Vector2(0f, 0f) }, // 2 TL
                new VertexPosUV { Position = new Vector3( 0.5f,  0.5f, z), UV = new Vector2(1f, 0f) }, // 3 TR
            };
            // CCW from +Z (front face toward +Z, toward camera — project uses FrontCounterClockwise=TRUE):
            // {0,2,3} and {0,3,1} make each triangle wind CCW when viewed from the +Z side.
            var indices = new ushort[] { 0, 2, 3, 0, 3, 1 };

            _vbQuad = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                device, BindFlags.VertexBuffer, verts));
            _ibQuad = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                device, BindFlags.IndexBuffer, indices));
        }

        /// <summary>
        /// 65×65 grid covering the same front face.  Displacement is applied in the VS.
        /// Index count = GridN × GridN × 6 = 64×64×6 = 24 576.
        /// All indices fit in UINT16 (max index = 65×65−1 = 4224 &lt; 65535).
        /// </summary>
        private void BuildGridGeometry(SharpDX.Direct3D11.Device device)
        {
            int n    = GridN;
            int nv   = n + 1;   // vertices per side
            float z  = FrontFaceZ;

            var verts = new VertexPosUV[nv * nv];
            for (int row = 0; row <= n; ++row)
            for (int col = 0; col <= n; ++col)
            {
                float u = col / (float)n;
                float v = row / (float)n;
                // x: -0.5 … +0.5; y: +0.5 … -0.5 (row 0 = top = +Y = uv.v=0)
                verts[row * nv + col] = new VertexPosUV
                {
                    Position = new Vector3(u - 0.5f, 0.5f - v, z),
                    UV       = new Vector2(u, v),
                };
            }

            var indices = new ushort[n * n * 6];
            int idx = 0;
            for (int row = 0; row < n; ++row)
            for (int col = 0; col < n; ++col)
            {
                ushort tl = (ushort)(row       * nv + col);
                ushort tr = (ushort)(row       * nv + col + 1);
                ushort bl = (ushort)((row + 1) * nv + col);
                ushort br = (ushort)((row + 1) * nv + col + 1);
                // CW in screen space (Y-down, FCC=false default): tl→tr→br and tl→br→bl
                // These match the quad's {0,2,3} and {0,3,1} CW winding seen from +Z (user's side).
                indices[idx++] = tl; indices[idx++] = tr; indices[idx++] = br;
                indices[idx++] = tl; indices[idx++] = br; indices[idx++] = bl;
            }

            _vbGrid = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                device, BindFlags.VertexBuffer, verts));
            _ibGrid = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                device, BindFlags.IndexBuffer, indices));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Side-face rendering
        // ─────────────────────────────────────────────────────────────────────

        private void RenderSideFace(DeviceContext ctx)
        {
            // Reuse the basic (flat-quad) shader pipeline — no displacement on side face.
            ctx.VertexShader.SetShader(_vsBasic, null, 0);
            ctx.VertexShader.SetConstantBuffers(0, _modelCB);
            ctx.VertexShader.SetConstantBuffers(2, _sideCB);
            ctx.VertexShader.SetShaderResource(1, null);

            if (!_usingVprt)
                ctx.GeometryShader.SetShader(_gsBasic, null, 0);

            ctx.PixelShader.SetShader(_psBasic, null, 0);
            ctx.PixelShader.SetShaderResource(0, _srvSideFace);
            ctx.PixelShader.SetSampler(0, _linearSampler);
            ctx.PixelShader.SetConstantBuffers(2, _sideCB);

            // Side texture is pre-cropped; full UV, no white-removal needed (it's already clean)
            var sideParams = new SpriteCbData
            {
                WhiteThreshold = _whiteThreshold,
                WhiteSoftness  = _whiteSoftness,
                Opacity        = 1f,
                DepthScale     = 0f,
                ContentBounds  = new Vector4(0f, 0f, 1f, 1f),
            };
            ctx.UpdateSubresource(ref sideParams, _sideCB);

            int stride = Marshal.SizeOf<VertexPosUV>();
            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vbSide, stride, 0));
            ctx.InputAssembler.SetIndexBuffer(_ibSide, Format.R16_UInt, 0);
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;

            ctx.DrawIndexedInstanced(6, 2, 0, 0, 0);

            // Restore front-face texture so post-draw state is clean
            ctx.PixelShader.SetShaderResource(0, _srvTexture);
        }

        private void BuildSideFaceGeometry(SharpDX.Direct3D11.Device device)
        {
            const float clearance = 0.501f;  // just outside the unit-cube face

            VertexPosUV[] verts;

            if (_viewType == ViewType.ThreeQuarterRight)
            {
                // Right face (+X): U=0 at front (z=+0.5), U=1 at back (z=-0.5)
                verts = new[]
                {
                    new VertexPosUV { Position = new Vector3( clearance, -0.5f,  0.5f), UV = new Vector2(0f, 1f) }, // BF
                    new VertexPosUV { Position = new Vector3( clearance,  0.5f,  0.5f), UV = new Vector2(0f, 0f) }, // TF
                    new VertexPosUV { Position = new Vector3( clearance, -0.5f, -0.5f), UV = new Vector2(1f, 1f) }, // BB
                    new VertexPosUV { Position = new Vector3( clearance,  0.5f, -0.5f), UV = new Vector2(1f, 0f) }, // TB
                };
            }
            else  // ThreeQuarterLeft → left face (−X)
            {
                verts = new[]
                {
                    new VertexPosUV { Position = new Vector3(-clearance, -0.5f, -0.5f), UV = new Vector2(0f, 1f) },
                    new VertexPosUV { Position = new Vector3(-clearance,  0.5f, -0.5f), UV = new Vector2(0f, 0f) },
                    new VertexPosUV { Position = new Vector3(-clearance, -0.5f,  0.5f), UV = new Vector2(1f, 1f) },
                    new VertexPosUV { Position = new Vector3(-clearance,  0.5f,  0.5f), UV = new Vector2(1f, 0f) },
                };
            }

            var indices = new ushort[] { 0, 2, 3, 0, 3, 1 };

            _vbSide?.Dispose();
            _ibSide?.Dispose();

            _vbSide = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, verts);
            _ibSide = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, indices);

            if (_sideCB == null)
            {
                var dummy = new SpriteCbData();
                _sideCB = SharpDX.Direct3D11.Buffer.Create(
                    device, BindFlags.ConstantBuffer, ref dummy);
            }
        }
    }
}
