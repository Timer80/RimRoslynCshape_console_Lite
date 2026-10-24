// RoslynProbe —— 控制台窗口（终端化重写，规格 docs/specs/2026-10-14-console-terminal-redesign-design.md）。
//
// 四段纵向：流（专用日志唯一渲染源）→ 固定输入栏 → 详情栏（跟随/锁定 + 只读拖选 + 复制）→ 状态行。
// 窗口本体仍继承 Verse.Window：暗色背景、阴影、标题栏拖拽、ESC 关闭、右上关闭叉、WindowStack 门控。
// IsDebug=true 是硬要求（UIRoot_Entry.ShouldDoMainMenu 会在非 debug Dialog 层窗口存在时隐藏主菜单）。
//
// 与旧实现的三处决裂：
//   1. 不再镜像 Verse.Log.Messages —— 红字风暴时旧镜像每帧重读 150 条是卡顿源（规格 §1）；
//      渲染只读 ConsoleLog（Source/Core/Repl/ConsoleLog.cs），与原版缓冲零耦合。
//   2. 不再做结果拾取 —— ReplHost.Eval 完成时直接落账（ConsoleLog.Complete），本类纯渲染+入队；
//      旧 WindowUpdate 的 pending 轮询删除。
//   3. 每行常驻复制按钮删除 —— 复制走详情栏按钮 + 详情栏/输入框原生拖选（决策 6）。
//
// 求值排队：上一条未完成再 Enter 不丢弃，入 Executor 的 FIFO（上限 GuiMaxQueue=32，决策 9）；
// 队列满/未接线走 ConsoleLog.SystemNote 独立系统行。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;
using RoslynProbe.Repl;

namespace RoslynProbe
{
    public class ConsoleWindow : Window
    {
        private const float TitleBarHeight = 30f;
        private const float StatusHeight = 20f;
        private const float RowH = 19f;
        private const float InputLineHeight = 19f;
        private const int MaxInputLines = 10;
        private const int GuiMaxQueue = 32;
        private const float DetailMinHeight = 44f;
        /// <summary>流区最小高度：详情栏上限必须给它留位，否则四段总高溢出（规格 §4.1）。</summary>
        private const float StreamMinHeight = 60f;
        private const float DetailMaxFraction = 0.5f;
        private const float DetailDefaultHeight = 96f;

        private readonly Executor executor;
        private readonly ReplHost host;

        private Vector2 scroll = Vector2.zero;
        private bool autoScroll = true;
        private long lockedId = -1;

        private string input = "";
        private readonly List<string> inputHistory = new List<string>();
        private int historyCursor = -1;
        private bool focusInput;

        private float detailHeight = DetailDefaultHeight;
        private bool dividerDrag;
        /// <summary>本帧详情栏的真正上限（拖拽与显示共用，避免死区）。</summary>
        private float detailMaxThisFrame = DetailDefaultHeight;

        private static Font monoFont;
        private GUIStyle monoLabel;
        private GUIStyle monoArea;

        // ---- 配色（规格 §5）----
        private static readonly Color ColBgStream = new Color(0.063f, 0.071f, 0.078f);   // #101214
        private static readonly Color ColBgInput = new Color(0.094f, 0.106f, 0.125f);    // #181b20
        private static readonly Color ColBgDetail = new Color(0.078f, 0.086f, 0.102f);   // #14161a
        private static readonly Color ColInput = new Color(0.910f, 0.918f, 0.941f);      // #e8eaf0
        private static readonly Color ColValue = new Color(0.604f, 0.655f, 0.722f);      // #9aa7b8
        private static readonly Color ColError = new Color(1.000f, 0.420f, 0.420f);      // #ff6b6b
        private static readonly Color ColDim = new Color(0.361f, 0.400f, 0.447f);        // #5c6572
        private static readonly Color ColSelectedBg = new Color(0.165f, 0.192f, 0.251f); // #2a3140
        private static readonly Color ColRule = new Color(0.28f, 0.31f, 0.36f);

        public override Vector2 InitialSize
        {
            get { return new Vector2(760f, 500f); }
        }

        public override bool IsDebug
        {
            get { return true; }
        }

        public ConsoleWindow(ReplHost host, Executor executor)
        {
            this.host = host;
            this.executor = executor;
            optionalTitle = ConsoleText.T("RoslynProbe.Console.Title", "RoslynProbe 控制台");
            doCloseX = true;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnCancel = true;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = true;
            preventCameraMotion = false; // 主菜单下没有相机/地图
            forcePause = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            focusInput = true;
        }

        public override void PostClose()
        {
            base.PostClose();
            // ESC / 关闭叉 / TryRemove 都会走到这里，同步 ConsoleLayer 静态引用，
            // 否则 IsOpen 永远 true、工具栏图标停在 Enabled 态（原逻辑保留）。
            ConsoleLayer.NotifyRemoved();
        }

        /// <summary>
        /// ESC 优先解锁（规格决策 5）。必须在 OnCancelKeyPressed 层拦截：原版 Window.InnerWindowOnGUI
        /// 在 DoWindowContents 之前就调用 WindowStack.Notify_PressedCancel → OnCancelKeyPressed → Close()，
        /// 且 KeyBindingDef.KeyDownEvent 不检查 Event.current.used，所以在 DoWindowContents 里
        /// Event.current.Use() 拦不住关窗。未锁定时交回原版行为（关窗）。
        /// </summary>
        public override void OnCancelKeyPressed()
        {
            if (lockedId >= 0)
            {
                lockedId = -1;
                Event.current.Use();
                return;
            }
            base.OnCancelKeyPressed();
        }

        // ---- 布局 ----

        public override void DoWindowContents(Rect inRect)
        {
            EnsureStyles();

            Rect titleRect = new Rect(0f, 0f, inRect.width, TitleBarHeight);
            DrawTitle(titleRect);
            float top = TitleBarHeight + 4f;

            float inputLines = Mathf.Clamp(CountNewlines(input) + 1, 1, MaxInputLines);
            float inputH = inputLines * InputLineHeight + 14f;
            // 详情栏上限要同时满足：显示上限（0.5·H）与"给流留足 StreamMinHeight"。
            // 否则多行输入 + 详情拖大时四段总高溢出、详情压住状态行（规格 §4.1）。
            float detailMax = Mathf.Min(inRect.height * DetailMaxFraction,
                                        inRect.height - top - inputH - 10f - 6f - StatusHeight - StreamMinHeight);
            detailMaxThisFrame = Mathf.Max(DetailMinHeight, detailMax);
            float detailH = Mathf.Clamp(detailHeight, DetailMinHeight, detailMaxThisFrame);

            float streamH = inRect.height - top - inputH - 10f - detailH - 6f - StatusHeight;
            streamH = Mathf.Max(streamH, StreamMinHeight);

            Rect streamRect = new Rect(0f, top, inRect.width, streamH);
            DrawStream(streamRect);

            Rect inputRect = new Rect(0f, streamRect.yMax + 4f, inRect.width, inputH);
            DrawInput(inputRect);

            Rect dividerRect = new Rect(0f, inputRect.yMax + 2f, inRect.width, 6f);
            DrawDivider(dividerRect);

            Rect detailRect = new Rect(0f, dividerRect.yMax + 2f, inRect.width, detailH);
            DrawDetail(detailRect);

            Rect statusRect = new Rect(0f, inRect.height - StatusHeight, inRect.width, StatusHeight);
            DrawStatus(statusRect);
        }

        // ---- 字体与样式 ----

        /// <summary>OS 等宽字体，Consolas → Cascadia Mono，取不到回退 skin 字体（规格 §5）。</summary>
        private static Font MonoFont
        {
            get
            {
                if (monoFont == null)
                {
                    try { monoFont = Font.CreateDynamicFontFromOSFont("Consolas", 13); } catch { }
                    if (monoFont == null)
                    {
                        try { monoFont = Font.CreateDynamicFontFromOSFont("Cascadia Mono", 13); } catch { }
                    }
                }
                return monoFont;
            }
        }

        private void EnsureStyles()
        {
            if (monoLabel != null) return;
            Font f = MonoFont;
            monoLabel = new GUIStyle(GUI.skin.label)
            {
                font = f,
                wordWrap = false,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.UpperLeft,
                clipping = TextClipping.Clip
            };
            monoArea = new GUIStyle(GUI.skin.textArea)
            {
                font = f,
                wordWrap = true,
                margin = new RectOffset(2, 2, 2, 2)
            };
            if (f != null)
            {
                monoLabel.fontSize = 13;
                monoArea.fontSize = 13;
            }
        }

        // ---- 流 ----

        private void DrawStream(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, ColBgStream);
            var list = ConsoleLog.Entries;
            if (list.Count == 0)
            {
                GUI.color = ColDim;
                GUI.Label(rect, ConsoleText.T("RoslynProbe.Console.Stream.Hint", "在此输入 C# 表达式。Enter 执行 / Shift+Enter 换行；求值进行中可继续入队。"), monoLabel);
                GUI.color = Color.white;
                return;
            }

            float viewW = rect.width - 16f;

            float contentH = 0f;
            for (int i = 0; i < list.Count; i++)
                contentH += RowsOf(list[i]).Count * RowH + 3f;
            contentH = Mathf.Max(contentH + 8f, rect.height);

            float prevY = scroll.y;
            if (autoScroll) scroll.y = 1E+06f;

            Widgets.BeginScrollView(rect, ref scroll, new Rect(0f, 0f, viewW, contentH));

            float y = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                ConsoleEntry e = list[i];
                var rows = RowsOf(e);
                float entryH = rows.Count * RowH;

                if (lockedId == e.Id)
                    Widgets.DrawBoxSolid(new Rect(0f, y, viewW, entryH), ColSelectedBg);

                float ry = y;
                for (int r = 0; r < rows.Count; r++)
                {
                    GUI.color = rows[r].Value;
                    GUI.Label(new Rect(0f, ry, viewW, RowH), rows[r].Key, monoLabel);
                    GUI.color = Color.white;
                    ry += RowH;
                }

                if (!e.IsSystem && Event.current.type == EventType.MouseDown && Event.current.button == 0
                    && new Rect(0f, y, viewW, entryH).Contains(Event.current.mousePosition))
                {
                    lockedId = (lockedId == e.Id) ? -1 : e.Id; // 点击选中锁定；再点同行解锁（决策 5）
                    Event.current.Use();
                }

                y += entryH + 3f;
            }

            Widgets.EndScrollView();

            // 自动贴底：用户上滚即暂停，滚回底部恢复（原版 log auto-scroll 同款，规格 §4.2）。
            if (scroll.y < prevY - 2f) autoScroll = false;
            if (scroll.y + rect.height >= contentH - 6f) autoScroll = true;
        }

        /// <summary>
        /// 流内一行一条：把多行文本压成单行（换行 → 空格），让这一行显示更多内容。
        /// 真正的多行内容在详情栏看（详情栏保留原始换行）。
        /// </summary>
        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        }

        /// <summary>
        /// 系统提示行的**规范文本**（GUI 产生点的唯一出口）：统一加一次 <c>"! "</c> 前缀；已带则不重复加。
        /// 与之配对的是 <see cref="SystemRowText"/>（渲染层，只显示、不再补前缀）—— 两侧各加一次曾让窗口
        /// 显示成 <c>!! {...}</c>，而缓冲里只有一个（S11-8-1 真机边角 B1）。
        /// 同一约定也适用于 pipe 壳（落账时传 <c>"! " + 行</c>）。
        /// </summary>
        public static string SystemNoteText(string body)
        {
            string b = body ?? "";
            return b.StartsWith("! ", StringComparison.Ordinal) ? b : "! " + b;
        }

        /// <summary>
        /// 系统提示行的**显示文本**：把 <c>ConsoleLog.SystemNote</c> 存下的文本原样显示（产生点已自带
        /// 一次 <c>"! "</c> 前缀）—— 渲染层不再补前缀，于是「窗口 == 缓冲」都是单前缀。
        /// oneLine=true 把换行压成空格（流里一行一条）；false 保留换行（详情栏）。
        /// </summary>
        public static string SystemRowText(string storedText, bool oneLine)
        {
            string t = storedText ?? "";
            return oneLine ? OneLine(t) : t;
        }

        /// <summary>「重置」按钮的系统行正文（产生点，经 <see cref="SystemNoteText"/> 落账）。</summary>
        public const string ResetNoteBody =
            "会话已重置：变量 / 类型 / usings 已清空（日志保留；要清日志请点「清空」）";

        private List<KeyValuePair<string, Color>> RowsOf(ConsoleEntry e)
        {
            var rows = new List<KeyValuePair<string, Color>>();
            if (e.IsSystem)
            {
                rows.Add(new KeyValuePair<string, Color>(SystemRowText(e.ErrorText, true), ColError));
                return rows;
            }
            rows.Add(new KeyValuePair<string, Color>("> " + OneLine(e.Input) + (e.Completed ? "" : " …"), ColInput));
            if (e.Completed)
            {
                string suffix = e.Repeats > 1 ? "  x" + e.Repeats : "";
                if (e.Ok) rows.Add(new KeyValuePair<string, Color>("= " + OneLine(e.ValueText ?? "null") + suffix, ColValue));
                else rows.Add(new KeyValuePair<string, Color>("! " + OneLine(e.ErrorText ?? "error") + suffix, ColError));
            }
            return rows;
        }

        // ---- 输入栏 ----

        private void DrawInput(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, ColBgInput);
            float pad = 8f;
            Rect prefixRect = new Rect(rect.x + pad, rect.y + 5f, 18f, rect.height - 10f);
            GUI.color = ColInput;
            GUI.Label(prefixRect, ">", monoLabel);
            GUI.color = Color.white;

            Rect fieldRect = new Rect(prefixRect.xMax + 2f, rect.y + 5f, rect.xMax - pad - (prefixRect.xMax + 2f), rect.height - 10f);

            // ---- 按键处理必须放在 GUI.TextArea 之前，否则“回车执行”永远不触发 ----
            // 本引擎的 UnityEngine.Event.Use() 会把 type 直接置成 EventType.used：
            //     Event.Use() { type = EventType.used; _rawType = EventType.used; }
            // 而 GUI.TextArea 内部处理 Enter 时（GUI.ApplyTextKey 的 multiline 分支：插入 "\n" 后调用 e.Use()）
            // 已经把事件标成 used。旧写法把本判断放在 TextArea 之后，
            // `Event.current.type == EventType.KeyDown` 必然为假 —— 于是回车只会换行、永不执行。
            // 放到前面并主动 Use()，TextArea 随后因 type 已是 used 而不再插入换行。
            // 聚焦判定：输入框是默认提交目标，只有详情栏（只读拖选）抑制提交；
            // 这样即使某帧取不到焦点名（返回空串）也能正常提交。
            if (Event.current.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() != "RoslynProbeDetail")
            {
                bool singleLine = input.IndexOf('\n') < 0;
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                {
                    if (!Event.current.shift)
                    {
                        Event.current.Use(); // 吃掉事件：TextArea 随后不会插入换行
                        Submit(input);       // 成功则清空 input，下面按新值绘制空框
                    }
                    // Shift+Enter：不 Use，交给 TextArea 原生换行（决策 7）
                }
                else if (singleLine && Event.current.keyCode == KeyCode.UpArrow)
                {
                    Event.current.Use();
                    HistoryStep(-1);
                }
                else if (singleLine && Event.current.keyCode == KeyCode.DownArrow)
                {
                    Event.current.Use();
                    HistoryStep(1);
                }
            }

            GUI.SetNextControlName("RoslynProbeInput");
            string next = GUI.TextArea(fieldRect, input, monoArea);
            if (next != input)
            {
                input = next;
                historyCursor = -1; // 手动编辑即退出历史浏览（程序化赋值不产生编辑事件，不受影响）
            }

            if (focusInput)
            {
                GUI.FocusControl("RoslynProbeInput");
                focusInput = false;
            }
        }

        /// <summary>
        /// quit 时该落账的 SystemNote 序列（纯逻辑，可单测）：先该指令自己的行（带 --json 时就是
        /// Data 的单行 JSON），最后才是 <c>bye</c>。与 pipe 同口径（它先逐行输出 Lines，
        /// 再由 SessionLine 追加 "&gt; bye"）。调用方给每行加 "! " 前缀后落 ConsoleLog。
        /// </summary>
        public static List<string> QuitNoteLines(SlashResult slash)
        {
            var notes = new List<string>();
            if (slash != null && slash.Lines != null)
                foreach (string line in slash.Lines) notes.Add(line);
            notes.Add("bye");
            return notes;
        }

        private void Submit(string code)
        {
            string trimmed = (code ?? "").Trim();
            if (trimmed.Length == 0) return;

            // Slash 指令分流（T4，计划 §0.7）：内核唯一实现（SlashCommands），HTTP 是同一内核的薄壳。
            // 指令是元操作：不进求值队列、不产生 "> /xxx" 假提交行、不占用 32 深度；结果走 SystemNote 落账。
            // 线程契约：Submit 跑在 GUI 线程，SystemNote 只允许游戏更新线程 —— 经 Executor 队列 marshal。
            if (SlashCommands.IsCommand(trimmed, SlashCommands.ChannelGui))
            {
                if (executor == null || host == null)
                {
                    ConsoleLog.SystemNote(SystemNoteText(ConsoleText.T("RoslynProbe.Console.Note.NotWired", "控制台未接线")));
                    return;
                }
                // 事实位(S11-3):通道 gui;Submit 跑在 OnGUI(游戏线程)⇒ IsGameThread=true。
                // S11-4:入口换成 SlashJson(剥离 --json 尾旗标 → 带旗标则把 Lines 换成 Data 的单行 JSON);
                // 不带旗标时交给内核的字符串与 trimmed 逐字相同 ⇒ 下面的 "! " 逐行落账行为不变。
                if (!SlashJson.TryDispatch(trimmed, host, executor, SlashCommands.ChannelGui, true,
                        out SlashResult slash))
                {
                    ConsoleLog.SystemNote(SystemNoteText(ConsoleText.Tf("RoslynProbe.Console.Note.SlashParseFailed", "指令解析失败: {0}", trimmed)));
                    return;
                }
                if (slash.Kind == "quit")
                {
                    // 边角 2（S11-4 复审）：quit 在 GUI 也必须先把该指令自己的行落账 —— 带 --json 时
                    // 那就是 Data 的单行 JSON（旧实现直接丢掉 slash.Lines，GUI 下这行会消失，而
                    // pipe 会先逐行输出 Lines 再由 SessionLine 追加 "> bye"，两通道口径不一致）。
                    // 这里直调 SystemNote 而非入队：进程即将 Root.Shutdown，队列可能不再被 Tick。
                    foreach (string note in QuitNoteLines(slash))
                        ConsoleLog.SystemNote(SystemNoteText(note));
                    executor.Enqueue(() =>
                    {
                        // I-6a 报告 §5：handler 异常会被 Executor.Tick 吞掉且**无日志** ⇒
                        // 「bye 收到但进程不退」将毫无痕迹。显式记一行；正常路径行为不变。
                        try { Root.Shutdown(); }
                        catch (Exception ex) { Log.Error("[Roslyn] /quit 退出失败: " + ex); }
                        return ReplResult.Success(null);
                    }, 5000);
                    return;
                }
                executor.Enqueue(() =>
                {
                    foreach (string line in slash.Lines) ConsoleLog.SystemNote(SystemNoteText(line));
                    return ReplResult.Success("slash:" + slash.Kind);
                }, 5000);
                input = "";
                focusInput = true;
                autoScroll = true;
                return;
            }

            if (executor == null || host == null)
            {
                ConsoleLog.SystemNote(SystemNoteText(ConsoleText.T("RoslynProbe.Console.Note.NotWired", "控制台未接线")));
                return;
            }

            // 决策 9：FIFO 排队不丢弃。PendingCount 是 GUI/HTTP 共用队列深度，上限 32 防粘贴失控。
            if (executor.PendingCount >= GuiMaxQueue)
            {
                ConsoleLog.SystemNote(SystemNoteText(ConsoleText.Tf("RoslynProbe.Console.Note.QueueFull", "队列已满（{0}），本次未入队", GuiMaxQueue)));
                return; // 保留输入文字
            }

            if (executor.Poisoned)
            {
                // Poisoned 时 Executor.Enqueue 会直接返回一个"已完成"的请求且**绝不执行 handler**
                // （Executor.cs:25-36）。GUI 这条路径的 Begin 在闭包外，若照常建组就会永久 pending。
                ConsoleLog.SystemNote(SystemNoteText(ConsoleText.T("RoslynProbe.Console.Note.Poisoned", "会话已失效（poisoned），本次未入队")));
                return; // 保留输入文字
            }

            long entryId = ConsoleLog.Begin(trimmed);
            try
            {
                executor.Enqueue(() => host.Eval(trimmed, 30000, entryId), 30000);
            }
            catch (Exception ex)
            {
                // 建组在闭包外：Enqueue 抛异常会让该组永远 pending，就地收口。
                ConsoleLog.Complete(entryId, false, null, "enqueue failed: " + ex.Message, null);
                ConsoleLog.SystemNote(SystemNoteText(ConsoleText.Tf("RoslynProbe.Console.Note.EnqueueFailed", "入队失败：{0}", ex.Message)));
                return; // 保留输入文字
            }

            if (inputHistory.Count == 0 || inputHistory[inputHistory.Count - 1] != trimmed)
                inputHistory.Add(trimmed);
            historyCursor = -1;
            input = "";
            focusInput = true;
            autoScroll = true;
        }

        private void HistoryStep(int dir)
        {
            if (inputHistory.Count == 0) return;
            historyCursor = Mathf.Clamp(historyCursor + dir, -1, inputHistory.Count - 1);
            input = historyCursor < 0 ? "" : inputHistory[inputHistory.Count - 1 - historyCursor];
        }

        // ---- 分割线与详情栏 ----

        private void DrawDivider(Rect rect)
        {
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y + rect.height / 2f - 0.5f, rect.width, 1f), ColRule);
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition)) { dividerDrag = true; ev.Use(); }
            else if (ev.type == EventType.MouseDrag && dividerDrag)
            {
                detailHeight = Mathf.Clamp(detailHeight - ev.delta.y, DetailMinHeight, detailMaxThisFrame);
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp && dividerDrag) { dividerDrag = false; ev.Use(); }
        }

        private void DrawDetail(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, ColBgDetail);
            ConsoleEntry e = CurrentDetailEntry();
            const float headerH = 20f;

            string title = (lockedId >= 0 && e != null)
                ? ConsoleText.Tf("RoslynProbe.Console.Detail.Locked", "详情 · 已锁定 #{0}", e.Id)
                : ConsoleText.T("RoslynProbe.Console.Detail.Following", "详情 · 跟随最新");
            GUI.color = ColDim;
            GUI.Label(new Rect(rect.x + 8f, rect.y + 3f, rect.width - 100f, headerH), title, monoLabel);
            GUI.color = Color.white;

            string body = e != null ? DetailText(e) : ConsoleText.T("RoslynProbe.Console.Detail.Empty", "（空）");
            Rect copyRect = new Rect(rect.xMax - 88f, rect.y + 3f, 56f, headerH);
            if (e != null && Widgets.ButtonText(copyRect, ConsoleText.T("RoslynProbe.Console.Button.Copy", "复制")))
                GUIUtility.systemCopyBuffer = body;

            Rect bodyRect = new Rect(rect.x + 4f, rect.y + headerH + 2f, rect.width - 8f, rect.height - headerH - 6f);
            GUI.SetNextControlName("RoslynProbeDetail");
            // 只读可拖选（决策 6，原版 Debug log 消息详情区同款）：
            // 拦下会改内容的按键，放行 Ctrl+C / Ctrl+A / 方向键 / IME。
            if (Event.current.type == EventType.KeyDown
                && GUI.GetNameOfFocusedControl() == "RoslynProbeDetail"
                && (Event.current.keyCode == KeyCode.Backspace
                    || Event.current.keyCode == KeyCode.Delete
                    || (Event.current.character != 0 && !Event.current.control && !Event.current.command)))
            {
                Event.current.Use();
            }
            string after = GUI.TextArea(bodyRect, body, monoArea);
            if (after != body)
            {
                // 兜底回滚：丢弃返回值，下一帧仍渲染原文（拦截之外的编辑路径，例如 IME 提交）。
                // 不动 keyboardControl——那会把焦点踢出详情区。
            }
        }

        /// <summary>锁定优先；锁定条目被淘汰则自动恢复跟随；跟随 = 最后一组（规格 §4.2）。</summary>
        private ConsoleEntry CurrentDetailEntry()
        {
            if (lockedId >= 0)
            {
                ConsoleEntry e = ConsoleLog.Find(lockedId);
                if (e != null) return e;
                lockedId = -1;
            }
            var list = ConsoleLog.Entries;
            return list.Count > 0 ? list[list.Count - 1] : null;
        }

        private string DetailText(ConsoleEntry e)
        {
            if (e.IsSystem) return SystemRowText(e.ErrorText, false);
            var sb = new StringBuilder();
            sb.Append("> ").Append(e.Input);
            if (!e.Completed) return sb.Append("\n…").ToString();
            sb.Append("\n");
            if (e.Ok) sb.Append("= ").Append(e.ValueText ?? "null");
            else sb.Append("! ").Append(e.ErrorText ?? "error");
            for (int i = 0; i < e.Attachments.Count; i++)
                sb.Append("\n└ ").Append(e.Attachments[i]);
            return sb.ToString();
        }

        // ---- 标题栏与状态行 ----

        private void DrawTitle(Rect rect)
        {
            GUI.color = ColInput;
            GUI.Label(new Rect(rect.x + 4f, rect.y + 5f, 260f, 20f), ConsoleText.T("RoslynProbe.Console.Title", "RoslynProbe 控制台"), monoLabel);
            GUI.color = Color.white;

            // 「重置」（会话）与「清空」（日志流）语义正交：重置只清变量/类型/usings，不动日志。
            Rect resetRect = new Rect(rect.xMax - 160f, rect.y + 5f, 56f, 20f);
            if (Widgets.ButtonText(resetRect, ConsoleText.T("RoslynProbe.Console.Button.Reset", "重置")) && executor != null && host != null)
            {
                // 会话重置：清空 submission 链（变量/类/方法）并把 usings 重置回 DefaultUsings。
                // 与 HTTP 时期的 /reset 同一条路径（当时在 Commands.cs，该文件随 T5 删除；
                // 现等价物是 Source/Game/Repl/SlashCommands.cs 里 Verb = "reset" 那条登记项），只是没有 keepUsings 开关。
                // 经 Executor 队列 marshal：队列 lambda 跑在游戏更新线程，满足 ConsoleLog 的线程契约。
                executor.Enqueue(() =>
                {
                    host.Reset();                 // keepUsings 默认 false → usings 回 DefaultUsings
                    ConsoleLog.AbandonPending("reset");
                    ConsoleLog.SystemNote(SystemNoteText(ConsoleText.T("RoslynProbe.Console.Note.Reset", ResetNoteBody)));
                    return ReplResult.Success("reset");
                }, 5000);
            }

            Rect clearRect = new Rect(rect.xMax - 100f, rect.y + 5f, 56f, 20f);
            if (Widgets.ButtonText(clearRect, ConsoleText.T("RoslynProbe.Console.Button.Clear", "清空")))
            {
                ConsoleLog.Clear();
                lockedId = -1; // 缓冲没了，锁定对象消失 → 恢复跟随
                scroll.y = 0f;
            }
            // revision/ticks 下沉状态行；游戏日志复选框随镜像废除（规格 §4.2）。右上留白给 doCloseX。

            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width, ColRule);
        }

        private void DrawStatus(Rect rect)
        {
            ConsoleEntry cur = CurrentDetailEntry();
            var list = ConsoleLog.Entries;
            int rowN = 0;
            if (cur != null)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Id == cur.Id) { rowN = i + 1; break; }
            }
            string text = ConsoleText.Tf("RoslynProbe.Console.Status.Rev", "rev {0}", executor != null ? executor.Revision : 0)
                + " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Ticks", "ticks {0}", executor != null ? executor.TickCount : 0)
                + " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Row", "行 {0}/{1}", rowN, list.Count)
                + " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Buffer", "缓冲 {0}/{1}", list.Count, ConsoleLog.MaxEntries)
                + (executor != null && executor.PendingCount > 0
                    ? " · " + ConsoleText.Tf("RoslynProbe.Console.Status.Queue", "队列 {0}", executor.PendingCount)
                    : "")
                + " · " + (lockedId >= 0
                    ? ConsoleText.T("RoslynProbe.Console.Status.Locked", "已锁定")
                    : ConsoleText.T("RoslynProbe.Console.Status.Following", "跟随"));
            GUI.color = ColDim;
            GUI.Label(rect, text, monoLabel);
            GUI.color = Color.white;
        }

        // ---- 小工具 ----

        private static int CountNewlines(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n') n++;
            return n;
        }
    }
}
