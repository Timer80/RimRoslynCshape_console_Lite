// RoslynProbe —— 方案 D 提交完整性判定(规格 2026-10-15 §3,审修版)。
// 纯解析,零副作用,零 Verse/Unity 依赖,无状态(线程安全)。
// 规则:不完整 ⇔ 解析器在 EOF 处缺东西,续行有意义 ——
//   a) 存在空 span 且钉在文本末尾(容忍末尾 LF/CRLF)的错误诊断(缺 token/收尾,如 if (x) { 的 CS1513);或
//   b) 存在未闭合的插值字符串(缺 InterpolatedStringEndToken,如 $"{x 的 CS8076,span 是非空实锚,
//      几何上与 int x = ; 的 CS1525 同为 End==EOF,只能靠 AST 缺 token 区分)。
// 其余错误(全部为实锚 token,且无未闭合插值)= 真语法错误 → 判完整,交求值报真实诊断,不困死用户。
// 解析用 Script kind —— 与 RoslynEngine 的 CSharpScript 求值同款语法面。

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynProbe.Repl
{
    public static class SubmissionCompleteness
    {
        private static readonly CSharpParseOptions Options = new CSharpParseOptions(
            languageVersion: LanguageVersion.Latest,
            documentationMode: DocumentationMode.None,
            kind: SourceCodeKind.Script);

        public static bool IsComplete(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, Options);
            var errors = tree.GetDiagnostics()
                             .Where(d => d.Severity == DiagnosticSeverity.Error)
                             .ToList();
            if (errors.Count == 0) return true;
            int eof = text.Length;
            if (text.EndsWith("\r\n")) eof -= 2;
            else if (text.EndsWith("\n") || text.EndsWith("\r")) eof -= 1;
            // 未闭合插值(缺 InterpolatedStringEndToken):CS8076 是钉在 { 后的非空实锚 span,
            // 几何上与 int x = ; 的 CS1525(End==EOF)不可区分,须靠 AST 缺 token 判定 → 续行。
            bool unterminatedInterp = tree.GetRoot()
                .DescendantNodes()
                .OfType<InterpolatedStringExpressionSyntax>()
                .Any(n => n.StringEndToken.IsMissing);
            if (unterminatedInterp) return false;
            return !errors.Any(d =>
            {
                var span = d.Location.SourceSpan;
                // a) 空 span 钉在 EOF(缺 token/收尾)→ 续行。
                return span.Length == 0 && span.End >= eof;
            });
        }
    }
}
