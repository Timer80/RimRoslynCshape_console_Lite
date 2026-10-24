# GUI C# 控制台语言文件支持 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（\`- [ ]\`）语法来跟踪进度。

**目标：** 让游戏内 GUI C# 控制台的可见文案走 RimWorld 标准语言文件（Languages/<lang>/Keyed/*.xml），提供 English 与 ChineseSimplified 两种语言。

**架构：** 在仓库根新建 Languages/English/Keyed/RoslynProbe.xml（英文，defaultLanguage 兜底，硬要求）与 Languages/ChineseSimplified/Keyed/RoslynProbe.xml（简中）——引擎 LanguageDatabase / LoadedLanguage.LoadData 自动扫描每个 mod 根的 Languages/，LoadFolders.xml 已挂载根目录 "/"，无需改动。新增薄封装 ConsoleText.T/Tf（缺键或语言系统不可用时回退代码里现有文案，保证 xunit 与 SlashHarness 零回归），ConsoleWindow 与 DebugToolbarPatch 的 22 处可见文案改为键化调用。协议性字符串与开发日志明确不翻译。

**技术栈：** C# / net10.0、RimWorld 1.7（Waylith 引擎）Verse 语言系统、xunit、SlashHarness 跑手。

---

## 范围（用户已裁定）

- ✅ GUI 控制台窗口全部可见文案、调试工具栏按钮悬停提示。
- ❌ 斜杠指令输出（/help 的 Usage/Group、/ui open 等 Lines）**本轮不做**。
- 语言集只做 **English + ChineseSimplified**。

## 不翻译清单（协议与机器面，禁止键化）

| 项 | 位置 | 理由 |
|---|---|---|
| 流内行前缀 "> " / "= " / "! " / "…" / "x{n}" | ConsoleWindow.RowsOf / DetailText | 渲染协议，ConsoleRenderTests 有回归断言 |
| bye（quit 落账行） | ConsoleWindow.QuitNoteLines | 与命名管道 "> bye" 同口径 |
| --json 单行 Data 及其字段名 | SlashJson / 各指令 Data | 管道消费方按字段名解析 |
| 斜杠指令 verb / Usage | SlashRegistry 等 | 机器面；本轮范围外 |
| Log.* 开发日志 | ConsoleLayer / DebugToolbarPatch / Bootstrap | 面向开发者，不进 GUI 流 |

## 引擎机制要点（已勘探确认，实现者无需重查）

1. 扫描：LanguageDatabase.InitAllMetadata() 扫每个运行中 mod 的 foldersToLoadDescendingOrder + "Languages"；LoadedLanguage.LoadData() 读 <语言文件夹>/Keyed/*.xml。本 mod LoadFolders.xml 已含 "/"，根目录 Languages/ 会被扫到。
2. 语言文件夹名必须是游戏标准名：English、ChineseSimplified（SupportedAutoSelectLanguages）。
3. LanguageDatabase.defaultLanguage 恒为 English：**English keyed 文件是硬要求**。缺它时任何非简中玩家缺键会直接显示**键名本身**（LoadedLanguage.TryGetTextFromKey 的 translated = key 回填），且 Prefs.DevMode 下还会被 PseudoTranslated 打成乱码。
4. XML 值支持 \n 转义（DirectXmlLoaderSimple.ValuesFromXmlFile 做 value.Replace("\\n", "\n")），不做 trim——单行书写最干净。
5. 值为字面 TODO 会被当成占位符，TryGetTextFromKey 按缺失处理——键值禁止写 TODO。
6. 数据懒加载：首次 TryGetTextFromKey 触发 LoadData，主菜单阶段可用，无需额外初始化。
7. 不裸用 key.Translate()：LanguageDatabase.activeLanguage == null 时会 Log.Error（SlashHarness 测试进程即为 null），且 DevMode 缺键伪翻译——所以走自己的封装。
8. LoadedLanguage.TryGetTextFromKey(string, out TaggedString) 是 public，可直接调用。

## 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| Languages/English/Keyed/RoslynProbe.xml | 创建 | 英文键值（兜底语言，必须全量） |
| Languages/ChineseSimplified/Keyed/RoslynProbe.xml | 创建 | 简中键值 |
| Source/Game/Ui/ConsoleText.cs | 创建 | 本地化薄封装 T/Tf（回退兜底文本） |
| Source/Game/Ui/ConsoleWindow.cs | 修改 | 21 处文案键化 |
| Source/Game/DebugToolbarPatch.cs | 修改 | 工具栏 tooltip 键化（1 键） |
| tests/LanguageFileTests.cs | 创建 | 键集一致性 / 占位符 / 源码键覆盖 扫描测试 |

不改：LoadFolders.xml（根 "/" 已挂载）、About/About.xml（RimWorld 不翻译 About 元数据）、Source/Core/**（零 Verse 依赖的测试面）。

## 键清单（22 键，定稿）

代码兜底 = 现有硬编码文本（保证无语言数据时行为逐字不变）；ChineseSimplified 的值与中文兜底相同（简中玩家读 keyed，缺文件时兜底也是中文）。

| # | 键 | 代码兜底（= ChineseSimplified 值） | English 值 |
|---|---|---|---|
| 1 | RoslynProbe.Console.Title | RoslynProbe 控制台 | RoslynProbe Console |
| 2 | RoslynProbe.Console.Stream.Hint | 在此输入 C# 表达式。Enter 执行 / Shift+Enter 换行；求值进行中可继续入队。 | Type C# expressions here. Enter runs / Shift+Enter adds a line; more can be queued while one is running. |
| 3 | RoslynProbe.Console.Note.Reset | 会话已重置：变量 / 类型 / usings 已清空（日志保留；要清日志请点「清空」） | Session reset: variables / types / usings cleared (log kept; use Clear to wipe it). |
| 4 | RoslynProbe.Console.Note.NotWired | 控制台未接线 | console not wired |
| 5 | RoslynProbe.Console.Note.SlashParseFailed | 指令解析失败: {0} | Command parsing failed: {0} |
| 6 | RoslynProbe.Console.Note.QueueFull | 队列已满（{0}），本次未入队 | Queue full ({0}); not enqueued. |
| 7 | RoslynProbe.Console.Note.Poisoned | 会话已失效（poisoned），本次未入队 | Session is poisoned; not enqueued. |
| 8 | RoslynProbe.Console.Note.EnqueueFailed | 入队失败：{0} | Enqueue failed: {0} |
| 9 | RoslynProbe.Console.Detail.Locked | 详情 · 已锁定 #{0} | Detail · locked #{0} |
| 10 | RoslynProbe.Console.Detail.Following | 详情 · 跟随最新 | Detail · following latest |
| 11 | RoslynProbe.Console.Detail.Empty | （空） | (empty) |
| 12 | RoslynProbe.Console.Button.Copy | 复制 | Copy |
| 13 | RoslynProbe.Console.Button.Reset | 重置 | Reset |
| 14 | RoslynProbe.Console.Button.Clear | 清空 | Clear |
| 15 | RoslynProbe.Console.Status.Rev | rev {0} | rev {0} |
| 16 | RoslynProbe.Console.Status.Ticks | ticks {0} | ticks {0} |
| 17 | RoslynProbe.Console.Status.Row | 行 {0}/{1} | row {0}/{1} |
| 18 | RoslynProbe.Console.Status.Buffer | 缓冲 {0}/{1} | buffer {0}/{1} |
| 19 | RoslynProbe.Console.Status.Queue | 队列 {0} | queue {0} |
| 20 | RoslynProbe.Console.Status.Locked | 已锁定 | locked |
| 21 | RoslynProbe.Console.Status.Following | 跟随 | following |
| 22 | RoslynProbe.Toolbar.Tooltip | 切换 RoslynProbe 控制台。\n\n本会话的实时 C# REPL。 | Toggle the RoslynProbe console.\n\nA live C# REPL for this session. |

键名只在调用点以字面量出现（每键用 1–2 次，不抽常量），由测试正则 ConsoleText\.T[f]?\(\s*"([^"]+)" 反向扫描核对覆盖。

---

### 任务 1：语言文件骨架 + 键集纪律测试

**文件：**
- 创建：Languages/English/Keyed/RoslynProbe.xml
- 创建：Languages/ChineseSimplified/Keyed/RoslynProbe.xml
- 创建：tests/LanguageFileTests.cs

- [ ] **步骤 1：编写失败的测试**

创建 tests/LanguageFileTests.cs（xunit 工程 tests/RoslynProbe.Tests.csproj 用 <Compile Include="**\*.cs"> 自动收编本目录，无需登记；该工程只编 Source/Core 全集，碰不到 Game/Ui 类型，所以全部走文件 IO）：

~~~csharp
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
                    Assert.NotEqual("TODO", kv.Value, lang + " 的键 " + kv.Key + " 值是占位符 TODO");
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
~~~

- [ ] **步骤 2：运行测试验证失败**

运行：dotnet test tests\RoslynProbe.Tests.csproj -c Release --filter FullyQualifiedName~LanguageFileTests
预期：FAIL——两条 Fact 都报「缺语言文件: ...\Languages\English\Keyed\RoslynProbe.xml」（目录还不存在）。

- [ ] **步骤 3：创建两个语言文件骨架（各含第 1 号键）**

Languages/English/Keyed/RoslynProbe.xml：

~~~xml
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <!-- RoslynProbe GUI 控制台键值（English = defaultLanguage 兜底，必须与其它语言键集一致） -->
  <RoslynProbe.Console.Title>RoslynProbe Console</RoslynProbe.Console.Title>
</LanguageData>
~~~

Languages/ChineseSimplified/Keyed/RoslynProbe.xml：

~~~xml
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <!-- RoslynProbe GUI 控制台键值 -->
  <RoslynProbe.Console.Title>RoslynProbe 控制台</RoslynProbe.Console.Title>
</LanguageData>
~~~

- [ ] **步骤 4：运行测试验证通过**

运行：dotnet test tests\RoslynProbe.Tests.csproj -c Release --filter FullyQualifiedName~LanguageFileTests
预期：PASS（2 条）。

- [ ] **步骤 5：Commit**

~~~bash
git add Languages tests/LanguageFileTests.cs
git commit -m "feat: 语言文件骨架（English/ChineseSimplified）与键集纪律测试"
~~~

---

### 任务 2：ConsoleText 封装 + ConsoleWindow 21 处文案键化

**文件：**
- 创建：Source/Game/Ui/ConsoleText.cs
- 修改：Source/Game/Ui/ConsoleWindow.cs（行号为当前基线，替换后会漂移，按内容定位）
- 修改：Languages/English/Keyed/RoslynProbe.xml、Languages/ChineseSimplified/Keyed/RoslynProbe.xml（补 20 键）
- 测试：tests/LanguageFileTests.cs（追加覆盖扫描 Fact）

- [ ] **步骤 1：编写失败的测试**

在 LanguageFileTests 类内追加：

~~~csharp
        [Fact]
        public void ConsoleWindow_Source_Keys_Are_Present_In_English_File()
        {
            var scanned = ScanKeys(Path.Combine(RepoRoot(), "Source", "Game", "Ui", "ConsoleWindow.cs"));
            Assert.True(scanned.Count >= 21, "ConsoleWindow.cs 里键化调用不足 21 个，实际 " + scanned.Count);
            var en = EntriesOf("English").Keys.ToHashSet();
            var missing = scanned.Where(k => !en.Contains(k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, "源码用了但 English 文件没有的键: " + string.Join(", ", missing));
        }
~~~

- [ ] **步骤 2：运行测试验证失败**

运行：dotnet test tests\RoslynProbe.Tests.csproj -c Release --filter FullyQualifiedName~LanguageFileTests
预期：FAIL——「ConsoleWindow.cs 里键化调用不足 21 个，实际 0」。

- [ ] **步骤 3：编写 ConsoleText 封装**

创建 Source/Game/Ui/ConsoleText.cs（该文件会被主工程 Source/RoslynProbe.csproj 与 SlashHarness 的 <Compile Include="..\..\Source\**\*.cs"> 自动收编）：

~~~csharp
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
~~~

- [ ] **步骤 4：键化 ConsoleWindow（逐处替换）**

以下替换全部位于 Source/Game/Ui/ConsoleWindow.cs。**ResetNoteBody 的 public const 必须保留**——tests/SlashHarness/ConsoleRenderTests.cs:64 直接引用它，它降级为兜底文本。

1. :86（构造函数标题）：
~~~csharp
// 原：optionalTitle = "RoslynProbe 控制台";
optionalTitle = ConsoleText.T("RoslynProbe.Console.Title", "RoslynProbe 控制台");
~~~

2. :221（空流提示）：
~~~csharp
// 原：GUI.Label(rect, "在此输入 C# 表达式。Enter 执行 / Shift+Enter 换行；求值进行中可继续入队。", monoLabel);
GUI.Label(rect, ConsoleText.T("RoslynProbe.Console.Stream.Hint", "在此输入 C# 表达式。Enter 执行 / Shift+Enter 换行；求值进行中可继续入队。"), monoLabel);
~~~

3. :416 与 :459（两处相同字面量）：
~~~csharp
// 原：ConsoleLog.SystemNote(SystemNoteText("console not wired"));
ConsoleLog.SystemNote(SystemNoteText(ConsoleText.T("RoslynProbe.Console.Note.NotWired", "控制台未接线")));
~~~

4. :425（slash 解析失败）：
~~~csharp
// 原：ConsoleLog.SystemNote(SystemNoteText("指令解析失败: " + trimmed));
ConsoleLog.SystemNote(SystemNoteText(ConsoleText.Tf("RoslynProbe.Console.Note.SlashParseFailed", "指令解析失败: {0}", trimmed)));
~~~

5. :466（队列满）：
~~~csharp
// 原：ConsoleLog.SystemNote(SystemNoteText("队列已满（" + GuiMaxQueue + "），本次未入队"));
ConsoleLog.SystemNote(SystemNoteText(ConsoleText.Tf("RoslynProbe.Console.Note.QueueFull", "队列已满（{0}），本次未入队", GuiMaxQueue)));
~~~

6. :474（poisoned）：
~~~csharp
// 原：ConsoleLog.SystemNote(SystemNoteText("会话已失效（poisoned），本次未入队"));
ConsoleLog.SystemNote(SystemNoteText(ConsoleText.T("RoslynProbe.Console.Note.Poisoned", "会话已失效（poisoned），本次未入队")));
~~~

7. :487（入队失败）：
~~~csharp
// 原：ConsoleLog.SystemNote(SystemNoteText("入队失败：" + ex.Message));
ConsoleLog.SystemNote(SystemNoteText(ConsoleText.Tf("RoslynProbe.Console.Note.EnqueueFailed", "入队失败：{0}", ex.Message)));
~~~

8. :527（详情栏标题）：
~~~csharp
// 原：string title = (lockedId >= 0 && e != null) ? ("详情 · 已锁定 #" + e.Id) : "详情 · 跟随最新";
string title = (lockedId >= 0 && e != null)
    ? ConsoleText.Tf("RoslynProbe.Console.Detail.Locked", "详情 · 已锁定 #{0}", e.Id)
    : ConsoleText.T("RoslynProbe.Console.Detail.Following", "详情 · 跟随最新");
~~~

9. :532（详情空态）：
~~~csharp
// 原：string body = e != null ? DetailText(e) : "（空）";
string body = e != null ? DetailText(e) : ConsoleText.T("RoslynProbe.Console.Detail.Empty", "（空）");
~~~

10. :534（复制按钮）：
~~~csharp
// 原：if (e != null && Widgets.ButtonText(copyRect, "复制"))
if (e != null && Widgets.ButtonText(copyRect, ConsoleText.T("RoslynProbe.Console.Button.Copy", "复制")))
~~~

11. :589（标题栏标签）：
~~~csharp
// 原：GUI.Label(new Rect(rect.x + 4f, rect.y + 5f, 260f, 20f), "RoslynProbe 控制台", monoLabel);
GUI.Label(new Rect(rect.x + 4f, rect.y + 5f, 260f, 20f), ConsoleText.T("RoslynProbe.Console.Title", "RoslynProbe 控制台"), monoLabel);
~~~

12. :594（重置按钮）：
~~~csharp
// 原：if (Widgets.ButtonText(resetRect, "重置") && executor != null && host != null)
if (Widgets.ButtonText(resetRect, ConsoleText.T("RoslynProbe.Console.Button.Reset", "重置")) && executor != null && host != null)
~~~

13. :604（重置提示落账，ResetNoteBody 作兜底）：
~~~csharp
// 原：ConsoleLog.SystemNote(SystemNoteText(ResetNoteBody));
ConsoleLog.SystemNote(SystemNoteText(ConsoleText.T("RoslynProbe.Console.Note.Reset", ResetNoteBody)));
~~~

14. :610（清空按钮）：
~~~csharp
// 原：if (Widgets.ButtonText(clearRect, "清空"))
if (Widgets.ButtonText(clearRect, ConsoleText.T("RoslynProbe.Console.Button.Clear", "清空")))
~~~

15. :631-636（状态行，整段替换）：
~~~csharp
// 原：
// string text = "rev " + (executor != null ? executor.Revision : 0)
//     + " · ticks " + (executor != null ? executor.TickCount : 0)
//     + " · 行 " + rowN + "/" + list.Count
//     + " · 缓冲 " + list.Count + "/" + ConsoleLog.MaxEntries
//     + (executor != null && executor.PendingCount > 0 ? " · 队列 " + executor.PendingCount : "")
//     + (lockedId >= 0 ? " · 已锁定" : " · 跟随");
string text = ConsoleText.Tf("RoslynProbe.Console.Status.Rev", "rev {0}", executor != null ? executor.Revision : 0)
    + " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Ticks", "ticks {0}", executor != null ? executor.TickCount : 0)
    + " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Row", "行 {0}/{1}", rowN, list.Count)
    + " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Buffer", "缓冲 {0}/{1}", list.Count, ConsoleLog.MaxEntries)
    + (executor != null && executor.PendingCount > 0
        ? " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Queue", "队列 {0}", executor.PendingCount)
        : "")
    + " · " + (lockedId >= 0
        ? ConsoleText.T("RoslynProbe.Console.Status.Locked", "已锁定")
        : ConsoleText.T("RoslynProbe.Console.Status.Following", "跟随"));
~~~

替换后自查：grep 中文残留 Source/Game/Ui/ConsoleWindow.cs，剩余中文只应出现在注释与上述兜底参数里。

- [ ] **步骤 5：补齐两个语言文件（各补 20 键，共 21 键）**

Languages/English/Keyed/RoslynProbe.xml（完整替换为）：

~~~xml
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <!-- RoslynProbe GUI 控制台键值（English = defaultLanguage 兜底，键集必须与其它语言一致） -->
  <RoslynProbe.Console.Title>RoslynProbe Console</RoslynProbe.Console.Title>
  <RoslynProbe.Console.Stream.Hint>Type C# expressions here. Enter runs / Shift+Enter adds a line; more can be queued while one is running.</RoslynProbe.Console.Stream.Hint>
  <RoslynProbe.Console.Note.Reset>Session reset: variables / types / usings cleared (log kept; use Clear to wipe it).</RoslynProbe.Console.Note.Reset>
  <RoslynProbe.Console.Note.NotWired>console not wired</RoslynProbe.Console.Note.NotWired>
  <RoslynProbe.Console.Note.SlashParseFailed>Command parsing failed: {0}</RoslynProbe.Console.Note.SlashParseFailed>
  <RoslynProbe.Console.Note.QueueFull>Queue full ({0}); not enqueued.</RoslynProbe.Console.Note.QueueFull>
  <RoslynProbe.Console.Note.Poisoned>Session is poisoned; not enqueued.</RoslynProbe.Console.Note.Poisoned>
  <RoslynProbe.Console.Note.EnqueueFailed>Enqueue failed: {0}</RoslynProbe.Console.Note.EnqueueFailed>
  <RoslynProbe.Console.Detail.Locked>Detail · locked #{0}</RoslynProbe.Console.Detail.Locked>
  <RoslynProbe.Console.Detail.Following>Detail · following latest</RoslynProbe.Console.Detail.Following>
  <RoslynProbe.Console.Detail.Empty>(empty)</RoslynProbe.Console.Detail.Empty>
  <RoslynProbe.Console.Button.Copy>Copy</RoslynProbe.Console.Button.Copy>
  <RoslynProbe.Console.Button.Reset>Reset</RoslynProbe.Console.Button.Reset>
  <RoslynProbe.Console.Button.Clear>Clear</RoslynProbe.Console.Button.Clear>
  <RoslynProbe.Console.Status.Rev>rev {0}</RoslynProbe.Console.Status.Rev>
  <RoslynProbe.Console.Status.Ticks>ticks {0}</RoslynProbe.Console.Status.Ticks>
  <RoslynProbe.Console.Status.Row>row {0}/{1}</RoslynProbe.Console.Status.Row>
  <RoslynProbe.Console.Status.Buffer>buffer {0}/{1}</RoslynProbe.Console.Status.Buffer>
  <RoslynProbe.Console.Status.Queue>queue {0}</RoslynProbe.Console.Status.Queue>
  <RoslynProbe.Console.Status.Locked>locked</RoslynProbe.Console.Status.Locked>
  <RoslynProbe.Console.Status.Following>following</RoslynProbe.Console.Status.Following>
</LanguageData>
~~~

Languages/ChineseSimplified/Keyed/RoslynProbe.xml（完整替换为）：

~~~xml
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <!-- RoslynProbe GUI 控制台键值 -->
  <RoslynProbe.Console.Title>RoslynProbe 控制台</RoslynProbe.Console.Title>
  <RoslynProbe.Console.Stream.Hint>在此输入 C# 表达式。Enter 执行 / Shift+Enter 换行；求值进行中可继续入队。</RoslynProbe.Console.Stream.Hint>
  <RoslynProbe.Console.Note.Reset>会话已重置：变量 / 类型 / usings 已清空（日志保留；要清日志请点「清空」）</RoslynProbe.Console.Note.Reset>
  <RoslynProbe.Console.Note.NotWired>控制台未接线</RoslynProbe.Console.Note.NotWired>
  <RoslynProbe.Console.Note.SlashParseFailed>指令解析失败: {0}</RoslynProbe.Console.Note.SlashParseFailed>
  <RoslynProbe.Console.Note.QueueFull>队列已满（{0}），本次未入队</RoslynProbe.Console.Note.QueueFull>
  <RoslynProbe.Console.Note.Poisoned>会话已失效（poisoned），本次未入队</RoslynProbe.Console.Note.Poisoned>
  <RoslynProbe.Console.Note.EnqueueFailed>入队失败：{0}</RoslynProbe.Console.Note.EnqueueFailed>
  <RoslynProbe.Console.Detail.Locked>详情 · 已锁定 #{0}</RoslynProbe.Console.Detail.Locked>
  <RoslynProbe.Console.Detail.Following>详情 · 跟随最新</RoslynProbe.Console.Detail.Following>
  <RoslynProbe.Console.Detail.Empty>（空）</RoslynProbe.Console.Detail.Empty>
  <RoslynProbe.Console.Button.Copy>复制</RoslynProbe.Console.Button.Copy>
  <RoslynProbe.Console.Button.Reset>重置</RoslynProbe.Console.Button.Reset>
  <RoslynProbe.Console.Button.Clear>清空</RoslynProbe.Console.Button.Clear>
  <RoslynProbe.Console.Status.Rev>rev {0}</RoslynProbe.Console.Status.Rev>
  <RoslynProbe.Console.Status.Ticks>ticks {0}</RoslynProbe.Console.Status.Ticks>
  <RoslynProbe.Console.Status.Row>行 {0}/{1}</RoslynProbe.Console.Status.Row>
  <RoslynProbe.Console.Status.Buffer>缓冲 {0}/{1}</RoslynProbe.Console.Status.Buffer>
  <RoslynProbe.Console.Status.Queue>队列 {0}</RoslynProbe.Console.Status.Queue>
  <RoslynProbe.Console.Status.Locked>已锁定</RoslynProbe.Console.Status.Locked>
  <RoslynProbe.Console.Status.Following>跟随</RoslynProbe.Console.Status.Following>
</LanguageData>
~~~

- [ ] **步骤 6：运行测试验证通过**

运行：dotnet test tests\RoslynProbe.Tests.csproj -c Release --filter FullyQualifiedName~LanguageFileTests
预期：PASS（3 条：键集一致、无 TODO、ConsoleWindow 键覆盖）。

- [ ] **步骤 7：编译面校验（SlashHarness 全集）**

运行：dotnet msbuild -restore:false tests\SlashHarness\SlashHarness.csproj -t:Rebuild -p:OutputPath=$env:TEMP\slashharness\ -p:BaseIntermediateOutputPath=$env:TEMP\slashobj\loc2\
预期：0 个错误。这验证 ConsoleText.cs 与 ConsoleWindow 改动在「Source 全集 + Verse 引用」下编译成立。

- [ ] **步骤 8：Commit**

~~~bash
git add Source/Game/Ui/ConsoleText.cs Source/Game/Ui/ConsoleWindow.cs Languages tests/LanguageFileTests.cs
git commit -m "feat: GUI 控制台窗口文案接入语言文件（ConsoleText 封装 + 21 键）"
~~~

---

### 任务 3：调试工具栏按钮悬停提示键化

**文件：**
- 修改：Source/Game/DebugToolbarPatch.cs:48-51
- 修改：两个 Languages/*/Keyed/RoslynProbe.xml（各 +1 键）
- 测试：tests/LanguageFileTests.cs（追加 toolbar 覆盖 Fact）

- [ ] **步骤 1：编写失败的测试**

在 LanguageFileTests 类内追加：

~~~csharp
        [Fact]
        public void Toolbar_Source_Keys_Are_Present_In_English_File()
        {
            var scanned = ScanKeys(Path.Combine(RepoRoot(), "Source", "Game", "DebugToolbarPatch.cs"));
            Assert.True(scanned.Count >= 1, "DebugToolbarPatch.cs 里键化调用不足 1 个，实际 " + scanned.Count);
            var en = EntriesOf("English").Keys.ToHashSet();
            var missing = scanned.Where(k => !en.Contains(k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, "源码用了但 English 文件没有的键: " + string.Join(", ", missing));
        }
~~~

- [ ] **步骤 2：运行测试验证失败**

运行：dotnet test tests\RoslynProbe.Tests.csproj -c Release --filter FullyQualifiedName~LanguageFileTests
预期：FAIL——「DebugToolbarPatch.cs 里键化调用不足 1 个，实际 0」。

- [ ] **步骤 3：键化 tooltip**

Source/Game/DebugToolbarPatch.cs 的 row.ButtonIcon(...) 调用（原 :48-51）：

~~~csharp
// 原：
// bool clicked = row.ButtonIcon(
//     icon,
//     "Toggle the RoslynProbe console.\n\nA live C# REPL for this session.",
//     GenUI.MouseoverColor);
bool clicked = row.ButtonIcon(
    icon,
    ConsoleText.T("RoslynProbe.Toolbar.Tooltip", "Toggle the RoslynProbe console.\n\nA live C# REPL for this session."),
    GenUI.MouseoverColor);
~~~

（ConsoleText 与本文件同在 namespace RoslynProbe，无需加 using。）

- [ ] **步骤 4：两个语言文件各 +1 键**

English 文件 </LanguageData> 前追加：

~~~xml
  <RoslynProbe.Toolbar.Tooltip>Toggle the RoslynProbe console.\n\nA live C# REPL for this session.</RoslynProbe.Toolbar.Tooltip>
~~~

ChineseSimplified 文件 </LanguageData> 前追加：

~~~xml
  <RoslynProbe.Toolbar.Tooltip>切换 RoslynProbe 控制台。\n\n本会话的实时 C# REPL。</RoslynProbe.Toolbar.Tooltip>
~~~

（\n 是引擎 DirectXmlLoaderSimple 支持的字面转义，加载后变成真换行。）

- [ ] **步骤 5：运行测试验证通过**

运行：dotnet test tests\RoslynProbe.Tests.csproj -c Release --filter FullyQualifiedName~LanguageFileTests
预期：PASS（4 条）。

- [ ] **步骤 6：Commit**

~~~bash
git add Source/Game/DebugToolbarPatch.cs Languages tests/LanguageFileTests.cs
git commit -m "feat: 调试工具栏按钮提示接入语言文件"
~~~

---

### 任务 4：全量回归（两个测试面都跑）

**文件：** 无新增（验证性任务）

- [ ] **步骤 1：跑 xunit 全量**

运行：dotnet test tests\RoslynProbe.Tests.csproj -c Release
预期：全部 PASS（含既有 ConsoleLogTests / ExecutorTests / JsonTests / PipeSessionTests 与新增 LanguageFileTests）。

- [ ] **步骤 2：跑 SlashHarness 跑手两遍**

运行（仓库根）：dotnet run --project tests/SlashHarness/SlashHarnessRunner.csproj -- --twice
预期：退出码 0；[第1/2遍] 与 [第2/2遍] 均为「失败合计 = 0」。这覆盖 19 个测试类 / 3800+ 断言，其中 ConsoleRenderTests（引用 ResetNoteBody）与 LegacyCommandsTests（断言中文输出）是本次改动的关键回归点——键化只动 GUI 渲染层，这些断言不应有任何变化。

- [ ] **步骤 3：无需 commit（零代码变更；若跑手或测试暴露问题，回对应任务修复后重跑本任务）**

---

### 任务 5：真机验证（English / ChineseSimplified 各一次）

**文件：** 无仓库内变更（只产截图证据）

> 原理：-savedatafolder 隔离出干净配置目录；ModsConfig.xml 必须**预先写入**（否则新目录首次启动只加载原版 DLC，本 mod 不在列表、控制台不存在）；Prefs.xml 的 langFolderName 决定 LanguageDatabase.activeLanguage。

- [ ] **步骤 1：准备隔离目录与配置**

~~~powershell
$iso = "$env:TEMP\rr-locale-test"
New-Item -ItemType Directory -Force -Path "$iso\Config" | Out-Null

# ModsConfig.xml：三个 packageId 按 About.xml 与依赖小写化
@'
<ModsConfigData>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ue.roslyn</li>
    <li>skylon.RimRoslynCshape_console.lite</li>
  </activeMods>
  <knowledgeBase>
    <li>1.7.4979 rev109</li>
  </knowledgeBase>
</ModsConfigData>
'@ | Set-Content "$iso\Config\ModsConfig.xml" -Encoding UTF8

# 先测 English；devMode 打开，顺带验证 DevMode 下无伪翻译乱码
@'
<prefsData>
  <langFolderName>English</langFolderName>
  <devMode>true</devMode>
</prefsData>
'@ | Set-Content "$iso\Config\Prefs.xml" -Encoding UTF8
~~~

- [ ] **步骤 2：启动游戏并打开控制台**

~~~powershell
& "C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe" -savedatafolder="$iso" -quicktest -NoWindow -NoCrashDialog -RoslynConsole=loc
~~~

等游戏进图（-quicktest 要等地图就绪），另开 PowerShell 用管道开窗：

~~~powershell
$p=[IO.Pipes.NamedPipeClientStream]::new('.','roslyn-console-loc',[IO.Pipes.PipeDirection]::InOut);$p.Connect(120000);$w=[IO.StreamWriter]::new($p);$w.AutoFlush=1;$r=[IO.StreamReader]::new($p);$r.ReadLine()|Out-Null;$w.WriteLine('/ui open');while(($l=$r.ReadLine())-ne '>'){$l};$p.Dispose()
~~~

- [ ] **步骤 3：截屏存证**

去掉 -NoWindow 以可见窗口重启（或直接在可见窗口下重复步骤 2），截图：

~~~powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Drawing.Rectangle]::FromLTRB(0, 0, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("$iso\console-English.png")
$g.Dispose(); $bmp.Dispose()
~~~

English 检查点：
- 标题 RoslynProbe Console；按钮 Copy / Reset / Clear；
- 空流提示为英文；状态行为 rev … · ticks … · row …/… · buffer …/… · following；
- 工具栏图标悬停提示为英文两行（\n 生效）；
- DevMode 开启下**无乱码**（à þ ç 之类的伪翻译字符）；
- 非 ASCII 字形无方框（本字体风险只涉及中/英，均应正常）。

- [ ] **步骤 4：切 ChineseSimplified 复验**

退出游戏，把 $iso\Config\Prefs.xml 的 <langFolderName> 改为 ChineseSimplified，重启重复步骤 2–3，存 console-ChineseSimplified.png。

简中检查点：标题 RoslynProbe 控制台、按钮 复制/重置/清空、状态行 行/缓冲/队列/已锁定/跟随 与键化前显示逐字一致。

- [ ] **步骤 5：清理与归档**

- 关闭游戏进程；$iso 目录保留在 $env:TEMP（不进仓库），两张截图作为验收证据附在交付说明里；
- 若某检查点不过：先判断是键值问题（改 XML 即可）还是字体/引擎问题（回退到 ConsoleText 兜底，UI 保持可用），修复后从本任务步骤 1 重来。

---

## 自检记录（计划作者已完成）

1. **规格覆盖**：GUI chrome 22 键全部落在键清单表与任务 2/3 的代码块中；不翻译清单与引擎机制要点独立成节；两个测试面的回归都有明确命令与预期。
2. **占位符扫描**：无「待定/TODO/类似任务 N」；所有代码步骤给出可直接落盘的完整代码或逐处替换块。
3. **类型一致性**：ConsoleText.T(string, string) / ConsoleText.Tf(string, string, params object[]) 在任务 2 定义、任务 3 沿用；测试方法名 ConsoleWindow_Source_Keys_Are_Present_In_English_File / Toolbar_Source_Keys_Are_Present_In_English_File 与 ScanKeys / EntriesOf / RepoRoot 在各任务间一致。
