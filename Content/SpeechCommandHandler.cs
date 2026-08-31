using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Handles voice input for product IDs (elnummer) using continuous speech recognition.
    /// 
    /// When the user is gazing at the keyboard:
    ///   - Spoken numbers (English/Norwegian) are typed into the keyboard field
    ///   - Non-numeric words trigger a product text search
    /// 
    /// Global commands (work regardless of gaze):
    ///   - "Clear" / "Remove all" / "Fjern alle" — removes all products
    ///   - "Close" / "Lukk" — dismisses search dialog
    /// </summary>
    internal sealed class SpeechCommandHandler : IDisposable
    {
        private SpeechRecognizer _recognizer;
        private bool _isListening = false;
        private bool _isInitialized = false;

        /// <summary>
        /// Set to true by the main loop when the user is gazing at the keyboard panel.
        /// When true, recognized speech is routed to keyboard input instead of direct product load.
        /// </summary>
        public bool IsGazingAtKeyboard { get; set; } = false;

        /// <summary>Fires when a valid product ID (elnummer) is recognized (only when NOT gazing at keyboard).</summary>
        public event Action<int> OnProductIdRecognized;

        /// <summary>Fires when "clear" or "remove all" is spoken.</summary>
        public event Action OnClearAllProducts;

        /// <summary>Fires when "close" or "lukk" is spoken (to dismiss dialogs).</summary>
        public event Action OnDismissDialog;

        /// <summary>Fires when "bookmarks" or "bøker" is spoken.</summary>
        public event Action OnShowBookmarks;

        /// <summary>Fires when a search query should be applied to bookmarks.</summary>
        /// <summary>Fires with recognized text so the app can match bookmark product aliases.</summary>
        public event Action<string> OnBookmarkProductRequested;
        public event Action<string> OnSearchBookmarks;

        /// <summary>Fires when recognized text should be typed into the keyboard.</summary>
        public event Action<string> OnTextForKeyboard;

        /// <summary>Fires when a non-numeric search query is spoken while gazing at keyboard.</summary>
        public event Action<string> OnSearchQuery;

        /// <summary>Fires when recognition status changes (for UI feedback).</summary>
        public event Action<string> OnStatusChanged;

        /// <summary>True when actively listening for voice commands.</summary>
        public bool IsListening => _isListening;

        /// <summary>
        /// Initializes the speech recognizer. Must be called before StartListening().
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                // Request microphone permission
                bool hasMic = await RequestMicrophonePermissionAsync();
                if (!hasMic)
                {
                    Debug.WriteLine("[Speech] Microphone permission denied");
                    OnStatusChanged?.Invoke("Mic denied");
                    return;
                }

                // Create recognizer with system language
                _recognizer = new SpeechRecognizer();

                // Add state change handler
                _recognizer.StateChanged += Recognizer_StateChanged;

                // Use dictation constraint for flexible number recognition
                // This allows the user to say numbers naturally
                var dictationConstraint = new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation, "ProductNumber");
                _recognizer.Constraints.Add(dictationConstraint);

                // Compile constraints
                var result = await _recognizer.CompileConstraintsAsync();
                if (result.Status != SpeechRecognitionResultStatus.Success)
                {
                    Debug.WriteLine("[Speech] Constraint compilation failed: " + result.Status);
                    OnStatusChanged?.Invoke("Init failed");
                    return;
                }

                // Set up continuous recognition events
                _recognizer.ContinuousRecognitionSession.ResultGenerated += Session_ResultGenerated;
                _recognizer.ContinuousRecognitionSession.Completed += Session_Completed;

                _isInitialized = true;
                Debug.WriteLine("[Speech] Initialized successfully");
                OnStatusChanged?.Invoke("Ready");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Speech] Initialization error: " + ex.Message);
                OnStatusChanged?.Invoke("Init error");
            }
        }

        /// <summary>
        /// Starts continuous listening for voice commands.
        /// </summary>
        public async Task StartListeningAsync()
        {
            if (!_isInitialized || _isListening)
                return;

            try
            {
                await _recognizer.ContinuousRecognitionSession.StartAsync();
                _isListening = true;
                Debug.WriteLine("[Speech] Listening started");
                OnStatusChanged?.Invoke("Listening...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Speech] Start listening error: " + ex.Message);
                OnStatusChanged?.Invoke("Error");
            }
        }

        /// <summary>
        /// Stops continuous listening.
        /// </summary>
        public async Task StopListeningAsync()
        {
            if (!_isInitialized || !_isListening)
                return;

            try
            {
                await _recognizer.ContinuousRecognitionSession.CancelAsync();
                _isListening = false;
                Debug.WriteLine("[Speech] Listening stopped");
                OnStatusChanged?.Invoke("Stopped");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Speech] Stop listening error: " + ex.Message);
            }
        }

        /// <summary>
        /// Toggles listening state.
        /// </summary>
        public async Task ToggleListeningAsync()
        {
            if (_isListening)
                await StopListeningAsync();
            else
                await StartListeningAsync();
        }

        private void Recognizer_StateChanged(SpeechRecognizer sender, SpeechRecognizerStateChangedEventArgs args)
        {
            Debug.WriteLine("[Speech] State: " + args.State);
        }

        private void Session_ResultGenerated(
            SpeechContinuousRecognitionSession sender,
            SpeechContinuousRecognitionResultGeneratedEventArgs args)
        {
            // Only process medium or high confidence results
            if (args.Result.Confidence == SpeechRecognitionConfidence.Low ||
                args.Result.Confidence == SpeechRecognitionConfidence.Rejected)
            {
                return;
            }

            string text = args.Result.Text?.Trim() ?? "";
            string textLower = text.ToLowerInvariant();
            Debug.WriteLine("[Speech] Recognized: \"" + text + "\" (" + args.Result.Confidence + ")");

            // ── Global commands (always active) ──────────────────────────
            if (textLower.Contains("clear") || textLower.Contains("remove all") || 
                textLower.Contains("fjern alle") || textLower.Contains("slett"))
            {
                OnClearAllProducts?.Invoke();
                OnStatusChanged?.Invoke("Cleared");
                return;
            }

            if (textLower == "close" || textLower == "lukk" || textLower == "avbryt" || textLower == "cancel")
            {
                OnDismissDialog?.Invoke();
                OnStatusChanged?.Invoke("Dismissed");
                return;
            }

            // ── Bookmarks commands (always active) ───────────────────────────
            if (textLower.Contains("bookmark") || textLower.Contains("bookmarks") ||
                textLower == "bøker" || textLower == "bokmerke")
            {
                // Check if followed by search terms
                if (textLower.Length > 15)
                {
                    string query = StripCommandPrefixes(textLower);
                    if (!string.IsNullOrWhiteSpace(query) && query.Length >= 2)
                    {
                        OnSearchBookmarks?.Invoke(query);
                        OnStatusChanged?.Invoke("Searching bookmarks: " + query);
                        return;
                    }
                }
                OnShowBookmarks?.Invoke();
                OnStatusChanged?.Invoke("Show bookmarks");
                return;
            }
            // Bookmark product aliases are evaluated by the app against the
            // bookmarks loaded from bookmarks.json (for example, "BILLY").
            OnBookmarkProductRequested?.Invoke(textLower);

            // ── Keyboard-gazing mode: route to keyboard ──────────────────
            if (IsGazingAtKeyboard)
            {
                // Try to extract digits from spoken text
                string converted = ConvertSpokenDigits(textLower);
                string digitsOnly = ExtractDigits(converted);

                if (!string.IsNullOrEmpty(digitsOnly))
                {
                    // User said a number — type it into the keyboard
                    Debug.WriteLine("[Speech→Keyboard] Digits: " + digitsOnly);
                    OnTextForKeyboard?.Invoke(digitsOnly);
                    OnStatusChanged?.Invoke("Typed: " + digitsOnly);
                }
                else
                {
                    // User said non-numeric text — treat as search query
                    // Strip command prefixes
                    string query = StripCommandPrefixes(textLower);
                    if (!string.IsNullOrWhiteSpace(query) && query.Length >= 2)
                    {
                        Debug.WriteLine("[Speech→Search] Query: " + query);
                        OnSearchQuery?.Invoke(query);
                        OnStatusChanged?.Invoke("Searching: " + query);
                    }
                }
                return;
            }

            // ── Not gazing at keyboard: legacy direct product load ────────
            int productId = ExtractProductNumber(textLower);
            if (productId > 0)
            {
                Debug.WriteLine("[Speech] Product ID extracted: " + productId);
                OnProductIdRecognized?.Invoke(productId);
                OnStatusChanged?.Invoke("Adding #" + productId);
            }
        }

        private void Session_Completed(
            SpeechContinuousRecognitionSession sender,
            SpeechContinuousRecognitionCompletedEventArgs args)
        {
            _isListening = false;

            if (args.Status != SpeechRecognitionResultStatus.Success)
            {
                Debug.WriteLine("[Speech] Session completed with status: " + args.Status);
                OnStatusChanged?.Invoke("Stopped");
            }
        }

        /// <summary>
        /// Extracts a product number from spoken text.
        /// Handles: "add 12345", "product 67890", "number 11111", or just "12345"
        /// Also converts spoken digits like "one two three" to "123"
        /// </summary>
        private int ExtractProductNumber(string text)
        {
            // Remove common command prefixes
            text = StripCommandPrefixes(text);

            // Convert spoken word digits to numbers
            text = ConvertSpokenDigits(text);

            string numberStr = ExtractDigits(text);
            if (string.IsNullOrEmpty(numberStr))
                return 0;

            // Parse the number
            if (int.TryParse(numberStr, out int result) && result > 0)
                return result;

            return 0;
        }

        /// <summary>Strips known command prefixes from text.</summary>
        private string StripCommandPrefixes(string text)
        {
            string[] prefixes = { "add", "product", "number", "load", "show", "search", "find",
                                   "legg til", "produkt", "nummer", "vis", "søk", "finn" };
            foreach (var prefix in prefixes)
            {
                if (text.StartsWith(prefix))
                {
                    text = text.Substring(prefix.Length).Trim();
                    break;
                }
            }
            return text;
        }

        /// <summary>Extracts only digit characters from text.</summary>
        private string ExtractDigits(string text)
        {
            var digits = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (char.IsDigit(c))
                    digits.Append(c);
            }
            return digits.ToString();
        }

        /// <summary>
        /// Converts spoken word numbers to digits.
        /// "one two three four five" → "12345"
        /// </summary>
        private string ConvertSpokenDigits(string text)
        {
            var wordToDigit = new Dictionary<string, string>
            {
                // English
                { "zero", "0" }, { "one", "1" }, { "two", "2" }, { "three", "3" },
                { "four", "4" }, { "five", "5" }, { "six", "6" }, { "seven", "7" },
                { "eight", "8" }, { "nine", "9" },
                // Norwegian
                { "null", "0" }, { "en", "1" }, { "ett", "1" }, { "to", "2" }, 
                { "tre", "3" }, { "fire", "4" }, { "fem", "5" }, { "seks", "6" },
                { "sju", "7" }, { "syv", "7" }, { "åtte", "8" }, { "atte", "8" },
                { "ni", "9" }
            };

            string result = text;
            foreach (var kvp in wordToDigit)
            {
                // Use word boundaries to avoid partial replacements
                result = System.Text.RegularExpressions.Regex.Replace(
                    result, 
                    @"\b" + kvp.Key + @"\b", 
                    kvp.Value,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return result;
        }

        private async Task<bool> RequestMicrophonePermissionAsync()
        {
            try
            {
                var settings = new Windows.Media.Capture.MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio,
                    MediaCategory = Windows.Media.Capture.MediaCategory.Speech
                };
                var capture = new Windows.Media.Capture.MediaCapture();
                await capture.InitializeAsync(settings);
                capture.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Speech] Mic permission check failed: " + ex.Message);
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_isListening)
                {
                    _recognizer?.ContinuousRecognitionSession.CancelAsync().AsTask().Wait(1000);
                }
                _recognizer?.Dispose();
            }
            catch { }
            _recognizer = null;
            _isInitialized = false;
            _isListening = false;
        }
    }
}
