---
name: roslynprobe-console
description: 当要用 RoslynProbe 控制台（RimWorld 1.7 mod 的 C# 求值加工具指令）时使用——用 -RoslynConsole=<名> 起命名管道、启动旗标与常见坑、连接名与一进一出协议、20 条指令分别做什么、以及未知与空白与 --json 的几条口径。裸 -RoslynConsole 不建通道，只告警。
---

# RoslynProbe 控制台

一条连到运行中 RimWorld 的命名管道会话：直接写 C# 求值，或用斜杠指令做工具（查程序集与类型、看日志与性能、管 Harmony 补丁、翻 Debug 开关、管 using 表）。

指令集以当前版本的实际行为为准：本文与游戏内 /help 的输出不一致时，以 /help 为准。第一次连上建议先发 /status（看通道与能力）再发 /help（看指令表）。

## 启动：-RoslynConsole=<名>

- `-RoslynConsole=短名`：起命名管道，连接名是 `roslyn-console-短名`（这个全名就是 /status 报出来的名字）。多实例各自起名互不干扰。
- 裸 `-RoslynConsole` 与 `-RoslynConsole=`（空值）：**不建通道**，只往日志打一行告警。
- 不给旗标：不起控制台通道，只有游戏内 GUI 控制台（`/ui open` 打开）。

常与它搭配的旗标（RimWorld 1.7）：

| 旗标 | 效果 | 注意 |
|---|---|---|
| `-quicktest` | 跳过主菜单直接开一张随机开发者地图 | 要等地图真正就绪（`Find.CurrentMap` 非空）；加载途中别再调开图接口，否则游戏会卡在半初始化状态 |
| `-NoWindow` | 窗口隐藏、桌面零打扰（渲染照常） | 要看画面就去掉它 |
| `-NoCrashDialog` | 崩溃不弹对话框 | 自动化常用 |
| `-savedatafolder=<新目录>` | 存档目录重定向 | **ModsConfig 会一起被隔离**：新目录首次启动写的是只含原版 DLC 的默认配置，你的 mod 不在里面 ⇒ mod 不加载、管道根本不存在。启动前先把带自己 mod 的 `Config/ModsConfig.xml` 写进该目录 |

## 自动化

下面的片段里，把 roslyn-console-短名 换成你的实例，把 /help 换成要发的指令：

```powershell
$p=[IO.Pipes.NamedPipeClientStream]::new('.','roslyn-console-短名',[IO.Pipes.PipeDirection]::InOut);$p.Connect(3000);$w=[IO.StreamWriter]::new($p);$w.AutoFlush=1;$r=[IO.StreamReader]::new($p);$r.ReadLine()|Out-Null;$w.WriteLine('/help');while(($l=$r.ReadLine())-ne '>'){$l}
```

四条要注意的：

- 短名就是启动时 -RoslynConsole=<短名> 给的名字，管道全名是 roslyn-console-<短名>。
- 判据是行内容逐字等于大于号：首行是就绪，之后每轮结束也是一行大于号。不回显你发的原文，结果行之后紧跟一行大于号。
- 单个管道名同一时刻只接**一个客户端**：同名第二个连接会挂起直到第一个断开，脚本收尾务必 Dispose。**多个服务端可以并行**——多个游戏实例各用不同短名（`-RoslynConsole=a`、`-RoslynConsole=b`），各连各的管道，互不干扰。
- `ReadLine` 是阻塞的，脚本里每条提交都必须收到收尾的那行 `>` 才继续，否则整段挂死。

要反复调用，就把上面的片段包成循环：每轮读到恰好一行的 `>` 为止，再写下一行。`/quit` 会回 `> bye` 并关闭连接，游戏随后退出。

## 发什么

- 不以斜杠开头的行：进 C# 求值器（表达式、语句都行），一行一条、写完即提交。缺了分号或括号会直接得到 `编译错误: CS…`，不会等你续行。
- 以斜杠开头：按指令走。

会话是一进一出：写一行 → 把它这次提交的输出读完（读到下一行 `>`）→ 再写下一行。

单条求值超时 15 秒，返回 `eval timed out`。**超时不代表没执行**：开图、遍历整张地图、写大文件这类重活经常在超时之后仍在继续，先查副作用再决定要不要重发。

## 常见失败

- **连不上**：先确认 mod 真的加载了 —— `-savedatafolder` 的 ModsConfig 坑最常见；再用 `/log` 看管道是否启动、名字与预期是否一致。
- **求值全超时**：游戏线程被占住（还在加载，或已被搞成半初始化状态），不是客户端的问题。等加载结束；等不到就重启游戏。
- **启错 mod**：同程序集名、不同 packageId 的两份目录很容易混，启动参数对、协议也对，但跑的是别人的代码。日志堆栈里的源码绝对路径能反证加载的是哪一份。

## 指令（每条一句，共 20 条）

| 写法 | 子命令 / 参数 | 一句话 | 关键 Data 键 | 改状态 |
|---|---|---|---|---|
| `/help` | — | 列出全部指令 | `count / verbs / commands[]` | — |
| `/usings` | — | 列 usings 与 directives 两张表 | `usings / directives` | — |
| `/usings add <条目>…`（表内 `/usings-add`） | `add <条目>…` | 新增 using | `added / accepted / rejected[]` | ✅ |
| `/usings remove <条目>…`（表内 `/usings-remove`） | `remove <条目>…` | 删条目 | `removed / denied / notFound` | ✅ |
| `/reset [keepUsings]` | — | 清空会话（变量 / 类型 / 提交链） | `ok / keepUsings` | ✅ |
| `/quit` | — | 干净退出游戏 | `ok / quit` | ✅ |
| `/status` | — | 控制台自检（阶段 / 载荷 / 探针 / 通道 / 能力） | `stage / inGame / ticks / revision / pending / poisoned / gate / probeA / capabilities[] / channels[] / retrievedAt / channel` | — |
| `/env [args\|waylithclr\|layer]` | `args / waylithclr / layer` | 启动参数 + `WAYLITH_*` + `WaylithClr.*` + 运行时 + 层注册 | `mode / args / flags / env / runtime / waylithClr / layer` | — |
| `/asm [filter]` | 过滤子串 | 已加载程序集清单 | `count / total / filter / assemblies[]` | — |
| `/types <短名>` | 短名 | 短名 → 命名空间 / 程序集候选 | `candidates[] / count / assembliesPartial` | — |
| `/gc` | — | GC 堆占用与分代 | `heapBytes / totalMemoryBytes / gen0 / gen1 / gen2 / allocatedBytes / workingSetBytes` | — |
| `/threads` | — | 进程线程清单 | `count / threads[] / currentThreadId / mainThreadId` | — |
| `/diag` | — | pid + 诊断管道名 + 运行时目录 + `DOTNET_*` | `pid / diagnosticPipe / runtimeDir / dotnetEnv` | — |
| `/log [n] [filter]` | 条数 / 过滤子串 | 最近 n 条原版游戏日志 | `count / total / filter / entries[]` | — |
| `/dev [list [filter]\|<字段> [on\|off]]` | `list` / 字段名 | 三张 Debug 表的 public static 字段：列 / 翻转 / 置位 / 清位 | `changed / toggledCallback / toggledState / toggledError / fields[] / count` | ✅ |
| `/debug-action [list [filter]\|refresh\|<路径> [参数]]` | list / refresh / <路径> | 原版调试动作树：分片索引 / 按 `node.Path` 执行 | `mode / indexed / nodes[] / executed / patchLeak / path / actionType / elapsedMs` | ✅ |
| `/hook list\|check\|off` | list / check / off | Harmony：全进程枚举 / 单方法查 / 按 owner 卸 | `hooks[] / owners[] / patches[] / rejit / removed / warnings[]` | ✅（`off`） |
| `/prof [status\|top [n]]` | `status / top [n]` | 只读：配置回读（7 项）/ 自耗时前 n 个 scope | `mode / prefs / entries[] / frames / torn / gaps` | — |
| `/ui-open` | — | 打开游戏内 GUI 控制台窗口（幂等） | `opened / alreadyOpen` | ✅ |
| `/ui open`（别名，与上同一 Kind） | `open` | 同上 | 同上 | ✅ |

> 消费方去重请**按 `verb`，不要按 `kind`** —— `/ui` 与 `/ui-open` 共用同一个 `kind`（别名不是新指令）。

## 几条口径

- 未知不静默：未知指令响亮拒绝，未知子动词给用法行。只有裸 /usings 才会列两张表。
- 空白：分隔符一律按 char.IsWhiteSpace，TAB、全角空格、NBSP 都算；只认 ASCII 空格是缺陷。
- --json：行里出现独立 token --json 就输出恰好一行 JSON（该指令的 Data），不输出人类文本；引号包住它就当字面参数。
- 两段式别名与表内 verb 完全等价（/usings add 就是 /usings-add add），区别只是写法。
- 默认 usings 是 8 条（System / System.Collections.Generic / System.Linq / System.Text / System.Threading.Tasks / UnityEngine / Verse / RimWorld）；权威值看 /usings 或 /usings 加 --json。
- C# 侧能裸调用的助手只有 Print / Warning / Error / GetVars / GetUsings（写错名字会得到 CS0103）；没有 Log（那是刻意留白，免得遮蔽 Verse.Log 的 Log.Message）、没有 GetUsing（单数）、没有 GetClasses。
