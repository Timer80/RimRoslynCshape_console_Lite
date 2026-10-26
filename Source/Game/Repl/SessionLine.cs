// RoslynProbe —— 会话行分派内核(协议 v2)。
//
// 把"一行如何分派"提为纯函数 + 可注入求值器;命名管道 v2(PipeConsole)与
// 测试共用同一份协议文本与判定顺序:
//   空行 → 忽略 | Slash → 派发(共享内核) | 其他 → 整行即一次完整提交 →
//   Enqueue 求值(TCS 等待/超时/MarkTimedOut) → 裸结果行 → ">"。
//
// 一进一出:管道对端是 agent,按轮次"写一行、读一段",每行都是完整提交;
// 本类不累积、不判续行、不回 "...",也没有跨行状态。
// 本类零 IO 零 Verse 依赖,协议判定路径可在 tests 单测。求值与 Slash 的
// 副作用(ConsoleLog 落账)经委托注入;quit 由 SlashResult.Kind=="quit" 识别。

using System;
using System.Collections.Generic;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>分派动作 + 应输出到客户端的协议行。</summary>
    public sealed class SessionAction
    {
        /// <summary>consume:空行忽略;lines:逐行输出 Lines
        /// (Slash 完成路径,末行 ">");quit:逐行输出 Lines(含 "> bye"),调用方入队
        /// Root.Shutdown 并结束会话;result:逐行输出结果行(末行 ">")。</summary>
        public string Kind;

        /// <summary>应写出的协议行(不含尾随换行);无则 null。</summary>
        public string[] Lines;

        /// <summary>result 动作携带的求值结果;其他动作为 null。</summary>
        public ReplResult EvalResult;

        public static SessionAction Consume = new SessionAction { Kind = "consume" };
        public static SessionAction OfLines(string kind, string[] lines) { return new SessionAction { Kind = kind, Lines = lines }; }
        public static SessionAction Result(ReplResult r, string[] lines) { return new SessionAction { Kind = "result", Lines = lines, EvalResult = r }; }
    }

    /// <summary>
    /// 会话行分派内核。Handle 返回本行应执行的输出动作,绝不直接做 IO。
    /// 每行独立成立:一进一出,无跨行累积状态。
    /// Slash 分支的 ConsoleLog 落账由调用方在 slashDispatch 里完成,quit 的
    /// Root.Shutdown 由调用方在 quit 动作处理里入队,保持本类零 Verse typeref。
    /// </summary>
    public static class SessionLine
    {
        public const int DefaultTimeoutMs = 15000;
        public const int MaxQueue = 32;

        /// <summary>一次完整求值:壳层闭包语义(Begin 落账 + host.Eval)。</summary>
        public delegate ReplResult Evaluator(string code);

        /// <summary>Slash 派发钩子:实现里做 ConsoleLog SystemNote 落账,返回派发结果;
        /// 返回 null = 派发失败(调用方输出 "slash dispatch failed")。</summary>
        public delegate SlashResult SlashDispatcher(string input);

        /// <summary>是否 Slash 指令的判定钩子(真实现 = SlashCommands.IsCommand;tests 注入免 Verse 依赖)。</summary>
        public delegate bool IsCommandPredicate(string input);

        /// <summary>对本行执行一次分派。总是返回一个动作(Kind 必有)。
        /// "not wired" 守卫口径:与 v1 的 executor==null||host==null 等价——v1 判两参,
        /// 本类不持 ReplHost 形参(避免牵入 Verse 依赖使 tests 不可编),两参数在
        /// Bootstrap 同源(同一 new ReplHost()),单侧 null 实际不可达;故 executor
        /// null 守卫即完整覆盖 not-wired 路径,依据见本注释。</summary>
        public static SessionAction Handle(
            string line,
            Executor executor,
            Evaluator eval,
            SlashDispatcher slashDispatch,
            IsCommandPredicate isCommand)
        {
            string trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0) return SessionAction.Consume;

            // Slash 指令:共享内核,不进求值队列
            if (isCommand(trimmed))
            {
                SlashResult slash = slashDispatch(trimmed);
                if (slash == null)
                    return SessionAction.OfLines("lines", new[] { "slash dispatch failed", ">" });

                var lines = new List<string>(slash.Lines);
                if (slash.Kind == "quit")
                {
                    lines.Add("> bye");
                    return SessionAction.OfLines("quit", lines.ToArray());
                }
                lines.Add(">");
                return SessionAction.OfLines("lines", lines.ToArray());
            }

            if (executor == null)
                return SessionAction.OfLines("lines", new[] { "console not wired", ">" });

            // 一进一出:整行就是一次完整提交,原样交给求值器。
            string code = trimmed;

            if (executor.PendingCount > MaxQueue)
                return SessionAction.OfLines("lines", new[] { "queue full", ">" });

            ReplResult r = eval(code);

            var outLines = new List<string>();
            // 超时文案以 Error 为准：SessionEval 会区分「未执行」与「可能仍在执行」两种语义，
            // 泛化掉就又把最关键的信息（到底跑没跑）丢了。只有上游没给明细时才回退到泛化文案。
            if (r.TimedOut) outLines.Add(string.IsNullOrEmpty(r.Error) ? "eval timed out" : r.Error);
            else if (r.Ok) outLines.Add(r.Value == null ? "null" : r.Value.ToString());
            else outLines.Add(r.Error);

            if (r.AutoUsings != null)
                foreach (string u in r.AutoUsings)
                    outLines.Add("└ 自动补 using " + u + ";(仅本次)");

            outLines.Add(">");
            return SessionAction.Result(r, outLines.ToArray());
        }
    }
}
