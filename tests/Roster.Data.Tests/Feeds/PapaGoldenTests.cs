// New: the papaparse port (Papa.cs) against the real papaparse 5.5.4. The expected outputs
// were produced by running papaparse itself (the TypeScript app's node_modules) on each input
// with the options lib/csv/parse.ts and lib/csv/ukgLeave.ts pass: header row, empty lines
// skipped, headers and cells trimmed. Keys are listed as Object.keys gives them.

using System.Text.Json;
using Roster.Data.Feeds;

namespace Roster.Data.Tests.Feeds;

public class PapaGoldenTests
{
    private sealed record Case(string Text, List<string> Fields, List<List<List<JsonElement>>> Rows);

    private const string Golden = """"
[{"text":"a,b\n1,2","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"﻿a,b\r\n1,2\r\n","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"a,b\r1,2\r3,4","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]],[["a",["3"]],["b",["4"]]]]},{"text":"a,b\n\"x, y\",2\n\"he said \"\"hi\"\"\",3","fields":["a","b"],"rows":[[["a",["x, y"]],["b",["2"]]],[["a",["he said \"hi\""]],["b",["3"]]]]},{"text":"a,b\n\"multi\nline\",2","fields":["a","b"],"rows":[[["a",["multi\nline"]],["b",["2"]]]]},{"text":"a;b;c\n1;2;3","fields":["a","b","c"],"rows":[[["a",["1"]],["b",["2"]],["c",["3"]]]]},{"text":"a\tb\n1\t2","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"a|b\n1|2","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"a,a,b,a\n1,2,3,4","fields":["a","a_1","b","a_2"],"rows":[[["a",["1"]],["a_1",["2"]],["b",["3"]],["a_2",["4"]]]]},{"text":"a,b\n1\n1,2,3,4","fields":["a","b"],"rows":[[["a",["1"]]],[["a",["1"]],["b",["2"]],["__parsed_extra",["3","4"]]]]},{"text":"\n\na,b\n1,2","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"a,b\n\n1,2\n\n","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":" a , b \n 1 , 2 ","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"a,b\n\"x\" ,2","fields":["a","b"],"rows":[[["a",["x"]],["b",["2"]]]]},{"text":"a,b\n\"x\"y,2\n3,4","fields":["a","b"],"rows":[[["a",["x\"y,2\n3,4"]]]]},{"text":"a,b\n\"unterminated,2\n3,4","fields":["a","b"],"rows":[[["a",["unterminated,2\n3,4"]]]]},{"text":"single","fields":["single"],"rows":[]},{"text":"","fields":[],"rows":[]},{"text":"a,b","fields":["a","b"],"rows":[]},{"text":"a,b\n\" \",2","fields":["a","b"],"rows":[[["a",[""]],["b",["2"]]]]},{"text":"x;y,z\n1;2,3\n4;5,6","fields":["x;y","z"],"rows":[[["x;y",["1;2"]],["z",["3"]]],[["x;y",["4;5"]],["z",["6"]]]]},{"text":"a,b\n1,\"2\"\n","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"a,b\r\n\"x\r\ny\",2\r\n","fields":["a","b"],"rows":[[["a",["x\r\ny"]],["b",["2"]]]]},{"text":"a,b,c\n1,\"\",3","fields":["a","b","c"],"rows":[[["a",["1"]],["b",[""]],["c",["3"]]]]},{"text":"a,b\n1,2\n   \n3,4","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]],[["a",[""]]],[["a",["3"]],["b",["4"]]]]},{"text":"a,,b\n1,2,3","fields":["a","","b"],"rows":[[["a",["1"]],["",["2"]],["b",["3"]]]]},{"text":"h1,h2\n\"a\"\"b\",c","fields":["h1","h2"],"rows":[[["h1",["a\"b"]],["h2",["c"]]]]},{"text":"a_1,a,a\n1,2,3","fields":["a_1","a","a_2"],"rows":[[["a_1",["1"]],["a",["2"]],["a_2",["3"]]]]},{"text":"\"q,h\",b\n1,2","fields":["q,h","b"],"rows":[[["q,h",["1"]],["b",["2"]]]]},{"text":"a,b\n1,2\r\n3,4\n","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]],[["a",["3"]],["b",["4"]]]]},{"text":"﻿﻿a,b\n1,2","fields":["a","b"],"rows":[[["a",["1"]],["b",["2"]]]]},{"text":"A,a\n1,2","fields":["A","a"],"rows":[[["A",["1"]],["a",["2"]]]]},{"text":"ukg","fields":["StoreID","Employee Name"],"rows":[[["StoreID",["G001"]],["Employee Name",["1-A, B"]]]]}]
"""";

    public static TheoryData<int> Cases() => new(Enumerable.Range(0, JsonSerializer.Deserialize<List<JsonElement>>(Golden)!.Count));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parses_exactly_as_papaparse(int index)
    {
        var c = JsonSerializer.Deserialize<List<JsonElement>>(Golden)![index];
        var text = c.GetProperty("text").GetString()!;
        var fields = c.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();
        var rows = c.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray()
            .Select(kv => (Key: kv[0].GetString()!, Values: kv[1].EnumerateArray().Select(v => v.GetString()!).ToList())).ToList()).ToList();

        // The last case is the UKG exports' header transform (trim, then a byte-order mark).
        var parsed = text == "ukg"
            ? UkgLeave.ParseUkgExport("StoreID,Employee Name\n\uFEFFG001,\"1-A, B\"", "Employee Name")
            : CsvParse.ParseCsv(text);

        Assert.Equal(fields, parsed.Headers);
        Assert.Equal(rows.Count, parsed.Rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = parsed.Rows[i];
            var actual = row.Keys.Select(k => (Key: k, Values: new List<string> { row[k]! })).ToList();
            if (row.ParsedExtra is { } extra) actual.Add(("__parsed_extra", extra.ToList()));
            Assert.Equal(rows[i].Select(kv => kv.Key), actual.Select(kv => kv.Key));
            for (var j = 0; j < actual.Count; j++) Assert.Equal(rows[i][j].Values, actual[j].Values);
        }
    }
}
