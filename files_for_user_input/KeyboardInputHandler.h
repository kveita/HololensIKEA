//********************************************************* 
// KeyboardInputHandler.h
//
// Handles text input for Microsoft HoloLens 1 in two ways:
//   1. Bluetooth physical keyboard via CoreWindow key events
//   2. Holographic virtual keypad (gaze + air-tap to select keys)
//
// Usage:
//   - Create one instance in your AppMain / HolographicMain.
//   - Call Update() every frame (pass current gaze direction).
//   - Call HandleKeyDown() from your CoreWindow::KeyDown handler.
//   - Call HandleKeyUp()   from your CoreWindow::KeyUp handler.
//   - Call HandleAirTap()  when a spatial-press/air-tap is detected.
//   - Call GetCurrentText() to read the accumulated string.
//   - Call Render()        to draw the keypad into the holographic frame.
//*********************************************************

#pragma once

#include "pch.h"
#include <string>
#include <vector>
#include <functional>
#include <DirectXMath.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.Perception.Spatial.h>

namespace BasicHologram
{
    // ------------------------------------------------------------------
    // Layout for one key on the virtual keypad
    // ------------------------------------------------------------------
    struct VirtualKey
    {
        std::wstring label;         // Text shown on the key (e.g. L"A")
        std::wstring value;         // Text inserted when tapped (may differ, e.g. L"\n")
        bool         isSpecial;     // Backspace / Enter / Space etc.

        // 2-D position on the keypad panel (metres, local to panel origin)
        float localX;
        float localY;
        float width;
        float height;
    };

    // ------------------------------------------------------------------
    // KeyboardInputHandler
    // ------------------------------------------------------------------
    class KeyboardInputHandler
    {
    public:
        KeyboardInputHandler();
        ~KeyboardInputHandler() = default;

        // ---- Lifecycle -----------------------------------------------

        /// Call once after D3D device is created to initialise GPU resources.
        void CreateDeviceDependentResources(
            ID3D11Device*        device,
            ID3D11DeviceContext* context);

        void ReleaseDeviceDependentResources();

        // ---- Per-frame -----------------------------------------------

        /// Supply the user's current gaze ray (world-space origin + direction).
        /// Returns true if the highlighted key changed (so caller can trigger
        /// haptic feedback etc.).
        bool Update(
            DirectX::XMVECTOR gazePosWorld,
            DirectX::XMVECTOR gazeDirWorld,
            const winrt::Windows::Perception::Spatial::SpatialCoordinateSystem& coordinateSystem);

        /// Render the virtual keypad into the current holographic frame.
        /// Call between BeginFrame / EndFrame in your render loop.
        void Render(
            ID3D11DeviceContext*        context,
            const DirectX::XMFLOAT4X4& viewProjection);  // combined VP matrix

        // ---- Input events --------------------------------------------

        /// Forward from CoreWindow::KeyDown. Handles Bluetooth / USB keyboard.
        void HandleKeyDown(winrt::Windows::UI::Core::KeyEventArgs const& args);

        /// Forward from CoreWindow::KeyUp (reserved for future use).
        void HandleKeyUp(winrt::Windows::UI::Core::KeyEventArgs const& args);

        /// Call when the user performs an air-tap / spatial press.
        /// Activates whichever key is currently highlighted by the gaze cursor.
        void HandleAirTap();

        // ---- Visibility ----------------------------------------------

        void Show() { m_visible = true;  }
        void Hide() { m_visible = false; }
        bool IsVisible() const { return m_visible; }
        void ToggleVisibility() { m_visible = !m_visible; }

        // ---- Result --------------------------------------------------

        /// Returns the current accumulated input text.
        const std::wstring& GetCurrentText() const { return m_inputText; }

        /// Clears the accumulated text.
        void ClearText() { m_inputText.clear(); m_cursorPos = 0; }

        /// Optional callback: invoked every time text changes.
        std::function<void(const std::wstring&)> OnTextChanged;

        /// Optional callback: invoked when user presses Enter.
        std::function<void(const std::wstring&)> OnSubmit;

        // ---- Placement -----------------------------------------------

        /// Call to place the keypad panel in world space.
        /// Typically called once (e.g. on first air-tap) to position it
        /// 1 metre in front of the user.
        void PlaceInFrontOfUser(
            DirectX::XMVECTOR cameraPosition,
            DirectX::XMVECTOR cameraForward);

    private:
        // ---- Key layout ---------------------------------------------
        void BuildKeyLayout();
        void AppendKey(const VirtualKey& key);

        // ---- Hit testing --------------------------------------------
        int  HitTestGaze(DirectX::XMVECTOR gazePosWorld,
                         DirectX::XMVECTOR gazeDirWorld);

        // ---- Rendering helpers --------------------------------------
        void DrawPanel(ID3D11DeviceContext* context,
                       const DirectX::XMFLOAT4X4& viewProjection);
        void DrawKey(ID3D11DeviceContext* context,
                     const VirtualKey& key,
                     bool highlighted, bool pressed,
                     const DirectX::XMFLOAT4X4& viewProjection);
        void DrawTextLabel(const std::wstring& text,
                           float worldX, float worldY, float worldZ);

        // ---- State ---------------------------------------------------
        bool         m_visible       = false;
        bool         m_initialised   = false;
        std::wstring m_inputText;
        size_t       m_cursorPos     = 0;   // insertion point (future use)
        int          m_hoveredKey    = -1;  // index into m_keys, -1 = none
        bool         m_shiftActive   = false;
        bool         m_capsLock      = false;

        // ---- Key data ------------------------------------------------
        std::vector<VirtualKey> m_keys;

        // ---- Panel world transform ----------------------------------
        // The keypad is rendered as a world-locked flat panel.
        DirectX::XMFLOAT4X4 m_panelWorld;   // world matrix of the panel
        DirectX::XMFLOAT3   m_panelNormal;  // facing direction (world)

        // Panel size (metres)
        static constexpr float PANEL_WIDTH  = 0.60f; // 60 cm wide
        static constexpr float PANEL_HEIGHT = 0.22f; // 22 cm tall
        static constexpr float PANEL_DEPTH  = 0.004f;

        // ---- D3D resources ------------------------------------------
        Microsoft::WRL::ComPtr<ID3D11Buffer>        m_vertexBuffer;
        Microsoft::WRL::ComPtr<ID3D11Buffer>        m_indexBuffer;
        Microsoft::WRL::ComPtr<ID3D11Buffer>        m_constantBuffer;
        Microsoft::WRL::ComPtr<ID3D11VertexShader>  m_vertexShader;
        Microsoft::WRL::ComPtr<ID3D11PixelShader>   m_pixelShader;
        Microsoft::WRL::ComPtr<ID3D11InputLayout>   m_inputLayout;
        Microsoft::WRL::ComPtr<ID3D11RasterizerState> m_rasterizerState;
        Microsoft::WRL::ComPtr<ID3D11BlendState>    m_blendState;
        Microsoft::WRL::ComPtr<ID3D11DepthStencilState> m_depthStencilState;

        // Per-object constant buffer layout (matches HLSL)
        struct KeyCB
        {
            DirectX::XMFLOAT4X4 modelViewProj;
            DirectX::XMFLOAT4   color;
        };
    };

} // namespace BasicHologram
