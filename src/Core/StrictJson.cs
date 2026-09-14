using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VencordAutoUpdate {
// Small bounded JSON reader: unlike JavaScriptSerializer, duplicate object keys are errors.
internal sealed class StrictJson {
    readonly string text;
    int index;
    StrictJson(string value) { text = value; }
    internal static object Parse(string value) {
        StrictJson p = new StrictJson(value); object result = p.Value(0); p.Space();
        if (p.index != value.Length) throw new IOException("Trailing JSON content.");
        return result;
    }
    internal static Dictionary<string, object> Object(object value) {
        Dictionary<string, object> result = value as Dictionary<string, object>;
        if (result == null) throw new IOException("Expected JSON object.");
        return result;
    }
    internal static string String(Dictionary<string, object> map, string key) {
        object value;
        if (!map.TryGetValue(key, out value) || !(value is string)) throw new IOException("Missing JSON string: " + key);
        return (string)value;
    }
    void Space() { while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r' || text[index] == '\n')) index++; }
    object Value(int depth) {
        if (depth > 64) throw new IOException("JSON nesting limit exceeded.");
        Space(); if (index >= text.Length) throw new IOException("Truncated JSON.");
        char c = text[index];
        if (c == '"') return ReadString();
        if (c == '{') {
            index++; Space(); Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            if (Take('}')) return map;
            do {
                Space(); if (index >= text.Length || text[index] != '"') throw new IOException("Invalid JSON key.");
                string key = ReadString(); Space(); Expect(':');
                if (map.ContainsKey(key)) throw new IOException("Duplicate JSON key.");
                map.Add(key, Value(depth + 1)); Space(); if (Take('}')) return map;
                Expect(',');
            } while (true);
        }
        if (c == '[') {
            index++; Space(); List<object> list = new List<object>(); if (Take(']')) return list;
            do { list.Add(Value(depth + 1)); Space(); if (Take(']')) return list; Expect(','); } while (true);
        }
        foreach (string literal in new [] { "true", "false", "null" }) {
            if (index + literal.Length <= text.Length && text.Substring(index, literal.Length) == literal) {
                index += literal.Length; return literal == "null" ? null : (object)(literal == "true");
            }
        }
        int start = index; Take('-');
        if (!Take('0')) { if (index >= text.Length || text[index] < '1' || text[index] > '9') throw new IOException("Invalid JSON value."); while (index < text.Length && Char.IsDigit(text[index])) index++; }
        if (Take('.')) { int before = index; while (index < text.Length && Char.IsDigit(text[index])) index++; if (index == before) throw new IOException("Invalid number."); }
        if (Take('e') || Take('E')) { if (!Take('+')) Take('-'); int before = index; while (index < text.Length && Char.IsDigit(text[index])) index++; if (index == before) throw new IOException("Invalid exponent."); }
        decimal number;
        if (!Decimal.TryParse(text.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new IOException("JSON number outside supported bounds.");
        return number;
    }
    bool Take(char c) { if (index < text.Length && text[index] == c) { index++; return true; } return false; }
    void Expect(char c) { if (!Take(c)) throw new IOException("Invalid JSON delimiter."); }
    string ReadString() {
        Expect('"'); StringBuilder result = new StringBuilder();
        while (index < text.Length) {
            char c = text[index++]; if (c == '"') return result.ToString();
            if (c < 32) throw new IOException("Invalid JSON control character.");
            if (c != '\\') { result.Append(c); continue; }
            if (index >= text.Length) throw new IOException("Truncated JSON escape.");
            c = text[index++];
            switch (c) {
                case '"': case '\\': case '/': result.Append(c); break;
                case 'b': result.Append('\b'); break; case 'f': result.Append('\f'); break;
                case 'n': result.Append('\n'); break; case 'r': result.Append('\r'); break; case 't': result.Append('\t'); break;
                case 'u':
                    int code;
                    if (index + 4 > text.Length || !Int32.TryParse(text.Substring(index,4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,out code)) throw new IOException("Invalid Unicode escape.");
                    result.Append((char)code); index += 4; break;
                default: throw new IOException("Invalid JSON escape.");
            }
        }
        throw new IOException("Unterminated JSON string.");
    }
}
}
