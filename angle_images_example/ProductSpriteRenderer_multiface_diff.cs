// ═══════════════════════════════════════════════════════════════════════════
// ProductSpriteRenderer — multi-face additions
//
// This file shows every change needed to make ProductSpriteRenderer render
// the front AND side face textures on the correct box faces.
//
// Sections:
//   §1  New fields to add
//   §2  New public API methods
//   §3  Update to Update()
//   §4  Update to Render()
//   §5  New private method: RenderSideFace()
//   §6  New private method: BuildSideFaceGeometry()
//   §7  Constant-buffer struct change
//   §8  Integration: how AppMain / the scene coordinator wires it all together
// ═══════════════════════════════════════════════════════════════════════════

using System.Numerics;
using SharpDX.Direct3D11;
using HololensIKEA.Common;

namespace HololensIKEA.Content
{
    // ───────────────────────────────────────────────────────────────────────
    // §1  NEW FIELDS  (add inside the class body, near the existing texture fields)
    // ───────────────────────────────────────────────────────────────────────

    /*
    // Which side face has a texture (mirrors ViewType from ProductViewClassifier)
    private ViewType    _viewType        = ViewType.FrontOnly;

    // Side-face texture (extracted by ProductFaceTextureBuilder)
    private ShaderResourceView _srvSideFace;

    // Side-face geometry: a quad on the right (+X) or left (−X) face of the unit cube.
    // Rebuilt when _depth or _viewType changes.
    private SharpDX.Direct3D11.Buffer _vbSide;
    private SharpDX.Direct3D11.Buffer _ibSide;

    // Side-face constant buffer (separate because UV bounds differ from front).
    private SharpDX.Direct3D11.Buffer _sideCB;

    // Cached depth for detecting when side geometry needs rebuild
    private float _lastDepthForSideGeom = -1f;
    */

    // ───────────────────────────────────────────────────────────────────────
    // §2  NEW PUBLIC API  (add alongside SetTexture / SetDisplacementTexture)
    // ───────────────────────────────────────────────────────────────────────

    /*
    /// <summary>
    /// Sets the extracted side-face texture (from ProductFaceTextureBuilder)
    /// and records which side it belongs to.
    /// Call after SetDimensions() so the side geometry gets correct depth.
    /// </summary>
    public void SetSideFaceTexture(ShaderResourceView srv, ViewType viewType)
    {
        if (!ReferenceEquals(srv, _srvSideFace))
        {
            _srvSideFace?.Dispose();
            _srvSideFace = srv;
        }
        _viewType = viewType;
    }

    public void ClearSideFaceTexture()
    {
        _srvSideFace?.Dispose();
        _srvSideFace = null;
        _viewType    = ViewType.FrontOnly;
    }
    */

    // ───────────────────────────────────────────────────────────────────────
    // §3  UPDATE to Update()
    //
    // EXISTING code already writes the model constant buffer.
    // Add: rebuild side-face geometry when depth or viewType changes.
    // ───────────────────────────────────────────────────────────────────────

    /*
    // At the end of Update(), after the existing model-CB write:
    if (_loadingComplete && _srvSideFace != null &&
        Math.Abs(_depth - _lastDepthForSideGeom) > 0.0001f)
    {
        BuildSideFaceGeometry(_dr.D3DDevice);
        _lastDepthForSideGeom = _depth;
    }
    */

    // ───────────────────────────────────────────────────────────────────────
    // §4  UPDATE to Render()
    //
    // EXISTING Render() draws the front-face quad / grid.
    // Add a second draw call for the side face immediately after.
    // ───────────────────────────────────────────────────────────────────────

    /*
    // ── Existing front-face draw (unchanged) ───────────────────────────────
    // [... existing code for front face ...]

    // ── NEW: side-face draw ────────────────────────────────────────────────
    if (_loadingComplete && _srvSideFace != null && _vbSide != null)
    {
        RenderSideFace(context);
    }
    */

    // ───────────────────────────────────────────────────────────────────────
    // §5  NEW PRIVATE METHOD: RenderSideFace()
    // ───────────────────────────────────────────────────────────────────────

    /*
    private void RenderSideFace(DeviceContext context)
    {
        // Re-use the Basic (flat-quad) shader pipeline — no displacement
        // needed on the side face since the photo is already showing it flat.
        context.VertexShader.SetShader(_vsBasic, null, 0);
        if (!_usingVprt)
            context.GeometryShader.SetShader(_gsBasic, null, 0);
        context.PixelShader.SetShader(_psBasic, null, 0);

        // Bind model CB (same world transform as front face — already set)
        context.VertexShader.SetConstantBuffers(0, _modelCB);

        // Bind the side-face sprite CB (white-removal params, full UV 0→1)
        var sideParams = new SpriteCbData
        {
            WhiteThreshold = _whiteThreshold,
            WhiteSoftness  = _whiteSoftness,
            Opacity        = 1f,
            DepthScale     = 0f,             // no depth displacement on side
            ContentBounds  = new Vector4(0f, 0f, 1f, 1f), // side tex already pre-cropped
        };
        context.UpdateSubresource(ref sideParams, _sideCB);
        context.PixelShader.SetConstantBuffers(2, _sideCB);

        // Bind side-face texture
        context.PixelShader.SetShaderResources(0, _srvSideFace);

        // Draw the side-face quad geometry
        int stride = SharpDX.Utilities.SizeOf<VertexPosUV>();
        context.InputAssembler.SetVertexBuffers(0,
            new VertexBufferBinding(_vbSide, stride, 0));
        context.InputAssembler.SetIndexBuffer(_ibSide,
            SharpDX.DXGI.Format.R16_UInt, 0);
        context.InputAssembler.PrimitiveTopology =
            SharpDX.Direct3D.PrimitiveTopology.TriangleList;

        context.DrawIndexedInstanced(6, 2, 0, 0, 0);  // 2 instances = stereo eyes

        // Restore the front-face texture so subsequent ops aren't confused
        context.PixelShader.SetShaderResources(0, _srvTexture);
    }
    */

    // ───────────────────────────────────────────────────────────────────────
    // §6  NEW PRIVATE METHOD: BuildSideFaceGeometry()
    //
    // Creates a quad on the local +X face (right side) or −X face (left side)
    // of the unit cube, at a Z clearance of 0.51 from the side face centre.
    //
    // The unit cube spans −0.5..+0.5 on all axes.
    // Right face (+X): x = +0.501, y ∈ [−0.5, +0.5], z ∈ [−0.5, +0.5]
    // UV: U=0 at front (z=+0.5), U=1 at back (z=−0.5); V=0 at top, V=1 at bottom.
    //
    // Important: the local-to-world scale matrix in the model CB is
    //   Scale(widthM, heightM, depthM).
    // So on the right face, the Y extent (height) is already handled.
    // The Z extent is the product depth in local space, which maps to U in the
    // texture so the side face is shown at its real depth:height ratio.
    // ───────────────────────────────────────────────────────────────────────

    /*
    private void BuildSideFaceGeometry(SharpDX.Direct3D11.Device device)
    {
        const float clearance = 0.501f; // just outside the unit cube face (avoids Z-fight)

        VertexPosUV[] verts;

        if (_viewType == ViewType.ThreeQuarterRight)
        {
            // Right face (+X), normal = +X
            // Vertices wound CCW from +X (from the right side):
            //   BF = Bottom-Front, TF = Top-Front, BB = Bottom-Back, TB = Top-Back
            verts = new[]
            {
                // (local x, local y, local z)              UV
                new VertexPosUV { Position = new Vector3( clearance, -0.5f,  0.5f), UV = new Vector2(0f, 1f) }, // BF — u=0 (front), v=1 (bottom)
                new VertexPosUV { Position = new Vector3( clearance,  0.5f,  0.5f), UV = new Vector2(0f, 0f) }, // TF — u=0 (front), v=0 (top)
                new VertexPosUV { Position = new Vector3( clearance, -0.5f, -0.5f), UV = new Vector2(1f, 1f) }, // BB — u=1 (back),  v=1 (bottom)
                new VertexPosUV { Position = new Vector3( clearance,  0.5f, -0.5f), UV = new Vector2(1f, 0f) }, // TB — u=1 (back),  v=0 (top)
            };
        }
        else // ThreeQuarterLeft → left face (−X)
        {
            verts = new[]
            {
                new VertexPosUV { Position = new Vector3(-clearance, -0.5f, -0.5f), UV = new Vector2(0f, 1f) },
                new VertexPosUV { Position = new Vector3(-clearance,  0.5f, -0.5f), UV = new Vector2(0f, 0f) },
                new VertexPosUV { Position = new Vector3(-clearance, -0.5f,  0.5f), UV = new Vector2(1f, 1f) },
                new VertexPosUV { Position = new Vector3(-clearance,  0.5f,  0.5f), UV = new Vector2(1f, 0f) },
            };
        }

        // Same index pattern as the front-face quad
        var indices = new ushort[] { 0, 2, 3, 0, 3, 1 };

        // Dispose old geometry before rebuilding
        _vbSide?.Dispose();
        _ibSide?.Dispose();

        _vbSide = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, verts);
        _ibSide = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, indices);

        // Side CB  (constant; only needs creating once)
        if (_sideCB == null)
        {
            var dummy = new SpriteCbData();
            _sideCB = SharpDX.Direct3D11.Buffer.Create(
                device, BindFlags.ConstantBuffer, ref dummy);
        }
    }
    */

    // ───────────────────────────────────────────────────────────────────────
    // §7  CONSTANT BUFFER  — no struct change needed
    //
    // SpriteCbData already contains ContentBounds (minU, minV, maxU, maxV).
    // The side face is uploaded pre-cropped so its bounds are always (0,0,1,1).
    // The existing white-removal shader handles it identically to the front face.
    // ───────────────────────────────────────────────────────────────────────

    // ───────────────────────────────────────────────────────────────────────
    // §8  INTEGRATION — how AppMain / scene coordinator wires it all together
    // ───────────────────────────────────────────────────────────────────────

    /*
    // ── In AppMain (or wherever product loading is orchestrated) ────────────

    private async Task LoadProductImageAsync(
        RenderableProduct     product,
        ProductSpriteRenderer sprite,
        CancellationToken     ct)
    {
        // 1. Download image + get raw BGRA pixels
        var loader = new ProductImageLoader(_deviceResources.D3DDevice);
        ImageLoadResult imgResult = await loader.DownloadAndDecodeAsync(product.ImageUrl, ct);
        if (imgResult == null) return;

        // 2. Upload front-face texture (works for all view types)
        sprite.SetTexture(imgResult.Srv);
        sprite.SetContentBounds(
            imgResult.Bounds.MinU, imgResult.Bounds.MinV,
            imgResult.Bounds.MaxU, imgResult.Bounds.MaxV);

        // 3. Run depth analysis (existing code — unchanged)
        var depthResult = ProductDepthAnalyzer.Analyze(
            imgResult.TightBgra, imgResult.Width, imgResult.Height);

        if (depthResult.DisplacementR8 != null)
        {
            var dispSrv = loader.UploadDisplacementMap(
                depthResult.DisplacementR8, depthResult.DispWidth, depthResult.DispHeight);
            sprite.SetDisplacementTexture(dispSrv);
        }

        // 4. NEW: classify view angle
        var classification = ProductViewClassifier.Classify(
            imgResult.TightBgra, imgResult.Width, imgResult.Height);

        if (classification.ViewType != ViewType.FrontOnly)
        {
            // 5. NEW: build perspective-corrected per-face textures
            var faceTextures = ProductFaceTextureBuilder.Build(
                srcBgra:         imgResult.TightBgra,
                srcWidth:        (int)imgResult.Width,
                srcHeight:       (int)imgResult.Height,
                classification:  classification,
                productDepthM:   product.DepthMeters,
                productHeightM:  product.HeightMeters);

            // 6. NEW: swap in the corrected front texture
            //    (better crop + perspective correction vs the raw full image)
            if (faceTextures.Front != null)
            {
                var correctedFrontSrv = loader.UploadBGRA(
                    faceTextures.Front.BgraPix,
                    (uint)faceTextures.Front.Width,
                    (uint)faceTextures.Front.Height);
                sprite.SetTexture(correctedFrontSrv);
                sprite.SetContentBounds(0f, 0f, 1f, 1f);   // pre-cropped; full UV
            }

            // 7. NEW: upload and assign side texture
            if (faceTextures.Side != null)
            {
                var sideSrv = loader.UploadBGRA(
                    faceTextures.Side.BgraPix,
                    (uint)faceTextures.Side.Width,
                    (uint)faceTextures.Side.Height);
                sprite.SetSideFaceTexture(sideSrv, classification.ViewType);
            }
        }

        // NOTE: UploadBGRA is already private in ProductImageLoader.
        // Make it internal, or add a public overload:
        //   public ShaderResourceView UploadBGRA(byte[] pixels, uint w, uint h)
        // (The existing private method is identical; just change the modifier.)
    }

    // ── Summary of what changes visually ────────────────────────────────────
    //
    // AC unit (Image 1):
    //   ViewType = FrontOnly
    //   Front texture = full white-removed image (same as before, but now
    //     cropped to tight content bounds for cleaner display)
    //   No side texture drawn
    //   Result: identical to current behaviour ✓
    //
    // ABB switch box (Image 2):
    //   ViewType = ThreeQuarterRight
    //   Front texture = perspective-corrected crop of the front face region
    //     (~left 62 % of image, trapezoid unwarped to rectangle)
    //     → shows just the door panel with ABB logo, correctly proportioned
    //   Side texture = perspective-corrected crop of the right side region
    //     (~right 38 % of image, foreshortened parallelogram unwarped)
    //     → aspect ratio set to depth:height from JSON (e.g. 0.15:0.35 ≈ 0.43)
    //     → applied to the box's right face (+X) in local space
    //   Result: box shows correct face on each physical surface ✓
    */
}
