// RoslynProbe —— GUI 文案的本地化薄封装。
//
// 键定义在仓库根 Languages/<语言>/Keyed/RoslynProbe.xml（引擎 LoadedLanguage.LoadData 自动扫描，
// LoadFolders.xml 已挂载根目录 "/"，无需登记）。提供 English（defaultLanguage 兜底，硬要求全量）
// 与 ChineseSimplified。
//
// 为什么不用 Verse 的 key.Translate()：
//   1. LanguageDatabase.activeLanguage == null 时 Translator 会 Log.Error —— SlashHarness 测试进程
//      里就是 null，会刷一屏错误；
//   2. Prefs.DevMode 下缺键回退会被 PseudoTranslated 打成乱码（à þ ç …）；
// 所以直接查 LoadedLanguage.TryGetTextFromKey：缺键 / 语言系统不可用一律回退调用方给的兜底文本
// （= 代码里原有的中文文案），保证无语言数据时的行为与键化前逐字一致。
//
// 不翻译清单（协议与机器面，勿键化）：流内前缀 "> " / "= " / "! " / "…" / "x{n}"、QuitNoteLines 的
// "bye"（与管道 "> bye" 同口径）、--json 的 Data 与其字段名、斜杠指令 verb、Log.* 开发日志。

using System;
using Verse;

namespace RoslynProbe
{
    public static class ConsoleText
    {
        /// <summary>取键的当前语言译文；缺键或语言系统不可用时原样回退 fallback（不再补前缀）。</summary>
        public static string T(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key)) return fallback;
            LoadedLanguage lang = LanguageDatabase.activeLanguage;
            if (lang != null && lang.TryGetTextFromKey(key, out TaggedString translated))
                return translated;
            return fallback;
        }

        /// <summary>T + string.Format：只在 args 非空时格式化；译文不含占位符或格式串坏了时原样返回，
        /// 不把 FormatException 抛进 OnGUI。</summary>
        public static string Tf(string key, string fallback, params object[] args)
        {
            string format = T(key, fallback);
            if (args == null || args.Length == 0) return format;
            try { return string.Format(format, args); }
            catch (FormatException) { return format; }
        }
    }
}
