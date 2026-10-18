// RoslynProbe —— /gc:GC 堆占用与分代(纯 BCL,不需要姿势 B)。
//
// 数据源全是只读 BCL 标量:GC.GetGCMemoryInfo()(堆占用 / 总量上限 / 已提交 / 碎片 /
// 内存负载 / 暂停时间占比)、GC.GetTotalMemory(false)、GC.CollectionCount(0/1/2)、
// GC.GetTotalAllocatedBytes(false)、GCSettings、Process.WorkingSet64。
// 只读 ⇒ GUI(游戏线程)/ stdio / 命名管道(读线程)三个壳都能直接跑,不需要 Executor marshal;
// 本文件绝不碰 ConsoleLog / Unity / Verse(线程契约见 docs/slash-command-authoring.md §3.4.1)。
//
// Data 键(小驼峰、稳定,工具消费方依赖):heapBytes,totalMemoryBytes,gen0,gen1,gen2,
// allocatedBytes,workingSetBytes —— 字节数一律 long,分代数一律 int;
// ok 是全局成功位,与 SlashResults.Error 的 ok=false 对称。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace RoslynProbe
{
    /// <summary>/gc —— GC 堆占用与分代(纯 BCL;无参数)。</summary>
    public static class GcCommand
    {
        /// <summary>RegisterAll 反射调用:public static void Register(),无参、返回 void。</summary>
        public static void Register()
        {
            SlashRegistry.Register(new SlashEntry
            {
                Verb = "gc",
                Usage = "—— GC 堆占用与分代",
                Group = "运行时",
                Kind = "gc",
                Handler = Provide
            });
        }

        /// <summary>指令实现(契约 v1.1 §3.5:纯 BCL 指令的 handler 也叫 Provide)。</summary>
        public static SlashResult Provide(string rest, SlashContext ctx)
        {
            string args = (rest ?? "").Trim();
            if (args.Length > 0 && IsHelp(args)) return Usage(null);
            if (args.Length > 0) return Usage("不接受参数: \"" + args + "\"(/gc 无参数)");

            try
            {
                GCMemoryInfo info = GC.GetGCMemoryInfo();
                long heapBytes = info.HeapSizeBytes;
                long totalMemoryBytes = info.TotalAvailableMemoryBytes;
                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);
                long allocatedBytes = GC.GetTotalAllocatedBytes(false);
                long managedBytes = GC.GetTotalMemory(false);
                long workingSetBytes = ReadWorkingSetBytes();

                var r = new SlashResult { Kind = "gc" };
                r.Data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["heapBytes"] = heapBytes,
                    ["totalMemoryBytes"] = totalMemoryBytes,
                    ["gen0"] = gen0,
                    ["gen1"] = gen1,
                    ["gen2"] = gen2,
                    ["allocatedBytes"] = allocatedBytes,
                    ["workingSetBytes"] = workingSetBytes
                };

                r.Lines.Add("GC 堆占用(HeapSizeBytes): " + Bytes(heapBytes)
                    + (heapBytes == 0 ? " —— 0 = 尚未发生 GC 或堆为空(还没有分配段)" : ""));
                r.Lines.Add("当前托管占用(GC.GetTotalMemory(false)): " + Bytes(managedBytes));
                r.Lines.Add("总量上限(TotalAvailableMemoryBytes): " + Bytes(totalMemoryBytes));
                r.Lines.Add("已提交(TotalCommittedBytes): " + Bytes(info.TotalCommittedBytes)
                    + ";碎片(FragmentedBytes): " + Bytes(info.FragmentedBytes)
                    + ";内存负载(MemoryLoadBytes): " + Bytes(info.MemoryLoadBytes));
                r.Lines.Add("暂停时间占比(PauseTimePercentage): " + Num(info.PauseTimePercentage)
                    + "%;最近 GC: index=" + info.Index.ToString(CultureInfo.InvariantCulture)
                    + " generation=" + info.Generation.ToString(CultureInfo.InvariantCulture));
                r.Lines.Add("分代回收次数: gen0=" + gen0.ToString(CultureInfo.InvariantCulture)
                    + " gen1=" + gen1.ToString(CultureInfo.InvariantCulture)
                    + " gen2=" + gen2.ToString(CultureInfo.InvariantCulture));
                r.Lines.Add("累计分配(GC.GetTotalAllocatedBytes(false)): " + Bytes(allocatedBytes));
                r.Lines.Add("进程工作集(Process.WorkingSet64): " + Bytes(workingSetBytes));
                r.Lines.Add("运行时: IsServerGC=" + GCSettings.IsServerGC
                    + " MaxGeneration=" + GC.MaxGeneration.ToString(CultureInfo.InvariantCulture)
                    + " LatencyMode=" + GCSettings.LatencyMode);
                return r;
            }
            catch (Exception ex)
            {
                return SlashResults.Error("gc", "GC 数据读取失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static SlashResult Usage(string problem)
        {
            SlashResult r = SlashResults.Usage("gc",
                "用法: /gc —— 打印 GC 堆占用、分代回收次数、累计分配与进程工作集(无参数)",
                "Data: heapBytes / totalMemoryBytes / gen0 / gen1 / gen2 / allocatedBytes / workingSetBytes");
            if (!string.IsNullOrEmpty(problem)) r.Lines.Insert(0, "/gc " + problem);
            return r;
        }

        private static bool IsHelp(string args)
        {
            return args.Equals("help", StringComparison.OrdinalIgnoreCase)
                || args.Equals("-h", StringComparison.OrdinalIgnoreCase)
                || args.Equals("--help", StringComparison.OrdinalIgnoreCase)
                || args.Equals("?", StringComparison.Ordinal);
        }

        /// <summary>进程工作集:优先 Process.WorkingSet64,退化到 Environment.WorkingSet,再退化到 0(绝不抛)。</summary>
        private static long ReadWorkingSetBytes()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    return p.WorkingSet64;
            }
            catch
            {
                try { return Environment.WorkingSet; }
                catch { return 0; }
            }
        }

        /// <summary>字节数按量级渲染(KiB/MiB/GiB/TiB),不变文化 ⇒ 与游戏 locale 无关。</summary>
        private static string Bytes(long bytes)
        {
            const double KiB = 1024.0;
            const double MiB = KiB * 1024.0;
            const double GiB = MiB * 1024.0;
            const double TiB = GiB * 1024.0;
            double b = bytes;
            if (b >= TiB) return Num(b / TiB) + " TiB";
            if (b >= GiB) return Num(b / GiB) + " GiB";
            if (b >= MiB) return Num(b / MiB) + " MiB";
            if (b >= KiB) return Num(b / KiB) + " KiB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        private static string Num(double value)
        {
            return value.ToString("N2", CultureInfo.InvariantCulture);
        }
    }
}
