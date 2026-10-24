// RoslynProbe —— T2.5b 会话分派单测(tests 工程单文件引入)。
//
// 覆盖 SessionLine.Handle 的协议判定路径:空行/Slash 派发/方案 D 累积/完整提交/
// 队列上限/未接线,以及 Echo 常量与协议文本。ReplHost 依赖 Verse 不可编入
// tests,故求值经 Evaluator 委托注入假实现;真机行为(管道 IO/状态延续)由
// 真机冒烟覆盖。Executor 是 Core 纯 C# 可直接实例化,覆盖求值与超时路径。

using System;
using System.Text;
using System.Threading;
using Xunit;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    public class PipeSessionTests
    {
        private static SessionAction Handle(string line, StringBuilder pending, Executor executor,
            SessionLine.Evaluator eval = null, SessionLine.SlashDispatcher slash = null)
        {
            // 默认 slash 钩子返回 null(派发失败路径);各 Slash 用例自带钩子。
            // SlashCommands/ReplHost 依赖 Verse 不可编入 tests,IsCommand 判定注入本地同款实现。
            // not-wired 守卫的等价性依据(单侧 null 不可达)见 SessionLine.Handle 注释。
            return SessionLine.Handle(line, pending, executor, eval,
                slash ?? (input => null),
                input => (input ?? "").TrimStart().Length > 0 && input.TrimStart()[0] == '/');
        }

        [Fact]
        public void EmptyLine_IsConsumed_NoOutput()
        {
            var pending = new StringBuilder();
            var a = Handle("", pending, new Executor());
            Assert.Equal("consume", a.Kind);
        }

        [Fact]
        public void IncompleteSubmission_EmitsEllipsis_AndAccumulates()
        {
            var pending = new StringBuilder();
            var exec = new Executor();
            var a1 = Handle("if (1 > 0) {", pending, exec, code => { throw new InvalidOperationException("must not eval"); });
            Assert.Equal("more", a1.Kind);
            Assert.Equal("...", a1.Lines[0]);
            var a2 = Handle("}", pending, exec, code => ReplResult.Success(42));
            Assert.Equal("result", a2.Kind);
            Assert.Equal("42", a2.Lines[0]);
            Assert.Equal(">", a2.Lines[1]);
            Assert.Equal(0, pending.Length);   // 完整提交后累积清空
        }

        [Fact]
        public void CompleteSubmission_RunsEvaluator_EmitsNullLiteral()
        {
            var pending = new StringBuilder();
            var exec = new Executor();
            string seen = null;
            var a = Handle("1 + 1", pending, exec, code => { seen = code; return ReplResult.Success(null); });
            Assert.Equal("result", a.Kind);
            Assert.Equal("1 + 1" + Environment.NewLine, seen);   // 方案 D 累积含换行
            Assert.Equal("null", a.Lines[0]);                     // null 照打
            Assert.Equal(">", a.Lines[1]);
            Assert.NotNull(a.EvalResult);
        }

        [Fact]
        public void EvalError_EmitsRawErrorText()
        {
            var pending = new StringBuilder();
            var exec = new Executor();
            var a = Handle("boom()", pending, exec, code => ReplResult.Failure("CS0103: boom"));
            Assert.Equal("result", a.Kind);
            Assert.Equal("CS0103: boom", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void AutoUsings_EmitsTrailingNoteLines()
        {
            var pending = new StringBuilder();
            var exec = new Executor();
            var a = Handle("Math.PI", pending, exec, code =>
                new ReplResult { Ok = true, Value = 3.14, AutoUsings = new System.Collections.Generic.List<string> { "System" } });
            Assert.Equal("3.14", a.Lines[0]);
            Assert.Equal("└ 自动补 using System;(仅本次)", a.Lines[1]);
            Assert.Equal(">", a.Lines[2]);
        }

        [Fact]
        public void SlashQuit_EmitsLinesPlusBye_AndClearsPending()
        {
            var pending = new StringBuilder();
            pending.Append("garbage");
            var exec = new Executor();
            var a = Handle("/quit", pending, exec,
                slash: input => new SlashResult { Kind = "quit" });
            Assert.Equal("quit", a.Kind);
            Assert.Equal("> bye", a.Lines[a.Lines.Length - 1]);
            Assert.Equal(0, pending.Length);   // Slash 清累积
        }

        [Fact]
        public void SlashHelp_EmitsHelpLinesPlusPrompt()
        {
            var pending = new StringBuilder();
            var exec = new Executor();
            var a = Handle("/help", pending, exec,
                slash: input => { var s = new SlashResult { Kind = "help" }; s.Lines.Add("可用指令:"); return s; });
            Assert.Equal("lines", a.Kind);
            Assert.Equal("可用指令:", a.Lines[0]);
            Assert.Equal(">", a.Lines[a.Lines.Length - 1]);
        }

        [Fact]
        public void SlashDispatchFailure_EmitsFailureAndPrompt()
        {
            var pending = new StringBuilder();
            var exec = new Executor();
            var a = Handle("/x", pending, exec, code => ReplResult.Success(null), slash: input => null);
            Assert.Equal("lines", a.Kind);
            Assert.Equal("slash dispatch failed", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void UnwiredHost_EmitsConsoleNotWired()
        {
            var pending = new StringBuilder();
            var a = SessionLine.Handle("1+1", pending, null,
                code => ReplResult.Success(null), input => null,
                input2 => (input2 ?? "").TrimStart().Length > 0 && input2.TrimStart()[0] == '/');
            Assert.Equal("lines", a.Kind);
            Assert.Equal("console not wired", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void QueueFull_EmitsQueueFull_WhenPendingCountExceedsMax()
        {
            var pending = new StringBuilder();
            var exec = new Executor();
            // 填队列:PendingCount > MaxQueue(32)。注意 Executor.Enqueue 非泛型,直接灌 33 个。
            for (int i = 0; i < SessionLine.MaxQueue + 1; i++)
                exec.Enqueue(() => ReplResult.Success(null), 1000);
            var a = Handle("1+1", pending, exec, code => ReplResult.Success(null));
            Assert.Equal("lines", a.Kind);
            Assert.Equal("queue full", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void EchoContract_TrimmedLineMatchesSessionInput()
        {
            // 回显契约(真实路径模拟):壳层读到原始行 → 回显 "> " + trimmed → Handle(trimmed 行)。
            // 断言 trimmed 口径下回显文本与进入判定器的文本一致,且首尾空白不影响求值结果。
            var pending = new StringBuilder();
            var exec = new Executor();
            string seen = null;
            string raw = "   1 + 1   ";
            string trimmed = raw.Trim();
            string echoText = "> " + trimmed;                    // PipeConsole.ServeSession 同款
            var a = Handle(trimmed, pending, exec, code => { seen = code; return ReplResult.Success(2); });
            Assert.Equal("> " + "1 + 1", echoText);              // 回显 = trimmed 形式
            Assert.Equal("1 + 1" + Environment.NewLine, seen);   // 判定器收到 trimmed 行,空白被剥除
            Assert.Equal("2", a.Lines[0]);
        }
    }
}