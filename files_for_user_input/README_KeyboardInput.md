# BasicHologram – Keyboard + Voice Input for HoloLens 1

## Overview

Three input methods, one shared text buffer:

| Method | Class | How it works |
|---|---|---|
| **Bluetooth keyboard** | `KeyboardInputHandler` | `CoreWindow::KeyDown/Up` |
| **Holographic virtual keypad** | `KeyboardInputHandler` | Gaze to highlight, air-tap to press |
| **Microphone / voice** | `VoiceInputHandler` | Command words + continuous dictation |

All three write into the same `m_currentInputText` / `m_currentCursorPos`
pair so you only have one place to read input from.

---

## Files

| File | Purpose |
|---|---|
| `KeyboardInputHandler.h/.cpp` | Keypad rendering + BT keyboard |
| `VoiceInputHandler.h/.cpp` | Speech recognition (commands + dictation) |
| `HolographicMain_additions.cpp` | Keyboard wiring diff |
| `VoiceInput_additions.cpp` | Voice wiring diff |

---

## VoiceInputHandler – two modes

### Mode 1 – Command recognition (always-on)
Uses a `SpeechRecognitionListConstraint` compiled from your registered phrases.
Starts immediately on app launch; very low CPU / battery overhead.
Works **fully offline** — no internet required.

**Built-in commands wired in `VoiceInput_additions.cpp`:**

| Say | Effect |
|---|---|
| `"select"` | Air-tap on currently gazed key |
| `"keyboard"` | Toggle virtual keypad visibility |
| `"dictate"` | Start / stop free-form dictation |
| `"stop listening"` | Stop dictation |
| `"backspace"` | Delete last character |
| `"clear"` | Erase entire input buffer |
| `"submit"` | Submit current text and hide keyboard |

Add your own with `AddCommand(L"my phrase", callback)`.

### Mode 2 – Dictation (on demand)
Uses `SpeechRecognitionTopicConstraint(Dictation)` — the full cloud-assisted
speech model. Activated by saying `"dictate"` or calling `StartDictationAsync()`.
Results append to the shared text buffer via `SetDictationTarget(...)`.

**Hypothesis events** (`OnHypothesis`) fire while the user is still speaking,
letting you show a live faint preview before the sentence is committed.

---

## Integration steps

### 1. Copy files
Add all four `.h`/`.cpp` files to `Samples/BasicHologram/cppwinrt/` and
include them in the Visual Studio project.

### 2. Follow the diffs
- `HolographicMain_additions.cpp` — keyboard wiring (steps 1-9)
- `VoiceInput_additions.cpp` — voice wiring (steps 1-8)

### 3. Add manifest capability (REQUIRED)

In `Package.appxmanifest`, add inside `<Capabilities>`:

```xml
<DeviceCapability Name="microphone" />
```

Without this, `SpeechRecognizer` throws `ACCESS_DENIED` on device.

### 4. Check linker inputs

`Project → Properties → Linker → Input → Additional Dependencies`:
```
D3DCompiler.lib
```

---

## Confidence tuning

```cpp
m_voiceInput->SetConfidenceLevel(VoiceConfidenceLevel::High);  // less noise, fewer matches
m_voiceInput->SetConfidenceLevel(VoiceConfidenceLevel::Low);   // noisy environment
// Default: Medium
```

---

## Language / locale

Both recognisers default to the system locale. To pin a language:

```cpp
// In VoiceInputHandler.cpp InitializeAsync(), replace:
m_commandRecognizer = SpeechRecognizer();
// with:
m_commandRecognizer = SpeechRecognizer(
    winrt::Windows::Globalization::Language(L"en-US"));
```

HoloLens 1 ships speech packs for: `en-US`, `de-DE`, `fr-FR`,
`es-ES`, `it-IT`, `ja-JP`, `zh-Hans`.

---

## HoloLens 1 constraints to be aware of

- **Microphone always available** — four built-in mics, no external hardware needed.
- **Internet required for dictation** — `SpeechRecognitionTopicConstraint` sends
  audio to Microsoft servers. Command `ListConstraint` works fully offline.
- **Privacy indicator** — Windows always shows a mic icon when recording;
  this cannot be suppressed.
- **Two recogniser instances** — command and dictation use separate
  `SpeechRecognizer` objects, which is fine. Don't call `StartAsync()` on
  the same instance twice simultaneously.

---

## Build target

- Windows SDK ≥ 10.0.14393 (HoloLens 1 baseline)
- Visual Studio 2022, C++/WinRT, `/std:c++17`
- Platform: `ARM` (device) or `x86` (emulator)
