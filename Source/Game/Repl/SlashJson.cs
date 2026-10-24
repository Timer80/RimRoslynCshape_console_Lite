// RoslynProbe —— 壳层 `--json` 尾旗标(S11-4):`SlashResult.Data` 的机器可读出口。
//
// 背景:删掉 HTTP 后 Data 一度没有消费者(两个壳只渲染 Lines),而 /debug-action list 这类指令的
// 完整清单只在 Data 里(Lines 有 200 行上限)。裁定:壳层支持输入行里的**独立 token** `--json` ——
// 该条指令渲染 Data 的 JSON 而不是 Lines。内核零改动:本文件只包一层,SlashRegistry 与 Cmds/* 不动。
//
// 三条纪律:
//   * **识别**:按独立 token 识别 —— 空白分隔、且**不在双引号内**、逐字等于 `--json`。
//     要一个字面 `--json` 参数就用引号包住(如 `/usings add "--json"`):引号内空白不是分隔符
//     (UsingAddCommand.ParseItems 的既有分段语义),故引号包住的 token 含引号、自然不等于旗标 ——
//     **不为解析加特例**。
//   * **剥离**:旗标在分派前从行里取掉,绝不进 handler 的 rest。没有旗标时**逐字原样**把整行交给内核
//     ⇒ 不带 --json 的行为与本改动之前完全一致(逐字不变)。
//   * **渲染**:一条**单行** JSON(`Json.Stringify(result.Data)`,可被 `Json.Parse` 解析),替代 Lines;
//     Data 为 null ⇒ 字面 `null`(该指令没有机器可读数据);序列化器抛(Data 里有不可序列化的值)
//     ⇒ 单行 `{"ok":false,"error":"…"}` —— 壳层绝不因 --json 而崩。Kind / Data 本身不动。
//
// 适用通道:GUI(行仍带既有的 "! " 前缀)、命名管道 v2。
// HTTP(Commands.cs)的 slash 响应体本就已把 Data 的键平铺进 JSON,故它不走本文件(该文件随 T5 删除)。
// 用法与 Data 作者纪律见 docs/slash-command-authoring.md §3.6。

using System;
using System.Collections.Generic;
using RoslynProbe.Repl;
using JsonNs = RoslynProbe.Json.Json;

namespace RoslynProbe
{
    /// <summary>
    /// `--json` 尾旗标的唯一实现(壳层):识别/剥离 + 把 <see cref="SlashResult.Data"/> 渲染成单行 JSON。
    /// 两个壳(GUI / 命名管道 v2)都经 <see cref="TryDispatch"/> 派发,故旗标行为两通道同源。
    /// </summary>
    public static class SlashJson
    {
        /// <summary>旗标字面量(独立 token;大小写敏感,比较用序数)。</summary>
        public const string Flag = "--json";

        /// <summary>
        /// 识别并剥离行里的全部独立 token <see cref="Flag"/>。
        /// 返回 false ⇒ <paramref name="stripped"/> **与 <paramref name="input"/> 逐字相同**(调用方原样分派);
        /// 返回 true ⇒ 剩余 token 以单个空格重连(仅带旗标的这条新路径会规范化空白)。
        /// 引号段整体作为一个 token 参与比较(`"--json"` 不等于 `--json`),采样与 ParseItems 同款最小转义。
        /// </summary>
        public static bool TryStrip(string input, out string stripped)
        {
            stripped = input;
            if (string.IsNullOrEmpty(input)) return false;

            var kept = new List<string>();
            bool seenFlag = false;

            int i = 0;
            while (i < input.Length)
            {
                while (i < input.Length && char.IsWhiteSpace(input[i])) i++;   // 跳过分隔空白
                if (i >= input.Length) break;

                int start = i;
                if (input[i] == '"')
                {
                    // 引号段:与 UsingAddCommand.ParseItems 同为最小转义(引号内空白不是分隔符)。
                    // 整段(**含两侧引号**)作为一个 token ⇒ '"--json"' 与 "--json" 不相等 ⇒ 不当旗标。
                    i++;
                    while (i < input.Length)
                    {
                        char ch = input[i];
                        if (ch == '\\' && i + 1 < input.Length && (input[i + 1] == '"' || input[i + 1] == '\\'))
                        {
                            i += 2;                                        // \" 或 \\:整对跳过
                            continue;
                        }
                        i++;
                        if (ch == '"') break;                              // 收尾引号
                    }
                }
                else
                {
                    while (i < input.Length && !char.IsWhiteSpace(input[i])) i++;
                }

                string token = input.Substring(start, i - start);
                if (string.Equals(token, Flag, StringComparison.Ordinal))
                {
                    seenFlag = true;                                       // 吃掉旗标:不重连、不进 rest
                    continue;
                }
                kept.Add(token);
            }

            if (!seenFlag) return false;
            stripped = string.Join(" ", kept);
            return true;
        }

        /// <summary>
        /// <see cref="SlashResult.Data"/> 的单行 JSON 文本。Data 为 null ⇒ `null`;序列化失败 ⇒
        /// 单行 `{"ok":false,"error":"--json 序列化失败: …"}`(仍是合法 JSON,壳层/agent 都不会拿到半截文本)。
        /// </summary>
        public static string JsonLine(SlashResult result)
        {
            try
            {
                return JsonNs.Stringify(result == null ? null : result.Data);
            }
            catch (Exception ex)
            {
                return JsonNs.Stringify(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "--json 序列化失败: " + ex.GetType().Name + ": " + ex.Message
                });
            }
        }

        /// <summary>
        /// 把结果换成 --json 渲染:Lines 置为**唯一一行** JSON 并清掉原有人类行;Kind 与 Data 原样不动
        /// (agent 拿到的就是这条指令的 Data)。null 原样返回。
        /// </summary>
        public static SlashResult RenderJson(SlashResult result)
        {
            if (result == null) return null;
            string json = JsonLine(result);
            result.Lines.Clear();
            result.Lines.Add(json);
            return result;
        }

        /// <summary>
        /// 壳层统一派发入口:剥离旗标 → <see cref="SlashCommands.TryDispatch"/> → 带旗标则渲染 Data 的 JSON。
        /// 返回 false 的语义与 <see cref="SlashCommands.TryDispatch"/> 完全一致(不是指令)。
        /// 无旗标时被派发的字符串与 <paramref name="input"/> 逐字相同 ⇒ 既有行为不变。
        /// 调用方仍在自己的 IsCommand 分支里调用本方法(判定不看旗标)。
        /// </summary>
        public static bool TryDispatch(string input, ReplHost host, Executor executor, string channel, bool isGameThread, out SlashResult result)
        {
            bool json = TryStrip(input, out string dispatchInput);
            if (!SlashCommands.TryDispatch(dispatchInput, host, executor, out result, channel, isGameThread))
                return false;
            if (json) RenderJson(result);
            return true;
        }
    }
}
