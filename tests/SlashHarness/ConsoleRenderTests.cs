// RoslynProbe —— 壳层「用户看到的形态」回归(S11-8-1 真机两条边角)。
//
// B1:GUI 的系统行曾显示**双前缀**(窗口 `!! {...}`,而 ConsoleLog 缓冲里只有一个 `! `)——
//     产生点(ConsoleWindow.Submit 等)加一次,渲染层(RowsOf/DetailText)又加一次。
//     现在的不变式是「窗口 == 缓冲,单前缀」:
//       产生点唯一出口 SystemNoteText 保证缓冲文本恰好一个 "! "(且幂等);
//       渲染层唯一出口 SystemRowText 原样显示,不再补前缀。
// 约定同 tests/SlashHarness/SlashRegistryTests.cs:零 xunit,public static int Run() 返回失败数,
// 由 SlashTestHarness.RunAll() 反射发现;用例必须幂等(§8.4)。

using System;
using System.Collections.Generic;
using System.Text;
using RoslynProbe;

namespace RoslynProbe.Tests
{
    /// <summary>壳层可见形态(系统行前缀)的断言套件。</summary>
    public static class ConsoleRenderTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static string currentCase = "(none)";
        private static int checks;

        public static int Run()
        {
            Failures.Clear();
            checks = 0;
            currentCase = "(none)";

            Case("B1 系统行：产生点单前缀 + 渲染层原样显示（曾显示 !! 双前缀）", SystemRowCases);

            Console.WriteLine("ConsoleRenderTests: " + (checks - Failures.Count) + "/" + checks + " 通过，"
                + Failures.Count + " 失败");
            foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
            return Failures.Count;
        }

        // ---------------- B1 ----------------

        private static void SystemRowCases()
        {
            // 真机形态:/quit --json 落账的就是这一行 JSON(旧实现窗口里显示成 "!! {...}")。
            string json = "{\"ok\":true,\"quit\":true}";
            string stored = ConsoleWindow.SystemNoteText(json);
            Check(stored == "! " + json, "产生点应存恰好一个 \"! \" 前缀，实际 " + Show(stored));
            Check(ConsoleWindow.SystemNoteText(stored) == stored,
                "产生点必须幂等（已是规范文本时不得再叠前缀），实际 " + Show(ConsoleWindow.SystemNoteText(stored)));

            // 渲染层:窗口文本 == 缓冲文本(这条就是双前缀事故的回归)。
            Check(ConsoleWindow.SystemRowText(stored, true) == stored,
                "流里的系统行必须等于缓冲文本（渲染层不得再补前缀），实际 " + Show(ConsoleWindow.SystemRowText(stored, true)));
            Check(ConsoleWindow.SystemRowText(stored, false) == stored, "详情栏同理");
            Check(!ConsoleWindow.SystemRowText(stored, true).StartsWith("! ! ", StringComparison.Ordinal),
                "窗口不得出现双前缀，实际 " + Show(ConsoleWindow.SystemRowText(stored, true)));

            // 既有渲染语义不变:流里压成单行、详情栏保留换行、null 不抛。
            Check(ConsoleWindow.SystemRowText("! a\r\nb", true) == "! a b", "流里仍把换行压成空格");
            Check(ConsoleWindow.SystemRowText("! a\r\nb", false) == "! a\r\nb", "详情栏仍保留换行");
            Check(ConsoleWindow.SystemRowText(null, true) == "", "null 文本不抛、渲染成空串");
            Check(ConsoleWindow.SystemNoteText(null) == "! ", "null 正文给一个裸前缀(不得抛)");

            // 「重置」按钮那条:旧实现是唯一**没有**前缀的系统行(窗口会替它补一个,与缓冲不一致)。
            string reset = ConsoleWindow.SystemNoteText(ConsoleWindow.ResetNoteBody);
            Check(reset.StartsWith("! ", StringComparison.Ordinal)
                && !reset.StartsWith("! ! ", StringComparison.Ordinal),
                "重置提示也必须恰好一个前缀，实际 " + Show(reset));
            Check(ConsoleWindow.SystemRowText(reset, true) == reset, "重置提示的窗口文本 == 缓冲文本");

            // 与 quit 的真实行序列串起来:每条都恰好一个前缀(三通道一致的落账口径)。
            var quit = new SlashResult { Kind = "quit" };
            quit.Lines.Add(json);
            List<string> notes = ConsoleWindow.QuitNoteLines(quit);
            Check(notes.Count == 2 && notes[1] == "bye", "quit 落账序列应为 [指令行, bye]");
            for (int i = 0; i < notes.Count; i++)
            {
                string t = ConsoleWindow.SystemNoteText(notes[i]);
                Check(t.StartsWith("! ", StringComparison.Ordinal) && !t.StartsWith("! ! ", StringComparison.Ordinal),
                    "quit 第 " + i + " 行的落账文本应恰好一个前缀，实际 " + Show(t));
            }
        }

        // ---------------- 断言 ----------------

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
