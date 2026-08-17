//=========================================================
// HolographicMain.cpp  –  ADDITIONS / DIFF
//
// This file shows exactly what to add to the existing
// BasicHologram HolographicMain.cpp (or AppMain.cpp).
// Lines prefixed with [ADD] must be inserted at the
// indicated locations. Lines prefixed with [EXISTING] are
// the original lines shown for context only.
//=========================================================

//---- (1) Near the top of the file, add the include --------

// [ADD]
#include "KeyboardInputHandler.h"


//---- (2) In the class body (HolographicMain.h or AppMain.h)
//         add the member variable: ----------------------------

// [ADD]  (inside the class, alongside SpinningCubeRenderer etc.)
std::unique_ptr<KeyboardInputHandler> m_keyboardInput;


//---- (3) In HolographicMain::Initialize() -----------------
//         (or the equivalent CreateDeviceDependentResources)

// [EXISTING]  // ... existing renderer setup ...
// [ADD]
m_keyboardInput = std::make_unique<KeyboardInputHandler>();
m_keyboardInput->CreateDeviceDependentResources(
    m_deviceResources->GetD3DDevice(),
    m_deviceResources->GetD3DDeviceContext());

// Wire up the text-changed callback (adapt to your UI needs)
m_keyboardInput->OnTextChanged = [this](const std::wstring& text)
{
    // e.g. store in a member, display as holographic text, etc.
    m_currentInputText = text;
    OutputDebugStringW((L"[Input] " + text + L"\n").c_str());
};

m_keyboardInput->OnSubmit = [this](const std::wstring& text)
{
    OutputDebugStringW((L"[Submit] " + text + L"\n").c_str());
    m_keyboardInput->ClearText();
    m_keyboardInput->Hide();
    // TODO: handle the submitted text (e.g. change cube colour,
    //       navigate, etc.)
};


//---- (4) In HolographicMain::Update() --------------------
//         After computing the gaze ray from the spatial input:

// [EXISTING]
// auto gaze = SpatialPointerPose::TryGetAtTimestamp(
//                 coordinateSystem, prediction.Timestamp());

// [ADD] – after gaze is obtained:
if (gaze && m_keyboardInput->IsVisible())
{
    auto head = gaze.Head();
    auto pos  = head.Position();    // Windows.Foundation.Numerics.float3
    auto dir  = head.ForwardDirection();

    XMVECTOR gazePos = XMVectorSet(pos.x, pos.y, pos.z, 1.f);
    XMVECTOR gazeDir = XMVectorSet(dir.x, dir.y, dir.z, 0.f);

    m_keyboardInput->Update(gazePos, gazeDir, coordinateSystem);
}


//---- (5) Hook the AIR-TAP gesture to open/confirm keys ----
//         In the block where you already handle InteractionSourcePressed:

// [EXISTING]
// for (auto& sourceState : interactionManager.GetDetectedSourcesAtTimestamp(...))
// {
//   ...existing air-tap logic...
// }

// [ADD]  inside that same pressed-event handler:
// (If you use SpatialGestureRecognizer, add a Tapped handler instead)
m_keyboardInput->HandleAirTap();

// -- Toggle visibility with a "bloom" gesture or second tap:
// If you want bloom to open/close the keyboard, hook
// m_keyboardInput->ToggleVisibility() to the bloom event.


//---- (6) On first air-tap, place keyboard in front of user -

// [ADD]  (e.g. in your air-tap handler, before HandleAirTap())
if (!m_keyboardInput->IsVisible())
{
    auto head = gaze.Head();
    auto pos  = head.Position();
    auto fwd  = head.ForwardDirection();
    m_keyboardInput->PlaceInFrontOfUser(
        XMVectorSet(pos.x, pos.y, pos.z, 1.f),
        XMVectorSet(fwd.x, fwd.y, fwd.z, 0.f));
    m_keyboardInput->Show();
}
else
{
    m_keyboardInput->HandleAirTap();
}


//---- (7) In HolographicMain::Render() --------------------
//         After rendering the spinning cube, add:

// [ADD]
if (m_keyboardInput->IsVisible())
{
    // Build combined ViewProjection for the current camera.
    // The sample already has this in viewProjectionConstantBufferData.
    // Pass it here:
    XMFLOAT4X4 vp;
    XMStoreFloat4x4(&vp, XMMatrixTranspose(
        XMLoadFloat4x4(&viewProjectionConstantBufferData.viewProjection[0])));

    m_keyboardInput->Render(
        m_deviceResources->GetD3DDeviceContext(),
        vp);
}


//---- (8) Hook CoreWindow keyboard events -----------------
//         In App::SetWindow() (or wherever you set up CoreWindow):

// [ADD]
window.KeyDown({ this, &App::OnKeyDown });
window.KeyUp  ({ this, &App::OnKeyUp   });

// Then add the handlers:

// [ADD]  App::OnKeyDown
void App::OnKeyDown(
    winrt::Windows::UI::Core::CoreWindow const& /*sender*/,
    winrt::Windows::UI::Core::KeyEventArgs const& args)
{
    if (m_main && m_main->GetKeyboardInput())
        m_main->GetKeyboardInput()->HandleKeyDown(args);
}

// [ADD]  App::OnKeyUp
void App::OnKeyUp(
    winrt::Windows::UI::Core::CoreWindow const& /*sender*/,
    winrt::Windows::UI::Core::KeyEventArgs const& args)
{
    if (m_main && m_main->GetKeyboardInput())
        m_main->GetKeyboardInput()->HandleKeyUp(args);
}

// Add a public accessor on HolographicMain:
// [ADD]  in HolographicMain.h
//   KeyboardInputHandler* GetKeyboardInput() const
//   { return m_keyboardInput.get(); }


//---- (9) Release resources on device lost ----------------

// [ADD]  in ReleaseDeviceDependentResources():
if (m_keyboardInput)
    m_keyboardInput->ReleaseDeviceDependentResources();

// [ADD]  in CreateDeviceDependentResources() (called after device recreation):
if (m_keyboardInput)
    m_keyboardInput->CreateDeviceDependentResources(
        m_deviceResources->GetD3DDevice(),
        m_deviceResources->GetD3DDeviceContext());
