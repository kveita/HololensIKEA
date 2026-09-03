using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using HololensIKEA.Common;
using HololensIKEA.Models;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Renders a holographic bookmarks dialog panel showing available IKEA products.
    /// The user selects a product by gazing at it and air-tapping.
    /// </summary>
    internal sealed class BookmarksDialog : Disposer
    {
        // ─────────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────────

        private const int   TEX_W = 1024;
        private const int   TEX_H = 768;
        private const float PANEL_WIDTH  = 0.7f;
        private const float PANEL_HEIGHT = 0.9f;
        private const float ROW_HEIGHT_PX = 70f;
        private const float HEADER_HEIGHT_PX = 64f;
        private const int   MAX_VISIBLE = 10;

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

        private SharpDX.Direct3D11.Buffer _vb;
        private SharpDX.Direct3D11.Buffer _ib;
        private SharpDX.Direct3D11.Buffer _cbModel;

        private Texture2D              _texture;
        private ShaderResourceView     _srv;
        private SharpDX.Direct2D1.RenderTarget _d2dTarget;

        // DirectWrite
        private SharpDX.DirectWrite.TextFormat _headerFont;
        private SharpDX.DirectWrite.TextFormat _itemFont;
        private SharpDX.DirectWrite.TextFormat _subFont;
        private SharpDX.Direct2D1.SolidColorBrush _brushWhite;
        private SharpDX.Direct2D1.SolidColorBrush _brushHighlight;
        private SharpDX.Direct2D1.SolidColorBrush _brushDim;
        private SharpDX.Direct2D1.SolidColorBrush _brushBg;
        private SharpDX.Direct2D1.SolidColorBrush _brushRowBg;
        private SharpDX.Direct2D1.SolidColorBrush _brushRowHover;

        // State
        private bool _loadingComplete = false;
        private bool _usingVprt = false;
        private bool _visible = false;
        private bool _labelsDirty = true;
        private int  _hoveredIndex = -1;
        private List<Bookmark> _bookmarks = new List<Bookmark>();

        // Collapse / drag state
        private bool _collapsed = false;
        private bool _hoveredCollapseButton = false;

        private Vector3    _position = new Vector3(0, 0, -1.5f);
        private Quaternion _rotation = Quaternion.Identity;

        // ─────────────────────────────────────────────────────────────────────
        // Events
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Fires when the user selects a bookmark (passes the bookmark).</summary>
        public event Action<Bookmark> OnBookmarkSelected;

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

        public BookmarksDialog(DeviceResources deviceResources)
        {
            _dr = deviceResources;
            CreateDeviceDependentResourcesAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        public bool IsVisible => _visible;

        /// <summary>Current world position of the panel (for drag offset calculations).</summary>
        public Vector3 Position => _position;

        /// <summary>True while the gaze ray is over the draggable title bar (not the collapse button).</summary>
        public bool IsGazeOnTitleBar { get; private set; }

        /// <summary>Repositions the panel, e.g. while the user is dragging it.</summary>
        public void SetPosition(Vector3 position) => _position = position;

        /// <summary>
        /// Shows the dialog with the given bookmarks. If the panel is already
        /// visible, only its contents are refreshed — position and collapsed
        /// state are preserved so the panel stays where the user left it.
        /// </summary>
        public void Show(List<Bookmark> bookmarks)
        {
            _bookmarks = bookmarks ?? new List<Bookmark>();
            if (!_visible)
            {
                _position = new Vector3(0, 0, -1.5f);
                _rotation = Quaternion.Identity;
                _collapsed = false;
            }
            _hoveredIndex = -1;
            _visible = true;
            _labelsDirty = true;
            Debug.WriteLine($"[Bookmarks] Showing {_bookmarks.Count} bookmarks");
        }

        /// <summary>Hides the dialog.</summary>
        public void Hide()
        {
            _visible = false;
            _hoveredIndex = -1;
            _bookmarks.Clear();
        }

        /// <summary>
        /// Updates the gaze highlight. Call with the gaze ray each frame.
        /// Returns the index of the hovered bookmark, or -1 if none.
        /// </summary>
        public int UpdateGaze(Vector3 gazeOrigin, Vector3 gazeDir)
        {
            if (!_visible)
            {
                SetHoveredIndex(-1);
                _hoveredCollapseButton = false;
                IsGazeOnTitleBar = false;
                return -1;
            }

            // Hit-test the panel plane
            var panelNormal = Vector3.Transform(new Vector3(0, 0, -1), _rotation);
            float denom = Vector3.Dot(panelNormal, gazeDir);
            if (Math.Abs(denom) < 1e-6f)
            {
                SetHoveredIndex(-1);
                _hoveredCollapseButton = false;
                IsGazeOnTitleBar = false;
                return -1;
            }

            float t = Vector3.Dot(_position - gazeOrigin, panelNormal) / denom;
            if (t < 0 || t > 5f)
            {
                SetHoveredIndex(-1);
                _hoveredCollapseButton = false;
                IsGazeOnTitleBar = false;
                return -1;
            }

            var hitWorld = gazeOrigin + gazeDir * t;
            var localHit = Vector3.Transform(hitWorld - _position, Quaternion.Inverse(_rotation));

            // Convert to UV coordinates
            float u = (localHit.X / PANEL_WIDTH) + 0.5f;
            float v = 0.5f - (localHit.Y / PANEL_HEIGHT);

            if (u < 0 || u > 1 || v < 0 || v > 1)
            {
                SetHoveredIndex(-1);
                _hoveredCollapseButton = false;
                IsGazeOnTitleBar = false;
                return -1;
            }

            // Header area: either the collapse button (top-right) or the
            // draggable title bar (the rest of the header).
            float headerFraction = HEADER_HEIGHT_PX / TEX_H;
            if (v < headerFraction)
            {
                float btnULo = (TEX_W - 56f) / TEX_W;
                float btnVLo = 4f / TEX_H;
                float btnVHi = (HEADER_HEIGHT_PX - 4f) / TEX_H;
                bool onButton = u >= btnULo && v >= btnVLo && v <= btnVHi;
                _hoveredCollapseButton = onButton;
                IsGazeOnTitleBar = !onButton;
                SetHoveredIndex(-1);
                return -1;
            }
            _hoveredCollapseButton = false;
            IsGazeOnTitleBar = false;

            if (_collapsed || _bookmarks.Count == 0)
            {
                SetHoveredIndex(-1);
                return -1;
            }

            float rowFraction = ROW_HEIGHT_PX / TEX_H;
            int rowIndex = (int)((v - headerFraction) / rowFraction);

            int visibleCount = Math.Min(_bookmarks.Count, MAX_VISIBLE);
            if (rowIndex >= 0 && rowIndex < visibleCount)
            {
                SetHoveredIndex(rowIndex);
                return rowIndex;
            }

            SetHoveredIndex(-1);
            return -1;
        }

        /// <summary>
        /// Handles air-tap selection: toggles the collapse button if hovered,
        /// otherwise selects the hovered row. The panel is intentionally left
        /// visible after a selection so the user can pick another product.
        /// </summary>
        public bool HandleAirTap()
        {
            if (!_visible)
                return false;

            if (_hoveredCollapseButton)
            {
                _collapsed = !_collapsed;
                _labelsDirty = true;
                Debug.WriteLine(_collapsed ? "[Bookmarks] Collapsed" : "[Bookmarks] Expanded");
                return true;
            }

            if (_hoveredIndex < 0 || _hoveredIndex >= _bookmarks.Count)
                return false;

            var selected = _bookmarks[_hoveredIndex];
            Debug.WriteLine($"[Bookmarks] Selected: {selected.Name}");
            OnBookmarkSelected?.Invoke(selected);
            return true;
        }

        /// <summary>Must be called once per frame before Render().</summary>
        public void Update()
        {
            if (!_loadingComplete || !_visible)
                return;

            if (_labelsDirty)
            {
                RenderToTexture();
                _labelsDirty = false;
            }
        }

        /// <summary>Renders the bookmarks panel.</summary>
        public void Render()
        {
            if (!_loadingComplete || !_visible || _srv == null)
                return;

            var ctx = _dr.D3DDeviceContext;

            // Update model constant buffer
            var model = Matrix4x4.Transpose(
                Matrix4x4.CreateScale(PANEL_WIDTH, PANEL_HEIGHT, 1f) *
                Matrix4x4.CreateFromQuaternion(_rotation) *
                Matrix4x4.CreateTranslation(_position));
            var cbData = new ModelConstantBuffer { model = model };
            ctx.UpdateSubresource(ref cbData, _cbModel);

            // Set state
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
            ctx.PixelShader.SetShaderResource(0, _srv);
            ctx.PixelShader.SetSampler(0, _sampler);

            ctx.DrawIndexedInstanced(6, 2, 0, 0, 0);

            // Restore
            ctx.OutputMerger.SetBlendState(null);
            ctx.OutputMerger.SetDepthStencilState(null);
            ctx.GeometryShader.SetShader(null, null, 0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private void SetHoveredIndex(int index)
        {
            if (_hoveredIndex != index)
            {
                _hoveredIndex = index;
                _labelsDirty = true;
            }
        }

        private void RenderToTexture()
        {
            if (_d2dTarget == null) return;

            _d2dTarget.BeginDraw();
            var panelBg = new SharpDX.Mathematics.Interop.RawColor4(0.05f, 0.05f, 0.15f, 0.92f);
            if (_collapsed)
            {
                // Fully transparent below the header so the collapsed panel
                // only occupies visual space for its title bar.
                _d2dTarget.Clear(new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0));
                var headerBgRect = new SharpDX.Mathematics.Interop.RawRectangleF(0, 0, TEX_W, HEADER_HEIGHT_PX);
                _d2dTarget.FillRectangle(headerBgRect, _brushBg);
            }
            else
            {
                _d2dTarget.Clear(panelBg);
            }

            // Header
            var headerRect = new SharpDX.Mathematics.Interop.RawRectangleF(
                20, 0, TEX_W - 90, HEADER_HEIGHT_PX);
            string headerText = _bookmarks.Count > 0
                ? $"IKEA bookmarks ({_bookmarks.Count})"
                : "No Bookmarks";
            _d2dTarget.DrawText(headerText, _headerFont, headerRect, _brushWhite);

            // Collapse / expand button (top-right of header)
            var buttonRect = new SharpDX.Mathematics.Interop.RawRectangleF(
                TEX_W - 56, 4, TEX_W - 8, HEADER_HEIGHT_PX - 4);
            _d2dTarget.FillRectangle(buttonRect, _hoveredCollapseButton ? _brushRowHover : _brushRowBg);
            string buttonGlyph = _collapsed ? "+" : "\u2212";
            _d2dTarget.DrawText(buttonGlyph, _headerFont, buttonRect, _brushWhite);

            // Draw separator line
            _d2dTarget.DrawLine(
                new SharpDX.Mathematics.Interop.RawVector2(10, HEADER_HEIGHT_PX - 2),
                new SharpDX.Mathematics.Interop.RawVector2(TEX_W - 10, HEADER_HEIGHT_PX - 2),
                _brushDim, 2f);

            if (_collapsed)
            {
                _d2dTarget.EndDraw();
                return;
            }

            // Bookmarks
            int visibleCount = Math.Min(_bookmarks.Count, MAX_VISIBLE);
            for (int i = 0; i < visibleCount; i++)
            {
                float y = HEADER_HEIGHT_PX + i * ROW_HEIGHT_PX;
                var rowRect = new SharpDX.Mathematics.Interop.RawRectangleF(0, y, TEX_W, y + ROW_HEIGHT_PX);

                // Row background (highlight on hover)
                var rowBrush = (i == _hoveredIndex) ? _brushRowHover :
                               (i % 2 == 0) ? _brushRowBg : _brushBg;
                _d2dTarget.FillRectangle(rowRect, rowBrush);

                // Top bar — draggable target indicator (brighter when gazed)
                var topBarH = 4f;
                var topBarBrush = (i == _hoveredIndex)
                    ? new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                        new SharpDX.Mathematics.Interop.RawColor4(0.4f, 0.9f, 1f, 1f))
                    : new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                        new SharpDX.Mathematics.Interop.RawColor4(0.2f, 0.4f, 0.6f, 0.7f));
                var topBarRect = new SharpDX.Mathematics.Interop.RawRectangleF(0, y, TEX_W, y + topBarH);
                _d2dTarget.FillRectangle(topBarRect, topBarBrush);
                topBarBrush.Dispose();

                // Row number
                var numRect = new SharpDX.Mathematics.Interop.RawRectangleF(10, y + 4, 50, y + ROW_HEIGHT_PX - 4);
                _d2dTarget.DrawText($"{i + 1}.", _itemFont, numRect, _brushDim);

                // Product name
                var bookmark = _bookmarks[i];
                string name = bookmark.Name ?? "";
                if (name.Length > 50) name = name.Substring(0, 47) + "...";

                var textRect = new SharpDX.Mathematics.Interop.RawRectangleF(55, y + 2, TEX_W - 20, y + 36);
                _d2dTarget.DrawText(name, _itemFont, textRect,
                    i == _hoveredIndex ? _brushHighlight : _brushWhite);

                // URL hint (truncated)
                string urlHint = bookmark.Url ?? "";
                if (urlHint.Length > 40) urlHint = urlHint.Substring(0, 37) + "...";
                var urlRect = new SharpDX.Mathematics.Interop.RawRectangleF(55, y + 32, TEX_W - 20, y + ROW_HEIGHT_PX - 2);
                _d2dTarget.DrawText(urlHint, _subFont, urlRect, _brushDim);
            }

            // Instruction at bottom
            if (_bookmarks.Count > 0)
            {
                float instrY = HEADER_HEIGHT_PX + (float)visibleCount * ROW_HEIGHT_PX + 8;
                var instrRect = new SharpDX.Mathematics.Interop.RawRectangleF(20, instrY, TEX_W - 20, instrY + 30);
                _d2dTarget.DrawText("Air-tap a product to place it  |  Drag the title bar to move  |  Tap \u2212 to collapse",
                    _subFont, instrRect, _brushDim);
            }

            _d2dTarget.EndDraw();
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
                // Shaders
                var vsFile = _usingVprt
                    ? "Content\\\\Shaders\\\\TextureVertexShader.cso"
                    : "Content\\\\Shaders\\\\TextureVertexShaderNoVPRT.cso";
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
                        await folder.GetFileAsync("Content\\\\Shaders\\\\TextureGeometryShader.cso"));
                    _gs = this.ToDispose(new GeometryShader(device, gsBytes));
                }

                var psBytes = await DirectXHelper.ReadDataAsync(
                    await folder.GetFileAsync("Content\\\\Shaders\\\\TexturePixelShader.cso"));
                _ps = this.ToDispose(new PixelShader(device, psBytes));

                // Quad geometry
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

                // Constant buffer
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

                // Blend state
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

                // Depth state
                var depthDesc = new DepthStencilStateDescription
                {
                    IsDepthEnabled = true,
                    DepthWriteMask = DepthWriteMask.Zero,
                    DepthComparison = Comparison.LessEqual,
                };
                _depthState = this.ToDispose(new DepthStencilState(device, depthDesc));

                // Texture + D2D target
                var texDesc = new Texture2DDescription
                {
                    Width = TEX_W,
                    Height = TEX_H,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                };
                _texture = this.ToDispose(new Texture2D(device, texDesc));
                _srv = this.ToDispose(new ShaderResourceView(device, _texture));

                using (var surf = _texture.QueryInterface<SharpDX.DXGI.Surface>())
                {
                    var rtProps = new SharpDX.Direct2D1.RenderTargetProperties(
                        SharpDX.Direct2D1.RenderTargetType.Default,
                        new SharpDX.Direct2D1.PixelFormat(Format.Unknown, SharpDX.Direct2D1.AlphaMode.Premultiplied),
                        96f, 96f,
                        SharpDX.Direct2D1.RenderTargetUsage.None,
                        SharpDX.Direct2D1.FeatureLevel.Level_DEFAULT);
                    _d2dTarget = this.ToDispose(
                        new SharpDX.Direct2D1.RenderTarget(_dr.D2DFactory, surf, rtProps));
                }

                // Fonts
                _headerFont = this.ToDispose(new SharpDX.DirectWrite.TextFormat(
                    _dr.DWriteFactory, "Segoe UI",
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 36f));
                _headerFont.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                _headerFont.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                _itemFont = this.ToDispose(new SharpDX.DirectWrite.TextFormat(
                    _dr.DWriteFactory, "Segoe UI",
                    SharpDX.DirectWrite.FontWeight.SemiBold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 26f));
                _itemFont.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                _itemFont.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                _subFont = this.ToDispose(new SharpDX.DirectWrite.TextFormat(
                    _dr.DWriteFactory, "Segoe UI",
                    SharpDX.DirectWrite.FontWeight.Normal,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 18f));
                _subFont.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                _subFont.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                // Brushes
                _brushWhite = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                    new SharpDX.Mathematics.Interop.RawColor4(1f, 1f, 1f, 1f)));
                _brushHighlight = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                    new SharpDX.Mathematics.Interop.RawColor4(0.3f, 0.8f, 1f, 1f)));
                _brushDim = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                    new SharpDX.Mathematics.Interop.RawColor4(0.6f, 0.6f, 0.7f, 1f)));
                _brushBg = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                    new SharpDX.Mathematics.Interop.RawColor4(0.05f, 0.05f, 0.15f, 0.92f)));
                _brushRowBg = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                    new SharpDX.Mathematics.Interop.RawColor4(0.08f, 0.08f, 0.2f, 0.95f)));
                _brushRowHover = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(_d2dTarget,
                    new SharpDX.Mathematics.Interop.RawColor4(0.15f, 0.25f, 0.6f, 0.98f)));

                _loadingComplete = true;
                Debug.WriteLine("[Bookmarks] Resources created");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Bookmarks] Init failed: " + ex.Message);
            }
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

            _brushWhite?.Dispose(); _brushWhite = null;
            _brushHighlight?.Dispose(); _brushHighlight = null;
            _brushDim?.Dispose(); _brushDim = null;
            _brushBg?.Dispose(); _brushBg = null;
            _brushRowBg?.Dispose(); _brushRowBg = null;
            _brushRowHover?.Dispose(); _brushRowHover = null;
            _headerFont?.Dispose(); _headerFont = null;
            _itemFont?.Dispose(); _itemFont = null;
            _subFont?.Dispose(); _subFont = null;
            _d2dTarget?.Dispose(); _d2dTarget = null;
            _srv?.Dispose(); _srv = null;
            _texture?.Dispose(); _texture = null;
        }
    }
}
