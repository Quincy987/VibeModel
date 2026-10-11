using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Tolerant accessors over JavaScriptSerializer's untyped object graph
    /// (Dictionary&lt;string, object&gt; / object[] / ArrayList / primitives). A pack file is
    /// hand-edited by an office admin, so every read degrades to a default instead of throwing.
    /// </summary>
    internal static class PackJson
    {
        internal static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static Dictionary<string, object> Parse(string json)
        {
            var raw = Serializer.DeserializeObject(json);
            return AsDict(raw);
        }

        public static Dictionary<string, object> AsDict(object o)
        {
            if (o is Dictionary<string, object> d)
                return new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase);
            return null;
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v)) return null;
            return AsDict(v);
        }

        public static string GetString(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return null;
            if (v is string s) return s;
            if (v is IConvertible c) return Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }

        public static double? GetDouble(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return null;
            try
            {
                if (v is string s)
                    return double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : (double?)null;
                return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        public static bool? GetBool(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return null;
            if (v is bool b) return b;
            if (v is string s && bool.TryParse(s, out var r)) return r;
            return null;
        }

        /// <summary>A list of strings; a single string is accepted as a one-item list.</summary>
        public static List<string> GetStrings(Dictionary<string, object> d, string key)
        {
            var result = new List<string>();
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return result;
            if (v is string single)
            {
                if (!string.IsNullOrWhiteSpace(single)) result.Add(single);
                return result;
            }
            if (v is IEnumerable e)
            {
                foreach (var item in e)
                    if (item is string s && !string.IsNullOrWhiteSpace(s))
                        result.Add(s);
            }
            return result;
        }

        public static List<Dictionary<string, object>> GetDictList(Dictionary<string, object> d, string key)
        {
            var result = new List<Dictionary<string, object>>();
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return result;
            return AsDictList(v);
        }

        public static List<Dictionary<string, object>> AsDictList(object v)
        {
            var result = new List<Dictionary<string, object>>();
            if (v is IEnumerable e && !(v is string) && !(v is IDictionary))
            {
                foreach (var item in e)
                {
                    var dict = AsDict(item);
                    if (dict != null) result.Add(dict);
                }
            }
            return result;
        }

        /// <summary>A string→string map (non-string values are skipped).</summary>
        public static Dictionary<string, string> GetStringMap(Dictionary<string, object> d, string key)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sub = GetDict(d, key);
            if (sub == null) return result;
            foreach (var kv in sub)
                if (kv.Value is string s)
                    result[kv.Key] = s;
            return result;
        }
    }
}
