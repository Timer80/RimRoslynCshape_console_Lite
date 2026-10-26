# RimRoslynCshape_console (Lite)

In-game C# console and REPL — RimWorld 1.7

This project is a 1.7 remake of the C# console from the original UESDdebuger.

Write C# in real time inside the game: inspect assemblies and types, read logs and performance, manage Harmony patches, flip vanilla debug flags — all without ever leaving the game. It can serve as a more agile debugging option than the vanilla DebugAction.
The console window opens from the main menu and keeps working once you load a save.

## Features

- Real-time C# evaluation: one expression or statement per line, write and run immediately; failed compilations automatically add usings by type short name.
- In-game tools callable externally as commands: /status self-check, /asm assemblies, /types type lookup, /log game log, /gc and /threads performance, /hook Harmony patch management, /dev debug switches, /debug-action vanilla debug action tree, /prof profiling, /usings management … full list in-game via /help.
- Dedicated named-pipe channel for AI agents: `-RoslynConsole=<name>` starts a pipe session, so scripts and AI agents can drive the running game directly; multiple named pipes allow parallel testing across several game instances;

## Quick start:

1. Install the dependencies and enable this mod;

- For humans:
2. Dev mode → click the console icon on the toolbar;

- For AI agents:
2. Tell your agent to add `-RoslynConsole=<shortname>` when launching the game console.
Then use a snippet like the one below, replacing roslyn-console-短名 with your instance, and /help with the command you want to send:
```powershell
$p=[IO.Pipes.NamedPipeClientStream]::new('.','roslyn-console-短名',[IO.Pipes.PipeDirection]::InOut);$p.Connect(3000);$w=[IO.StreamWriter]::new($p);$w.AutoFlush=1;$r=[IO.StreamReader]::new($p);$r.ReadLine()|Out-Null;$w.WriteLine('/help');while(($l=$r.ReadLine())-ne '>'){$l}
```

See the skill bundled with the mod for details.

## Notes

- A single evaluation times out after 15 seconds; a timeout does not mean it did not run — check side effects before resending;
- `/quit` quits the game immediately.

Source & docs: https://github.com/Timer80/RimRoslynCshape_console_Lite
License: MIT
