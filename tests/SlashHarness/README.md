# tests/SlashHarness —— Slash 指令的专用测试面 + 自带载荷跑手

本目录有两个工程,共用同一批源文件(`..\..\Source\**\*.cs` + 本目录 `**\*.cs`):

| 工程 | 形态 | 用途 | 输出目录 |
|---|---|---|---|
| [SlashHarness.csproj](SlashHarness.csproj) | 库(`SlashHarness.dll`) | 编译校验面:**19 个测试类** + `SlashTestHarness` 与 `Source/Game` 其余文件在**同一编译面**共存;引用块沿用主工程语义(`Private=false`,**不外带**载荷)。 | `$(TEMP)\slashharness\` |
| [SlashHarnessRunner.csproj](SlashHarnessRunner.csproj) | Exe(`SlashHarnessRunner.exe`) | **自带载荷的跑手**(S11-5b):把载荷以 `Private=true` 带上,一条命令给出「跑两遍」的逐类结果与退出码。 | `$(TEMP)\slashrunner\` |

> 口径（数这个数就按它）：**`RoslynProbe.Tests.*` 命名空间下有 `public static int Run()`（返回失败数）的类**，由 `SlashTestHarness.RunAll()` 反射枚举并打印。
> 当前 = **19 个类 / 18 个 .cs 文件**（`AsmTypesCommandTests.cs` 里有两个类：`AsmCommandTests` 与 `TypesCommandTests`）—— 按文件数会把 19 数成 18（S11-4 起加了 `SlashJsonTests`/`ConsoleRenderTests`，R1 又加 `WiringCoverageTests`）。
>
> 为什么要有跑手:只引用 `SlashHarness.dll` 的临时跑手在反射 `GetTypes()` 时会抛未捕获的
> `ReflectionTypeLoadException`(进程崩),必须先手工把 `Assembly-CSharp` / `UnityStubs` / `0Harmony` / `Waylith*` / `Brutal*` / `Microsoft.CodeAnalysis*`
> 摆到能解析的位置 —— 三位写手各自踩过一次。跑手把这些做成工程的一部分,除本文件外不需要任何手工准备。

## 一条命令

在仓库根执行:

```powershell
dotnet run --project tests/SlashHarness/SlashHarnessRunner.csproj -- --twice
```

它在**同一个进程**里连续跑两遍 `RoslynProbe.Tests.SlashTestHarness.RunAll()`(§8.4 的幂等验收),每遍给 19 个测试类的逐类行
打上前缀(`[第1/2遍] …`)并打印该遍失败合计;**只有每一遍都是 0 失败才返回 0**。

### 参数与退出码

| 参数 | 说明 |
|---|---|
| `--once` | 跑一遍(默认) |
| `--twice` | 连跑两遍 |
| `--payload-dir <RimWorld 根>` | 追加载荷探测根,**优先**于 `$env:RIMWORLD_DIR`(可重复) |
| `--payload-verbose` | 逐次打印 `Resolving` 未命中(排查载荷根时用) |
| `--allow-shadow-harmony` | 允许探测/接受影子副本 0Harmony 2.4.1(默认拒绝,见下「编译面 == 运行面」) |
| `-h` / `--help` | 用法 |

| 退出码 | 含义 |
|---|---|
| `0` | 每一遍都是 0 失败 |
| `1` | 有测试失败(逐条 `FAIL` 行在被失败的那一类里) |
| `2` | 用法错误 / 必需载荷找不到 / 编译面≠运行面(见下) / 跑手崩溃(含 `ReflectionTypeLoadException` 的逐条 `LoaderExceptions`) |

### 输出长什么样

```text
SlashHarnessRunner: 跑 2 遍;载荷探测目录 4 个:
  probe: C:\Users\…\AppData\Local\Temp\slashrunner\
  probe: C:\…\RimWorld\
  probe: C:\…\RimWorld\Mods\Roslyn\Roslyn
  probe: C:\…\steamapps\workshop\content\294100\2009463077\1.7\Assemblies
载荷自检(必需 7 件):
  Assembly-CSharp.dll  <=  C:\…\Temp\slashrunner\Assembly-CSharp.dll
  …
Harmony 面(编译面 == 运行面 自检):
  运行面: C:\…\Temp\slashrunner\0Harmony.dll
          AssemblyVersion 3.0.0.0 / FileVersion 3.0.0.0 / product 3.0.0.0-…
          SHA256 C8EF7856B2BE3CF24B345C9B5C2F197F68FCF92649635064FCE7B3CCC8F77CB0
  编译面: C:\…\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll
          SHA256 C8EF7856B2BE3CF24B345C9B5C2F197F68FCF92649635064FCE7B3CCC8F77CB0
[第1/2遍] GcCommandTests: 全通过
…
[第1/2遍] SlashTestHarness: 共 19 个测试类，失败 0
[第 1/2 遍] 失败合计 = 0
[第2/2遍] …
===== 结论 =====
  第 1/2 遍:失败 0
  第 2/2 遍:失败 0
  每遍都是 0 失败(§8.4「连续两遍全绿」达成)
载荷的加载面(本进程实际加载了哪一份):
  loaded: 0Harmony 3.0.0.0 <= C:\…\Temp\slashrunner\0Harmony.dll
  …
```

运行期若有程序集解析不到,收尾在 **stderr** 汇总一行(不逐条刷屏):测试自造的夹具程序集(W4 的 `W4*Dep` 一类)属正常;
名单里出现游戏/Roslyn 载荷名时才需要核对载荷根。

## 形态:link,不是 ProjectReference

跑手把源文件**编进自己**(而不是 `ProjectReference` 到 `SlashHarness.csproj`),理由:

1. 全程只有一个程序集 ⇒ 从根上不存在「引用 `SlashHarness.dll` 但载荷摆不齐」的形态;
2. 不依赖 `SlashHarness.csproj` 的 `OutputPath` 语义,也没有跨程序集同名类型(CS0436)噪声;
3. 一份 `deps.json`、一份载荷副本。

代价:`RunnerMain.cs` 落在跑手自己的 glob 里,而 `SlashHarness.csproj` 已把它 **Exclude** 出编译面 —— 加本跑手不会改变 `SlashHarness.dll`
的字节内容(否则「加个跑手」会让绑在纯净检出上的 harness 哈希作废)。

## 载荷是怎么被发现的

### 构建期(`SlashHarnessRunner.csproj`)

`RimWorldDir` 按下面的顺序取**第一个**能用的,绝不写死只在一台机器成立的绝对路径:

1. 显式 `-p:RimWorldDir=<dir>`;
2. 环境变量 `RIMWORLD_DIR`;
3. **仓库相对位置** —— 本 csproj 在 `<RimWorld>\Mods\RoslynProbe\tests\SlashHarness\`,向上四级就是 `<RimWorld>\`。

三个载荷目录由它派生:

| 别名 | 路径 | 取哪些文件 |
|---|---|---|
| 游戏根 | `$(RimWorldDir)` | `Assembly-CSharp.dll`、`UnityStubs.dll`、`RimWorld.dll`、`Waylith*.dll`、`Brutal*.dll` 等 |
| Roslyn 载荷 | `$(RimWorldDir)Mods\Roslyn\Roslyn\` | `Microsoft.CodeAnalysis*.dll` + 6 个 `System.*` 引用面 |
| Harmony 载荷(**真身**) | `<Steam 库>\workshop\content\294100\2009463077\<版本>\Assemblies\`(本机 = `C:\SteamLibrary\steamapps\workshop\...\1.7\Assemblies\`) | `0Harmony.dll`(3.0.0) |
| ~~Harmony 影子副本~~ | `$(RimWorldDir)Mods\2009463077\Current\Assemblies\` | **默认不参与**(那里是 2.4.1;要它得 `--allow-shadow-harmony`,规则 §10) |

- 游戏三件 + `Microsoft.CodeAnalysis` 四件以 **`Private=true`** 拷进输出目录(这就是「自带载荷」);其余 `System.*` 六件保持 `Private=false` ——
  它们都在 `Microsoft.NETCore.App` 里,运行期由共享框架按简单名绑定,把游戏载荷目录里的旧副本拷进输出反而可能制造类型身份冲突。
- 微软的 `ResolveAssemblyReferences` 会顺带把 `Assembly-CSharp` 的**传递依赖**(`Waylith*`、`Brutal*`、`Microsoft.Extensions.*`、`K4os.*` 等)
  一起拷到输出目录,所以输出目录本身就是一份可解析的载荷集。
- 构建前若三个载荷目录缺文件,会以 `SLASH001/002/003` **在编译前响亮报错**并给出处置(不是一串 MSB3245 警告)。
- 构建后把 `RimWorldDir` 写进输出目录的 `payload-root.txt`(运行期要用;输出目录在 `$(TEMP)`,反推不出仓库位置),
  并把编译面用的 `0Harmony.dll` 路径写进 `payload-compile-harmony.txt`(运行期拿它做「编译面 == 运行面」比对,见下)。

Harmony 的**版本子目录**不写死:从游戏自己的 `<RimWorldDir>Version.txt` 推(`1.7.4979 rev109` ⇒ `1.7`)——
本 mod 的 `LoadFolders.xml` 对 `<v1.7>` 给出的目录也正是 `1.7`(它给的 `/` 对应 mod 根,而本 mod 根下没有 `Assemblies`)。

### 运行期(`RunnerMain.cs`)

`AssemblyLoadContext.Default.Resolving` 按下面的顺序取第一个命中的 `<name>.dll`:

1. `--payload-dir` 给的根(优先);
2. `$env:RIMWORLD_DIR`;
3. 运行目录(`AppContext.BaseDirectory`,即「自带载荷」那一份);
4. `payload-root.txt` 里构建期写下的 `RimWorldDir`。

每个根都展开成三处已知布局:`<root>`、`<root>\Mods\Roslyn\Roslyn`、
`<root>\..\..\workshop\content\294100\2009463077\<版本>\Assemblies`(版本名从 `<root>Version.txt` 推;Harmony **真身优先**,
影子副本 `Mods\2009463077\Current\Assemblies` 只有 `--allow-shadow-harmony` 才加入)。
启动时打印全部探测目录、必需 7 件的解析结果与 Harmony 面的自检(见下);必需件一件都找不到就直接退出 2,不会跑到一半才崩。

### 编译面 == 运行面(Harmony 自检,防 §10 的坑在工具里复活)

S11-7 已把**编译面**的 HintPath 指到真身 `workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll`(3.0.0);
跑手把这件事做成**运行期断言**,而不是约定:

- 构建期把 `HarmonyPayloadDir\0Harmony.dll` 的路径写进输出目录的 `payload-compile-harmony.txt`;
- 启动时解析运行面的那份,打印 `AssemblyVersion / FileVersion / SHA256`,并与 `payload-compile-harmony.txt` 里那份**比 SHA256**;
  不一致 ⇒ 响亮报错、退出 2;
- 运行面若落在影子副本目录 `Mods\2009463077\Current\Assemblies`(0Harmony 2.4.1)⇒ 同样报错退出 2;
  只有显式 `--allow-shadow-harmony` 才放行(且只打印警告)—— **任何 Harmony 结论不得以影子副本为准**(docs/slash-fleet-rules.md §10);
- 两遍跑完后打印**加载面**:本进程实际加载的那几份载荷(名称 + 版本 + 路径),`loaded: 0Harmony 3.0.0.0` 就是「运行面用真身」的直接证据;
  进程真的加载了影子副本 ⇒ 退出 2。

- 影子副本只是**最后兜底**:真身候选一个都找不到时才轮到它,而且还要 `-p:SLASH_RUNNER_ALLOW_SHADOW_HARMONY=true`;
  `-p:HarmonyPayloadDir=<dir>` 可以强指任何目录,强指影子同样会被上面的自检拦下(除非两侧都 opt-in)。

本机实测:运行面 == 编译面 == `C8EF7856B2BE3CF24B345C9B5C2F197F68FCF92649635064FCE7B3CCC8F77CB0`(真身 3.0.0.0)。

## 中间目录与并发

- 输出 `$(TEMP)\slashrunner\`;中间目录 `$(TEMP)\slashrunnerobj\`。(`BaseIntermediateOutputPath` / `MSBuildProjectExtensionsPath`
  **必须在 Microsoft.Common.props 之前**落定,所以本工程用显式 `Sdk.props`/`Sdk.targets` 导入而不是 `Sdk="…"` 属性 ——
  写在工程体里会被静默忽略,会就地造出 `tests/SlashHarness/obj/`。)
- 两个并发跑手会争同一个 `$(TEMP)\slashrunner\` / `slashrunnerobj`,此时给各自的目录(环境变量对 MSBuild 同样生效):

```powershell
$env:OutputPath = "$env:TEMP\slashrunner-me\"
$env:BaseIntermediateOutputPath = "$env:TEMP\slashrunnerobj-me\"
dotnet run --project tests/SlashHarness/SlashHarnessRunner.csproj -- --twice
$env:OutputPath = $null; $env:BaseIntermediateOutputPath = $null   # 只复位,不删任何文件
```

## 排错

| 现象 | 处置 |
|---|---|
| `error SLASH001`(找不到 `Assembly-CSharp.dll`) | `RimWorldDir` 推错了;用 `RIMWORLD_DIR` 或 `-p:RimWorldDir=` 指对 RimWorld 根 |
| `error SLASH002`(找不到**真身** `0Harmony.dll`) | 报错文案会列出试过的三个候选目录;用 `-p:HarmonyPayloadDir=<含 0Harmony.dll 的目录>` 指定真身,或 `-p:_SlashRunnerGameVersion=<版本目录>`;要影子副本得 `-p:SLASH_RUNNER_ALLOW_SHADOW_HARMONY=true` |
| `error SLASH003` | Roslyn 载荷目录不在预期位置;确认 Roslyn mod 在位,或指对 `RIMWORLD_DIR` |
| `[编译面≠运行面]` | 运行面的 0Harmony 与编译面那份 SHA256 不一致,或落在影子副本目录;按提示让 `HarmonyPayloadDir` 指真身后重建 |
| `[载荷缺失] … 有 N 个必需载荷在任何探测目录都找不到` | 同上;跑手会在跑测试**之前**退出 2 |
| 逐条 `[载荷未命中]`(默认不打印) | 加 `--payload-verbose`;测试自造夹具程序集(`W4*Dep`)解析不到是正常的 |
| `ReflectionTypeLoadException` | 跑手打印去重后的 `LoaderExceptions`(含文件名/类型名)并以 2 退出 |
| `error NETSDK1004: 找不到资产文件 … project.assets.json` | 与 `SlashHarness.csproj` 同款兜底(`Source/obj/project.assets.json`);仍报就先 `dotnet restore` 一次 |

## 与其他文件的关系

- `SlashHarness.csproj` 的用法**不变**(编译校验面仍然只做「能编过」这件事);
- `RunnerMain.cs` 是跑手入口,不参与 `SlashTestHarness` 的发现(`RunAll()` 只挑 `RoslynProbe.Tests.*` 下 `public static int Run()`);
- 两者都不写 `1.7/Assemblies/`,也不改主工程;新文件只在 `tests/SlashHarness/` 下。
