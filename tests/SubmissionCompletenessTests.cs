using RoslynProbe.Repl;
using Xunit;

public class SubmissionCompletenessTests
{
    [Theory]
    [InlineData("1+1", true)]
    [InlineData("int x = 5;", true)]
    [InlineData("var a = new { A = 1, B = 2 };", true)]
    public void SingleLine_Complete(string code, bool expected)
        => Assert.Equal(expected, SubmissionCompleteness.IsComplete(code));

    [Theory]
    [InlineData("if (x) {")]                    // 块未闭合
    [InlineData("string s = \"abc")]            // 字符串未闭合
    [InlineData("for (int i = 0;")]             // 括号未闭合
    [InlineData("Log.Message(\"a\nb")]         // 字面换行落在未闭合字符串里
    public void Incomplete_Submissions(string code)
        => Assert.False(SubmissionCompleteness.IsComplete(code));

    [Fact]
    public void TwoStatements_Complete()
        => Assert.True(SubmissionCompleteness.IsComplete("int a = 1;\nint b = 2;\na+b"));

    [Fact]
    public void RealSyntaxError_Complete_SubmitsForEval()   // 不困死用户
        => Assert.True(SubmissionCompleteness.IsComplete("int x = ;"));

    [Theory]                                     // 审修 Important-1:未闭合插值须续行,
    [InlineData("$\"{x")]                       // CS8076 是非空实锚 span,几何与 CS1525 同为 End==EOF,
    [InlineData("$\"abc{x")]                    // 只能靠 AST 缺 InterpolatedStringEndToken 区分。
    [InlineData("var s = $\"{ 1 +")]            // 插值内多行续写
    [InlineData("$\"{ x = ")]                   // 插值内表达式未写完
    [InlineData("$\"{ new int[3] { 1, 2")]       // 插值内嵌套集合初始化
    public void Incomplete_Interpolation_ContinuesLine(string code)
        => Assert.False(SubmissionCompleteness.IsComplete(code));

    [Theory]                                     // 完整插值(有闭合引号)不得误伤
    [InlineData("$\"complete{ names } test\"")]    // 全闭合,零诊断
    [InlineData("$\"ok{ 1 } done\"")]             // 干净闭合,零诊断(裸表达式无需分号)
    [InlineData("$\"{1}+{2}\"")]                   // 多段插值全闭合
    public void Complete_Interpolation_Submits(string code)
        => Assert.True(SubmissionCompleteness.IsComplete(code));

    [Fact]                                       // CS1039 未闭合引号(插值文本带 raw 引号也没闭合)→ 续行
    public void Unterminated_InterpolatedText_ContinuesLine()
        => Assert.False(SubmissionCompleteness.IsComplete("$\"complete{ names } test"));

    [Fact]                                       // 未闭合插值 + 末尾换行,EOF 容差不破坏 b) 规则
    public void Incomplete_Interpolation_WithTrailingNewline()
        => Assert.False(SubmissionCompleteness.IsComplete("$\"{x\n"));
}
