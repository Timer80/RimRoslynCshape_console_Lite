// RoslynProbe —— /asm：已加载程序集清单。
//
// 规格：列出已加载程序集的名称、版本、位置、是否动态程序集（条数随运行环境变化）。
//   * 纯 BCL/反射，不需要姿势 B：唯一数据源是 AppDomain.CurrentDomain.GetAssemblies()。
//   * **绝不做全类型枚举** —— 本文件不出现 GetTypes()/GetExportedTypes()（那是 /types 的活；
//     全类型枚举会让这条指令在 111 个程序集上从毫秒级变成秒级）。
//   * filter 为子串匹配：name 或 location，不区分大小写。
//   * Data:{count, total, filter, assemblies:[{name, version, location, dynamic}]}
//     count = 本次返回条数（filter 之后），total = 已加载程序集总数；无 filter 时 count == total。
//     （count/total 口径与 W2 的 /log 一致：一个"返回了几条"、一个"一共有几条"。）
//
// 错误处理：逐程序集 try/catch —— 病态加载器下 GetName()/Location 可能抛；单个坏程序集
// 只降级成 "?"/空字段，绝不整条结果失败。Location 对动态程序集可能抛 NotSupportedException
// 或返回空串，两种都收敛成 ""（渲染成"(无位置)"）。

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RoslynProbe
{
    /// <summary>/asm [filter] —— 已加载程序集清单（纯 BCL/反射，零 Verse/Unity 依赖）。</summary>
    public static class AsmCommand
    {
        /// <summary>RegisterAll 反射调用（全程序集 public static void Register()，无参、返回 void）。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "asm",
                Usage = "[filter] —— 已加载程序集清单（名称/版本/位置/是否动态；filter 为 name 或 location 子串，不区分大小写）",
                Group = "运行时",
                Kind = "asm",
                Handler = Provide
            });
        }

        /// <summary>
        /// 指令入口实现（姿势 A：本指令不需要注入点，Provide 就是"该指令的实现"本身，
        /// 与 GcCommand / DiagCommand / ThreadsCommand 完全同形 —— 契约 v1.1 §3.5 的唯一形态；
        /// 由 AsmCommandTests 的命名断言兜住，防止漂回 Handle）。
        /// </summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string filter = (rest ?? "").Trim();

            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception ex)
            {
                return SlashResults.Error("asm",
                    "读取已加载程序集失败: " + ex.GetType().Name + ": " + ex.Message);
            }

            int total = assemblies == null ? 0 : assemblies.Length;
            var items = new List<Dictionary<string, object>>();
            if (assemblies != null)
            {
                foreach (Assembly asm in assemblies)
                {
                    if (asm == null) continue;
                    Dictionary<string, object> item = ShapeAssembly(asm);
                    if (filter.Length > 0 && !MatchesFilter(item, filter)) continue;
                    items.Add(item);
                }
            }
            // 稳定输出：GetAssemblies() 的顺序不是契约，按 (name, version, location) 排序，
            // 让同一次运行的两条相同指令、以及单测断言都可复现。
            items.Sort(CompareItems);

            var r = new SlashResult { Kind = "asm" };
            if (filter.Length == 0)
                r.Lines.Add("已加载程序集: " + items.Count + " 个");
            else
                r.Lines.Add("已加载程序集: " + items.Count + " / " + total
                    + " 个（filter=\"" + filter + "\"：name 或 location 子串，不区分大小写）");

            if (items.Count == 0)
            {
                r.Lines.Add("（无匹配程序集）");
            }
            else
            {
                int nameWidth = ColumnWidth(items, "name", 48);
                int verWidth = ColumnWidth(items, "version", 16);
                foreach (Dictionary<string, object> item in items)
                {
                    string name = AsString(item["name"]);
                    // 版本可能为空（GenerateAssemblyInfo=false 的程序集没有 AssemblyVersion 特性）：
                    // Data 里保持空串（诚实），行里渲染成 "-" 保住列对齐。
                    string ver = AsString(item["version"]);
                    if (ver.Length == 0) ver = "-";
                    string loc = AsString(item["location"]);
                    bool dyn = item["dynamic"] is bool && (bool)item["dynamic"];
                    r.Lines.Add("  " + Pad(name, nameWidth) + "  " + Pad(ver, verWidth) + "  "
                        + (dyn ? "[动态]" : "[静态]") + "  " + (loc.Length == 0 ? "(无位置)" : loc));
                }
            }
            r.Lines.Add("（机器可读清单见 Data[\"assemblies\"]；按短名找类型用 /types。）");

            r.Data = new Dictionary<string, object>
            {
                ["count"] = items.Count,
                ["total"] = total,
                ["filter"] = filter,
                ["assemblies"] = items
            };
            return r;
        }

        /// <summary>
        /// 单个程序集 → Data 项（name/version/location/dynamic）。逐项 try/catch：坏程序集降级不抛。
        /// internal 供单测（含动态程序集分支）直接喂 AssemblyBuilder。
        /// </summary>
        internal static Dictionary<string, object> ShapeAssembly(Assembly asm)
        {
            bool dynamic = false;
            string name = "?";
            string version = "";
            string location = "";

            try { dynamic = asm.IsDynamic; }
            catch { }

            try
            {
                AssemblyName an = asm.GetName();
                if (an != null)
                {
                    if (!string.IsNullOrEmpty(an.Name)) name = an.Name;
                    version = an.Version == null ? "" : an.Version.ToString();
                }
            }
            catch { }

            // 动态程序集（AssemblyBuilder / Roslyn 脚本提交）没有磁盘位置：可能抛，也可能是空串。
            try { location = asm.Location ?? ""; }
            catch { location = ""; }

            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["version"] = version,
                ["location"] = location,
                ["dynamic"] = dynamic
            };
        }

        /// <summary>filter 命中判定：name 或 location 的子串，不区分大小写。internal 供单测。</summary>
        internal static bool MatchesFilter(Dictionary<string, object> item, string filter)
        {
            if (item == null || string.IsNullOrEmpty(filter)) return false;
            string name = AsString(item["name"]);
            string loc = AsString(item["location"]);
            return name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || loc.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CompareItems(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            int c = string.Compare(AsString(a["name"]), AsString(b["name"]), StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.CompareOrdinal(AsString(a["version"]), AsString(b["version"]));
            if (c != 0) return c;
            return string.Compare(AsString(a["location"]), AsString(b["location"]), StringComparison.OrdinalIgnoreCase);
        }

        private static int ColumnWidth(List<Dictionary<string, object>> items, string key, int cap)
        {
            int w = 0;
            foreach (Dictionary<string, object> item in items)
            {
                int len = AsString(item[key]).Length;
                if (len > w) w = len;
            }
            return w > cap ? cap : w;
        }

        /// <summary>PadRight 的安全版（width 可能小于串长，直接 PadRight 会抛 ArgumentException）。</summary>
        private static string Pad(string s, int width)
        {
            s = s ?? "";
            return s.Length >= width ? s : s.PadRight(width);
        }

        private static string AsString(object o)
        {
            return o as string ?? "";
        }
    }
}
