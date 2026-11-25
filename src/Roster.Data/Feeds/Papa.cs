// Port of papaparse 5.5.4 (node_modules/papaparse/papaparse.js): the path that
// lib/csv/parse.ts and lib/csv/ukgLeave.ts take, Papa.parse(text, { header: true,
// skipEmptyLines: true, transformHeader, transform }) on a whole string. Everything that
// decides which rows and cells come out is copied: the byte-order mark, line-ending and
// delimiter guessing, quoted fields (with papaparse's tolerance for spaces after a closing
// quote, and its handling of malformed quotes), empty-line skipping, duplicate-header
// renaming and the __parsed_extra overflow. Streaming, workers, dynamic typing, comments
// and the error list (which the app never reads) are not.

using System.Globalization;
using System.Text;
using Roster.Engine.Core;

namespace Roster.Data.Feeds;

/// <summary>
/// One data row of a parsed CSV, keyed by header: papaparse's <c>header: true</c> object.
/// A row shorter than the header row lacks the trailing keys; cells past the last header
/// are kept in <see cref="ParsedExtra"/>.
/// </summary>
public sealed class CsvRow
{
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private List<string>? _extra;

    /// <summary>The row's keys in insertion (column) order: <c>Object.keys(row)</c>.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>Cells beyond the header row (<c>row.__parsed_extra</c>), or null.</summary>
    public IReadOnlyList<string>? ParsedExtra => _extra;

    /// <summary><c>header in row</c>.</summary>
    public bool Has(string key) => _values.ContainsKey(key);

    /// <summary><c>row[key]</c>: null when the row has no such key.</summary>
    public string? this[string key] => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary><c>row[key] = value</c>: a repeated key keeps its first position.</summary>
    public void Set(string key, string value)
    {
        if (!_values.ContainsKey(key)) _keys.Add(key);
        _values[key] = value;
    }

    internal void AddExtra(string value) => (_extra ??= []).Add(value);

    /// <summary>A row from header/value pairs, in order (for tests and direct feeds).</summary>
    public static CsvRow Of(params (string Key, string Value)[] cells)
    {
        var row = new CsvRow();
        foreach (var (k, v) in cells) row.Set(k, v);
        return row;
    }
}

internal static class Papa
{
    /// <summary>The parse options the app uses, minus the two that are always on.</summary>
    public sealed record Config(Func<string, string>? TransformHeader, Func<string, string>? Transform);

    /// <summary><c>result.meta.fields</c> and <c>result.data</c>.</summary>
    public sealed record Result(IReadOnlyList<string> Fields, IReadOnlyList<CsvRow> Data);

    private const string DefaultDelimiter = ",";
    private const char QuoteChar = '"';
    private const string ParsedExtraKey = "__parsed_extra";
    private static readonly string[] DelimitersToGuess = [",", "\t", "|", ";", "\u001E", "\u001F"];
    private static readonly string[] BadDelimiters = ["\r", "\n", "\"", "﻿"];

    /// <summary>
    /// <c>Papa.parse(input, { header: true, skipEmptyLines: true, transformHeader, transform })</c>.
    /// </summary>
    public static Result Parse(string input, Config config)
    {
        // CsvToJson: a string input loses a leading byte-order mark.
        input = StripBom(input);

        // ParserHandle.parse(input, baseIndex 0, ignoreLastRow false): one chunk, the whole string.
        var newline = GuessLineEndings(input);
        var guess = GuessDelimiter(input, newline);
        var delimiter = guess ?? DefaultDelimiter;
        var data = new CoreParser(delimiter, newline, preview: 0, header: true, config.TransformHeader).Parse(input);

        // processResults: skip empty lines, take the header row, then key each row by it.
        data = data.Where(r => !IsEmptyLine(r)).ToList();
        var fields = new List<string>();
        if (data.Count > 0)
        {
            foreach (var h in data[0])
            {
                var header = StripBom(h);
                if (config.TransformHeader is not null) header = config.TransformHeader(header);
                fields.Add(header);
            }
            data.RemoveAt(0);
        }

        var rows = new List<CsvRow>(data.Count);
        foreach (var source in data)
        {
            var row = new CsvRow();
            for (var j = 0; j < source.Count; j++)
            {
                var field = j >= fields.Count ? ParsedExtraKey : fields[j];
                var value = config.Transform is not null ? config.Transform(source[j]) : source[j];
                if (field == ParsedExtraKey) row.AddExtra(value);
                else row.Set(field, value);
            }
            rows.Add(row);
        }
        return new Result(fields, rows);
    }

    private static string StripBom(string s) => s.Length > 0 && s[0] == '﻿' ? s[1..] : s;

    // skipEmptyLines: true (not 'greedy'): only a line with one empty cell is empty.
    private static bool IsEmptyLine(List<string> row) => row.Count == 1 && row[0].Length == 0;

    /// <summary><c>input.indexOf(value, from)</c>: -1 past the end instead of throwing.</summary>
    private static int IndexOf(string input, string value, int from) =>
        from > input.Length ? -1 : input.IndexOf(value, Math.Max(from, 0), StringComparison.Ordinal);

    private static int IndexOf(string input, char value, int from) =>
        from >= input.Length ? -1 : input.IndexOf(value, Math.Max(from, 0));

    private static string GuessLineEndings(string input)
    {
        input = input.Length > 1024 * 1024 ? input[..(1024 * 1024)] : input; // max length 1 MB
        // Replace all the text inside quotes: /"([^]*?)"/gm.
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var open = input.IndexOf(QuoteChar, i);
            var close = open == -1 ? -1 : IndexOf(input, QuoteChar, open + 1);
            if (close == -1)
            {
                sb.Append(input, i, input.Length - i);
                break;
            }
            sb.Append(input, i, open - i);
            i = close + 1;
        }
        var text = sb.ToString();

        var r = text.Split('\r');
        var n = text.Split('\n');
        var nAppearsFirst = n.Length > 1 && n[0].Length < r[0].Length;
        if (r.Length == 1 || nAppearsFirst) return "\n";

        var numWithN = 0;
        foreach (var part in r)
            if (part.Length > 0 && part[0] == '\n') numWithN++;
        return numWithN >= r.Length / 2.0 ? "\r\n" : "\r";
    }

    /// <summary>The best delimiter, or null when none is convincing (papaparse then uses ",").</summary>
    private static string? GuessDelimiter(string input, string newline)
    {
        string? bestDelim = null;
        double? bestDelta = null;
        double? maxFieldCount = null;

        foreach (var delim in DelimitersToGuess)
        {
            double delta = 0, avgFieldCount = 0;
            var emptyLinesCount = 0;
            int? fieldCountPrevRow = null;

            var preview = new CoreParser(delim, newline, preview: 10, header: false, null).Parse(input);
            foreach (var row in preview)
            {
                if (IsEmptyLine(row))
                {
                    emptyLinesCount++;
                    continue;
                }
                var fieldCount = row.Count;
                avgFieldCount += fieldCount;
                if (fieldCountPrevRow is null)
                {
                    fieldCountPrevRow = fieldCount;
                    continue;
                }
                if (fieldCount > 0)
                {
                    delta += Math.Abs(fieldCount - fieldCountPrevRow.Value);
                    fieldCountPrevRow = fieldCount;
                }
            }

            if (preview.Count > 0) avgFieldCount /= preview.Count - emptyLinesCount; // 0/0 is NaN, as in JS

            if ((bestDelta is null || delta <= bestDelta) &&
                (maxFieldCount is null || avgFieldCount > maxFieldCount) && avgFieldCount > 1.99)
            {
                bestDelta = delta;
                bestDelim = delim;
                maxFieldCount = avgFieldCount;
            }
        }
        return bestDelim;
    }

    /// <summary>papaparse's core <c>Parser</c>: text to rows of raw cells.</summary>
    private sealed class CoreParser(string delim, string newline, int preview, bool header, Func<string, string>? transformHeader)
    {
        public List<List<string>> Parse(string input)
        {
            // Delimiter must be valid.
            if (Array.IndexOf(BadDelimiters, delim) > -1) delim = ",";

            var data = new List<List<string>>();
            if (input.Length == 0) return Returnable(data);

            var inputLen = input.Length;
            var delimLen = delim.Length;
            var newlineLen = newline.Length;
            var cursor = 0;
            var row = new List<string>();

            // Fast mode: no quote character anywhere, so a plain split is exact.
            if (input.IndexOf(QuoteChar) == -1)
            {
                var lines = input.Split(newline, StringSplitOptions.None);
                for (var i = 0; i < lines.Length; i++)
                {
                    data.Add([.. lines[i].Split(delim, StringSplitOptions.None)]);
                    if (preview > 0 && i >= preview)
                    {
                        data = data.GetRange(0, preview);
                        return Returnable(data);
                    }
                }
                return Returnable(data);
            }

            var nextDelim = IndexOf(input, delim, cursor);
            var nextNewline = IndexOf(input, newline, cursor);
            var quoteSearch = IndexOf(input, QuoteChar, cursor);

            // Parser loop.
            for (;;)
            {
                // Field has an opening quote.
                if (JsText.CharIs(input, cursor, QuoteChar))
                {
                    // Start the search for the closing quote where the cursor is, then skip the opening quote.
                    quoteSearch = cursor;
                    cursor++;

                    for (;;)
                    {
                        // Find the closing quote.
                        quoteSearch = IndexOf(input, QuoteChar, quoteSearch + 1);

                        // No other quotes: no closing quote (papaparse records MissingQuotes).
                        if (quoteSearch == -1) return Finish(null);

                        // Closing quote at EOF.
                        if (quoteSearch == inputLen - 1)
                            return Finish(Unescape(JsText.Substring(input, cursor, quoteSearch)));

                        // An escaped quote ("") is part of the data; skip it.
                        if (JsText.CharIs(input, quoteSearch + 1, QuoteChar))
                        {
                            quoteSearch++;
                            continue;
                        }

                        if (nextDelim != -1 && nextDelim < quoteSearch + 1)
                            nextDelim = IndexOf(input, delim, quoteSearch + 1);
                        if (nextNewline != -1 && nextNewline < quoteSearch + 1)
                            nextNewline = IndexOf(input, newline, quoteSearch + 1);

                        // Check up to nextDelim or nextNewline, whichever is closest.
                        var checkUpTo = nextNewline == -1 ? nextDelim : Math.Min(nextDelim, nextNewline);
                        var spacesBetweenQuoteAndDelimiter = ExtraSpaces(checkUpTo);

                        // Closing quote followed by the delimiter, or by spaces and then the delimiter.
                        if (JsText.Substr(input, quoteSearch + 1 + spacesBetweenQuoteAndDelimiter, delimLen) == delim)
                        {
                            row.Add(Unescape(JsText.Substring(input, cursor, quoteSearch)));
                            cursor = quoteSearch + 1 + spacesBetweenQuoteAndDelimiter + delimLen;

                            // If the character after the delimiter is not a quote, find the next quote.
                            if (!JsText.CharIs(input, quoteSearch + 1 + spacesBetweenQuoteAndDelimiter + delimLen, QuoteChar))
                                quoteSearch = IndexOf(input, QuoteChar, cursor);
                            nextDelim = IndexOf(input, delim, cursor);
                            nextNewline = IndexOf(input, newline, cursor);
                            break;
                        }

                        var spacesBetweenQuoteAndNewLine = ExtraSpaces(nextNewline);

                        // Closing quote followed by a newline, or by spaces and then a newline.
                        var afterQuote = quoteSearch + 1 + spacesBetweenQuoteAndNewLine;
                        if (JsText.Substring(input, afterQuote, afterQuote + newlineLen) == newline)
                        {
                            row.Add(Unescape(JsText.Substring(input, cursor, quoteSearch)));
                            SaveRow(afterQuote + newlineLen);
                            nextDelim = IndexOf(input, delim, cursor); // the quoted field may have skipped it
                            quoteSearch = IndexOf(input, QuoteChar, cursor); // the first quote of the next line

                            if (preview > 0 && data.Count >= preview) return Returnable(data);
                            break;
                        }

                        // A quote inside a quoted field that closes nothing (papaparse records
                        // InvalidQuotes): treat it as data and keep looking.
                        quoteSearch++;
                    }
                    continue;
                }

                // The next delimiter comes before the next newline: end of field.
                if (nextDelim != -1 && (nextDelim < nextNewline || nextNewline == -1))
                {
                    row.Add(JsText.Substring(input, cursor, nextDelim));
                    cursor = nextDelim + delimLen;
                    nextDelim = IndexOf(input, delim, cursor);
                    continue;
                }

                // End of row.
                if (nextNewline != -1)
                {
                    row.Add(JsText.Substring(input, cursor, nextNewline));
                    SaveRow(nextNewline + newlineLen);
                    if (preview > 0 && data.Count >= preview) return Returnable(data);
                    continue;
                }

                break;
            }

            return Finish(null);

            // Spaces (and only spaces) between a closing quote and `index`: how many.
            int ExtraSpaces(int index)
            {
                var spaceLength = 0;
                if (index != -1)
                {
                    var between = JsText.Substring(input, quoteSearch + 1, index);
                    if (between.Length > 0 && Js.Trim(between).Length == 0) spaceLength = between.Length;
                }
                return spaceLength;
            }

            // The rest of the input (or the given value) is the last cell of the last row.
            List<List<string>> Finish(string? value)
            {
                value ??= JsText.Substring(input, cursor);
                row.Add(value);
                cursor = inputLen;
                data.Add(row);
                return Returnable(data);
            }

            void SaveRow(int newCursor)
            {
                cursor = newCursor;
                data.Add(row);
                row = [];
                nextNewline = IndexOf(input, newline, cursor);
            }
        }

        private static string Unescape(string s) => s.Replace("\"\"", "\"", StringComparison.Ordinal);

        // returnable(): with header on, the first row's headers are transformed and any
        // duplicate is renamed "name_1", "name_2"... (before empty lines are skipped).
        private List<List<string>> Returnable(List<List<string>> data)
        {
            if (header && data.Count > 0)
            {
                var result = data[0];
                var headerCount = new Dictionary<string, int>(StringComparer.Ordinal);
                var usedHeaders = new HashSet<string>(result, StringComparer.Ordinal);
                for (var i = 0; i < result.Count; i++)
                {
                    var h = StripBom(result[i]);
                    if (transformHeader is not null) h = transformHeader(h);
                    if (!headerCount.TryGetValue(h, out var count) || count == 0)
                    {
                        headerCount[h] = 1;
                        result[i] = h;
                    }
                    else
                    {
                        string newHeader;
                        var suffixCount = count;
                        do
                        {
                            newHeader = h + "_" + suffixCount.ToString(CultureInfo.InvariantCulture);
                            suffixCount++;
                        } while (usedHeaders.Contains(newHeader));
                        usedHeaders.Add(newHeader);
                        result[i] = newHeader;
                        headerCount[h] = count + 1;
                    }
                    usedHeaders.Add(h);
                }
            }
            return data;
        }
    }
}
