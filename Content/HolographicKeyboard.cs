using System;
using System.Diagnostics;
using Windows.UI.Text.Core;
using Windows.UI.ViewManagement;

namespace HololensIKEA.Content
{
    /// <summary>
    /// System holographic keyboard for HoloLens 1 UWP DirectX apps.
    /// Uses ONLY CoreTextEditContext events (TextUpdating / SelectionUpdating / FocusRemoved).
    /// Do NOT mix CoreWindow.CharacterReceived or KeyDown while the IME is active.
    /// </summary>
    internal class HolographicKeyboard
    {
        private readonly CoreTextEditContext _context;
        private readonly InputPane           _inputPane;

        private string        _text      = "";
        private CoreTextRange _selection = new CoreTextRange { StartCaretPosition = 0, EndCaretPosition = 0 };

        public string Text => _text;

        /// <summary>Fires with the full accumulated text after every edit.</summary>
        public event Action<string> TextChanged;

        /// <summary>Fires when Enter (\r) is received. Hide() is called automatically.</summary>
        public event Action EnterPressed;

        public HolographicKeyboard()
        {
            var manager = CoreTextServicesManager.GetForCurrentView();
            _context = manager.CreateEditContext();
            _context.Name = "HologramTextInput";

            // Text InputScope gives the keyboard correct key labels
            _context.InputScope = CoreTextInputScope.Text;

            // Let Windows manage the keyboard UI; Show() notifies focus-enter
            _context.InputPaneDisplayPolicy = CoreTextInputPaneDisplayPolicy.Automatic;

            // Required: the system queries these to keep IME state consistent
            _context.TextRequested      += Context_TextRequested;
            _context.SelectionRequested += Context_SelectionRequested;

            // Main event: all characters, backspace, and Enter arrive here
            _context.TextUpdating      += Context_TextUpdating;
            _context.SelectionUpdating += Context_SelectionUpdating;
            _context.FocusRemoved      += Context_FocusRemoved;

            // IME composition stubs (required even if unused)
            _context.CompositionStarted   += (s, e) => { };
            _context.CompositionCompleted += (s, e) => { };

            _inputPane = InputPane.GetForCurrentView();
        }

        // CoreTextEditContext callbacks

        private void Context_TextRequested(
            CoreTextEditContext sender,
            CoreTextTextRequestedEventArgs args)
        {
            args.Request.Text = _text;
        }

        private void Context_SelectionRequested(
            CoreTextEditContext sender,
            CoreTextSelectionRequestedEventArgs args)
        {
            args.Request.Selection = _selection;
        }

        private void Context_SelectionUpdating(
            CoreTextEditContext sender,
            CoreTextSelectionUpdatingEventArgs args)
        {
            _selection = args.Selection;
            sender.NotifySelectionChanged(_selection);
            args.Result = CoreTextSelectionUpdatingResult.Succeeded;
        }

        private void Context_TextUpdating(
            CoreTextEditContext sender,
            CoreTextTextUpdatingEventArgs args)
        {
            int start = args.Range.StartCaretPosition;
            int end   = args.Range.EndCaretPosition;

            if (start < 0)            start = 0;
            if (end   < start)        end   = start;
            if (end   > _text.Length) end   = _text.Length;

            string incoming = args.Text;
            _text = _text.Substring(0, start) + incoming + _text.Substring(end);

            int newCaret = start + incoming.Length;
            _selection = new CoreTextRange
            {
                StartCaretPosition = newCaret,
                EndCaretPosition   = newCaret
            };

            // CRITICAL: without this the keyboard breaks after the first keypress
            sender.NotifyTextChanged(args.Range, _text.Length, _selection);

            args.Result = CoreTextTextUpdatingResult.Succeeded;

            Debug.WriteLine("[HolographicKeyboard] Text='" + _text + "'");
            TextChanged?.Invoke(_text);

            // Enter is delivered as \r by the IME
            if (incoming.Contains("\r"))
            {
                _text = _text.Replace("\r", "");
                EnterPressed?.Invoke();
                Hide();
            }
        }

        private void Context_FocusRemoved(
            CoreTextEditContext sender,
            object args)
        {
            Hide();
        }

        // Public API

        /// <summary>Give focus to this edit context and show the system keyboard.</summary>
        public void Show()
        {
            _context.NotifyFocusEnter();
            _inputPane.TryShow();
        }

        /// <summary>Release focus and dismiss the system keyboard.</summary>
        public void Hide()
        {
            _context.NotifyFocusLeave();
            _inputPane.TryHide();
        }

        /// <summary>Reset the text buffer (call before starting a new entry).</summary>
        public void Clear()
        {
            _text = "";
            _selection = new CoreTextRange { StartCaretPosition = 0, EndCaretPosition = 0 };
        }
    }
}