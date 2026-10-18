// RoslynProbe —— Slash 指令执行结果(纯数据,零依赖;T2.5b 从 SlashCommands.cs 拆出,
// 使 SessionLine/tests 可引用而不牵入 ReplHost 的 Verse 依赖)。

using System.Collections.Generic;

namespace RoslynProbe
{
    /// <summary>Slash 指令的执行结果:结构化行 + 可选机器可读数据,壳层各取所需(GUI → SystemNote 行;HTTP → JSON)。</summary>
    public sealed class SlashResult
    {
        /// <summary>help / usings / usings-remove / reset / quit / unknown。</summary>
        public string Kind;
        /// <summary>给人看的行(GUI 落 SystemNote,一行一条)。</summary>
        public readonly List<string> Lines = new List<string>();
        /// <summary>机器可读数据(HTTP 壳直接转 JSON;无则为 null)。</summary>
        public Dictionary<string, object> Data;
    }
}
