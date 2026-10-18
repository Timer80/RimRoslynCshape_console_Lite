using System;
using System.Collections.Generic;
using Xunit;

namespace RoslynProbe.Tests
{
    public class JsonTests
    {
        [Fact]
        public void Parse_NestedObject_YieldsExpectedShape()
        {
            string json = "{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{}}";
            object parsed = RoslynProbe.Json.Json.Parse(json);

            var root = Assert.IsType<Dictionary<string, object>>(parsed);
            Assert.Equal(1.0, Convert.ToDouble(root["a"]));

            var b = Assert.IsType<List<object>>(root["b"]);
            Assert.True((bool)b[0]);
            Assert.Null(b[1]);
            Assert.Equal("x", b[2]);

            Assert.IsType<Dictionary<string, object>>(root["c"]);
            Assert.Empty((Dictionary<string, object>)root["c"]);
        }

        [Fact]
        public void Stringify_Parse_RoundTripsStructurally()
        {
            string sample = "{\"name\":\"h\u00e9llo\",\"n\":42,\"f\":3.5,\"flags\":[true,false],\"empty\":{},\"nil\":null}";
            object parsed = RoslynProbe.Json.Json.Parse(sample);
            string back = RoslynProbe.Json.Json.Stringify(parsed);
            object reparsed = RoslynProbe.Json.Json.Parse(back);

            var root = Assert.IsType<Dictionary<string, object>>(reparsed);
            Assert.Equal("h\u00e9llo", (string)root["name"]);
            Assert.Equal(42.0, Convert.ToDouble(root["n"]));
            Assert.Equal(3.5, (double)root["f"]);

            var flags = Assert.IsType<List<object>>(root["flags"]);
            Assert.True((bool)flags[0]);
            Assert.False((bool)flags[1]);

            Assert.IsType<Dictionary<string, object>>(root["empty"]);
            Assert.Null(root["nil"]);
        }

        [Fact]
        public void Stringify_EscapesSpecialCharacters()
        {
            var dict = new Dictionary<string, object>
            {
                { "q", "a\"b" },
                { "bs", "a\\b" },
                { "uni", "c\u00e9d" }
            };
            string s = RoslynProbe.Json.Json.Stringify(dict);
            object reparsed = RoslynProbe.Json.Json.Parse(s);

            var root = Assert.IsType<Dictionary<string, object>>(reparsed);
            Assert.Equal("a\"b", (string)root["q"]);
            Assert.Equal("a\\b", (string)root["bs"]);
            Assert.Equal("c\u00e9d", (string)root["uni"]);
        }

        [Fact]
        public void Parse_InvalidJson_Throws()
        {
            Assert.Throws<Exception>(() => RoslynProbe.Json.Json.Parse("{"));
            Assert.Throws<Exception>(() => RoslynProbe.Json.Json.Parse("{\"a\":}"));
            Assert.Throws<Exception>(() => RoslynProbe.Json.Json.Parse("[1,2,"));
        }
    }
}
