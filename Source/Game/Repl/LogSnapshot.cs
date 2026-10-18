// RoslynProbe —— 原版日志快照差集。
//
// 求值前后各取一次 Log.Messages.Count，区间 [before, after) 即"本次求值产生的消息"，
// 挂到该结果组上显示在详情栏（规格 §3.2/§4.1，决策 8 的附件路径）。
// 已知误差：原版把新消息合并进旧条目（LogMessage.repeats 自增）时不产生新索引 → 漏计，
// 可接受（规格 §6）；UI 措辞"本次求值产生的消息"，不做穷举承诺。
// 线程：ReplHost.Eval 只在游戏更新线程被调；读取用 Log.LockMessages() 与原版自身同款保护。

using System.Collections.Generic;
using Verse;

namespace RoslynProbe
{
    public static class LogSnapshot
    {
        public static int CaptureCount()
        {
            int count = 0;
            using (Log.LockMessages())
            {
                foreach (LogMessage m in Log.Messages)
                    count++;
            }
            return count;
        }

        public static List<string> CollectSince(int before, int after)
        {
            var list = new List<string>();
            if (after <= before) return list;
            using (Log.LockMessages())
            {
                int i = 0;
                foreach (LogMessage m in Log.Messages)
                {
                    if (i >= before && i < after) list.Add(m.text);
                    i++;
                }
            }
            return list;
        }
    }
}
