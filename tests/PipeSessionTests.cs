// RoslynProbe —— 会话分派单测(tests 工程单文件引入)。
//
// 覆盖 SessionLine.Handle 的一进一出判定:空行/Slash 派发/整行提交/队列上限/未接线,
// 以及 Echo 常量与协议文本。ReplHost 依赖 Verse 不可编入 tests,故求值经 Evaluator
// 委托注入假实现;真机行为(管道 IO/状态延续)由真机冒烟覆盖。Executor 是 Core 纯 C#
// 可直接实例化,覆盖求值与超时路径。

using System;
using Xunit;
using RoslynProbe;
using RoslynProbe.Repl;

namespace RoslynProbe.Tests
{
    public class PipeSessionTests
    {
        private static SessionAction Handle(string line, Executor executor,
            SessionLine.Evaluator eval = null, SessionLine.SlashDispatcher slash = null)
        {
            // 默认 slash 钩子返回 null(派发失败路径);各 Slash 用例自带钩子。
            // SlashCommands/ReplHost 依赖 Verse 不可编入 tests,IsCommand 判定注入本地同款实现。
            // not-wired 守卫的等价性依据(单侧 null 不可达)见 SessionLine.Handle 注释。
            return SessionLine.Handle(line, executor, eval,
                slash ?? (input => null),
                input => (input ?? "").TrimStart().Length > 0 && input.TrimStart()[0] == '/');
        }

        [Fact]
        public void EmptyLine_IsConsumed_NoOutput()
        {
            var a = Handle("", new Executor());
            Assert.Equal("consume", a.Kind);
        }

        [Fact]
        public void BareLine_IsSubmittedVerbatim_NoAccumulation()
        {
            // 一进一出:一行就是一次完整提交,首尾空白剥掉后原样交给求值器。
            var exec = new Executor();
            string seen = null;
            var a = Handle("   1 + 1   ", exec, code => { seen = code; return ReplResult.Success(2); });
            Assert.Equal("result", a.Kind);
            Assert.Equal("1 + 1", seen);
            Assert.Equal("2", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void MultiLineInput_IsOneChunk_NotResumed()
        {
            // 输入里自带的换行不再触发续行:整段一次性提交。
            var exec = new Executor();
            string seen = null;
            var a = Handle("int a = 1;\nint b = 2;\na + b", exec, code => { seen = code; return ReplResult.Success(3); });
            Assert.Equal("result", a.Kind);
            Assert.Equal("int a = 1;\nint b = 2;\na + b", seen);
            Assert.Equal("3", a.Lines[0]);
        }

        [Fact]
        public void CompleteSubmission_RunsEvaluator_EmitsNullLiteral()
        {
            var exec = new Executor();
            var a = Handle("1 + 1", exec, code => ReplResult.Success(null));
            Assert.Equal("result", a.Kind);
            Assert.Equal("null", a.Lines[0]);   // null 照打
            Assert.Equal(">", a.Lines[1]);
            Assert.NotNull(a.EvalResult);
        }

        [Fact]
        public void EvalError_EmitsRawErrorText()
        {
            var exec = new Executor();
            var a = Handle("boom()", exec, code => ReplResult.Failure("CS0103: boom"));
            Assert.Equal("result", a.Kind);
            Assert.Equal("CS0103: boom", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void AutoUsings_EmitsTrailingNoteLines()
        {
            var exec = new Executor();
            var a = Handle("Math.PI", exec, code =>
                new ReplResult { Ok = true, Value = 3.14, AutoUsings = new System.Collections.Generic.List<string> { "System" } });
            Assert.Equal("3.14", a.Lines[0]);
            Assert.Equal("└ 自动补 using System;(仅本次)", a.Lines[1]);
            Assert.Equal(">", a.Lines[2]);
        }

        [Fact]
        public void SlashQuit_EmitsLinesPlusBye()
        {
            var exec = new Executor();
            var a = Handle("/quit", exec,
                slash: input => new SlashResult { Kind = "quit" });
            Assert.Equal("quit", a.Kind);
            Assert.Equal("> bye", a.Lines[a.Lines.Length - 1]);
        }

        [Fact]
        public void SlashHelp_EmitsHelpLinesPlusPrompt()
        {
            var exec = new Executor();
            var a = Handle("/help", exec,
                slash: input => { var s = new SlashResult { Kind = "help" }; s.Lines.Add("可用指令:"); return s; });
            Assert.Equal("lines", a.Kind);
            Assert.Equal("可用指令:", a.Lines[0]);
            Assert.Equal(">", a.Lines[a.Lines.Length - 1]);
        }

        [Fact]
        public void SlashDispatchFailure_EmitsFailureAndPrompt()
        {
            var exec = new Executor();
            var a = Handle("/x", exec, code => ReplResult.Success(null), slash: input => null);
            Assert.Equal("lines", a.Kind);
            Assert.Equal("slash dispatch failed", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void UnwiredHost_EmitsConsoleNotWired()
        {
            var a = SessionLine.Handle("1+1", null,
                code => ReplResult.Success(null), input => null,
                input2 => (input2 ?? "").TrimStart().Length > 0 && input2.TrimStart()[0] == '/');
            Assert.Equal("lines", a.Kind);
            Assert.Equal("console not wired", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void QueueFull_EmitsQueueFull_WhenPendingCountExceedsMax()
        {
            var exec = new Executor();
            // 填队列:PendingCount > MaxQueue(32)。注意 Executor.Enqueue 非泛型,直接灌 33 个。
            for (int i = 0; i < SessionLine.MaxQueue + 1; i++)
                exec.Enqueue(() => ReplResult.Success(null), 1000);
            var a = Handle("1+1", exec, code => ReplResult.Success(null));
            Assert.Equal("lines", a.Kind);
            Assert.Equal("queue full", a.Lines[0]);
            Assert.Equal(">", a.Lines[1]);
        }

        [Fact]
        public void TrimmedLine_IsEvaluated_WhitespaceStripped()
        {
            // 壳层读到的原始行首尾空白被剥除后进入判定器;协议不回显输入。
            var exec = new Executor();
            string seen = null;
            string raw = "   1 + 1   ";
            var a = Handle(raw.Trim(), exec, code => { seen = code; return ReplResult.Success(2); });
            Assert.Equal("1 + 1", seen);
            Assert.Equal("2", a.Lines[0]);
        }
    }
}
