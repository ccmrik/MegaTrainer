using System.Collections.Generic;

namespace MegaTrainer
{
    /// <summary>
    /// Lightweight JSON parser for trainer_state.json.
    /// Avoids external dependencies — parses the known MegaLoad format:
    /// { "active": { "cheats": [ { "id": "...", "enabled": true/false }, ... ] }, "saved_profiles": [...] }
    /// </summary>
    internal static class JsonParser
    {
        /// <summary>
        /// Parse trainer_state.json and return a dictionary of cheat_id → enabled.
        /// </summary>
        public static Dictionary<string, bool> ParseTrainerData(string json)
        {
            var result = new Dictionary<string, bool>();
            if (string.IsNullOrEmpty(json)) return result;

            // Find the "active" → "cheats" array
            int cheatsIdx = json.IndexOf("\"cheats\"");
            if (cheatsIdx < 0) return result;

            int arrayStart = json.IndexOf('[', cheatsIdx);
            if (arrayStart < 0) return result;

            int arrayEnd = FindMatchingBracket(json, arrayStart, '[', ']');
            if (arrayEnd < 0) return result;

            string cheatsArray = json.Substring(arrayStart, arrayEnd - arrayStart + 1);

            // Parse each { "id": "...", "enabled": true/false } object
            int pos = 0;
            while (pos < cheatsArray.Length)
            {
                int objStart = cheatsArray.IndexOf('{', pos);
                if (objStart < 0) break;

                int objEnd = FindMatchingBracket(cheatsArray, objStart, '{', '}');
                if (objEnd < 0) break;

                string obj = cheatsArray.Substring(objStart, objEnd - objStart + 1);

                string id = ExtractStringValue(obj, "id");
                bool enabled = ExtractBoolValue(obj, "enabled");

                if (!string.IsNullOrEmpty(id))
                    result[id] = enabled;

                pos = objEnd + 1;
            }

            return result;
        }

        private static int FindMatchingBracket(string s, int start, char open, char close)
        {
            int depth = 0;
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == open) depth++;
                else if (s[i] == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static string ExtractStringValue(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int keyIdx = json.IndexOf(pattern);
            if (keyIdx < 0) return null;

            int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
            if (colonIdx < 0) return null;

            int quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return null;

            int quoteEnd = json.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return null;

            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static bool ExtractBoolValue(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int keyIdx = json.IndexOf(pattern);
            if (keyIdx < 0) return false;

            int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
            if (colonIdx < 0) return false;

            string rest = json.Substring(colonIdx + 1).TrimStart();
            return rest.StartsWith("true");
        }

        /// <summary>
        /// Extract a float value from a JSON key at the top level of active object.
        /// Falls back to defaultValue if key not found.
        /// </summary>
        public static float ExtractFloatValue(string json, string key, float defaultValue = 1.0f)
        {
            // Look for "key": number within the "active" block
            int activeIdx = json.IndexOf("\"active\"");
            if (activeIdx < 0) return defaultValue;

            // Find the active object bounds
            int braceStart = json.IndexOf('{', activeIdx);
            if (braceStart < 0) return defaultValue;
            int braceEnd = FindMatchingBracket(json, braceStart, '{', '}');
            if (braceEnd < 0) return defaultValue;

            string activeBlock = json.Substring(braceStart, braceEnd - braceStart + 1);
            string pattern = "\"" + key + "\"";
            int keyIdx = activeBlock.IndexOf(pattern);
            if (keyIdx < 0) return defaultValue;

            int colonIdx = activeBlock.IndexOf(':', keyIdx + pattern.Length);
            if (colonIdx < 0) return defaultValue;

            // Parse the number
            string rest = activeBlock.Substring(colonIdx + 1).TrimStart();
            int len = 0;
            while (len < rest.Length && (char.IsDigit(rest[len]) || rest[len] == '.' || rest[len] == '-'))
                len++;
            if (len == 0) return defaultValue;

            float result;
            if (float.TryParse(rest.Substring(0, len), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result))
                return result;
            return defaultValue;
        }
    }
}
