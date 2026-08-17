//********************************************************* 
// KeyboardInputHandler.cpp
//*********************************************************

#include "pch.h"
#include "KeyboardInputHandler.h"

using namespace DirectX;
using namespace winrt::Windows::UI::Core;
using namespace winrt::Windows::System;
using namespace winrt::Windows::Perception::Spatial;

namespace BasicHologram
{

// ============================================================
// Inline HLSL shaders (compiled at runtime via D3DCompile)
// ============================================================

static const char* KEY_VS_SRC = R"hlsl(
cbuffer KeyCB : register(b0)
{
    float4x4 modelViewProj;
    float4   color;
};
struct VS_IN  { float3 pos : POSITION; };
struct VS_OUT { float4 pos : SV_POSITION; float4 col : COLOR; };

VS_OUT main(VS_IN i)
{
    VS_OUT o;
    o.pos = mul(float4(i.pos, 1.0f), modelViewProj);
    o.col = color;
    return o;
}
)hlsl";

static const char* KEY_PS_SRC = R"hlsl(
struct PS_IN { float4 pos : SV_POSITION; float4 col : COLOR; };
float4 main(PS_IN i) : SV_TARGET { return i.col; }
)hlsl";

// ============================================================
// Constructor
// ============================================================

KeyboardInputHandler::KeyboardInputHandler()
{
    // Identity panel transform – will be placed properly via PlaceInFrontOfUser
    XMStoreFloat4x4(&m_panelWorld, XMMatrixIdentity());
    m_panelNormal = { 0.f, 0.f, -1.f };

    BuildKeyLayout();
}

// ============================================================
// Key layout
// ============================================================

void KeyboardInputHandler::BuildKeyLayout()
{
    m_keys.clear();

    // Key dimensions (metres on the panel)
    const float KW  = 0.050f; // standard key width
    const float KH  = 0.044f; // key height
    const float GAP = 0.004f; // gap between keys

    // Panel local coordinates: origin at top-left corner of key area
    // X grows right, Y grows down (we'll flip Y when building the world matrix)

    auto addKey = [&](std::wstring lbl, std::wstring val, bool special,
                      float x, float y, float w = -1.f)
    {
        VirtualKey k;
        k.label     = lbl;
        k.value     = val;
        k.isSpecial = special;
        k.localX    = x;
        k.localY    = y;
        k.width     = (w < 0.f) ? KW : w;
        k.height    = KH;
        m_keys.push_back(k);
    };

    // ---- Row 0: numbers ----
    float x = 0.f, y = 0.f;
    const wchar_t* numbers = L"1234567890";
    for (int i = 0; i < 10; ++i)
    {
        wchar_t buf[2] = { numbers[i], 0 };
        addKey(buf, buf, false, x, y);
        x += KW + GAP;
    }
    addKey(L"⌫", L"\b", true, x, y, KW * 1.3f);  // Backspace

    // ---- Row 1: QWERTY ----
    x = 0.f; y += KH + GAP;
    const wchar_t* row1 = L"QWERTYUIOP";
    for (int i = 0; i < 10; ++i)
    {
        wchar_t buf[2] = { row1[i], 0 };
        addKey(buf, buf, false, x, y);
        x += KW + GAP;
    }

    // ---- Row 2: ASDF ----
    x = (KW + GAP) * 0.3f; y += KH + GAP;
    const wchar_t* row2 = L"ASDFGHJKL";
    for (int i = 0; i < 9; ++i)
    {
        wchar_t buf[2] = { row2[i], 0 };
        addKey(buf, buf, false, x, y);
        x += KW + GAP;
    }

    // ---- Row 3: ZXCV + Enter ----
    x = (KW + GAP) * 0.7f; y += KH + GAP;
    const wchar_t* row3 = L"ZXCVBNM";
    for (int i = 0; i < 7; ++i)
    {
        wchar_t buf[2] = { row3[i], 0 };
        addKey(buf, buf, false, x, y);
        x += KW + GAP;
    }
    addKey(L"↵", L"\n", true, x, y, KW * 1.8f);  // Enter

    // ---- Row 4: Space + punctuation ----
    x = 0.f; y += KH + GAP;
    addKey(L",",   L",",  false, x, y); x += KW + GAP;
    addKey(L".",   L".",  false, x, y); x += KW + GAP;
    addKey(L"SPACE", L" ", true, x, y, KW * 5.0f); x += KW * 5.0f + GAP;
    addKey(L"!",   L"!",  false, x, y); x += KW + GAP;
    addKey(L"?",   L"?",  false, x, y); x += KW + GAP;
    addKey(L"CLR", L"",   true,  x, y, KW * 1.5f); // Clear all
}

// ============================================================
// Device resources
// ============================================================

void KeyboardInputHandler::CreateDeviceDependentResources(
    ID3D11Device*        device,
    ID3D11DeviceContext* /*context*/)
{
    if (m_initialised) return;

    // ---- Compile shaders ----
    Microsoft::WRL::ComPtr<ID3DBlob> vsBlob, psBlob, errBlob;

    HRESULT hr = D3DCompile(
        KEY_VS_SRC, strlen(KEY_VS_SRC), nullptr, nullptr, nullptr,
        "main", "vs_5_0", 0, 0, &vsBlob, &errBlob);
    if (FAILED(hr)) { OutputDebugStringA((char*)errBlob->GetBufferPointer()); return; }

    hr = D3DCompile(
        KEY_PS_SRC, strlen(KEY_PS_SRC), nullptr, nullptr, nullptr,
        "main", "ps_5_0", 0, 0, &psBlob, &errBlob);
    if (FAILED(hr)) { OutputDebugStringA((char*)errBlob->GetBufferPointer()); return; }

    device->CreateVertexShader(vsBlob->GetBufferPointer(),
                               vsBlob->GetBufferSize(), nullptr, &m_vertexShader);
    device->CreatePixelShader(psBlob->GetBufferPointer(),
                              psBlob->GetBufferSize(), nullptr, &m_pixelShader);

    // ---- Input layout ----
    D3D11_INPUT_ELEMENT_DESC layout[] = {
        { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0,
          D3D11_INPUT_PER_VERTEX_DATA, 0 }
    };
    device->CreateInputLayout(layout, 1,
        vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), &m_inputLayout);

    // ---- Unit quad vertex/index buffers (reused for every key) ----
    // A quad from (0,0,0) to (1,1,0) – scaled via constant buffer
    struct SimpleVertex { float x, y, z; };
    SimpleVertex verts[4] = {
        { 0.f, 0.f, 0.f },
        { 1.f, 0.f, 0.f },
        { 1.f, 1.f, 0.f },
        { 0.f, 1.f, 0.f }
    };
    WORD indices[6] = { 0, 1, 2, 0, 2, 3 };

    D3D11_BUFFER_DESC vbd = {};
    vbd.Usage          = D3D11_USAGE_DEFAULT;
    vbd.ByteWidth      = sizeof(verts);
    vbd.BindFlags      = D3D11_BIND_VERTEX_BUFFER;
    D3D11_SUBRESOURCE_DATA vd = { verts };
    device->CreateBuffer(&vbd, &vd, &m_vertexBuffer);

    D3D11_BUFFER_DESC ibd = {};
    ibd.Usage     = D3D11_USAGE_DEFAULT;
    ibd.ByteWidth = sizeof(indices);
    ibd.BindFlags = D3D11_BIND_INDEX_BUFFER;
    D3D11_SUBRESOURCE_DATA id = { indices };
    device->CreateBuffer(&ibd, &id, &m_indexBuffer);

    // ---- Constant buffer ----
    D3D11_BUFFER_DESC cbd = {};
    cbd.Usage          = D3D11_USAGE_DYNAMIC;
    cbd.ByteWidth      = sizeof(KeyCB);
    cbd.BindFlags      = D3D11_BIND_CONSTANT_BUFFER;
    cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    device->CreateBuffer(&cbd, nullptr, &m_constantBuffer);

    // ---- Rasterizer: no cull (visible from both sides) ----
    D3D11_RASTERIZER_DESC rd = {};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    device->CreateRasterizerState(&rd, &m_rasterizerState);

    // ---- Alpha blend ----
    D3D11_BLEND_DESC bd = {};
    bd.RenderTarget[0].BlendEnable           = TRUE;
    bd.RenderTarget[0].SrcBlend             = D3D11_BLEND_SRC_ALPHA;
    bd.RenderTarget[0].DestBlend            = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOp              = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].SrcBlendAlpha        = D3D11_BLEND_ONE;
    bd.RenderTarget[0].DestBlendAlpha       = D3D11_BLEND_ZERO;
    bd.RenderTarget[0].BlendOpAlpha         = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].RenderTargetWriteMask= D3D11_COLOR_WRITE_ENABLE_ALL;
    device->CreateBlendState(&bd, &m_blendState);

    // ---- Depth-stencil: write but still test ----
    D3D11_DEPTH_STENCIL_DESC dsd = {};
    dsd.DepthEnable    = TRUE;
    dsd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    dsd.DepthFunc      = D3D11_COMPARISON_LESS;
    device->CreateDepthStencilState(&dsd, &m_depthStencilState);

    m_initialised = true;
}

void KeyboardInputHandler::ReleaseDeviceDependentResources()
{
    m_vertexBuffer.Reset();
    m_indexBuffer.Reset();
    m_constantBuffer.Reset();
    m_vertexShader.Reset();
    m_pixelShader.Reset();
    m_inputLayout.Reset();
    m_rasterizerState.Reset();
    m_blendState.Reset();
    m_depthStencilState.Reset();
    m_initialised = false;
}

// ============================================================
// Placement
// ============================================================

void KeyboardInputHandler::PlaceInFrontOfUser(
    XMVECTOR cameraPosition,
    XMVECTOR cameraForward)
{
    // Place panel 0.75 m in front of the user, slightly below eye level
    XMVECTOR panelPos = cameraPosition
                      + cameraForward * 0.75f
                      + XMVectorSet(0, -0.15f, 0, 0);

    // Panel faces the user (inverse of cameraForward)
    XMVECTOR fwd = XMVectorNegate(cameraForward);
    fwd = XMVector3Normalize(fwd);

    XMVECTOR up    = XMVectorSet(0, 1, 0, 0);
    XMVECTOR right = XMVector3Normalize(XMVector3Cross(up, fwd));
    up = XMVector3Normalize(XMVector3Cross(fwd, right));

    // Build world matrix: scale by panel size, then orient + translate
    // We centre the panel on panelPos
    XMMATRIX panelMat = XMMatrixSet(
        XMVectorGetX(right), XMVectorGetX(up), XMVectorGetX(fwd), 0,
        XMVectorGetY(right), XMVectorGetY(up), XMVectorGetY(fwd), 0,
        XMVectorGetZ(right), XMVectorGetZ(up), XMVectorGetZ(fwd), 0,
        XMVectorGetX(panelPos) - PANEL_WIDTH * 0.5f * XMVectorGetX(right),
        XMVectorGetY(panelPos) - PANEL_HEIGHT * 0.5f * XMVectorGetY(up),
        XMVectorGetZ(panelPos) - PANEL_HEIGHT * 0.5f * XMVectorGetZ(up),
        1
    );

    XMStoreFloat4x4(&m_panelWorld, panelMat);
    XMStoreFloat3(&m_panelNormal, fwd);
}

// ============================================================
// Update (gaze hit-test)
// ============================================================

int KeyboardInputHandler::HitTestGaze(XMVECTOR gazePosWorld,
                                       XMVECTOR gazeDirWorld)
{
    // Ray-plane intersection: find where gaze hits the panel plane
    XMVECTOR panelNorm = XMLoadFloat3(&m_panelNormal);
    XMMATRIX panelWorld = XMLoadFloat4x4(&m_panelWorld);

    // Plane origin = translation column of m_panelWorld
    XMVECTOR planeOrigin = XMVectorSet(
        m_panelWorld._41, m_panelWorld._42, m_panelWorld._43, 1.f);

    float denom = XMVectorGetX(XMVector3Dot(gazeDirWorld, panelNorm));
    if (fabsf(denom) < 1e-6f) return -1; // parallel

    float t = XMVectorGetX(
        XMVector3Dot(planeOrigin - gazePosWorld, panelNorm)) / denom;
    if (t < 0.f || t > 5.f) return -1; // behind or too far

    XMVECTOR hitWorld = gazePosWorld + gazeDirWorld * t;

    // Transform hit point to panel-local space
    XMMATRIX invPanel = XMMatrixInverse(nullptr, panelWorld);
    XMVECTOR hitLocal = XMVector3TransformCoord(hitWorld, invPanel);
    float lx = XMVectorGetX(hitLocal);
    float ly = XMVectorGetY(hitLocal);

    // Test each key
    for (int i = 0; i < (int)m_keys.size(); ++i)
    {
        const auto& k = m_keys[i];
        if (lx >= k.localX && lx <= k.localX + k.width &&
            ly >= k.localY && ly <= k.localY + k.height)
        {
            return i;
        }
    }
    return -1;
}

bool KeyboardInputHandler::Update(
    XMVECTOR gazePosWorld,
    XMVECTOR gazeDirWorld,
    const SpatialCoordinateSystem& /*coordinateSystem*/)
{
    if (!m_visible) return false;

    int prev = m_hoveredKey;
    m_hoveredKey = HitTestGaze(gazePosWorld, gazeDirWorld);
    return (m_hoveredKey != prev);
}

// ============================================================
// Input event handlers
// ============================================================

void KeyboardInputHandler::HandleKeyDown(KeyEventArgs const& args)
{
    // Map Windows VirtualKey → character insertion or action
    auto vk = args.VirtualKey();

    // Printable ASCII range
    if (vk >= VirtualKey::A && vk <= VirtualKey::Z)
    {
        bool shifted = m_shiftActive ^ m_capsLock;
        wchar_t ch = (wchar_t)(L'A' + ((int)vk - (int)VirtualKey::A));
        if (!shifted) ch = towlower(ch);
        m_inputText.insert(m_cursorPos, 1, ch);
        m_cursorPos++;
        if (m_shiftActive) m_shiftActive = false; // one-shot shift
    }
    else if (vk >= VirtualKey::Number0 && vk <= VirtualKey::Number9)
    {
        wchar_t ch = (wchar_t)(L'0' + ((int)vk - (int)VirtualKey::Number0));
        m_inputText.insert(m_cursorPos, 1, ch);
        m_cursorPos++;
    }
    else if (vk == VirtualKey::Space)
    {
        m_inputText.insert(m_cursorPos, 1, L' ');
        m_cursorPos++;
    }
    else if (vk == VirtualKey::Back && !m_inputText.empty() && m_cursorPos > 0)
    {
        m_inputText.erase(m_cursorPos - 1, 1);
        m_cursorPos--;
    }
    else if (vk == VirtualKey::Delete && m_cursorPos < m_inputText.size())
    {
        m_inputText.erase(m_cursorPos, 1);
    }
    else if (vk == VirtualKey::Left && m_cursorPos > 0)
    {
        m_cursorPos--;
    }
    else if (vk == VirtualKey::Right && m_cursorPos < m_inputText.size())
    {
        m_cursorPos++;
    }
    else if (vk == VirtualKey::Enter)
    {
        if (OnSubmit) OnSubmit(m_inputText);
        return; // don't fire OnTextChanged for submit
    }
    else if (vk == VirtualKey::Shift)
    {
        m_shiftActive = true;
        return;
    }
    else if (vk == VirtualKey::CapitalLock)
    {
        m_capsLock = !m_capsLock;
        return;
    }
    else if (vk == VirtualKey::Escape)
    {
        Hide();
        return;
    }
    else
    {
        // Punctuation – map common keys
        static const struct { VirtualKey vk; wchar_t normal; wchar_t shifted; } PUNCT[] = {
            { VirtualKey::Period,    L'.', L'>'  },
            { VirtualKey::Comma,     L',', L'<'  },
            { (VirtualKey)0xBE,      L'.', L'>'  },
            { (VirtualKey)0xBC,      L',', L'<'  },
            { (VirtualKey)0xBF,      L'/', L'?'  },
            { (VirtualKey)0xBA,      L';', L':'  },
            { (VirtualKey)0xDE,      L'\'',L'"'  },
            { (VirtualKey)0xBB,      L'=', L'+'  },
            { (VirtualKey)0xBD,      L'-', L'_'  },
        };
        for (auto& p : PUNCT)
        {
            if (p.vk == vk)
            {
                wchar_t ch = m_shiftActive ? p.shifted : p.normal;
                m_inputText.insert(m_cursorPos, 1, ch);
                m_cursorPos++;
                if (m_shiftActive) m_shiftActive = false;
                break;
            }
        }
    }

    if (OnTextChanged) OnTextChanged(m_inputText);
}

void KeyboardInputHandler::HandleKeyUp(KeyEventArgs const& args)
{
    if (args.VirtualKey() == VirtualKey::Shift)
        m_shiftActive = false;
}

void KeyboardInputHandler::HandleAirTap()
{
    if (!m_visible) return;
    if (m_hoveredKey < 0 || m_hoveredKey >= (int)m_keys.size()) return;

    const VirtualKey& k = m_keys[m_hoveredKey];

    if (k.value == L"\b")
    {
        // Backspace
        if (!m_inputText.empty() && m_cursorPos > 0)
        {
            m_inputText.erase(m_cursorPos - 1, 1);
            m_cursorPos--;
        }
    }
    else if (k.value == L"\n")
    {
        // Enter / submit
        if (OnSubmit) OnSubmit(m_inputText);
        return;
    }
    else if (k.label == L"CLR")
    {
        ClearText();
    }
    else
    {
        // Normal character
        bool shifted = m_shiftActive ^ m_capsLock;
        std::wstring val = k.value;
        if (!k.isSpecial && shifted && !val.empty())
            val[0] = towupper(val[0]);

        m_inputText.insert(m_cursorPos, val);
        m_cursorPos += val.size();
        if (m_shiftActive) m_shiftActive = false;
    }

    if (OnTextChanged) OnTextChanged(m_inputText);
}

// ============================================================
// Render
// ============================================================

void KeyboardInputHandler::Render(
    ID3D11DeviceContext* context,
    const XMFLOAT4X4&   viewProjection)
{
    if (!m_visible || !m_initialised) return;

    // Set pipeline state
    context->VSSetShader(m_vertexShader.Get(), nullptr, 0);
    context->PSSetShader(m_pixelShader.Get(), nullptr, 0);
    context->IASetInputLayout(m_inputLayout.Get());
    context->RSSetState(m_rasterizerState.Get());

    float blendFactor[4] = {};
    context->OMSetBlendState(m_blendState.Get(), blendFactor, 0xffffffff);
    context->OMSetDepthStencilState(m_depthStencilState.Get(), 0);

    UINT stride = sizeof(float) * 3, offset = 0;
    context->IASetVertexBuffers(0, 1, m_vertexBuffer.GetAddressOf(), &stride, &offset);
    context->IASetIndexBuffer(m_indexBuffer.Get(), DXGI_FORMAT_R16_UINT, 0);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetConstantBuffers(0, 1, m_constantBuffer.GetAddressOf());

    XMMATRIX vp = XMLoadFloat4x4(&viewProjection);
    XMMATRIX world = XMLoadFloat4x4(&m_panelWorld);

    // Draw background panel
    {
        XMMATRIX scale = XMMatrixScaling(PANEL_WIDTH, PANEL_HEIGHT, PANEL_DEPTH);
        XMMATRIX mvp   = XMMatrixTranspose(scale * world * vp);

        D3D11_MAPPED_SUBRESOURCE mapped;
        context->Map(m_constantBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        KeyCB* cb = (KeyCB*)mapped.pData;
        XMStoreFloat4x4(&cb->modelViewProj, mvp);
        cb->color = { 0.05f, 0.05f, 0.08f, 0.88f }; // dark translucent
        context->Unmap(m_constantBuffer.Get(), 0);

        context->DrawIndexed(6, 0, 0);
    }

    // Draw each key
    for (int i = 0; i < (int)m_keys.size(); ++i)
    {
        const VirtualKey& k = m_keys[i];
        bool hovered = (i == m_hoveredKey);

        // Key quad: scale to key size, translate to key position on panel
        XMMATRIX scale = XMMatrixScaling(k.width, k.height, PANEL_DEPTH * 2.f);
        XMMATRIX trans = XMMatrixTranslation(k.localX, k.localY, -PANEL_DEPTH);
        XMMATRIX mvp   = XMMatrixTranspose(scale * trans * world * vp);

        XMFLOAT4 col;
        if (hovered)
            col = { 0.2f, 0.6f, 1.0f, 0.95f }; // bright blue when gazed at
        else if (k.isSpecial)
            col = { 0.18f, 0.18f, 0.22f, 0.92f };
        else
            col = { 0.25f, 0.25f, 0.30f, 0.92f };

        D3D11_MAPPED_SUBRESOURCE mapped;
        context->Map(m_constantBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        KeyCB* cb = (KeyCB*)mapped.pData;
        XMStoreFloat4x4(&cb->modelViewProj, mvp);
        cb->color = col;
        context->Unmap(m_constantBuffer.Get(), 0);

        context->DrawIndexed(6, 0, 0);

        // Key border (slightly smaller, darker)
        float bInset = 0.001f;
        XMMATRIX bScale = XMMatrixScaling(k.width - bInset * 2, k.height - bInset * 2, 1.f);
        XMMATRIX bTrans = XMMatrixTranslation(k.localX + bInset, k.localY + bInset, -PANEL_DEPTH * 1.5f);
        XMMATRIX bMvp   = XMMatrixTranspose(bScale * bTrans * world * vp);

        XMFLOAT4 bCol = { col.x * 0.6f, col.y * 0.6f, col.z * 0.6f, 0.5f };

        context->Map(m_constantBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        cb = (KeyCB*)mapped.pData;
        XMStoreFloat4x4(&cb->modelViewProj, bMvp);
        cb->color = bCol;
        context->Unmap(m_constantBuffer.Get(), 0);

        context->DrawIndexed(6, 0, 0);
    }

    // NOTE: Key label text rendering requires a text/sprite renderer
    // (e.g. DirectWrite + D2D overlay, or a sprite-font system).
    // See the integration notes in README_KeyboardInput.md for how to
    // wire in a SpriteFont or DirectWrite text renderer for labels.
}

} // namespace BasicHologram
