using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AgentRoom
{
    internal sealed class RoomError : Exception
    {
        internal readonly string Code;
        internal RoomError(string code, string message) : base(message) { Code = code; }
    }

    internal static class Json
    {
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 2097152, RecursionLimit = 24 }; }
        internal static string Write(object value) { return Serializer().Serialize(value); }
        internal static Dictionary<string, object> Read(string text, int maximum = 65536)
        {
            if (text == null || text.Length > maximum) throw new RoomError("invalid_request", "JSON exceeds the size limit.");
            if (text.Length != 0 && text[0] == '\uFEFF') text = text.Substring(1);
            try
            {
                var result = Serializer().DeserializeObject(text) as Dictionary<string, object>;
                if (result == null) throw new ArgumentException();
                return result;
            }
            catch (Exception e) { if (!(e is ArgumentException) && !(e is InvalidOperationException)) throw;
                throw new RoomError("invalid_request", "Expected a JSON object."); }
        }
        internal static Dictionary<string, object> Obj(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
        internal static string Text(IDictionary<string, object> data, string key, string fallback = "", int max = 8192)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return fallback;
            var text = value as string;
            if (text == null || text.Length > max || text.IndexOf('\0') >= 0)
                throw new RoomError("invalid_argument", key + " must be text of at most " + max + " characters.");
            return text;
        }
        internal static string Required(IDictionary<string, object> data, string key, int max = 8192)
        {
            string value = Text(data, key, "", max).Trim();
            if (value.Length == 0) throw new RoomError("invalid_argument", key + " is required.");
            return value;
        }
        internal static long Number(IDictionary<string, object> data, string key, long fallback = 0)
        {
            object value;
            // An explicit null means "not given", as it does for text: callers often send "reply_to": null.
            if (!data.TryGetValue(key, out value) || value == null) return fallback;
            if (!(value is int) && !(value is long)) throw new RoomError("invalid_argument", key + " must be an integer.");
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        internal static Dictionary<string, object> Map(IDictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return Obj();
            var map = value as Dictionary<string, object>;
            if (map == null) throw new RoomError("invalid_argument", key + " must be an object.");
            return map;
        }
        internal static string[] Strings(IDictionary<string, object> data, string key, int maximum = 8)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return new string[0];
            var array = value as object[];
            if (array == null || array.Length > maximum || array.Any(x => !(x is string)))
                throw new RoomError("invalid_argument", key + " must be a short array of strings.");
            return array.Cast<string>().ToArray();
        }
        internal static string Canonical(object value)
        {
            var map = value as IDictionary<string, object>;
            if (map != null) return "{" + String.Join(",", map.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => Write(k) + ":" + Canonical(map[k]))) + "}";
            var list = value as IEnumerable;
            if (list != null && !(value is string)) return "[" + String.Join(",", list.Cast<object>().Select(Canonical)) + "]";
            return Write(value);
        }
        internal static string Hash(string value)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        internal static string Secret()
        {
            byte[] bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
        internal static Dictionary<string, object> Ok(object value) { return Obj("ok", true, "result", value); }
        internal static Dictionary<string, object> Error(string code, string message) { return Obj("ok", false, "error", Obj("code", code, "message", message)); }
    }
}
