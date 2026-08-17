using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using HololensIKEA.Common;
using SharpDX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Holographic virtual keypad for HoloLens 1 with full Direct3D rendering.
    /// Renders colored quads for keyboard panel and keys using the standard shader pipeline.
    /// Supports gaze-based highlighting and air-tap selection.
    /// </summary>
    internal class KeyboardInputHandler : Disposer
    {
        private const float PANEL_WIDTH        = 0.60f;
        private const float KEY_AREA_HEIGHT    = 0.22f;   // height used for the 4 numpad rows
        private const float INPUT_BAR_HEIGHT_M = 0.06f;   // input-bar strip above the keys
        private const float PANEL_HEIGHT       = KEY_AREA_HEIGHT + INPUT_BAR_HEIGHT_M; // 0.28 m total
        private const float PANEL_DISTANCE     = 0.75f;
        private const float PANEL_DROP         = 0.15f;
        private const float KEY_WIDTH          = 0.050f;
        private const float KEY_HEIGHT         = 0.044f;
        private const float KEY_GAP            = 0.004f;
        private const float CURSOR_SIZE        = 0.014f;  // gaze-cursor square size (metres)

        private struct VirtualKey
        {
            public string label;
            public string value;
            public bool isSpecial;
            public float localX;
            public float localY;
            public float width;
            public float height;
        }

        public event Action<string> OnTextChanged;
        public event Action<string> OnSubmit;

        private DeviceResources m_deviceResources;
        private List<VirtualKey> m_keys = new List<VirtualKey>();
        private Matrix4x4 m_panelWorld = Matrix4x4.Identity;
        private Vector3 m_panelNormal = new Vector3(0, 0, -1);

        private bool m_visible = false;
        private string m_inputText = "";
        private int m_cursorPos = 0;
        private int m_hoveredKey = -1;
        private bool m_shiftActive = false;
        private bool m_capsLock = false;

        // Direct3D 11 rendering resources
        private InputLayout m_inputLayout;
        private SharpDX.Direct3D11.Buffer m_vertexBuffer;
        private SharpDX.Direct3D11.Buffer m_indexBuffer;
        private SharpDX.Direct3D11.Buffer m_modelConstantBuffer;
        private VertexShader m_vertexShader;
        private PixelShader m_pixelShader;
        private GeometryShader m_geometryShader;

        private int m_indexCount = 0;
        private bool m_loadingComplete = false;
        private bool m_usingVprtShaders = false;

        // Mutable vertex cache for per-frame color updates (hover/pressed highlighting)
        private VertexPositionColor[] m_vertexData;
        private int m_lastHoveredKey = -2;   // -2 forces first update
        private int m_pressedKey = -1;
        private int m_pressedFlashFrames = 0;

        // Key colors (bright – HoloLens additive display; dark = transparent)
        private static readonly Vector3 COLOR_PANEL    = new Vector3(0.15f, 0.15f, 0.55f);
        private static readonly Vector3 COLOR_NORMAL   = new Vector3(0.70f, 0.70f, 0.85f);
        private static readonly Vector3 COLOR_SPECIAL  = new Vector3(0.50f, 0.50f, 0.70f);
        private static readonly Vector3 COLOR_HOVERED  = new Vector3(1.00f, 0.90f, 0.10f);  // yellow
        private static readonly Vector3 COLOR_PRESSED  = new Vector3(0.20f, 1.00f, 0.20f);  // green
        private static readonly Vector3 COLOR_CURSOR   = new Vector3(1.00f, 1.00f, 0.20f);  // bright yellow

        // Gaze cursor tracking
        private Vector2 m_gazeLocalHit   = new Vector2(0.30f, 0.11f);
        private Vector2 m_lastCursorPos;
        private bool    m_gazeOnPanel    = false;
        private int     m_cursorVertStart;

        // Alpha blend state for D2D texture overlay (premultiplied)
        private BlendState m_blendState;

        // Textured-quad pipeline for D2D label / input-bar overlay
        private SharpDX.Direct3D11.Buffer   m_texVertexBuffer;
        private SharpDX.Direct3D11.Buffer   m_texIndexBuffer;
        private InputLayout                 m_texInputLayout;
        private VertexShader                m_texVertexShader;
        private PixelShader                 m_texPixelShader;
        private GeometryShader              m_texGeometryShader;
        private SharpDX.Direct3D11.Buffer   m_texConstantBuffer;
        private Texture2D                   m_labelTexture;
        private ShaderResourceView          m_labelSrv;
        private SharpDX.Direct2D1.RenderTarget m_d2dRenderTarget;
        private SharpDX.DirectWrite.TextFormat m_dwriteKeyFont;
        private SharpDX.DirectWrite.TextFormat m_dwriteInputFont;
        private SharpDX.Direct2D1.SolidColorBrush m_brushWhite;
        private SharpDX.Direct2D1.SolidColorBrush m_brushGray;
        private SamplerState                m_samplerState;
        private bool                        m_labelsDirty      = true;
        private string                      m_lastRenderedText = null;

        // Texture atlas dimensions (pixels)
        private const int TEX_W       = 1024;
        private const int TEX_H       = 512;
        private const int INPUT_BAR_H = 80;

        // Click-sound feedback
        private MediaPlayer m_clickPlayer;

        public KeyboardInputHandler(DeviceResources deviceResources)
        {
            m_deviceResources = deviceResources;
            BuildKeyLayout();
            // Click sound (silently skipped if file absent)
            try
            {
                m_clickPlayer = new MediaPlayer();
                m_clickPlayer.Source = MediaSource.CreateFromUri(
                    new Uri("ms-appx:///Assets/keyboard-click.wav"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Keyboard] Click sound unavailable: " + ex.Message);
            }
            CreateDeviceDependentResourcesAsync();
        }

        public bool IsVisible => m_visible;
        public string GetCurrentText() => m_inputText;
        public void Show()             { m_visible = true;  m_labelsDirty = true; }
        public void Hide()             { m_visible = false; }
        public void ToggleVisibility() { m_visible = !m_visible; if (m_visible) m_labelsDirty = true; }
        public void ClearText()        { m_inputText = ""; m_cursorPos = 0; m_labelsDirty = true; }

        /// <summary>Inserts text at the current cursor position (used by voice input).</summary>
        public void InsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            m_inputText = m_inputText.Insert(m_cursorPos, text);
            m_cursorPos += text.Length;
            m_labelsDirty = true;
            OnTextChanged?.Invoke(m_inputText);
        }

        /// <summary>Returns true if gaze is currently on the keyboard panel.</summary>
        public bool IsGazeOnPanel => m_gazeOnPanel;

        /// <summary>Initialize GPU resources for Direct3D rendering with full shader pipeline.</summary>
        public async void CreateDeviceDependentResourcesAsync()
        {
            ReleaseDeviceDependentResources();

            m_usingVprtShaders = m_deviceResources.D3DDeviceSupportsVprt;
            var folder = Windows.ApplicationModel.Package.Current.InstalledLocation;

            try
            {
                // Load vertex shader
                var vertexShaderFileName = m_usingVprtShaders ? "Content\\Shaders\\VPRTVertexShader.cso" : "Content\\Shaders\\VertexShader.cso";
                var vertexShaderByteCode = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync(vertexShaderFileName));
                m_vertexShader = this.ToDispose(new VertexShader(m_deviceResources.D3DDevice, vertexShaderByteCode));

                // Create input layout
                SharpDX.Direct3D11.InputElement[] vertexDesc =
                {
                    new SharpDX.Direct3D11.InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float,  0, 0, InputClassification.PerVertexData, 0),
                    new SharpDX.Direct3D11.InputElement("COLOR",    0, SharpDX.DXGI.Format.R32G32B32_Float, 12, 0, InputClassification.PerVertexData, 0),
                };
                m_inputLayout = this.ToDispose(new InputLayout(m_deviceResources.D3DDevice, vertexShaderByteCode, vertexDesc));

                // Load geometry shader if not using VPRT
                if (!m_usingVprtShaders)
                {
                    var geometryShaderByteCode = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\GeometryShader.cso"));
                    m_geometryShader = this.ToDispose(new GeometryShader(m_deviceResources.D3DDevice, geometryShaderByteCode));
                }

                // Load pixel shader
                var pixelShaderByteCode = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\PixelShader.cso"));
                m_pixelShader = this.ToDispose(new PixelShader(m_deviceResources.D3DDevice, pixelShaderByteCode));

                // Create keyboard geometry
                CreateKeyboardGeometry();

                // Create model constant buffer using Default usage (required for UpdateSubresource),
                // matching the exact pattern of SpinningCubeRenderer and ProductBoxRenderer.
                ModelConstantBuffer initialCbData = new ModelConstantBuffer() { model = Matrix4x4.Identity };
                m_modelConstantBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                    m_deviceResources.D3DDevice,
                    SharpDX.Direct3D11.BindFlags.ConstantBuffer,
                    ref initialCbData));

                m_loadingComplete = true;
                Debug.WriteLine("[Keyboard] Direct3D resources created successfully");

                // ---- Textured-quad pipeline for labels ----
                await CreateLabelResourcesAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Keyboard] Failed to create resources: {ex}");
            }
        }

        private void CreateKeyboardGeometry()
        {
            // Build a single keyboard panel as a mesh of colored quads
            List<VertexPositionColor> vertices = new List<VertexPositionColor>();
            List<ushort> indices = new List<ushort>();

            ushort vertexOffset = 0;

            // Panel background quad (z=0).  Bright blue — dark colours are transparent on HoloLens.
            vertices.Add(new VertexPositionColor(new Vector3(0,           0,            0), COLOR_PANEL));
            vertices.Add(new VertexPositionColor(new Vector3(PANEL_WIDTH, 0,            0), COLOR_PANEL));
            vertices.Add(new VertexPositionColor(new Vector3(PANEL_WIDTH, PANEL_HEIGHT, 0), COLOR_PANEL));
            vertices.Add(new VertexPositionColor(new Vector3(0,           PANEL_HEIGHT, 0), COLOR_PANEL));
            indices.AddRange(new ushort[] { 0, 2, 1, 0, 3, 2 });
            vertexOffset = 4;

            // One quad per key, slightly in front of panel (+Z toward user after world transform)
            for (int i = 0; i < m_keys.Count; ++i)
            {
                var k = m_keys[i];
                Vector3 keyColor = k.isSpecial ? COLOR_SPECIAL : COLOR_NORMAL;
                const float Z = 0.002f;

                vertices.Add(new VertexPositionColor(new Vector3(k.localX,           k.localY,            Z), keyColor));
                vertices.Add(new VertexPositionColor(new Vector3(k.localX + k.width, k.localY,            Z), keyColor));
                vertices.Add(new VertexPositionColor(new Vector3(k.localX + k.width, k.localY + k.height, Z), keyColor));
                vertices.Add(new VertexPositionColor(new Vector3(k.localX,           k.localY + k.height, Z), keyColor));

                indices.Add((ushort)(vertexOffset + 0));
                indices.Add((ushort)(vertexOffset + 2));
                indices.Add((ushort)(vertexOffset + 1));
                indices.Add((ushort)(vertexOffset + 0));
                indices.Add((ushort)(vertexOffset + 3));
                indices.Add((ushort)(vertexOffset + 2));
                vertexOffset += 4;
            }

            // Cursor marker quad (4 verts at end; position updated every frame)
            m_cursorVertStart = vertexOffset;
            float cHalf = CURSOR_SIZE * 0.5f;
            const float ZC = 0.004f;  // in front of D2D texture (0.003) and keys (0.002)
            vertices.Add(new VertexPositionColor(new Vector3(-cHalf, -cHalf, ZC), COLOR_CURSOR));
            vertices.Add(new VertexPositionColor(new Vector3( cHalf, -cHalf, ZC), COLOR_CURSOR));
            vertices.Add(new VertexPositionColor(new Vector3( cHalf,  cHalf, ZC), COLOR_CURSOR));
            vertices.Add(new VertexPositionColor(new Vector3(-cHalf,  cHalf, ZC), COLOR_CURSOR));
            indices.Add((ushort)(vertexOffset + 0));
            indices.Add((ushort)(vertexOffset + 2));
            indices.Add((ushort)(vertexOffset + 1));
            indices.Add((ushort)(vertexOffset + 0));
            indices.Add((ushort)(vertexOffset + 3));
            indices.Add((ushort)(vertexOffset + 2));

            m_indexCount = indices.Count;
            m_vertexData = vertices.ToArray();  // keep mutable copy for per-frame colour updates

            m_vertexBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                m_deviceResources.D3DDevice,
                SharpDX.Direct3D11.BindFlags.VertexBuffer,
                m_vertexData));

            m_indexBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                m_deviceResources.D3DDevice,
                SharpDX.Direct3D11.BindFlags.IndexBuffer,
                indices.ToArray()));

            m_lastHoveredKey = -2;  // force colour update on first render
        }

        public void ReleaseDeviceDependentResources()
        {
            m_vertexShader = null;
            m_pixelShader = null;
            m_geometryShader = null;
            m_inputLayout = null;
            m_vertexBuffer = null;
            m_indexBuffer = null;
            m_modelConstantBuffer = null;
            // Textured-quad resources
            m_texVertexShader = null;
            m_texPixelShader = null;
            m_texGeometryShader = null;
            m_texInputLayout = null;
            m_texVertexBuffer = null;
            m_texIndexBuffer = null;
            m_texConstantBuffer = null;
            m_samplerState = null;
            m_brushWhite = null;
            m_brushGray = null;
            m_dwriteKeyFont = null;
            m_dwriteInputFont = null;
            m_d2dRenderTarget = null;
            m_labelSrv = null;
            m_labelTexture = null;
            m_blendState = null;
            m_loadingComplete = false;
            m_labelsDirty = true;
        }

        public void PlaceInFrontOfUser(Vector3 cameraPosition, Vector3 cameraForward)
        {
            // Panel center: in front of user, slightly below eye level
            Vector3 panelCenter = cameraPosition + cameraForward * PANEL_DISTANCE - Vector3.UnitY * PANEL_DROP;

            // Panel faces the user: normal points from panel toward user
            Vector3 panelNormal = -Vector3.Normalize(cameraForward);
            Vector3 worldUp = Vector3.UnitY;
            Vector3 right = Vector3.Normalize(Vector3.Cross(worldUp, panelNormal));
            Vector3 up = Vector3.Normalize(Vector3.Cross(panelNormal, right));

            // Bottom-left corner: center the panel on panelCenter using full vector subtraction
            Vector3 origin = panelCenter - right * (PANEL_WIDTH * 0.5f) - up * (PANEL_HEIGHT * 0.5f);

            // Row-major world matrix: each row is one local-space axis expressed in world space.
            // Vector3.Transform(localPos, m_panelWorld) == origin + localPos.X*right + localPos.Y*up + localPos.Z*normal
            m_panelWorld = new Matrix4x4(
                right.X,       right.Y,       right.Z,       0,
                up.X,          up.Y,          up.Z,          0,
                panelNormal.X, panelNormal.Y, panelNormal.Z, 0,
                origin.X,      origin.Y,      origin.Z,      1);

            m_panelNormal = panelNormal;
            Debug.WriteLine($"[Keyboard] Placed at origin={origin} normal={panelNormal}");
        }

        public bool Update(Vector3 gazePos, Vector3 gazeDir)
        {
            if (!m_visible) return false;
            int prev = m_hoveredKey;
            m_hoveredKey = HitTestGaze(gazePos, gazeDir);
            return m_hoveredKey != prev;
        }

        public void HandleAirTap()
        {
            if (!m_visible || m_hoveredKey < 0 || m_hoveredKey >= m_keys.Count) return;

            // Brief green flash so the user sees the tap was registered
            m_pressedKey = m_hoveredKey;
            m_pressedFlashFrames = 8;

            var k = m_keys[m_hoveredKey];

            if (k.value == "\b")
            {
                if (!string.IsNullOrEmpty(m_inputText) && m_cursorPos > 0)
                {
                    m_inputText = m_inputText.Remove(m_cursorPos - 1, 1);
                    m_cursorPos--;
                }
            }
            else if (k.value == "\n")
            {
                OnSubmit?.Invoke(m_inputText);
                return;
            }
            else if (k.label == "CLR")
            {
                ClearText();
            }
            else
            {
                bool shifted = m_shiftActive ^ m_capsLock;
                string val = k.value;
                if (!k.isSpecial && shifted && !string.IsNullOrEmpty(val))
                    val = char.ToUpper(val[0]).ToString();
                m_inputText = m_inputText.Insert(m_cursorPos, val);
                m_cursorPos += val.Length;
                if (m_shiftActive) m_shiftActive = false;
            }

            try { m_clickPlayer?.Play(); } catch { }
            OnTextChanged?.Invoke(m_inputText);
            m_labelsDirty = true;
        }

        /// <summary>Render the holographic keyboard with Direct3D colored quads.</summary>
        public void Render(SharpDX.Direct3D11.DeviceContext context)
        {
            if (!m_visible || !m_loadingComplete) return;

            try
            {
                UpdateKeyboardGeometryForHover(context);

                context.VertexShader.SetShader(m_vertexShader, null, 0);
                context.PixelShader.SetShader(m_pixelShader, null, 0);
                if (m_geometryShader != null)
                    context.GeometryShader.SetShader(m_geometryShader, null, 0);
                else
                    context.GeometryShader.SetShader(null, null, 0);
                context.InputAssembler.InputLayout = m_inputLayout;

                int stride = SharpDX.Utilities.SizeOf<VertexPositionColor>();
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(m_vertexBuffer, stride, 0));
                context.InputAssembler.SetIndexBuffer(m_indexBuffer, SharpDX.DXGI.Format.R16_UInt, 0);
                context.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;

                ModelConstantBuffer cbData = new ModelConstantBuffer() { model = Matrix4x4.Transpose(m_panelWorld) };
                context.UpdateSubresource(ref cbData, m_modelConstantBuffer);
                context.VertexShader.SetConstantBuffers(0, m_modelConstantBuffer);

                context.DrawIndexedInstanced(m_indexCount, 2, 0, 0, 0);

                // ---- Textured-quad pass (key labels + input bar) ----
                if (m_labelsDirty || m_lastRenderedText != m_inputText)
                    RenderLabelsToTexture();

                if (m_texVertexShader != null && m_labelSrv != null)
                {
                    context.VertexShader.SetShader(m_texVertexShader, null, 0);
                    context.PixelShader.SetShader(m_texPixelShader, null, 0);
                    if (m_texGeometryShader != null)
                        context.GeometryShader.SetShader(m_texGeometryShader, null, 0);
                    else
                        context.GeometryShader.SetShader(null, null, 0);
                    context.InputAssembler.InputLayout = m_texInputLayout;

                    int texStride = 5 * sizeof(float);
                    context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(m_texVertexBuffer, texStride, 0));
                    context.InputAssembler.SetIndexBuffer(m_texIndexBuffer, SharpDX.DXGI.Format.R16_UInt, 0);

                    context.UpdateSubresource(ref cbData, m_texConstantBuffer);
                    context.VertexShader.SetConstantBuffers(0, m_texConstantBuffer);

                    context.PixelShader.SetShaderResource(0, m_labelSrv);
                    context.PixelShader.SetSampler(0, m_samplerState);

                    if (m_blendState != null) context.OutputMerger.SetBlendState(m_blendState, null, -1);
                    context.DrawIndexedInstanced(6, 2, 0, 0, 0);
                    if (m_blendState != null) context.OutputMerger.SetBlendState(null, null, -1);  // restore
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Keyboard] Render failed: {ex}");
            }
        }

        /// <summary>
        /// Recolours hovered/pressed keys in the mutable vertex cache and uploads to GPU.
        /// Only re-uploads when the hovered or pressed state has actually changed.
        /// </summary>
        private void UpdateKeyboardGeometryForHover(SharpDX.Direct3D11.DeviceContext context)
        {
            if (m_vertexData == null || m_vertexBuffer == null) return;

            bool cursorMoved = Math.Abs(m_gazeLocalHit.X - m_lastCursorPos.X) > 0.0002f ||
                               Math.Abs(m_gazeLocalHit.Y - m_lastCursorPos.Y) > 0.0002f;
            bool dirty = (m_hoveredKey != m_lastHoveredKey) || (m_pressedFlashFrames > 0) || cursorMoved;
            if (!dirty) return;

            // Restore previous hovered key to its base colour (unless it's currently being pressed)
            if (m_lastHoveredKey >= 0 && m_lastHoveredKey < m_keys.Count && m_lastHoveredKey != m_pressedKey)
            {
                var k = m_keys[m_lastHoveredKey];
                Vector3 baseColor = k.isSpecial ? COLOR_SPECIAL : COLOR_NORMAL;
                int v = 4 + m_lastHoveredKey * 4;
                for (int j = 0; j < 4; j++) m_vertexData[v + j].color = baseColor;
            }

            // Apply pressed-flash colour (overrides hover) for a few frames
            if (m_pressedFlashFrames > 0 && m_pressedKey >= 0 && m_pressedKey < m_keys.Count)
            {
                int v = 4 + m_pressedKey * 4;
                for (int j = 0; j < 4; j++) m_vertexData[v + j].color = COLOR_PRESSED;
                m_pressedFlashFrames--;
                if (m_pressedFlashFrames == 0)
                {
                    // Flash done: restore pressed key to base colour
                    var k = m_keys[m_pressedKey];
                    Vector3 baseColor = k.isSpecial ? COLOR_SPECIAL : COLOR_NORMAL;
                    for (int j = 0; j < 4; j++) m_vertexData[v + j].color = baseColor;
                    m_pressedKey = -1;
                    // Force re-evaluate hover colour next frame even if hover index unchanged
                    m_lastHoveredKey = -2;
                }
            }

            // Highlight newly hovered key (unless it's mid-flash)
            if (m_hoveredKey >= 0 && m_hoveredKey < m_keys.Count && m_hoveredKey != m_pressedKey)
            {
                int v = 4 + m_hoveredKey * 4;
                for (int j = 0; j < 4; j++) m_vertexData[v + j].color = COLOR_HOVERED;
            }

            // Update gaze cursor position
            int cv = m_cursorVertStart;
            if (cv >= 0 && cv + 3 < m_vertexData.Length)
            {
                if (m_gazeOnPanel)
                {
                    float cHalf = CURSOR_SIZE * 0.5f;
                    float cx = Math.Max(cHalf, Math.Min(PANEL_WIDTH  - cHalf, m_gazeLocalHit.X));
                    float cy = Math.Max(cHalf, Math.Min(PANEL_HEIGHT - cHalf, m_gazeLocalHit.Y));
                    const float ZC = 0.004f;
                    m_vertexData[cv + 0].pos = new Vector3(cx - cHalf, cy - cHalf, ZC);
                    m_vertexData[cv + 1].pos = new Vector3(cx + cHalf, cy - cHalf, ZC);
                    m_vertexData[cv + 2].pos = new Vector3(cx + cHalf, cy + cHalf, ZC);
                    m_vertexData[cv + 3].pos = new Vector3(cx - cHalf, cy + cHalf, ZC);
                    for (int j = 0; j < 4; j++) m_vertexData[cv + j].color = COLOR_CURSOR;
                }
                else
                {
                    // Hide cursor by making it black (= transparent on HoloLens additive display)
                    for (int j = 0; j < 4; j++) m_vertexData[cv + j].color = Vector3.Zero;
                }
            }
            m_lastCursorPos = m_gazeLocalHit;

            m_lastHoveredKey = m_hoveredKey;
            context.UpdateSubresource(m_vertexData, m_vertexBuffer);
        }

        private void BuildKeyLayout()
        {
            m_keys.Clear();

            // 4 rows × 3 columns numpad — keys auto-sized to fill the panel
            const int ROWS = 4;
            const int COLS = 3;
            float keyW = (PANEL_WIDTH     - (COLS - 1) * KEY_GAP) / COLS;
            float keyH = (KEY_AREA_HEIGHT  - (ROWS - 1) * KEY_GAP) / ROWS; // size keys to the key area only

            float rowY(int row) => (ROWS - 1 - row) * (keyH + KEY_GAP);
            float colX(int col) => col * (keyW + KEY_GAP);

            // Row 0: 7 8 9
            AddKey("7", "7", false, colX(0), rowY(0), keyW, keyH);
            AddKey("8", "8", false, colX(1), rowY(0), keyW, keyH);
            AddKey("9", "9", false, colX(2), rowY(0), keyW, keyH);

            // Row 1: 4 5 6
            AddKey("4", "4", false, colX(0), rowY(1), keyW, keyH);
            AddKey("5", "5", false, colX(1), rowY(1), keyW, keyH);
            AddKey("6", "6", false, colX(2), rowY(1), keyW, keyH);

            // Row 2: 1 2 3
            AddKey("1", "1", false, colX(0), rowY(2), keyW, keyH);
            AddKey("2", "2", false, colX(1), rowY(2), keyW, keyH);
            AddKey("3", "3", false, colX(2), rowY(2), keyW, keyH);

            // Row 3: ⌫  0  ↵
            AddKey("⌫", "\b", true,  colX(0), rowY(3), keyW, keyH);
            AddKey("0",   "0",  false, colX(1), rowY(3), keyW, keyH);
            AddKey("↵",  "\n", true,  colX(2), rowY(3), keyW, keyH);
        }

        private void AddKey(string label, string value, bool special, float x, float y, float w, float h)
        {
            m_keys.Add(new VirtualKey { label = label, value = value, isSpecial = special, localX = x, localY = y, width = w, height = h });
        }

        // ---- D2D label texture pipeline ------------------------------------------------

        private async Task CreateLabelResourcesAsync()
        {
            try
            {
                var folder = Windows.ApplicationModel.Package.Current.InstalledLocation;
                var device = m_deviceResources.D3DDevice;

                bool vprt = m_usingVprtShaders;
                string vsFile = vprt ? "Content\\Shaders\\TextureVertexShader.cso"
                                     : "Content\\Shaders\\TextureVertexShaderNoVPRT.cso";

                var vsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync(vsFile));
                m_texVertexShader = this.ToDispose(new VertexShader(device, vsBytes));

                SharpDX.Direct3D11.InputElement[] layout =
                {
                    new SharpDX.Direct3D11.InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float, 0,  0, InputClassification.PerVertexData, 0),
                    new SharpDX.Direct3D11.InputElement("TEXCOORD", 0, SharpDX.DXGI.Format.R32G32_Float,    12, 0, InputClassification.PerVertexData, 0),
                };
                m_texInputLayout = this.ToDispose(new InputLayout(device, vsBytes, layout));

                if (!vprt)
                {
                    var gsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\TextureGeometryShader.cso"));
                    m_texGeometryShader = this.ToDispose(new GeometryShader(device, gsBytes));
                }

                var psBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\TexturePixelShader.cso"));
                m_texPixelShader = this.ToDispose(new PixelShader(device, psBytes));

                ModelConstantBuffer cbData = new ModelConstantBuffer() { model = Matrix4x4.Identity };
                m_texConstantBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(device, BindFlags.ConstantBuffer, ref cbData));

                var samplerDesc = new SamplerStateDescription
                {
                    Filter             = Filter.MinMagMipLinear,
                    AddressU           = TextureAddressMode.Clamp,
                    AddressV           = TextureAddressMode.Clamp,
                    AddressW           = TextureAddressMode.Clamp,
                    MaximumAnisotropy  = 1,
                    ComparisonFunction = Comparison.Never,
                    MinimumLod         = 0,
                    MaximumLod         = float.MaxValue,
                };
                m_samplerState = this.ToDispose(new SamplerState(device, samplerDesc));

                // Premultiplied-alpha blend: transparent D2D pixels let key-quad hover colors show through
                var blendDesc = new BlendStateDescription();
                blendDesc.RenderTarget[0].IsBlendEnabled        = true;
                blendDesc.RenderTarget[0].SourceBlend           = BlendOption.One;  // RGB already premultiplied
                blendDesc.RenderTarget[0].DestinationBlend      = BlendOption.InverseSourceAlpha;
                blendDesc.RenderTarget[0].BlendOperation        = BlendOperation.Add;
                blendDesc.RenderTarget[0].SourceAlphaBlend      = BlendOption.One;
                blendDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
                blendDesc.RenderTarget[0].AlphaBlendOperation   = BlendOperation.Add;
                blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
                m_blendState = this.ToDispose(new BlendState(device, blendDesc));

                var texDesc = new Texture2DDescription
                {
                    Width             = TEX_W,
                    Height            = TEX_H,
                    MipLevels         = 1,
                    ArraySize         = 1,
                    Format            = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                    SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                    Usage             = ResourceUsage.Default,
                    BindFlags         = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CpuAccessFlags    = CpuAccessFlags.None,
                    OptionFlags       = ResourceOptionFlags.None,
                };
                m_labelTexture = this.ToDispose(new Texture2D(device, texDesc));
                m_labelSrv     = this.ToDispose(new ShaderResourceView(device, m_labelTexture));

                using (var surf = m_labelTexture.QueryInterface<SharpDX.DXGI.Surface>())
                {
                    var rtProps = new SharpDX.Direct2D1.RenderTargetProperties(
                        SharpDX.Direct2D1.RenderTargetType.Default,
                        new SharpDX.Direct2D1.PixelFormat(SharpDX.DXGI.Format.Unknown, SharpDX.Direct2D1.AlphaMode.Premultiplied),
                        96f, 96f,
                        SharpDX.Direct2D1.RenderTargetUsage.None,
                        SharpDX.Direct2D1.FeatureLevel.Level_DEFAULT);
                    m_d2dRenderTarget = this.ToDispose(
                        new SharpDX.Direct2D1.RenderTarget(m_deviceResources.D2DFactory, surf, rtProps));
                }

                m_dwriteKeyFont = this.ToDispose(new SharpDX.DirectWrite.TextFormat(
                    m_deviceResources.DWriteFactory, "Segoe UI",
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 52f));
                m_dwriteKeyFont.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                m_dwriteKeyFont.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                m_dwriteInputFont = this.ToDispose(new SharpDX.DirectWrite.TextFormat(
                    m_deviceResources.DWriteFactory, "Segoe UI",
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 36f));
                m_dwriteInputFont.TextAlignment      = SharpDX.DirectWrite.TextAlignment.Leading;
                m_dwriteInputFont.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                m_brushWhite = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(
                    m_d2dRenderTarget, new SharpDX.Mathematics.Interop.RawColor4(1, 1, 1, 1)));
                m_brushGray  = this.ToDispose(new SharpDX.Direct2D1.SolidColorBrush(
                    m_d2dRenderTarget, new SharpDX.Mathematics.Interop.RawColor4(0.6f, 0.6f, 0.6f, 1)));

                CreateTextureQuadGeometry();
                m_labelsDirty = true;
                Debug.WriteLine("[Keyboard] Label resources created");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Keyboard] Label resource creation failed: " + ex);
            }
        }

        private void CreateTextureQuadGeometry()
        {
            const float Z = 0.003f;
            float[] verts =
            {
                0,           0,            Z,  0, 1,
                PANEL_WIDTH, 0,            Z,  1, 1,
                PANEL_WIDTH, PANEL_HEIGHT, Z,  1, 0,
                0,           PANEL_HEIGHT, Z,  0, 0,
            };
            ushort[] idx = { 0, 2, 1, 0, 3, 2 };
            m_texVertexBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(m_deviceResources.D3DDevice, BindFlags.VertexBuffer, verts));
            m_texIndexBuffer  = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(m_deviceResources.D3DDevice, BindFlags.IndexBuffer, idx));
        }

        private void RenderLabelsToTexture()
        {
            if (m_d2dRenderTarget == null) return;

            // Texture maps linearly over the full panel (PANEL_WIDTH x PANEL_HEIGHT).
            // UV(0,0)=top-left=world Y=PANEL_HEIGHT;  UV(1,1)=bottom-right=world Y=0.
            float pxPerM_X    = TEX_W / PANEL_WIDTH;
            float pxPerM_Y    = TEX_H / PANEL_HEIGHT;
            float inputBarPxH = INPUT_BAR_HEIGHT_M * pxPerM_Y;  // input-bar height in pixels

            m_d2dRenderTarget.BeginDraw();
            m_d2dRenderTarget.Clear(new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0));

            // Input bar — top strip of texture, above the keys
            var barRect = new SharpDX.Mathematics.Interop.RawRectangleF(0, 0, TEX_W, inputBarPxH);
            using (var barBrush = new SharpDX.Direct2D1.SolidColorBrush(m_d2dRenderTarget,
                new SharpDX.Mathematics.Interop.RawColor4(0.05f, 0.05f, 0.25f, 0.95f)))
                m_d2dRenderTarget.FillRectangle(barRect, barBrush);

            string displayText = string.IsNullOrEmpty(m_inputText) ? "▶ Enter elnummer..." : m_inputText + "▌";
            m_d2dRenderTarget.DrawText(displayText, m_dwriteInputFont,
                new SharpDX.Mathematics.Interop.RawRectangleF(12, 0, TEX_W - 12, inputBarPxH),
                m_brushWhite);

            // Key labels — world Y=PANEL_HEIGHT → tex Y=0 (top), world Y=0 → tex Y=TEX_H (bottom)
            foreach (var k in m_keys)
            {
                float kPxX    = k.localX * pxPerM_X;
                float kPxW    = k.width  * pxPerM_X;
                float kPxH    = k.height * pxPerM_Y;
                float kPxYTop = (PANEL_HEIGHT - (k.localY + k.height)) * pxPerM_Y;
                m_d2dRenderTarget.DrawText(
                    k.label, m_dwriteKeyFont,
                    new SharpDX.Mathematics.Interop.RawRectangleF(kPxX + 2, kPxYTop + 2, kPxX + kPxW - 2, kPxYTop + kPxH - 2),
                    k.isSpecial ? m_brushGray : m_brushWhite);
            }

            m_d2dRenderTarget.EndDraw();
            m_labelsDirty = false;
            m_lastRenderedText = m_inputText;
        }

        // ---- End D2D label pipeline --------------------------------------------------

        private int HitTestGaze(Vector3 gazePosWorld, Vector3 gazeDirWorld)
        {
            Vector3 planeOrigin = new Vector3(m_panelWorld.M41, m_panelWorld.M42, m_panelWorld.M43);
            float denom = Vector3.Dot(gazeDirWorld, m_panelNormal);
            if (Math.Abs(denom) < 1e-6f) { m_gazeOnPanel = false; return -1; }

            float t = Vector3.Dot(planeOrigin - gazePosWorld, m_panelNormal) / denom;
            if (t < 0f || t > 5f) { m_gazeOnPanel = false; return -1; }

            Vector3 hitWorld = gazePosWorld + gazeDirWorld * t;
            Matrix4x4.Invert(m_panelWorld, out Matrix4x4 invPanel);
            Vector3 hitLocal = Vector3.Transform(hitWorld, invPanel);

            // Store gaze local position for cursor rendering
            m_gazeOnPanel  = hitLocal.X >= 0 && hitLocal.X <= PANEL_WIDTH &&
                             hitLocal.Y >= 0 && hitLocal.Y <= PANEL_HEIGHT;
            m_gazeLocalHit = new Vector2(
                Math.Max(0f, Math.Min(PANEL_WIDTH,  hitLocal.X)),
                Math.Max(0f, Math.Min(PANEL_HEIGHT, hitLocal.Y)));

            for (int i = 0; i < m_keys.Count; ++i)
            {
                var k = m_keys[i];
                if (hitLocal.X >= k.localX && hitLocal.X <= k.localX + k.width &&
                    hitLocal.Y >= k.localY && hitLocal.Y <= k.localY + k.height)
                    return i;
            }
            return -1;
        }
    }
}
