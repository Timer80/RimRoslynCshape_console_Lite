// RoslynProbe —— 壳层 `--json` 尾旗标(S11-4)的单测。
//
// 覆盖:TryStrip 的识别/剥离(独立 token、任意位置、引号内不算、无旗标逐字不变)、
// JsonLine/RenderJson 的单行与兜底(Data=null ⇒ null;序列化失败 ⇒ 合法 JSON),以及**端到端**:
// 经 SlashJson.TryDispatch 派发(三个壳走的就是这一条)⇒ 输出可被 Json.Parse 解析、与同一指令的
// Data 键集/值一致;同一指令**不带**旗标时仍走 Lines(逐行,且与内核入口 SlashCommands.TryDispatch 逐行一致)。
// 用例全部零 Verse 依赖且幂等(§8.4:同进程第二遍 RunAll 也绿)。

using System;
using System.Collections.Generic;
using RoslynProbe;
using JsonNs = RoslynProbe.Json.Json;

namespace RoslynProbe.Tests
{
    /// <summary>壳层 --json 的断言套件(返回失败数;由 SlashTestHarness 反射发现)。</summary>
    public static class SlashJsonTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        /// <summary>端到端探针的 verb(每遍唯一,避免重名;见 §8.4)。</summary>
        private static string stubVerb;

        /// <summary>探针 handler 收到的 rest(用来证明旗标没被当成参数)。</summary>
        private static string stubRest;

        /// <summary>返回失败数;由 SlashTestHarness 反射发现并调用。</summary>
        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("TryStrip:独立 token 识别与剥离(任意位置)", StripCases);
            Case("TryStrip:引号包住/引号内的 --json 不是旗标(字面参数逃生口)", QuotedCases);
            Case("TryStrip:无旗标 ⇒ 逐字原样(既有行为不变)", NoFlagCases);
            Case("JsonLine/RenderJson:单行可 Parse、Data=null ⇒ null、序列化失败 ⇒ 合法 JSON 兜底", RenderCases);
            Case("端到端:TryDispatch 剥离旗标(不进 rest)+ JSON ⇄ Data 一致 + 不带旗标仍走 Lines", EndToEndCases);
            Case("端到端:真实指令 /help --json 键集 {count,verbs,commands} 与 Data 同源", HelpCases);
            Case("端到端:真实指令 /status --json(示例)可 Parse 且与 Data 一致;不带旗标逐行", StatusCases);

            // 收尾:把表还原成"只含真实登记"的状态(别的写手的测试也这么做)。
            SlashRegistry.ResetForTests();
            SlashRegistry.RegisterAll();

            Console.WriteLine("SlashJsonTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- 用例 ----------------

        private static void StripCases()
        {
            CheckStrip("/status --json", "/status");
            CheckStrip("/help --json --json", "/help");
            CheckStrip("/debug-action list --json", "/debug-action list");
            CheckStrip("/dev list --json extra", "/dev list extra");
            CheckStrip("/status     --json", "/status");
            CheckStrip("/usings add " + Q + "SB = System.Text.StringBuilder" + Q + " --json",
                "/usings add " + Q + "SB = System.Text.StringBuilder" + Q);
        }

        private static void QuotedCases()
        {
            // 裁定:要一个字面 --json 参数就用引号包住 —— 引号段整体是一个 token,不含引号的字面量
            // 才等于旗标;故引号包住的写法天然不被识别,**不为解析加特例**。
            CheckNoStrip("/usings add " + Q + "--json" + Q);
            CheckNoStrip("/usings add " + Q + "x --json y" + Q);

            // 字面参数 + 真旗标并存:前者保留、后者撕掉。
            bool seen = SlashJson.TryStrip("/usings add " + Q + "--json" + Q + " --json", out string stripped);
            Check(seen, "并存时应识别到真旗标");
            Check(stripped == "/usings add " + Q + "--json" + Q,
                "字面参数应保留、真旗标应剥掉，实际 " + Show(stripped));

            // 引号段里的最小转义(反斜杠引号)不得让扫描提前收尾。
            string backslash = "\\";
            string input3 = "/usings add " + Q + "a" + backslash + Q + "b" + Q + " --json";
            bool seen2 = SlashJson.TryStrip(input3, out string stripped2);
            Check(seen2, "含转义引号的引号段后仍应识别到旗标");
            Check(stripped2 == "/usings add " + Q + "a" + backslash + Q + "b" + Q,
                "引号段应逐字保留，实际 " + Show(stripped2));
        }

        private static void NoFlagCases()
        {
            string[] inputs =
            {
                null, "", "   ", "/help", "/usings add System.IO", "/debug-action list filter=x",
                "int x = 1;", "/status --json=1", "/status --JSON"
            };
            foreach (string s in inputs)
            {
                bool seen = SlashJson.TryStrip(s, out string stripped);
                Check(!seen, "不应识别旗标: " + Show(s));
                Check(stripped == s, "无旗标时输出必须与输入逐字相同: " + Show(s));
            }
        }

        private static void RenderCases()
        {
            var r = new SlashResult { Kind = "k" };
            r.Lines.Add("人类行 1");
            r.Lines.Add("人类行 2");
            r.Data = new Dictionary<string, object>
            {
                ["a"] = 1,
                ["b"] = new List<string> { "x", "y" },
                ["c"] = new Dictionary<string, object> { ["d"] = true },
                ["e"] = null,
                ["f"] = "带\"引号\"与换行\n"
            };

            SlashResult same = SlashJson.RenderJson(r);
            Check(ReferenceEquals(same, r), "RenderJson 应原地返回同一结果");
            Check(r.Lines.Count == 1, "渲染后应恰好一行，实际 " + r.Lines.Count + " 行");
            Check(r.Kind == "k", "Kind 不得被改，实际 " + Show(r.Kind));
            Check(r.Lines[0].IndexOf('\n') < 0, "JSON 行必须单行(换行已转义)");

            var parsed = JsonNs.Parse(r.Lines[0]) as Dictionary<string, object>;
            Check(parsed != null, "单行应可被 Json.Parse 解析为对象");
            if (parsed != null)
            {
                Check((long)parsed["a"] == 1L, "a 应为 1");
                var b = parsed["b"] as List<object>;
                Check(b != null && b.Count == 2 && (string)b[1] == "y", "b 应是 [x,y]");
                var c = parsed["c"] as Dictionary<string, object>;
                Check(c != null && (bool)c["d"], "c.d 应为 true");
                Check(parsed["e"] == null && parsed.ContainsKey("e"), "e 应为 null 且键在");
                Check((string)parsed["f"] == "带\"引号\"与换行\n", "f 的引号/换行应往返无损");
            }

            // Data 为 null ⇒ 字面 null(仍是合法 JSON)。
            var noData = new SlashResult { Kind = "n" };
            Check(SlashJson.JsonLine(noData) == "null", "Data=null 应渲染字面 null");
            Check(JsonNs.Parse(SlashJson.JsonLine(noData)) == null, "null 应可被 Json.Parse 解析");
            SlashJson.RenderJson(noData);
            Check(noData.Lines.Count == 1 && noData.Lines[0] == "null", "RenderJson 对 Data=null 也应给一行");

            // 序列化器抛 ⇒ 合法 JSON 兜底(绝不给半截文本)。
            var bad = new SlashResult { Kind = "bad" };
            bad.Data = new Dictionary<string, object> { ["obj"] = new object() };
            string line = SlashJson.JsonLine(bad);
            Check(line.IndexOf('\n') < 0, "兜底行也必须是单行");
            var bp = JsonNs.Parse(line) as Dictionary<string, object>;
            Check(bp != null && (bool)bp["ok"] == false, "兜底应是 {ok:false,…}");
            Check(bp != null && ((string)bp["error"]).StartsWith("--json 序列化失败", StringComparison.Ordinal),
                "兜底应说明序列化失败，实际 " + Show(bp == null ? null : bp["error"]));
        }

        private static void EndToEndCases()
        {
            RegisterStub();

            SlashResult json;
            Check(SlashJson.TryDispatch("/" + stubVerb + " --json", null, null, "pipe", false, out json),
                "应识别为指令");
            Check(stubRest == "", "旗标不得进 rest(应剥成空 rest)，实际 " + Show(stubRest));
            Check(json.Lines.Count == 1, "带旗标应只渲染一行 JSON，实际 " + json.Lines.Count + " 行");
            Check(json.Lines[0] == JsonNs.Stringify(json.Data), "JSON 行应与同一 Dispatch 的 Data 序列化逐字相同");

            var parsed = JsonNs.Parse(json.Lines[0]) as Dictionary<string, object>;
            Check(parsed != null, "JSON 行应可被 Json.Parse 解析为对象");
            Check(parsed != null && parsed.Count == json.Data.Count, "解析出的键集条数应与 Data 一致");
            Check(parsed != null && parsed.ContainsKey("n") && parsed.ContainsKey("items"), "键集应与 Data 一致");
            Check(parsed != null && (long)parsed["n"] == Convert.ToInt64(json.Data["n"]), "n 的值应与 Data 一致");
            Check(parsed != null
                && ((List<object>)parsed["items"]).Count == ((List<string>)json.Data["items"]).Count,
                "items 的条数应与 Data 一致");

            // 不带旗标:仍走 Lines(逐行),rest 逐字。
            SlashResult plain;
            Check(SlashJson.TryDispatch("/" + stubVerb + " list extra", null, null, "pipe", false, out plain),
                "应识别为指令");
            Check(stubRest == "list extra", "不带旗标时 rest 应逐字原样，实际 " + Show(stubRest));
            Check(plain.Lines.Count == 2 && plain.Lines[0] == "第一行" && plain.Lines[1] == "第二行",
                "不带旗标仍应逐行渲染 Lines");
            Check(!IsJson(plain.Lines[0]), "不带旗标时不得渲染 JSON");

            // 旗标在中间:剥掉它,其余 token 原样。
            SlashResult mid;
            Check(SlashJson.TryDispatch("/" + stubVerb + " list --json extra", null, null, "pipe", false, out mid),
                "应识别为指令");
            Check(stubRest == "list extra", "中间位置的旗标也应剥离且两侧 token 原样，实际 " + Show(stubRest));
            Check(mid.Lines.Count == 1 && IsJson(mid.Lines[0]), "中间旗标同样渲染一行 JSON");

            // 字面参数逃生口:引号包住的 --json 进 rest,且不触发 JSON 渲染。
            SlashResult quoted;
            Check(SlashJson.TryDispatch("/" + stubVerb + " " + Q + "--json" + Q, null, null, "pipe", false, out quoted),
                "应识别为指令");
            Check(stubRest == Q + "--json" + Q, "引号包住的 --json 必须原样进 rest，实际 " + Show(stubRest));
            Check(quoted.Lines.Count == 2, "字面参数不该触发 JSON 渲染，应仍走两行 Lines");
        }

        private static void HelpCases()
        {
            SlashResult json;
            Check(SlashJson.TryDispatch("/help --json", null, null, "gui", true, out json), "/help 应是指令");
            Check(json.Lines.Count == 1, "带旗标应只有一行 JSON，实际 " + json.Lines.Count + " 行");

            var parsed = JsonNs.Parse(json.Lines[0]) as Dictionary<string, object>;
            Check(parsed != null, "JSON 行应可解析为对象");
            Check(parsed != null && parsed.Count == 3 && parsed.ContainsKey("count")
                && parsed.ContainsKey("verbs") && parsed.ContainsKey("commands"),
                "键集应恰为 count/verbs/commands(与 Data 顶层的键集一致)");
            Check(parsed != null && (long)parsed["count"] == (long)SlashRegistry.Count,
                "count 应等于表条数(说明扫到了非空表;与 Data 同源)");
            Check(parsed != null && (long)parsed["count"] == Convert.ToInt64(json.Data["count"]),
                "count 应与 Data 的值一致");
            Check(parsed != null && ((List<object>)parsed["verbs"]).Count == ((List<string>)json.Data["verbs"]).Count,
                "verbs 条数应与 Data 一致");
            Check(parsed != null && ((List<object>)parsed["commands"]).Count
                == ((List<Dictionary<string, object>>)json.Data["commands"]).Count, "commands 条数应与 Data 一致");

            // 不带旗标:逐行,且与内核入口逐行逐字一致。
            SlashResult plain;
            Check(SlashJson.TryDispatch("/help", null, null, "gui", true, out plain), "/help 应是指令");
            SlashResult kernel;
            SlashCommands.TryDispatch("/help", null, null, out kernel, "gui", true);
            CheckSameLines(plain.Lines, kernel.Lines, "/help 不带旗标应与内核入口逐行一致");
            Check(plain.Lines.Count == SlashRegistry.Count + 2,
                "help 逐行数应是 标题 + N 条 + 尾注，实际 " + plain.Lines.Count);
            Check(plain.Lines[0] == "可用指令:", "首行应是标题，实际 " + Show(plain.Lines[0]));
            Check(!IsJson(plain.Lines[0]), "不带旗标时不得渲染 JSON");
        }

        private static void StatusCases()
        {
            // 简报里的示例:/status --json 的输出必须能被 Json.Parse 解析,且字段与同一指令的 Data 一致。
            SlashResult json;
            Check(SlashJson.TryDispatch("/status --json", null, null, "stdio", false, out json), "/status 应是指令");
            Check(json.Lines.Count == 1, "带旗标应只有一行 JSON(S11-4 出口)");

            var parsed = JsonNs.Parse(json.Lines[0]) as Dictionary<string, object>;
            Check(parsed != null, "/status --json 的输出应可被 Json.Parse 解析");
            Check(parsed != null && parsed.Count > 0, "解析出的对象不应为空(否则下面的键集断言是空转)");

            if (json.Data == null)
            {
                Check(parsed == null, "Data 为 null 时应渲染 null");
            }
            else
            {
                Check(parsed != null && parsed.Count == json.Data.Count, "键集条数应与同一指令的 Data 一致");
                if (parsed != null)
                    foreach (KeyValuePair<string, object> kv in json.Data)
                        Check(parsed.ContainsKey(kv.Key), "/status Data 的键应出现在 JSON 里: " + kv.Key);
                Check(parsed != null && SameJson(parsed,
                        (Dictionary<string, object>)JsonNs.Parse(JsonNs.Stringify(json.Data))),
                    "JSON 的键集与值应与 Data 逐键一致(含嵌套 capabilities/channels)");
            }

            // 不带旗标:仍走人类行(逐行),且与内核入口逐行一致。
            SlashResult plain;
            Check(SlashJson.TryDispatch("/status", null, null, "stdio", false, out plain), "/status 应是指令");
            SlashResult kernel;
            SlashCommands.TryDispatch("/status", null, null, out kernel, "stdio", false);
            CheckSameLines(plain.Lines, kernel.Lines, "/status 不带旗标应与内核入口逐行一致");
            Check(!IsJson(plain.Lines[0]), "/status 不带旗标时首行不是 JSON(走人类行)");
        }

        // ---------------- 探针指令 ----------------

        /// <summary>登记一条唯一 verb 的探针指令(回两行 Lines + 一个两层 Data),再把真实表登记回来。</summary>
        private static void RegisterStub()
        {
            SlashRegistry.ResetForTests();
            stubVerb = "json-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            stubRest = null;
            SlashRegistry.Register(new SlashEntry
            {
                Verb = stubVerb,
                Usage = "—— 测试专用:回两行 Lines + 一个 Data",
                Group = "测试",
                Kind = "json-probe",
                Handler = StubHandler
            });
            SlashRegistry.RegisterAll();
        }

        private static SlashResult StubHandler(string rest, SlashContext ctx)
        {
            stubRest = rest;
            var r = new SlashResult { Kind = "json-probe" };
            r.Lines.Add("第一行");
            r.Lines.Add("第二行");
            r.Data = new Dictionary<string, object>
            {
                ["n"] = 7,
                ["items"] = new List<string> { "a", "b" }
            };
            return r;
        }

        // ---------------- 辅助 ----------------

        /// <summary>双引号的写法常量(避免在断言里散落转义)。</summary>
        private const string Q = "\"";

        private static void CheckStrip(string input, string expected)
        {
            bool seen = SlashJson.TryStrip(input, out string stripped);
            Check(seen, "应识别旗标: " + Show(input));
            Check(stripped == expected, "剥离结果应为 " + Show(expected) + "，实际 " + Show(stripped));
        }

        private static void CheckNoStrip(string input)
        {
            bool seen = SlashJson.TryStrip(input, out string stripped);
            Check(!seen, "不应识别旗标: " + Show(input));
            Check(stripped == input, "不应改写输入: " + Show(input) + " → " + Show(stripped));
        }

        private static bool IsJson(string s)
        {
            try { JsonNs.Parse(s); return true; }
            catch { return false; }
        }

        /// <summary>JSON 结构深比较(对象按键、数组按序、标量用 Equals)。</summary>
        private static bool SameJson(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;

            var da = a as Dictionary<string, object>;
            var db = b as Dictionary<string, object>;
            if (da != null || db != null)
            {
                if (da == null || db == null || da.Count != db.Count) return false;
                foreach (KeyValuePair<string, object> kv in da)
                {
                    object other;
                    if (!db.TryGetValue(kv.Key, out other) || !SameJson(kv.Value, other)) return false;
                }
                return true;
            }

            var la = a as List<object>;
            var lb = b as List<object>;
            if (la != null || lb != null)
            {
                if (la == null || lb == null || la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++)
                    if (!SameJson(la[i], lb[i])) return false;
                return true;
            }

            return a.Equals(b);
        }

        private static void CheckSameLines(List<string> actual, List<string> expected, string message)
        {
            if (actual == null || expected == null) { Check(false, message + "(某一侧为 null)"); return; }
            if (actual.Count != expected.Count)
            {
                Check(false, message + "(行数 " + actual.Count + " vs " + expected.Count + ")");
                return;
            }
            for (int i = 0; i < actual.Count; i++)
            {
                if (actual[i] != expected[i])
                {
                    Check(false, message + "(第 " + i + " 行: " + Show(actual[i]) + " vs " + Show(expected[i]) + ")");
                    return;
                }
            }
            Check(true, message);
        }

        private static void Case(string name, Action body)
        {
            currentCase = name;
            try { body(); }
            catch (Exception ex)
            {
                checks++;
                Failures.Add("[" + name + "] 用例本身抛出: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) Failures.Add("[" + currentCase + "] " + message);
        }

        private static string Show(object o)
        {
            if (o == null) return "<null>";
            string s = o as string;
            return s == null ? Convert.ToString(o) : "\"" + s + "\"";
        }
    }
}
