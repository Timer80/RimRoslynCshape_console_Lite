// RoslynProbe —— REPL 交互基类（Roslyn submission 继承链的根）。
//
// 对标 UESD (Mono.CSharp) 的 ScriptInteraction / InteractiveBaseClass：
// 用户代码被包进 __SubN : ... : ReplInteractiveBase 的静态成员类里，
// 因此这里提供的 public static 助手在控制台里可以裸调用（Print(...)、Warning(...)、Error(...)、
// GetVars() 等）。注意**不要**再引入名为 `Log` 的成员，否则会遮蔽 `Verse.Log`（见类注释）。
//
// 本类必须保持「无 Microsoft.CodeAnalysis typeref」——它常驻 RoslynProbe.dll，
// 而 RoslynProbe.dll 的静态字段扫描（ReportProbablyMissingAttributes）在 Roslyn
// 载荷未就绪时也必须能通过（见 Mods/Roslyn/Source/RoslynLoader.cs 的设计红线）。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace RoslynProbe.Repl
{
    /// <summary>
    /// Roslyn submission 的公共基类（官方 Scripting 的 globals 类型）：向用户代码暴露
    /// Print / Warning / Error / Inspect / GetVars / GetUsings 等助手。
    /// **刻意不提供 `Log`**：那会遮蔽 `Verse.Log`，使 `Log.Message(...)` 无法编译。
    /// 全部静态成员且必须 public——官方 globals 机制按 globals 类型的成员做名字绑定
    /// （非继承，protected 会 CS0122），用户代码裸调用这些名字即绑定到此类型成员。
    /// </summary>
    public class ReplInteractiveBase
    {
        /// <summary>内部桥：引擎在会话建立时注入转储委托，避免基类反向依赖引擎类型。</summary>
        internal static class Bridge
        {
            internal static Func<string> VariablesDump;
            internal static Func<string> UsingsDump;
        }

        // 注意：本类型**不能**再有任何名为 `Log` 的成员。globals 类型的成员会遮蔽同名类型，
        // 一旦存在 `Log(...)`，用户代码里的 `Log.Message(...)` / `Log.Error(...)` 会被解析成
        // “对方法 Log 取成员”而报 CS0119 —— 而 Verse.Log 的用法是 RimWorld 开发里最常写的，
        // 必须优先保证可用。故消息助手统一叫 Print(...)（等价于 Verse.Log.Message）。

        public static void Print(object msg) { Verse.Log.Message(Inspect(msg)); }

        public static void Warning(object msg) { Verse.Log.Warning(Inspect(msg)); }

        public static void Error(object msg) { Verse.Log.Error(Inspect(msg)); }

        /// <summary>
        /// 友好转储：标量/字符串直接输出；集合截断列出；其它对象列出类型 + 公共字段/属性。
        /// </summary>
        public static string Inspect(object o)
        {
            if (o == null) return "null";
            string s = o as string;
            if (s != null) return s;
            Type type = o.GetType();
            if (type.IsPrimitive || type.IsEnum) return o.ToString();

            try
            {
                IEnumerable e = o as IEnumerable;
                if (e != null)
                {
                    var items = new List<string>();
                    foreach (object item in e)
                    {
                        items.Add(Inspect(item));
                        if (items.Count >= 12) { items.Add("…"); break; }
                    }
                    return type.Name + " [" + string.Join(", ", items.ToArray()) + "]";
                }
            }
            catch { /* 集合枚举失败回落到字段转储 */ }

            var sb = new StringBuilder();
            sb.Append(type.Name).Append(" { ");
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                int shown = 0;
                foreach (FieldInfo f in type.GetFields(flags))
                {
                    if (shown >= 8) { sb.Append("… "); break; }
                    sb.Append(f.Name).Append(" = ").Append(Safe(f.GetValue(o))).Append("; ");
                    shown++;
                }
                if (shown == 0)
                {
                    foreach (PropertyInfo p in type.GetProperties(flags))
                    {
                        if (shown >= 8) { sb.Append("… "); break; }
                        if (p.GetIndexParameters().Length != 0) continue;
                        object v = null;
                        try { v = p.GetValue(o, null); } catch { }
                        sb.Append(p.Name).Append(" = ").Append(Safe(v)).Append("; ");
                        shown++;
                    }
                }
            }
            catch { }
            sb.Append("}");
            return sb.ToString();
        }

        private static string Safe(object v)
        {
            if (v == null) return "null";
            string s = v as string;
            if (s != null) return "\"" + s + "\"";
            try
            {
                string t = v.ToString();
                return string.IsNullOrEmpty(t) ? v.GetType().Name : t;
            }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }

        public static string GetVars()
        {
            Func<string> f = Bridge.VariablesDump;
            return f != null ? f() : "(无活动会话)";
        }

        public static string GetUsings()
        {
            Func<string> f = Bridge.UsingsDump;
            return f != null ? f() : "(无活动会话)";
        }
    }
}
