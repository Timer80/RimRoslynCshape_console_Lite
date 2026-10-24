using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace RoslynProbe.Tests
{
    /// <summary>
    /// 语言文件纪律测试（RimWorld Keyed 翻译）。xunit 工程只编 Source/Core，碰不到 Game/Ui 的类型，
    /// 所以这里全部走文件 IO：解析 Languages/&lt;lang&gt;/Keyed/RoslynProbe.xml，
    /// 并正则扫描 GUI 源码里 ConsoleText.T/Tf 的键调用。
    /// </summary>
    public class LanguageFileTests
    {
        /// <summary>从测试输出目录向上找仓库根（判据 = LoadFolders.xml，它在仓库根且永远存在）。</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoadFolders.xml")))
                dir = dir.Parent;
            Assert.True(dir != null, "向上找不到仓库根（LoadFolders.xml）——测试输出目录布局变了？");
            return dir.FullName;
        }

        private static string KeyedPath(string lang)
        {
            return Path.Combine(RepoRoot(), "Languages", lang, "Keyed", "RoslynProbe.xml");
        }

        private static Dictionary<string, string> EntriesOf(string lang)
        {
            string path = KeyedPath(lang);
            Assert.True(File.Exists(path), "缺语言文件: " + path);
            XDocument doc = XDocument.Load(path);
            Assert.NotNull(doc.Root);
            var map = new Dictionary<string, string>();
            foreach (XElement e in doc.Root.Elements())
            {
                string key = e.Name.ToString();
                Assert.False(map.ContainsKey(key), "重复键 " + key + "（" + lang + "）");
                map[key] = e.Value;
            }
            return map;
        }

        [Fact]
        public void English_And_ChineseSimplified_Have_Identical_Key_Sets()
        {
            var en = EntriesOf("English").Keys.ToHashSet();
            var zh = EntriesOf("ChineseSimplified").Keys.ToHashSet();
            Assert.Equal(en, zh); // SetEqual 会列出双方差集
        }

        [Fact]
        public void No_Key_Value_Is_TODO_Placeholder()
        {
            // 引擎把字面 "TODO" 当占位符：TryGetTextFromKey 直接按缺失处理（回填键名 + DevMode 伪翻译）。
            foreach (string lang in new[] { "English", "ChineseSimplified" })
                foreach (var kv in EntriesOf(lang))
                    Assert.True(kv.Value != "TODO", lang + " 的键 " + kv.Key + " 值是占位符 TODO");
        }

        [Fact]
        public void ConsoleWindow_Source_Keys_Are_Present_In_English_File()
        {
            var scanned = ScanKeys(Path.Combine(RepoRoot(), "Source", "Game", "Ui", "ConsoleWindow.cs"));
            Assert.True(scanned.Count >= 21, "ConsoleWindow.cs 里键化调用不足 21 个，实际 " + scanned.Count);
            var en = EntriesOf("English").Keys.ToHashSet();
            var missing = scanned.Where(k => !en.Contains(k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, "源码用了但 English 文件没有的键: " + string.Join(", ", missing));
        }

        private static HashSet<string> ScanKeys(string file)
        {
            Assert.True(File.Exists(file), "缺源文件: " + file);
            string text = File.ReadAllText(file);
            var set = new HashSet<string>();
            foreach (Match m in Regex.Matches(text, "ConsoleText\\.T[f]?\\(\\s*\"([^\"]+)\""))
                set.Add(m.Groups[1].Value);
            return set;
        }
    }
}
