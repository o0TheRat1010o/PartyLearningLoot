using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PartyLearningLoot.Core;

/// <summary>
/// Small, dependency-free JSON writer that produces exactly one line (no pretty printing) per record.
/// Commas are inserted automatically; non-finite numbers are written as null because JSON has no NaN.
/// </summary>
public sealed class JsonLineWriter
{
    private struct Frame
    {
        public bool IsArray;
        public bool HasItems;
    }

    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private readonly StringBuilder sb = new StringBuilder(256);
    private readonly List<Frame> frames = new List<Frame>(8);

    public int Depth => frames.Count;

    /// <summary>Optional metadata for the producer (the audit log stores the record sequence number here).</summary>
    public long Sequence { get; set; }

    /// <summary>Optional metadata for the producer (the audit log stores the record type here).</summary>
    public string RecordType { get; set; }

    public JsonLineWriter BeginObject(string _name = null) => Open(_name, false);

    public JsonLineWriter EndObject() => Close(false);

    public JsonLineWriter BeginArray(string _name = null) => Open(_name, true);

    public JsonLineWriter EndArray() => Close(true);

    public JsonLineWriter Prop(string _name, string _value)
    {
        Prefix(_name);
        WriteString(_value);
        return this;
    }

    public JsonLineWriter Prop(string _name, bool _value)
    {
        Prefix(_name);
        sb.Append(_value ? "true" : "false");
        return this;
    }

    public JsonLineWriter Prop(string _name, int _value)
    {
        Prefix(_name);
        sb.Append(_value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public JsonLineWriter Prop(string _name, long _value)
    {
        Prefix(_name);
        sb.Append(_value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public JsonLineWriter Prop(string _name, float _value)
    {
        Prefix(_name);
        WriteFloat(_value);
        return this;
    }

    public JsonLineWriter Prop(string _name, double _value)
    {
        Prefix(_name);
        WriteDouble(_value);
        return this;
    }

    public JsonLineWriter PropNull(string _name)
    {
        Prefix(_name);
        sb.Append("null");
        return this;
    }

    public JsonLineWriter PropStrings(string _name, IEnumerable<string> _values)
    {
        BeginArray(_name);
        if (_values != null)
        {
            foreach (string value in _values)
            {
                Value(value);
            }
        }
        return EndArray();
    }

    public JsonLineWriter PropInts(string _name, IEnumerable<int> _values)
    {
        BeginArray(_name);
        if (_values != null)
        {
            foreach (int value in _values)
            {
                Value(value);
            }
        }
        return EndArray();
    }

    public JsonLineWriter Value(string _value)
    {
        Prefix(null);
        WriteString(_value);
        return this;
    }

    public JsonLineWriter Value(int _value)
    {
        Prefix(null);
        sb.Append(_value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public JsonLineWriter Value(float _value)
    {
        Prefix(null);
        WriteFloat(_value);
        return this;
    }

    public JsonLineWriter Value(double _value)
    {
        Prefix(null);
        WriteDouble(_value);
        return this;
    }

    public JsonLineWriter Value(bool _value)
    {
        Prefix(null);
        sb.Append(_value ? "true" : "false");
        return this;
    }

    /// <summary>Closes every open container and returns the finished line (without newline).</summary>
    public string Finish()
    {
        while (frames.Count > 0)
        {
            Close(frames[frames.Count - 1].IsArray);
        }
        return sb.ToString();
    }

    public override string ToString() => sb.ToString();

    public static string Escape(string _value)
    {
        var writer = new JsonLineWriter();
        writer.WriteString(_value);
        return writer.sb.ToString();
    }

    // ----- internals ------------------------------------------------------------------------

    private JsonLineWriter Open(string _name, bool _isArray)
    {
        Prefix(_name);
        sb.Append(_isArray ? '[' : '{');
        frames.Add(new Frame { IsArray = _isArray });
        return this;
    }

    private JsonLineWriter Close(bool _isArray)
    {
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("JSON container stack underflow.");
        }
        Frame top = frames[frames.Count - 1];
        if (top.IsArray != _isArray)
        {
            throw new InvalidOperationException(_isArray ? "EndArray called inside an object." : "EndObject called inside an array.");
        }
        frames.RemoveAt(frames.Count - 1);
        sb.Append(_isArray ? ']' : '}');
        return this;
    }

    private void Prefix(string _name)
    {
        if (frames.Count > 0)
        {
            int top = frames.Count - 1;
            Frame frame = frames[top];
            if (frame.HasItems)
            {
                sb.Append(',');
            }
            frame.HasItems = true;
            frames[top] = frame;
            if (!frame.IsArray && _name == null)
            {
                throw new InvalidOperationException("Object members need a name.");
            }
        }
        if (_name != null)
        {
            WriteString(_name);
            sb.Append(':');
        }
    }

    private void WriteFloat(float _value)
    {
        if (float.IsNaN(_value) || float.IsInfinity(_value))
        {
            sb.Append("null");
            return;
        }
        sb.Append(_value.ToString("R", CultureInfo.InvariantCulture));
    }

    private void WriteDouble(double _value)
    {
        if (double.IsNaN(_value) || double.IsInfinity(_value))
        {
            sb.Append("null");
            return;
        }
        sb.Append(_value.ToString("R", CultureInfo.InvariantCulture));
    }

    private void WriteString(string _value)
    {
        if (_value == null)
        {
            sb.Append("null");
            return;
        }
        sb.Append('"');
        foreach (char c in _value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    // Control characters and the JavaScript line separators U+2028/U+2029 are escaped.
                    if (c < 0x20 || c == LineSeparator || c == ParagraphSeparator)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
