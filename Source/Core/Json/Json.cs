using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RoslynProbe.Json
{
    /// <summary>
    /// Minimal dependency-free JSON parser and serializer.
    /// object -&gt; Dictionary&lt;string, object&gt;, array -&gt; List&lt;object&gt;,
    /// string -&gt; string, number -&gt; long (integral) or double, bool -&gt; bool, null -&gt; null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string json)
        {
            if (json == null)
                throw new ArgumentNullException(nameof(json));
            var p = new Parser(json);
            object result = p.ParseValue();
            p.SkipWhitespace();
            if (p.Index != p.Length)
                throw new Exception("Invalid JSON: trailing characters at position " + p.Index);
            return result;
        }

        public static string Stringify(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        // ---- Parser ----
        private sealed class Parser
        {
            private readonly string _s;

            public Parser(string s)
            {
                _s = s;
            }

            public int Index { get; private set; }
            public int Length => _s.Length;

            public void SkipWhitespace()
            {
                while (Index < _s.Length)
                {
                    char c = _s[Index];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                        Index++;
                    else
                        break;
                }
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (Index >= _s.Length)
                    throw new Exception("Invalid JSON: unexpected end of input");
                char c = _s[Index];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': case 'f': return ParseBool();
                    case 'n': return ParseNull();
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                            return ParseNumber();
                        throw new Exception("Invalid JSON: unexpected character '" + c + "' at position " + Index);
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                Index++; // consume '{'
                SkipWhitespace();
                if (Index < _s.Length && _s[Index] == '}')
                {
                    Index++;
                    return dict;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (Index >= _s.Length || _s[Index] != '"')
                        throw new Exception("Invalid JSON: expected string key at position " + Index);
                    string key = ParseString();
                    SkipWhitespace();
                    if (Index >= _s.Length || _s[Index] != ':')
                        throw new Exception("Invalid JSON: expected ':' at position " + Index);
                    Index++;
                    object val = ParseValue();
                    dict[key] = val;
                    SkipWhitespace();
                    if (Index >= _s.Length)
                        throw new Exception("Invalid JSON: unexpected end in object");
                    char c = _s[Index];
                    if (c == ',')
                    {
                        Index++;
                        continue;
                    }
                    if (c == '}')
                    {
                        Index++;
                        return dict;
                    }
                    throw new Exception("Invalid JSON: expected ',' or '}' at position " + Index);
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                Index++; // consume '['
                SkipWhitespace();
                if (Index < _s.Length && _s[Index] == ']')
                {
                    Index++;
                    return list;
                }
                while (true)
                {
                    object val = ParseValue();
                    list.Add(val);
                    SkipWhitespace();
                    if (Index >= _s.Length)
                        throw new Exception("Invalid JSON: unexpected end in array");
                    char c = _s[Index];
                    if (c == ',')
                    {
                        Index++;
                        continue;
                    }
                    if (c == ']')
                    {
                        Index++;
                        return list;
                    }
                    throw new Exception("Invalid JSON: expected ',' or ']' at position " + Index);
                }
            }

            private string ParseString()
            {
                Index++; // consume opening quote
                var sb = new StringBuilder();
                while (Index < _s.Length)
                {
                    char c = _s[Index++];
                    if (c == '"')
                        return sb.ToString();
                    if (c == '\\')
                    {
                        if (Index >= _s.Length)
                            throw new Exception("Invalid JSON: unfinished escape sequence");
                        char e = _s[Index++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (Index + 4 > _s.Length)
                                    throw new Exception("Invalid JSON: incomplete unicode escape");
                                string hex = _s.Substring(Index, 4);
                                Index += 4;
                                int code;
                                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    throw new Exception("Invalid JSON: bad unicode escape \\u" + hex);
                                sb.Append((char)code);
                                break;
                            default:
                                throw new Exception("Invalid JSON: unknown escape \\" + e);
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                throw new Exception("Invalid JSON: unterminated string");
            }

            private object ParseNumber()
            {
                int start = Index;
                bool isDouble = false;
                if (Index < _s.Length && _s[Index] == '-')
                    Index++;
                while (Index < _s.Length)
                {
                    char c = _s[Index];
                    if (c >= '0' && c <= '9')
                    {
                        Index++;
                    }
                    else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        isDouble = true;
                        Index++;
                    }
                    else
                    {
                        break;
                    }
                }
                string num = _s.Substring(start, Index - start);
                if (isDouble)
                {
                    double d;
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        throw new Exception("Invalid JSON: bad number '" + num + "'");
                    return d;
                }
                long l;
                if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return l;
                double dl;
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dl))
                    return dl;
                throw new Exception("Invalid JSON: bad number '" + num + "'");
            }

            private object ParseBool()
            {
                if (TryMatch("true"))
                    return true;
                if (TryMatch("false"))
                    return false;
                throw new Exception("Invalid JSON: expected true/false at position " + Index);
            }

            private object ParseNull()
            {
                if (TryMatch("null"))
                    return null;
                throw new Exception("Invalid JSON: expected null at position " + Index);
            }

            private bool TryMatch(string word)
            {
                if (Index + word.Length > _s.Length)
                    return false;
                for (int i = 0; i < word.Length; i++)
                {
                    if (_s[Index + i] != word[i])
                        return false;
                }
                Index += word.Length;
                return true;
            }
        }

        // ---- Serializer ----
        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is string s)
            {
                WriteString(sb, s);
                return;
            }
            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }
            if (value is double d)
            {
                sb.Append(FormatDouble(d));
                return;
            }
            if (value is long l)
            {
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is Dictionary<string, object> dict)
            {
                WriteObject(sb, dict);
                return;
            }
            if (value is List<object> list)
            {
                WriteArray(sb, list);
                return;
            }
            if (value is System.Collections.IEnumerable enu && !(value is string))
            {
                WriteEnumerable(sb, enu);
                return;
            }
            if (value is int || value is short || value is byte)
            {
                sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is float || value is decimal)
            {
                sb.Append(FormatDouble(Convert.ToDouble(value)));
                return;
            }
            throw new Exception("Cannot stringify value of type " + value.GetType().FullName);
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        private static void WriteEnumerable(StringBuilder sb, System.Collections.IEnumerable enu)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in enu)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
        }

        private static void WriteArray(StringBuilder sb, List<object> list)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u" + ((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string FormatDouble(double d)
        {
            if (double.IsInfinity(d) || double.IsNaN(d))
                throw new Exception("Cannot stringify non-finite double");
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0)
                s += ".0";
            return s;
        }
    }
}
