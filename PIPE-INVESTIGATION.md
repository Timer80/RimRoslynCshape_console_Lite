# 命名管道控制台调查笔记（"只能连一次 / 一执行就崩"）

状态：**已定位并修复（2026-10-26）**。本文保留原始事故记录与取证方法，结论见下。

## 结论：不是服务端崩，是游戏主循环根本没跑 Update

### 根因链（逐环有证据）

1. **Waylith 引擎的主循环在窗口失焦时会整帧跳过 Update。**
   `Waylith.EngineApplication.RunLoop`（反编译核实）：

   ```csharp
   bool flag3 = !(EngineWindow.IsFocused = Window.IsFocused)
                && !EngineWindow.RunInBackground
                && !Options.HideWindow;
   if (flag3) { Glfw.WaitEventsTimeout(0.1); continue; }   // ← 跳过整帧
   ...
   using (FrameProfiler.Scope("GameUpdate")) { OnUpdate?.Invoke(); }  // ← Update 在这行之后
   ```

   `continue` 一跳，`OnUpdate`（即 Unity 的 `Update`）就不执行。而
   `BootstrapBehaviour.Update → Executor.Tick` 是求值队列的**唯一**排空者，于是：
   求值永远留在队列里 → 读线程 `Tcs.Task.Wait(15000)` 超时 → 客户端只看到 `eval timed out`。

2. **为什么大多数时候感觉不到：主配置里 runInBackground 是 true。**
   `Config/Prefs.xml` 的 `<runInBackground>` 为 True 时 `flag3` 恒假，失焦照跑。
   但 **`-savedatafolder=<新目录>` 会把整个 Config 隔离**，新目录没有 `Prefs.xml`，
   用的是 `PrefsData` 的**字段默认值 false** —— agent 自动化恰好总用隔离目录，于是必踩。

3. **实测闭环（2026-10-26 12:07，实例 pipeonce）**
   - `1+1` 发出 → 15 秒 `eval timed out`，服务端日志零增量；
   - `12:07:58.459 [Vulkan] Focus regained after 2111.9 s idle`（失焦 35.2 分钟）；
   - `12:07:58.552 [Roslyn] 1+1 => 2` —— **焦点恢复 93 毫秒后同一条求值就完成了**；
   - `/status` 同时报 `pending=1`（那条超时请求还挂在队列里），而 `/status` 本身秒回
     （slash 走会话线程，不经过 Executor）。
   - 反射读真实值：`PrefsData.runInBackground=False`、`EngineWindow=True`（护盾设的）。

4. **无窗实例反证**：`-NoWindow` 让 `Options.HideWindow == true`，`flag3` 恒假，
   `1+1`、`2+2`、`Sum()` 全部正常执行 —— 与机制完全吻合。

### "只能连一次"是误判

`PipeConsole.AcceptLoop` 是 `while(true)` → 新建管道 → `WaitForConnection` → `ServeSession` → dispose → 循环，
支持重连（2026-10-26 11:32 实测与 12:16 系列实测均多次重连成功）。
当时的"连不上"其实是：**客户端脚本在超时后留下挂起的异步读**，之后所有读取立即返回空；
外加 `Write` 无超时，服务端不读时永久阻塞 —— 看起来像服务端死了。

## 已修复

| 位置 | 修复 |
|---|---|
| `Source/Game/RunInBackgroundForce.cs`（新增） | Harmony 拦 `UnityEngine.Application.runInBackground` 的 setter（所有写入的总入口）+ 钉 `Verse.Prefs.RunInBackground` getter。**只在带 `-RoslynConsole` 的 agent 通道启用**，且只改运行时值，**不写玩家配置**。 |
| `Source/Game/Repl/SessionEval.cs` | 轮询等待 + 停摆判定（2 秒）：引擎没跑 Update 时立刻报「本次提交未执行」，不再沉默 15 秒；两种超时文案区分「未执行」与「可能仍在执行」。 |
| `Source/Core/Repl/Executor.cs` | 新增 `LastTickMs` / `IdleMs` / `TickInProgress`（引擎活性事实）；`Tick` **跳过已超时收口的请求**，不再让副作用凭空发生。 |
| `Source/Game/Repl/SessionLine.cs` | 超时文案透传 `r.Error`（不再一律打成 `eval timed out`）。 |
| `Source/Game/Repl/Cmds/StatusCommand.cs` | `/status` 增加 `lastTickAgoMs`（只读标量，不做进程内冻结判定）。 |
| `AerocraftFrameworkStub/tools/roslyn-eval.ps1` | 修客户端三坑：2 秒硬空闲超时、放弃读不 EndRead、Write 无超时。 |

## 验证证据（2026-10-26 12:29，实例 focustest，窗口已 `SW_MINIMIZE`）

- `/status` 两次相隔 3 秒：`ticks` 9520 → 10013（增长 493），`idle=0ms` ⇒ Update 在跑；
- `1+1 => 2`、`"ok" => "ok"`、`new[]{1,2,3}.Sum() => 6` 全部成功，758ms；
- `EngineWindow.RunInBackground=True` 而 `PrefsData.runInBackground` 仍为 `False` ⇒ 没动玩家配置；
- 单测：21 个测试类两遍全绿（含新增 `SessionEvalTests`、`ExecutorTests` 三条、`StatusCommandTests` 的 `lastTickAgoMs`）。

## 补充验证（2026-10-26 12:34–12:44，实例 pipeonce PID 42372，加载 12:26:40 修复版 DLL）

在上述根因修复之后，用修复版客户端跑的完整测试矩阵：

| # | 用例 | 结果 |
|---|---|---|
| 1 | 单条冒烟（`"probe-ok"` + `1+1` 连发） | ✅ |
| 2 | **事故 2 原样 5 条 DefDatabase 查询**（与当年 seq 2491 逐字相同） | ✅ **不复现**，全部正确返回（56 / texPath=Base / FighterJetThing / modExtensions=4） |
| 3a | 连续 5 轮连接-查询-断开 | ✅ 2/4/6/8/10 |
| 3b | 会话中途杀连接 → 立即重连 | ✅ |
| 4a | 多行续行（`if` 块） | ❌→✅ 发现**第四坑**：客户端只认 `>` 不认 `...`（续行提示），等到超时作废连接。已修（`Get-PromptKind` 三态 + `Remove-LastPromptLine`），重测协议层通过 |
| 4b | slash 指令 `/status` | ✅ |
| 4c | A 挂开时 B 连接排队 | ✅ **时间戳铁证**：A 窗口 `00.669→15.697`，B 于 `12.207`（窗口内）发起 → `15.698` 接入 = **A 断开后 1ms 被 accept**，排队 3.49s |

服务端日志：仅 3 条 `Pipe is broken`（3b/4c 客户端突断的正常记录，均"继续 accept"且其后连接全成功），无其他 `[Roslyn]` ERROR。

## 客户端脚本的原始 bug（历史记录，已修）

`Read-UntilPrompt`（旧版）：
1. 每轮 `BeginRead` 只 `WaitOne(2000)` —— 2 秒没数据就 `break`，远小于 `TotalTimeoutMs`；
2. `break` 后挂起的 `BeginRead` 从不 `EndRead` → 下次 `BeginRead` 抛"已有异步读进行中" → 被
   `catch { break }` 吞掉 → **此后所有读取立即返回空字符串**（"第二条起再无输出"的真身）；
3. `$client.Write()` 无超时 —— 服务端不读时永久阻塞（命令挂 180~240 秒的唯一路径）。
4. （补，12:34 测试发现）不识别服务端的 `...` 续行提示 → 多行表达式必等到超时作废连接。

## 上下文取回方法（日志已被轮转覆盖时用这个）

游戏 `Player.log` 只留两代，早期现场日志已丢失。但 DSH 会话原始轮次完整保留：

```
$DSH_HOME\sessions\--C-SteamLibrary-steamapps-common-RimWorld-Mods-AerocraftFramework1.6--\
  session-32d8d3f4-b023-4f7c-87b4-1dc6c3931b70\session.v4.jsonl.zstd
```

解压（Python 3.14 标准库自带 zstd）：

```powershell
$py = 'C:\Users\Timer_0\AppData\Local\Programs\Python\Python314\python.exe'
& $py -c @"
import compression.zstd, shutil
with compression.zstd.open(r'<src>.zstd','rb') as fin, open(r'<dst>.jsonl','wb') as fout:
    shutil.copyfileobj(fin, fout, 1<<20)
"@
```

解出的 jsonl 按 `"seq":` / 关键词 grep 即可（`roslyn-eval`、`probe-ok`、`still running after` 等）。
本笔记的原始会话解压产物：`_scratch_session.jsonl`（14.9 MB，用完可删）。

## 协议要点（不变）

连接后服务端首行发 `>`；每收到一个非空行先回显 `> 行`；提交完成回结果行 + `>`；未完成提交回 `...`。
`maxNumberOfServerInstances: 1`，同一时间单客户端，第二个连接在内核排队。

## 遗留

- `-savedatafolder` 新建目录的 `Prefs.xml` 默认 `runInBackground=false` 这个坑，现在被护盾兜住了；
  但它同样影响**其它**依赖 `Update` 的 mod —— 与本 mod 无关，仅记录。
- 并行跑多实例时 `Player.log` 会互相覆盖；取证请用 `-FileLog=<path>`（见 12:17 的 inst6 实例）。
