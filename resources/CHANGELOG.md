# CHANGELOG

记录 LightClipboard 从零到当前版本的开发过程。日期为开发当日（2026-09-18）。

## v1.2.0 — 顶部导航改版 + 可调隐藏延迟

### 新增

1. **设置页新增「隐藏延迟」**（`DeferredHideDelayMs`，默认 700ms，范围 50–1000ms）：
   滑块（刻度 50ms）+ 手动输入框双通道，越界值自动钳制（`SettingsService.Normalize` 兜底）。
   改动即时生效 —— `MainWindow` 订阅 `SettingsService.Changed` 同步 `DispatcherTimer.Interval`，
   不必重开面板。旧 `settings.json` 没有该字段时按 700ms 生效（与 v1.1.0 行为一致）。
   - 解决的真实痛点：原先 700ms 是写死的，想"失焦即收起"的用户觉得延迟感过强。
   - 调小时**不会**破坏拖拽：防打断靠的是 `DragEnter/DragOver` 取消 + "鼠标键仍按下就继续等待"，
     与时长无关（见 [04 坑点 6](04-实现细节与坑点.md)）。
   - 新增样式 `SettingValueBoxStyle`（设置页数值输入框）。

2. **顶部导航胶囊改版**：由「历史 / Emoji / 设置」+「全部 / 文本 / 图片 / 文件 / ⭐」两组，
   合并为**一条主链**，顺序固定为
   **全部 → 文本 → 图片 → 文件 → 😀 Emoji → ⭐ → 设置**。
   - 删除冗余的「历史」项：「全部」本身就是完整历史列表。
   - 七个胶囊共用一个 `GroupName`，任何时候只有一个选中态；默认 470px 宽下正好一行放得下，
     容器用 `WrapPanel` 兜底，窄窗口自动换行（不再有"两组胶囊互相挤占"的问题）。
   - 点任意筛选胶囊都会切回历史页（`OnMainChipChecked` 里先切页再设筛选），
     因此从 Emoji / 设置页点「全部」也能直接回到列表。

### 开发过程中修复的问题

| 现象 | 根因 | 修复 |
| --- | --- | --- |
| 点击顶部胶囊界面毫无反应，且不报任何绑定错误 | 胶囊同时挂了 `Command` 且 `IsChecked` 走 `OneWay`：`RadioButton` 点击只改变本地 `IsChecked`（同组互斥由 WPF 内部处理），**不会像 `Button` 那样触发 `Command`** | 去掉 `Command`，只保留 `IsChecked` 双向绑定；已写入 [04 坑点 9](04-实现细节与坑点.md) 的⚠️A 段与「不可回退清单」 |
| **进设置页后再点「全部」，界面停在设置面板不动**（点「文本/图片」正常） | 页面切换只挂在 `OnFilterChanged` 上，而它要求 `Filter` 的值**真的发生变化**；进设置页时 `Filter` 本来就是 `All`，点「全部」不产生变化 → 回调不执行 → 页面不切 | 页面/筛选的落地改挂胶囊的 `Checked` 事件（`OnMainChipChecked` + `Tag`，先切回历史页再设筛选），并用 `_chipClickInProgress` 防重入；`OnFilterChanged` 的回切保留作程序化调用兜底。见 [04 坑点 9](04-实现细节与坑点.md) 的⚠️B 段 |

### 验证

- `dotnet build`（Debug / Release）均 0 警告 0 错误；`--selftest` 50/50 PASS，退出码 0。
- 顶部胶囊：UI Automation 逐个选中核对（7 个胶囊顺序/选中态正确），历史 / Emoji / 设置三页截图核对；
  并逐条验证跨页跳转：**设置页点「全部」→ 回到完整历史列表**、Emoji 页点「文本」→ 回到历史列表且只剩文本条目
  （卡片数 8 → 5）、再点「全部」→ 恢复 7 条。
- 隐藏延迟：手动输入 `1000` → `settings.json` 落盘 1000；输入 `20` → 钳制为 50；输入 `9999` → 钳制为 1000；
  输入 `abc` → 回弹原值；旧版 `settings.json`（无该字段）→ 界面显示 700ms；
  改值后日志出现 `隐藏缓冲时长已更新为 100 ms`，确认计时器实时套用。
- 窄窗口：缩到最小尺寸 380×420，确认 7 个胶囊自动换成两行（截图 `tools/shots/v12-narrow.png`）。
- 界面截图全套重拍为 v1.2.0（`tools/shots/v12-*.png`，新增脚本 `tools/shoot-pages.ps1`），
  删除 v1.1.0 的旧截图，项目根 README 与 `resources/README.md` 的引用同步更新。
- 端到端联调后清理了本次排障引入的全部临时诊断代码（`Services/Diag.cs` 与若干 `tools/diag-*.ps1`、
  独立 `HookProbe` 探针工程），源码与 `tools/` 均无残留。

### 文档整理（本次一并完成）

- 新增 [10-高权限窗口支持决策记录.md](10-高权限窗口支持决策记录.md)：Everything/UIPI 问题的完整决策记录
  （现象、可复现的定位过程、根因、4 个备选方案的代价对比、当前决定、将来动手的落地要点）。
- 修正 `02-源码导读.md` 全部行数（此前停留在 v1.1.0，多数文件对不上）与版本号。
- `01-架构设计.md` 补「顶部导航从点击到页面切换」的落地链路、§6.5 失焦隐藏与延迟缓冲流程，
  以及 3 条新设计决策。
- `06-Win32与系统集成.md` §4.1 补 UIPI 完整性级别前提与实测对照表，§6 补抢焦点同样受 UIPI 限制。
- `07-构建发布与测试.md` 补"用 UI Automation 驱动顶部胶囊"的做法与两个坑、
  `shoot-pages.ps1` 用法、Release 构建被自身进程锁定的处置、以及 6 条新排查项。
- `08-后续开发路线.md` 修正已知限制第 5 条的**程度描述**（原写作"可能抢不到焦点"，实为钩子回调被丢弃），
  新增"提权钩子伴随进程"候选方案、3 条小维护项与 2 条验收建议。
- `resources/README.md` 更新索引（新增第 10 篇）、规模数据与截图说明。

### 入库整理（可移植性）

把项目整理进 git 仓库时做的三件事：

1. **消除机器专属硬编码路径**：`tools/ui-flow.ps1`、`tools/winv-test.ps1`、`tools/shoot-pages.ps1`
   原本写死了 `D:\harness\LightClipboard\...`，改为按 `$PSScriptRoot` 推导；
   文档里的绝对路径统一改成 `<仓库根>\...`。现在仓库克隆到任何机器上都能直接跑。
2. **新增 `.gitignore`**：挡住 `bin/`、`obj/`、`publish/` 与本程序的运行期数据
   （`clipboard.db`、`Logs/`、`Images/`、`Exports/`、`settings.json`）。
   后者平时位于 `%LocalAppData%\LightClipboard`，本就不在仓库里；这条是防"用
   `LIGHTCLIPBOARD_HOME` 把数据目录重定向到仓库内做隔离演示"时误提交。
3. **新增 `.gitattributes`**：`* text=auto eol=lf`，与仓库既有的 LF 保持一致，避免 CRLF 噪音；
   `*.ico/*.png` 标为 binary；需求方原件 `00-原始设计文档.txt` 标 `-text` 保持原样。

> `tools/shoot-pages.ps1` 顺带加固：面板处于隐藏状态时会自动唤起再截图，
> 且在原生层不可见时报错退出（否则会截出一张全黑图）；输出文件名固定为
> `v12-history.png` / `v12-emoji.png` / `v12-settings.png`，与 README 引用一致。
> 仓库里的四张截图由**隔离的演示数据实例**重拍，不含任何真实剪切板历史。

### 已知边界（本次实测确认，未修改架构）

**以管理员权限运行的前台窗口（如 `run_as_admin=1` 的 Everything）无法被本程序接管。**
用独立探针做 A/B/C 三组对照（同一 `WH_KEYBOARD_LL` 钩子）实测：

| 前台窗口 | 钩子是否收到按键 |
| --- | --- |
| 与钩子进程同级 | ✔ 收到 |
| Everything（HIGH，钩子进程 MEDIUM） | ✘ 完全收不到 |
| Everything 最小化后 | ✔ 恢复收到 |

根因是 UIPI：**低于前台窗口完整性级别的进程，其低级键盘钩子回调会被系统直接丢弃**，
Win+V 原样落给 Shell，于是弹出系统原生剪切板历史；拖拽同样因抢不回焦点而中断。
本次评估过的备选方案与结论：

- 改用 `RegisterHotKey(Win+V)`：实测返回 **1409 ERROR_HOTKEY_ALREADY_REGISTERED**（原生剪切板历史占用），
  且临时把 `EnableClipboardHistory` 置 0 并重启 explorer 后仍被占用，此路不通。
- 引入"以管理员权限常驻的轻量钩子进程 + IPC"：业界标准解法，但属架构级改动，本次**未采纳**
  （需求方决定先只做上面两项）。
- 让该程序取消管理员权限运行：零代码改动且立即可用，已写入 README「已知限制」。

完整记录（含实测手段与"方案 B 只能解决拦截、不能解决抢焦点"这一关键提醒）见
[10-高权限窗口支持决策记录.md](10-高权限窗口支持决策记录.md)。

---

## v1.0.0 — 首个可用版本

### 新增（按实现顺序）

1. **项目骨架**：`net10.0-windows` + WPF-UI 4.3.0 + CommunityToolkit.Mvvm 8.4.2 + LiteDB 5.0.21 +
   H.NotifyIcon.Wpf 2.4.1；`app.manifest` 采用 `asInvoker` + PerMonitorV2 DPI。
2. **剪切板监听**：`AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE`，80ms 去抖。
3. **数据解析**：`FileDrop → PNG → DIB/Bitmap → UnicodeText` 优先级链，3 次指数退避重试。
4. **存储**：LiteDB 单文件库，内容指纹去重、收藏、按 `LastUsedAt` 排序、超限裁剪。
5. **图片管理**：PNG 按内容哈希落盘（年月分目录），缩略图 `DecodePixelWidth=320` + 120 条 LRU 缓存。
6. **拖拽**：拖出提供 FileDrop + Bitmap + PNG 三种格式；拖入支持文件/图片/文本。
7. **面板 UI**：Fluent 深色主题、卡片列表、搜索与筛选胶囊、虚拟化列表、空状态、Toast、拖放提示遮罩。
8. **Emoji 面板**：439 个 Emoji / 8 分类，中英文关键词搜索。
9. **设置页**：自动粘贴、失焦隐藏、暂停监听、开机自启、历史上限、外观主题、关于。
10. **系统集成**：全局热键（含降级链）、托盘图标与菜单、`SendInput` 模拟 Ctrl+V、单实例。
11. **自检与演示**：`--selftest`（46 项）+ `--demo` 演示数据 + 图标生成脚本。

### 开发过程中修复的问题

| 现象 | 根因 | 修复 |
| --- | --- | --- |
| 截图入库后全透明 | CF_DIB 的 alpha 全为 0 | DIB 路径统一 `MakeOpaque` |
| 点击卡片后记录被重复入库 | 自身写剪切板触发了监听 | 写入前开启 1.5s 屏蔽窗口 |
| 无法把外部文件拖进面板 | 拖拽起点导致面板先失焦隐藏 | 失焦延迟 700ms 隐藏，`DragEnter` 取消 |
| 切到 Emoji 页显示空白 | 历史页搜索词被带过去 | 切换页面时清空搜索框 |
| 深色主题下 Emoji 不可见 | `Button` 默认前景色是黑色 | 显式指定 Emoji 按钮前景色 |
| Emoji 分类行被横向滚动条横穿、末尾分类被裁 | 浮层滚动条 + 分类过多 | 改 WrapPanel 两行显示，去掉横向滚动 |
| 提示文字显示成一排方块 | ToolTip 继承了按钮的图标字体 | 图标字体下沉到内部 TextBlock + 全局 ToolTip 样式 |
| 滚动条压住卡片右边缘 | 浮层滚动条不挤压内容 | 卡片/设置行/Emoji 网格各自留出右侧间距 |

### 验收

- 自检 46 项全部通过（退出码 0）。
- 真机端到端验证：外部复制→收录、热键唤出、点击卡片→自动粘贴进目标程序、
  图片拖到资源管理器生成文件、文件拖入面板、托盘图标、开机自启开关（注册表读写）、窗口尺寸记忆。
- 三页面截图核对通过。

---

## v1.0.1 — 修复真机日志暴露的问题

### 修复

| 现象 | 根因 | 修复 |
| --- | --- | --- |
| **每次图片入库都记录** `[WARN ] 后台保存图片失败，改为同步保存`（`BitmapFrameDecode.get_InternalMetadata` 抛 `InvalidOperationException`） | 剪切板取回的 `BitmapFrame` 内部仍绑定 UI 线程，即便 `Freeze()` 也无法在线程池访问 | 新增 `ImageCacheManager.Normalize()`，先在 UI 线程拷贝成纯像素位图再后台编码；新增自检断言防止回归 |

> 该问题由用户真实使用日志发现，功能上有兜底（回退同步保存）所以未表现为崩溃，
> 但每次截图都会阻塞 UI 线程数百毫秒。

**实测确认**：修复后用户在真实环境又复制了 3 张图片，日志中 `[WARN ` / `[ERROR` 级别记录为 0
（修复部署前最后一次告警时间为 23:40:06，部署后无任何告警）。

### 新增

- 窗口尺寸记忆：面板缩放后写入 `settings.json`（`WindowWidth`/`WindowHeight`），下次按原尺寸贴右下角弹出。
- 自检项 46 → **47**。

### 文档

- 新增 `resources/` 开发资料库（本目录）：架构、源码导读、依赖、坑点、数据模型、
  Win32 集成、构建测试、后续路线。
- 项目 `README.md` 补充三种发布方式的体积/内存对比。

---

## v1.1.0 — Win+V 接管（低级键盘钩子）

需求来源：[09-Win+V拦截需求与方案.txt](09-Win+V拦截需求与方案.txt)（需求方提供的 Win+V 低级键盘钩子开发方案，原文存档）。

### 新增

1. **`Services/KeyboardHookService.cs`**：`WH_KEYBOARD_LL` 全局钩子拦截 Win+V。
   - 检测「Win 按住 + V 按下」（`GetAsyncKeyState` 判定左右 Win）→ 注入哑键
     `VK_NONAME (0xFC)` 重置 Shell 的 Win 键状态机 → `Dispatcher.BeginInvoke` 异步派发唤起
     → `return (IntPtr)1` 把 V 键从输入流里摘掉（系统剪切板历史与目标窗口都收不到）。
   - 工程约束：委托存字段防 GC 回收崩溃；回调内只做判定与注入，绝不阻塞
     （`LowLevelHooksTimeout` 默认 300ms，超时会被系统强制卸载钩子）；
     长按 V 的自动重复用 `_winVHandled` 抑制；异常一律吞掉，不允许穿到原生栈。
2. **设置项 `EnableWinVHook`（默认 true）**：设置页新增「Win+V 拦截」开关，托盘菜单同步新增同名勾选项；
   开关由 `AppHost` 统一落到钩子的 `Start()` / `Stop()`（订阅 `SettingsService.Changed`），
   关闭后 Win+V 立即交还系统，`Ctrl+Shift+V` 热键始终可用（两条路径并存）。
3. **`PasteService.ForceForeground()`**：强制抢占前台焦点（先 `SetForegroundWindow`，
   失败则把当前线程挂到**当前前台窗口所在线程**的输入队列再抢，最后 `BringWindowToTop` 兜底）。
4. **自检项 47 → 50**：新增「Win+V 低级键盘钩子」分组（挂载 / 运行状态 / 卸载）。
5. **`tools/winv-test.ps1`**：真机端到端验证脚本（`intercept` / `hotkey` 两种模式）。

### 开发过程中修复的问题

| 现象 | 根因 | 修复 |
| --- | --- | --- |
| 面板被 Win+V 唤起后浮在最上层，却打不了字（焦点仍在原窗口） | 本进程不是"最后接收输入的进程"，前台锁定驳回 `Window.Activate()`；`ActivateWindow` 的 `AttachThreadInput` 兜底对**自己的窗口**无效（目标线程 == 当前线程，直接返回 false） | 新增 `PasteService.ForceForeground`：挂到**前台窗口线程**再抢焦点；`MainWindow.EnsureForeground()` 以 `GetForegroundWindow()` 实际值判定，并在聚焦回调里复查一次 |
| 抢焦点过程中面板刚弹出就被自己收起 | 抢前台前的 `WM_ACTIVATE(INACTIVE)` 触发了"失焦即隐藏" | 新增 `_showInProgress` 标记，显示流程内忽略失焦隐藏，延时聚焦回调里解除 |
| 按住 V 时面板反复开关 | 键盘自动重复每次都触发唤起 | `_winVHandled` 标记，V / Win 抬起后才允许下一次拦截 |

### 验收

- `--selftest` 50/50 通过（退出码 0），build 0 警告 0 错误（Debug 与 Release 各构建一次）。
- `tools/winv-test.ps1 -Mode intercept`：面板可见 + 取得前台焦点 + 无其它新增顶层窗口 + 再按一次收起 → PASS。
- `tools/winv-test.ps1 -Mode hotkey`：`Ctrl+Shift+V` 路径回归 → PASS。
- **真机验收（用户实机，2026-09-19，Release 构建 v1.1.0）**：按 Win+V 时系统原生剪切板历史**不再弹出**，
  改为弹出本面板；其余主体功能（监听、粘贴、拖拽、托盘、热键）回归正常。
- 单文件发布产物已重建为 v1.1.0（`publish\LightClipboard.exe`，61.71 MB，FileVersion 1.1.0.0）并复验：
  `tools/winv-test.ps1 -Mode intercept` 对**发布版 exe** 同样 PASS（面板弹出 / 取得前台焦点 / 再按收起）。
  修复前该目录仍是 v1.0.1 的旧产物，是最初"功能没生效"误判的直接原因。
- 排障提示：`keybd_event` 属注入输入，Windows 不把注入的 Win 组合键交给 Shell 的剪切板历史处理，
  所以"原生面板是否被压住"这一条自动化脚本无法对照，只能用真机键盘人工核对（已由上条覆盖）。
  脚本能自动核对的是：钩子是否命中、面板是否弹出、是否取得前台焦点、开关语义。
- 一个容易踩的坑（本次实际发生）：改完代码后若仍启动**旧目录里的 exe**（例如只 `dotnet build` 了 Debug，
  却运行 `bin\Release\...\LightClipboard.exe`），会表现为"设置页没有开关、Win+V 也没被拦截"。
  判断方法：看日志有没有 `[KeyboardHookService] 已挂载低级键盘钩子`，以及设置页「关于」里的版本号。
  重新构建：`dotnet build -c Release`（或 `dotnet publish -c Release` 刷新单文件版）。

---

## 实测验证记录（v1.0.1 发布时）

| 验证项 | 方式 | 结果 |
| --- | --- | --- |
| 核心链路 | `--selftest`（Debug 与 Release 各一次） | 47/47 通过，退出码 0 |
| 外部复制 → 自动收录 | 另起进程写系统剪切板（含 Emoji） | 日志出现新记录 |
| 全局热键 → 面板弹出 | 焦点在其它程序时发送 Ctrl+Shift+V | 面板弹出并取得前台 |
| 点击卡片 → 自动粘贴 | 真实鼠标输入 + WinForms 文本框目标 | 目标收到卡片文本 |
| 图片拖出 | 卡片拖到资源管理器 | 目录生成真实 PNG，日志 `已拖出内容: 图片 -> Copy` |
| 文件拖入 | 资源管理器文件拖进面板 | 新增 Files 记录 |
| 托盘图标 | 截取任务栏溢出面板放大 | 图标已注册显示 |
| 开机自启开关 | 点界面开关后读注册表，再点一次关闭 | 写入/删除均正确 |
| 窗口尺寸记忆 | 缩放到 520×560 → 隐藏 → 重启 | 尺寸保持 |
| 搜索 / 分页 / 失焦隐藏 / Esc | 真实输入驱动 + 截图 | 均正常 |
| 滚动条遮挡 | 三页面在 380×420 / 498×540 下放大截图 | 卡片与 Emoji 均未被压住 |
| 图片后台编码 | 用户真实使用（3 张图片） | 0 条告警 |

---

## 待办（未完成）

- 彩色 Emoji 渲染（方案对比见 [08-后续开发路线.md](08-后续开发路线.md#21-彩色-emoji-渲染用户已两次提及值得优先评估)）。
- 富文本（RTF/HTML）支持。
- 可配置快捷键。
- 单元测试工程。

---

## 版本号约定

`LightClipboard.csproj` 的 `<Version>` 决定「设置 → 关于」显示与 exe 文件版本。
发布流程见 [07-构建发布与测试.md](07-构建发布与测试.md) 第 5 节的检查表。
