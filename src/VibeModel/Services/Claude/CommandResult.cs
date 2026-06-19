using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace VibeModel.Services.Claude
{
    public enum ResponseFormat { Text, Json }

    /// <summary>
    /// One command's outcome, rendered as text (default) or JSON at the HTTP boundary.
    ///
    /// Three flavors:
    ///  - Ok(text, data)         — a migrated command: text is built by the SAME code as before
    ///                             (byte-identical), data is the additive structured payload.
    ///  - Error(code, msg, sugg) — a NATIVE structured error: text renders "ERROR: msg" plus an
    ///                             additive "Hint: sugg" line; code appears only in JSON.
    ///  - Legacy(rawText)        — an unmigrated command: rawText is rendered VERBATIM in text
    ///                             mode (byte-identical to pre-Plan-03), wrapped for JSON.
    /// </summary>
    public sealed class CommandResult
    {
        public bool Success { get; private set; }
        public object Data { get; private set; }        // Dictionary / List — never ArrayList
        public string Text { get; private set; }        // human form (migrated success)
        public string ErrorCode { get; private set; }   // machine code; JSON only
        public string Message { get; private set; }     // human error message
        public string Suggestion { get; private set; }  // self-correction hint; optional

        // Legacy passthrough: original text, rendered verbatim in text mode.
        private string _rawText;
        private bool _isLegacy;

        public static CommandResult Ok(string text, object data = null)
            => new CommandResult { Success = true, Text = text, Data = data };

        public static CommandResult Error(string code, string message, string suggestion = null)
            => new CommandResult { Success = false, ErrorCode = code, Message = message, Suggestion = suggestion };

        // Wrap an unmigrated command's raw string. Detects the project-wide "ERROR" convention,
        // but always preserves the original text verbatim for text-mode rendering.
        public static CommandResult Legacy(string rawText)
        {
            bool isError = rawText != null && rawText.StartsWith("ERROR", StringComparison.Ordinal);
            return new CommandResult
            {
                Success = !isError,
                _rawText = rawText,
                _isLegacy = true,
                Text = isError ? null : rawText,
                ErrorCode = isError ? "INTERNAL" : null,
                Message = isError ? StripErrorPrefix(rawText) : null
            };
        }

        public string Render(ResponseFormat fmt, JavaScriptSerializer json)
        {
            if (fmt == ResponseFormat.Text)
            {
                // Legacy passthrough → verbatim (byte-identical to pre-Plan-03 output).
                if (_isLegacy) return _rawText ?? "";
                if (Success) return Text ?? "";
                // Native structured error → "ERROR: msg" with an additive Hint line.
                var t = "ERROR: " + Message;
                if (!string.IsNullOrEmpty(Suggestion)) t += "\nHint: " + Suggestion;
                return t;
            }

            return json.Serialize(ToJsonObject());
        }

        // The JSON envelope as an object graph (not serialized) — lets the batch path embed
        // per-command results in an array and serialize the whole thing once.
        public object ToJsonObject()
        {
            return Success
                ? (object)new Dictionary<string, object>
                  {
                      { "ok", true },
                      { "data", Data },
                      { "text", Data == null ? (Text ?? _rawText) : null }
                  }
                : new Dictionary<string, object>
                  {
                      { "ok", false },
                      { "error", new Dictionary<string, object>
                          {
                              { "code", ErrorCode },
                              { "message", Message },
                              { "suggestion", Suggestion }
                          }
                      }
                  };
        }

        // Text-mode render without needing a serializer (for callers like the batch path).
        public string RenderText() => Render(ResponseFormat.Text, null);

        // Convert a TransactionHelper result ("ERROR: msg" on failure) into a structured error.
        // Modification commands call this to turn the helper's string into a CommandResult.
        public static CommandResult FromTransactionError(string error, string code = "TRANSACTION_FAILED",
            string suggestion = null)
        {
            return Error(code, StripErrorPrefix(error), suggestion);
        }

        // "ERROR: foo" -> "foo"; tolerant of "ERROR foo".
        private static string StripErrorPrefix(string text)
        {
            if (text == null) return null;
            int colon = text.IndexOf(':');
            if (colon >= 0 && colon < 8) return text.Substring(colon + 1).Trim();
            return text.Trim();
        }
    }
}
