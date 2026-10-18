// RoslynProbe —— REPL 门面。
//
// 职责：
//   1. 反射调用 UE.Roslyn 前置门闩（Roslyn.RoslynLoader.TryEnsureLoaded）——
//      本文件绝不编译期引用 Roslyn 前置包程序集（Mods/Roslyn/README.md §4 的红线）。
//   2. 把求值委托给 RoslynEngine —— 所有 Microsoft.CodeAnalysis typeref 都 confinement
//      在引擎类内；本文件不出现任何 Roslyn typeref，保证 JIT 顺序安全
//      （Eval 先跑完 EnsureGate，引擎方法体才会在首次调用时 JIT）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Verse;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>一条被拒的 using 候选：Item 是规范化后的候选，Error 点名该条并附编译器原始诊断。</summary>
    public sealed class UsingRejection
    {
        public readonly string Item;
        public readonly string Error;

        public UsingRejection(string item, string error)
        {
            Item = item;
            Error = error;
        }
    }

    /// <summary>
    /// AddUsings 的落盘报告。Added = 本次真正追加的条数；Accepted = 未被拒绝的条数（含已存在而被
    /// 去重跳过的候选）——/using 的 count 用它，保持"提交 N 条且都能用 → count=N"的旧口径。
    /// </summary>
    public sealed class UsingAddReport
    {
        public int Added;
        public int Accepted;
        public readonly List<UsingRejection> Rejected = new List<UsingRejection>();
    }

    /// <summary>
    /// RemoveUsings 的删除报告。Removed = 实际从 usings/directives 删除的条目；
    /// Denied = 命中 DefaultUsings 被拒绝的条目（点名）；NotFound = 两张表都没匹配上的条目。
    /// </summary>
    public sealed class UsingRemoveReport
    {
        public readonly List<string> Removed = new List<string>();
        public readonly List<string> Denied = new List<string>();
        public readonly List<string> NotFound = new List<string>();
    }

    /// <summary>两张 using 表的只读快照（/usings 指令与 GET /usings 的数据源）。</summary>
    public sealed class UsingsSnapshot
    {
        public readonly List<string> Usings = new List<string>();
        public readonly List<string> Directives = new List<string>();
        public readonly List<string> Defaults = new List<string>();
    }

    /// <summary>
    /// T8 REPL host。UE.Roslyn 门闩就绪后，用 Roslyn submission 引擎执行任意 C#。
    /// Eval 只能经 Executor 在游戏更新线程上被调（用户代码要触碰 Unity/Verse API）。
    /// </summary>
    public class ReplHost
    {
        private bool gateChecked;
        private bool gateLoaded;
        private string gateFailure;
        private int gateLoadedCount;
        private string gateDiagnostics;

        private readonly object engineLock = new object();
        private RoslynEngine engine;
        private string scriptingNote = "未装载";

        private RoslynEngine GetEngine()
        {
            lock (engineLock)
            {
                if (engine == null)
                {
                    // 两件 Scripting 程序集必须在引擎方法体首次 JIT 前就位（它们的 typeref
                    // 全部 confinement 在引擎类内，此处装载顺序即 JIT 顺序）。
                    EnsureScriptingAssemblies();
                    engine = new RoslynEngine();
                }
                return engine;
            }
        }

        /// <summary>
        /// 装载官方 Scripting 两件（核心 Microsoft.CodeAnalysis.Scripting + CSharp 层）。
        /// 优先 UE.Roslyn 载荷目录（与门闩同一份字节），本 mod Assemblies 目录兜底；
        /// 按名查重，若前置包或其它 mod 已装载则跳过。
        /// </summary>
        private void EnsureScriptingAssemblies()
        {
            LoadIfMissing("Microsoft.CodeAnalysis.Scripting");
            LoadIfMissing("Microsoft.CodeAnalysis.CSharp.Scripting");
        }

        private void LoadIfMissing(string simpleName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == simpleName)
                {
                    scriptingNote = simpleName + "=已装载(前置)";
                    return;
                }
            }

            string fileName = simpleName + ".dll";
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Mods", "Roslyn", "Roslyn", fileName),
                Path.Combine(modDir, fileName),
            };
            foreach (string p in candidates)
            {
                if (!File.Exists(p)) continue;
                Assembly.LoadFrom(p);
                scriptingNote = simpleName + "=LoadFrom(" + Path.GetDirectoryName(p) + ")";
                return;
            }
            scriptingNote = simpleName + "=NOT_FOUND";
        }

        /// <summary>
        /// 执行一段 C#。timeoutMs 仅由 HTTP 层的 Task.Wait 承担（编译本身无法安全中断）；
        /// 若游戏更新线程停摆，/eval 会以 503 超时，本方法不会被调用。
        /// entryId >= 0 时执行双日志落账（规格 §3.2）：ConsoleLog.Complete（专用日志，唯一渲染源）
        /// + Log.Message/Warning（原版缓冲，单向只写不读）。GUI 与 HTTP /eval 共用此路径；
        /// entryId 经委托闭包携带，ReplRequest 不改（规格 §3.2 的"内部字段"以闭包实现，等价）。
        /// </summary>
        public ReplResult Eval(string code, int timeoutMs, long entryId = -1)
        {
            int before = entryId >= 0 ? LogSnapshot.CaptureCount() : 0;

            ReplResult r;
            try
            {
                EnsureGate();
                if (!gateLoaded)
                    r = ReplResult.Failure("Roslyn 前置门未就绪: " + (gateFailure ?? "unknown") +
                        "\n" + (gateDiagnostics ?? "").TrimEnd());
                else
                    r = GetEngine().Eval(code);
            }
            catch (Exception ex)
            {
                // 引擎逃逸异常也必须落账：Executor.Tick 的兜底 catch 拿不到 entryId，
                // 不在这里收口该组会永远 pending。
                r = ReplResult.Failure(ex.Message);
            }

            if (entryId >= 0)
            {
                // 落账自身也必须不可抛：本方法一旦抛出，Executor.Tick 的兜底 catch 拿不到 entryId，
                // 该组会永远 pending（与上文 :112-113 是同一个不变量）。附件采集与文案计算单独包一层，
                // 任何失败都退化为"无附件 + 占位文案"，绝不阻断 Complete。
                List<string> attachments = null;
                string valueText = null, errorText = null;
                try
                {
                    attachments = LogSnapshot.CollectSince(before, LogSnapshot.CaptureCount());
                    if (r.Ok) valueText = r.Value == null ? "null" : r.Value.ToString();
                    else errorText = r.TimedOut ? "timed out" : (r.Error ?? "error");
                }
                catch (Exception ex)
                {
                    attachments = null;
                    valueText = null;
                    errorText = "landing failed: " + ex.Message;
                }
                ConsoleLog.Complete(entryId, r.Ok, valueText, errorText, attachments);

                // 原版缓冲双写：成功走 Message（白字），失败走 Warning（黄字）—— 不用 Error，
                // 控制台求值失败不给原版红海添砖（规格 §3.2）。双写失败吞掉：留痕是赠品（规格 §6）。
                try
                {
                    string oneLine = code.Replace("\r", "").Replace("\n", " ⏎ ");
                    string payload = r.Ok ? (r.Value == null ? "null" : r.Value.ToString()) : ("! " + errorText);
                    string line = "[Roslyn] " + oneLine + " => " + payload;
                    if (r.Ok) Log.Message(line);
                    else Log.Warning(line);
                }
                catch { }
            }
            return r;
        }

        /// <summary>校验并落盘一批 using（坏条目被拒，见 RoslynEngine.AddUsings），返回落盘报告。</summary>
        public UsingAddReport AddUsings(IEnumerable<string> items)
        {
            return GetEngine().AddUsings(items);
        }

        /// <summary>RemoveUsings 内核透传（T2）：两张表都能删，DefaultUsings 拒删点名，见 RoslynEngine.RemoveUsings。</summary>
        public UsingRemoveReport RemoveUsings(IEnumerable<string> items)
        {
            return GetEngine().RemoveUsings(items);
        }

        /// <summary>两张 using 表快照（T1/T4）：/usings 指令与 GET /usings 的数据源。</summary>
        public UsingsSnapshot SnapshotUsings()
        {
            return GetEngine().SnapshotUsings();
        }

        /// <summary>清空 submission 链。keepUsings=false（默认）时 usings 一并重置回 DefaultUsings，见 RoslynEngine.Reset。</summary>
        public void Reset(bool keepUsings = false)
        {
            lock (engineLock)
            {
                if (engine != null)
                    engine.Reset(keepUsings);
            }
        }

        public string GateStatus()
        {
            EnsureGate();
            return "loaded=" + gateLoaded + ";count=" + gateLoadedCount + ";failure=" + (gateFailure ?? "") +
                ";diag=" + (gateDiagnostics ?? "") +
                ";scripting=" + scriptingNote +
                ";evalThread=" + (engine != null ? engine.LastEvalCallerThreadId : 0);
        }

        public Dictionary<string, object> ProbeA()
        {
            EnsureGate();
            return RunProbeA();
        }

        private void EnsureGate()
        {
            if (gateChecked) return;
            gateChecked = true;
            try
            {
                Type loader = FindType("Roslyn.RoslynLoader");
                if (loader == null) { gateFailure = "Roslyn.RoslynLoader not found"; return; }

                var tryEnsure = loader.GetMethod("TryEnsureLoaded", new[] { typeof(string).MakeByRefType() });
                if (tryEnsure == null)
                {
                    gateFailure = "TryEnsureLoaded not found";
                }
                else
                {
                    var p = new object[] { null };
                    object ret = tryEnsure.Invoke(null, p);
                    gateLoaded = ret is bool b && b;
                    gateFailure = p[0] as string;
                }

                var isLoaded = loader.GetProperty("IsLoaded");
                if (isLoaded != null) { var v = isLoaded.GetValue(null); if (v is bool bb) gateLoaded = bb; }
                var loadedCount = loader.GetProperty("LoadedCount");
                if (loadedCount != null) { var v = loadedCount.GetValue(null); if (v is int ii) gateLoadedCount = ii; }
                var failureReason = loader.GetProperty("FailureReason");
                if (failureReason != null) { var v = failureReason.GetValue(null); gateFailure = v as string ?? gateFailure; }
                var diag = loader.GetMethod("GetDiagnostics");
                if (diag != null) { var v = diag.Invoke(null, null); gateDiagnostics = v == null ? "" : v.ToString(); }
            }
            catch (Exception ex)
            {
                gateFailure = ex.Message;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        // ---- Probe A：Assembly-CSharp typeref 解析自检 ----
        public Dictionary<string, object> RunProbeA()
        {
            var result = new Dictionary<string, object>();
            bool positive = false;
            bool negative = false;
            string details = "";

            try
            {
                // Game 层编译期就引用 Assembly-CSharp：typeof 是真实 typeref，
                // JIT 时即走 WaylithCLR 的解析链，与字段扫描的解码路径等价且更严格。
                // 注意：1.7 起 Pawn 已从 RimWorld 命名空间迁到 Verse（旧版 probeA 误报的根因）。
                Type pawn = typeof(Verse.Pawn);
                positive = true;
                details += "positive: typeof(Verse.Pawn) OK (" + pawn.Assembly.GetName().Name +
                    ", members=" + pawn.GetMembers().Length + ");";
                try
                {
                    var inst = Activator.CreateInstance(pawn, nonPublic: true);
                    details += " created=" + (inst != null) + ";";
                }
                catch (Exception cex)
                {
                    details += " createAttempt=" + cex.GetType().Name + ";";
                }
            }
            catch (Exception ex)
            {
                details += "positive: typeof(Verse.Pawn) FAILED: " + ex.GetType().Name + ": " + ex.Message + ";";
            }

            try
            {
                Type ghost = FindType("RimWorld.ThisTypeMostCertainlyDoesNotExistXYZ");
                if (ghost == null)
                {
                    negative = true;
                    details += " negative: ghost correctly unresolved;";
                }
                else
                {
                    details += " negative: ghost WRONGLY resolved!;";
                }
            }
            catch (Exception ex)
            {
                details += " negative probe exception: " + ex.Message + ";";
            }

            result["positive"] = positive;
            result["negative"] = negative;
            result["details"] = details;
            return result;
        }
    }
}