using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PartyLearningLoot.Commands;

/// <summary>Builds aligned plain-text tables for console output (telnet, web console and the in-game F1 console).</summary>
internal sealed class TextTable
{
    private readonly string[] headers;
    private readonly List<string[]> rows = new List<string[]>();
    private readonly bool[] rightAligned;

    public TextTable(params string[] _headers)
    {
        headers = _headers;
        rightAligned = new bool[_headers.Length];
    }

    public TextTable AlignRight(params int[] _columns)
    {
        foreach (int column in _columns)
        {
            rightAligned[column] = true;
        }
        return this;
    }

    public TextTable Add(params object[] _cells)
    {
        rows.Add(_cells.Select(c => c?.ToString() ?? string.Empty).ToArray());
        return this;
    }

    public int Count => rows.Count;

    public IEnumerable<string> Lines()
    {
        int columns = headers.Length;
        var widths = new int[columns];
        for (int i = 0; i < columns; i++)
        {
            widths[i] = Math.Max(headers[i].Length, rows.Count == 0 ? 0 : rows.Max(r => i < r.Length ? r[i].Length : 0));
        }
        yield return Format(headers, widths);
        yield return string.Join("  ", widths.Select(w => new string('-', w)));
        foreach (string[] row in rows)
        {
            yield return Format(row, widths);
        }
    }

    private string Format(string[] _cells, int[] _widths)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _widths.Length; i++)
        {
            string cell = i < _cells.Length ? _cells[i] : string.Empty;
            if (i > 0)
            {
                sb.Append("  ");
            }
            sb.Append(rightAligned[i] ? cell.PadLeft(_widths[i]) : (i == _widths.Length - 1 ? cell : cell.PadRight(_widths[i])));
        }
        return sb.ToString().TrimEnd();
    }
}
