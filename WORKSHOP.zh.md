# RimRoslynCshape_console (Lite)

游戏内 C# 控制台与 REPL —— RimWorld 1.7

本项目是原UESDdebuger  C#控制台  的1.7版本重置。

在游戏里实时写 C#：查程序集、翻类型、看日志与性能、管 Harmony 补丁、开关原版 Debug 项，全程不用离开游戏。可作为一种相比原版DebugAction更加敏捷的调试方案。
控制台窗口从主菜单就能打开，开档后照常使用。

## 特性

- 实时 C# 求值：表达式与语句一行一条，即写即跑；编译失败时按类型短名自动补 using。
- 游戏内置工具外部指令化调用：/status 自检、/asm 程序集、/types 类型检索、/log 游戏日志、/gc 与 /threads 性能、/hook Harmony 补丁管理、/dev Debug 开关、/debug-action 原版调试动作树、/prof 性能采样、/usings 管理……完整清单见游戏内 /help。
- AI Agent专用命名管道通道：`-RoslynConsole=<名>` 起一条管道会话，脚本与 AI agent 可直接驱动运行中的游戏，可通过多个命名管道进行并行多游戏实例测试；


## 快速开始：

1. 安装前置并启用本 mod；

- 对于人类：
2. 开发者模式 → 工具栏点控制台图标；

- 对于AI Agent：
2. 告诉你的Agent游戏终端启动时加 `-RoslynConsole=短名`。
然后类似下面的片段，把 roslyn-console-短名 换成你的实例，把 /help 换成要发的指令：
```powershell
$p=[IO.Pipes.NamedPipeClientStream]::new('.','roslyn-console-短名',[IO.Pipes.PipeDirection]::InOut);$p.Connect(3000);$w=[IO.StreamWriter]::new($p);$w.AutoFlush=1;$r=[IO.StreamReader]::new($p);$r.ReadLine()|Out-Null;$w.WriteLine('/help');while(($l=$r.ReadLine())-ne '>'){$l}
```

具体可详见模组附带skill

## 注意事项

- 单条求值超时 15 秒，超时不代表没执行，重发前先查副作用；
- `/quit` 会直接退出游戏。

源码与文档：https://github.com/Timer80/RimRoslynCshape_console_Lite
许可：MIT
