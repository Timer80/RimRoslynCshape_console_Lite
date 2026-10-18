// RoslynProbe —— RoslynEngine 的纯逻辑单测（当前覆盖自动补 using 的「短名抽取」判定）。
//
// 为什么单列这一条：CS0246/CS0103 错误里抠出来的「短名」必须是**不含任何空白**的标识符。
// 只认 ASCII 空格（IndexOf(' ')）会让含 TAB / 全角空格 U+3000 / NBSP U+00A0 的串被当成合法短名
// 放行，进而去做类型索引查询 —— 与内核 verb/rest 切分、指令参数切分是同一族的空白口径缺陷（R1c/C1）。
//
// 约定同 tests/SlashHarness/SlashRegistryTests.cs：零 xunit，public static int Run() 返回失败数，
// 由 SlashTestHarness 反射发现；用例幂等（§8.4）。

using System;
using System.Collections.Generic;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    /// <summary>RoslynEngine 里可离线验证的纯逻辑（目前是短名抽取判定）。</summary>
    public static class RoslynEngineTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("短名抽取：含任意空白（空格/TAB/全角/NBSP）一律拒绝", ExtractNameCases);

            Console.WriteLine("RoslynEngineTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        private static void ExtractNameCases()
        {
            // 正例锚点：普通标识符必须照常抽出（否则"全都拒绝"也能让下面的断言假绿）。
            string ok = "error CS0246: The type or namespace name 'Foo' could not be found";
            Check(RoslynEngine.ExtractMissingName(ok) == "Foo",
                "普通标识符应照常抽出，实际 " + Show(RoslynEngine.ExtractMissingName(ok)));
            Check(RoslynEngine.ExtractMissingName("error CS0103: The name 'Bar' does not exist") == "Bar",
                "CS0103 的普通标识符也应抽出");

            // 既有规则：带点的形态（不是短名）仍拒绝。
            Check(RoslynEngine.ExtractMissingName("... name 'System.Text.StringBuilder' ...") == null,
                "带点的形态仍应拒绝");

            // 反例：含空白（任意空白）的形态一律拒绝 —— 这些是 C1 的 load-bearing 断言。
            string[] blanks = { " ", "\t", "\u3000", "\u00A0" };
            string[] names = { "普通空格", "TAB", "全角空格(U+3000)", "NBSP(U+00A0)" };
            for (int i = 0; i < blanks.Length; i++)
            {
                string msg = "error CS0246: The type or namespace name 'Foo" + blanks[i] + "Bar' could not be found";
                Check(RoslynEngine.ExtractMissingName(msg) == null,
                    names[i] + " 含空白的名字必须被拒（不得当短名放行），实际 "
                    + Show(RoslynEngine.ExtractMissingName(msg)));
            }
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

        private static string Show(string s) { return s == null ? "<null>" : "\"" + s + "\""; }
    }
}
