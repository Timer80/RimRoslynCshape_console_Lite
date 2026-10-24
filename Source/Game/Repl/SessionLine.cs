// RoslynProbe —— 会话行分派内核(协议 v2)。
//
// 把"一行如何分派"提为纯函数 + 可注入求值器;命名管道 v2(PipeConsole)与
// 壳层共用同一份协议文本与判定顺序:
//   空行 → 忽略 | Slash → 派发(共享内核) | 方案 D 累积判定 → "..." |
//   完整提交 → Enqueue 求值(TCS 等待/超时/MarkTimedOut) → 裸结果行 → ">"。
//
// 累积状态(pending StringBuilder)由调用方持有(与会话/进程生命周期绑定);
// 本类零 IO 零 Verse 依赖,协议判定路径可在 tests 单测。求值与 Slash 的
// 副作用(ConsoleLog 落账)经委托注入;quit 由 SlashResult.Kind=="quit" 识别。

using System;
using System.Collections.Generic;
using System.Text;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    /// <summary>分派动作 + 应输出到客户端的协议行。</summary>
    public sealed class SessionAction
    {
        /// <summary>consume:空行忽略;more:回 "..." 续行;lines:逐行输出 Lines
        /// (Slash 完成路径,末行 ">");quit:逐行输出 Lines(含 "> bye"),调用方入队
        /// Root.Shutdown 并结束会话;result:逐行输出结果行(末行 ">")。</summary>
        public string Kind;

        /// <summary>应写出的协议行(不含尾随换行);无则 null。</summary>
        public string[] Lines;

        /// <summary>result 动作携带的求值结果;其他动作为 null。</summary>
        public ReplResult EvalResult;

        public static SessionAction Consume = new SessionAction { Kind = "consume" };
        public static SessionAction More = new SessionAction { Kind = "more", Lines = new[] { "..." } };
        public static SessionAction OfLines(string kind, string[] lines) { return new SessionAction { Kind = kind, Lines = lines }; }
        public static SessionAction Result(ReplResult r, string[] lines) { return new SessionAction { Kind = "result", Lines = lines, EvalResult = r }; }
    }

    /// <summary>
    /// 会话行分派内核。Handle 返回本行应执行的输出动作,绝不直接做 IO;
    /// pending 状态由调用方传入的 StringBuilder 承载(累积跨调用延续)。
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

        /// <summary>对累积 + 行执行一次分派。总是返回一个动作(Kind 必有)。
        /// "not wired" 守卫口径:与 v1 的 executor==null||host==null 等价——v1 判两参,
        /// 本类不持 ReplHost 形参(避免牵入 Verse 依赖使 tests 不可编),两参数在
        /// Bootstrap 同源(同一 new ReplHost()),单侧 null 实际不可达;故 executor
        /// null 守卫即完整覆盖 not-wired 路径,依据见本注释。</summary>
        public static SessionAction Handle(
            string line,
            StringBuilder pending,
            Executor executor,
            Evaluator eval,
            SlashDispatcher slashDispatch,
            IsCommandPredicate isCommand)
        {
            string trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0) return SessionAction.Consume;

            // Slash 指令:共享内核,清累积,不进求值队列
            if (isCommand(trimmed))
            {
                pending.Clear();
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

            // 方案 D:累积 + 纯解析判定
            pending.AppendLine(trimmed);
            string candidate = pending.ToString();
            if (!SubmissionCompleteness.IsComplete(candidate))
                return SessionAction.More;
            string code = candidate;
            pending.Clear();

            if (executor.PendingCount > MaxQueue)
                return SessionAction.OfLines("lines", new[] { "queue full", ">" });

            ReplResult r = eval(code);

            var outLines = new List<string>();
            if (r.TimedOut) outLines.Add("eval timed out");
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