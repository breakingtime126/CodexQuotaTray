# Latest verification: 0.2.10 (2026-10-12)

- User-requested administrator variant: embedded nativeRT_MANIFEST id1 verified requireAdministrator,uiAccess=false,PerMonitorV2; release version0.2.10.0. No scheduled task, service, UAC bypass or permission changes added.
- Ordinary Run startup is visibly unavailable. Initialize,Inspect,SetEnabled(true/false) short-circuit without touching storage; four throwing-storage tests prove existing registrations/preferences are preserved, not silently migrated or removed.
- 830/830 portable regression tests passed (29.49s). Release net48/net8Windows solution compiled with0warnings/errors. Independent static review found no definite handoff blocker; existing identity checks remain unchanged.
- Windows UAC, alternate-account profile behavior, cross-integrity handoff and actual Desktop/Start(5) outcome were not executed. Elevation does not establish the root cause or promise to fix that error; child Codex/Node also inherit elevation.

The following sections retain earlier release history.

# Latest verification: 0.2.9 (2026-10-12)

- Exact-size synthetic cold-start regressions first reproduced four intended failures. The anonymous metadata projection retained original source offsets and line lengths, with omitted bodies and three gaps explicitly filled synthetically:0.2.8 stopped Boundary after262,144bytes; the corrected production recovery reached the matching historical model/effort after2,097,152bytes. This is a metadata projection replay, not a complete original-rollout replay. TPS remained2275/90.969=25.0085.
- Independent replay confirmed the screenshot73.0 is one response:1270 output tokens, including27 reasoning, over17.389seconds. Both old and corrected parsers agree; no cross-session summation or reasoning double-count. A separate synthetic regression fixes an observed-completion-without-generated-item integrity hole; it did not cause this73.0 sample.
- Temporary exported evidence and both private replay directories were deleted after analysis. Source/tests contain only independent synthetic fixtures, no user log or copied response bodies.
- Normal quota protocol now sends onlyinitialize,initialized,account/rateLimits/read. Account information is never requested. Quota snapshot supplies the plan; missing plans remain unknown. Official desktop, CLI and localVSCode discovery/fallback are tested synthetically; the fixed diagnostics window exposes only safe source/version/stage/numeric-code/category.
- Final826/826 portable tests passed (29.40s). Release net48/net8Windows solution, FrameworkSmoke and preview projects compile with0warnings/errors. Independent read-only review found no critical blocker. The production painter rendered401 PNGs with0 unintentional text overflow across100/125/150/200% and light/dark/high-contrast states; all decode.
- The final production-renderer preview is synthetic Linux output with fallback fonts, not a Windows screenshot. New Windows backend installs, native diagnostics dialog, actual log following and monitor/input behavior were not run in this cloud release. No Plus-specific root cause on the colleagues' machines has been established.

The following sections retain earlier release history.

# Latest verification: 0.2.8 (2026-10-12)

- The confirmed anonymous Windows metadata shape places the matching model context 4,311,873 bytes before usage and about4.62MiB before EOF. This exact-distance synthetic regression failed against0.2.7 (702/703, the added root-cause case only), then passed with retained-cursor progressive recovery. No user log bodies were copied into fixtures.
- Final758/758 portable tests passed in UTC, Asia/Shanghai and America/Los_Angeles (28.28/28.26/28.23s). Coverage includes12MiB turns, fixed per-file/global budgets, cancellation, continuous append fairness, truncation, multiple files, explicit safety limits, historical timestamp guards and persistent duplicate-copy metadata conflicts. TPS arithmetic remains unchanged.
- Release solution (net48 and net8Windows), Windows FrameworkSmoke and production preview compile with0warnings/errors. Independent read-only review found no remaining blocker.
- Production geometry rendered401 synthetic PNGs at100/125/150/200% across light/dark/high-contrast, Plus/Pro, expiry boundaries, long dates, unknown/old TPS and long-list states. All images decode;100 high-contrast theme pairs are pixel-identical. Text-width audit has0 unintentional overflow (model ellipsis is deliberate).
- Actual reference crops and final single-line previews were visually checked. Visible width remains280px; three-card Pro/Plus including shadow+TPS are300×320 and300×354. Expiry days+hours and absolute seconds/year remain complete, using fixed-size numeric dates if needed. Historical effort is displayed in Chinese.
- This cloud task did not operate the user computer. Windows native startup, real log following, theme broadcasts, font appearance and monitor/input interaction were not run for this new EXE. Compilation/preview is not native Windows acceptance.

The following sections retain earlier release history.

# Latest verification: 0.2.7 (2026-10-12)

- Model metadata fix independently reviewed: bounded same-thread/same-turn historical lookup, cached success/miss/conflict, no TPS formula or timing change. No real user logs read by the cloud task.
- 690/690 portable tests passed independently in UTC, Asia/Shanghai and America/Los_Angeles (27.77–28.23s), including model recovery and compact relative-time presentation regressions.
- Release solution, net48 single EXE and Windows FrameworkSmoke compile successfully with0warnings/errors. Production painter rendered337 synthetic PNGs across100/125/150/200%, light/dark/high-contrast and long-list/state fixtures.
- Actual reference pixels and generated light/dark/Plus/expiry/TPS states were visually checked. High-contrast light/dark output is identical. Typography uses existing opaque native GDI tiles on Windows; Linux uses explicit fallback fonts.
- Windows runtime startup, theme broadcasts, native font appearance, actual log following, registry variants and monitor/input behavior were not executed in this cloud release. No native Windows interaction pass is claimed.

The following sections retain earlier release history.

# 验证记录

## 2026-10-08 0.2.5 小图标、轻标题与两位日

- 仅按本次确认缩小日历为约16×16、票卡为20×14（96DPI），物理像素对齐描边（去掉叠加半像素偏移，预览直线为单个实色像素）；简化票卡缺口/虚线，图标居中。重置卡标题保持13px和原深色，仅Bold改Regular
- 用户随后要求11月7日显示为11月07日：中文日期统一dd日，月份维持M不额外补零；重置卡HH:mm:ss、跨年规则、280px卡面/32px行高及其余运行逻辑保留，没有加入VSCode发现功能
- 已查看96/120/144/192DPI生产几何预览。**555/555回归在UTC/上海/洛杉矶全部通过（27.34/27.38/27.39秒）**，双目标、预览及Framework自检编译0警告0错误；本轮未操作用户电脑，原生字体/实际Windows交互仍不以Linux预览代替
- 最终EXE1,399,296字节，版本0.2.5.0，x64/CLR4与内嵌Core/JSON/许可验证通过；SHA256 `70632dbc19b4b8720b3902c11f6843a164b6555c9275c079709a4ff8217abff4`

## 2026-10-08 0.2.4 重置卡精确到秒

- 以已交付0.2.3源码归档（原提交5d980148，SHA256 `a18bd05f526844edda5773426adf491d28c091b567ff2cd724570be954312bc1`）为基线核对恢复，保留TokenLogonSid、原生GDI、紧凑Pro/Plus与版本接管等修复
- 重置卡显示电脑本地 `M月d日 HH:mm:ss`，跨年带年份；卡片、备用提示和可访问文字共用格式。官方Unix秒值/排序/状态/临期色不变，不给缺失日期或不过期卡编造时间。普通额度窗口的重置时间仍为原先HH:mm
- 宽280px、卡行32px不变；长跨年文本12px，其余13px。已查看96/120/144/192DPI长跨年+秒预览，完整文字无碰撞；增加原生GDI13px/12px长行fit自检（云端仅编译）
- **553/553回归在UTC、Asia/Shanghai、America/Los_Angeles全部通过（27.27/27.38/27.39秒）**；整solution双目标、预览、FrameworkSmoke与最终单EXE编译0警告0错误。新增精确秒、跨年/时区、相邻秒、到期边界、未知/不过期与尺寸回归
- EXE **1,397,760字节**，版本0.2.4.0，SHA256 `0dda9c442d89d36085743b3f5a56a41b7c5b14db5dcc61d4f6548d6c44bd0b5c`。本轮云端完成，没有访问用户电脑；Windows实际字形/自检运行不以Linux预览或编译代替

## 2026-10-08 0.2.3 紧凑尺寸与原生文字

- 已查看用户11:23实际截图：可见白卡约384×473px，与旧96DPI基准相符；没有把“大”直接断言为重复DPI缩放。用户要求接近右键菜单大小，并明确保留Plus双窗口，随后反馈卡片和菜单小字模糊
- 新外窗96DPI三卡场景：Pro **300×342px**，Plus **300×350px**；白卡宽280px。正文13px与菜单9.5pt≈12.7px接近；主数字从70px降为46px，Plus36px。Plus左右两区分别保留比例、进度、完整重置日期/时刻，高度不再翻倍
- 仅缩字号不能保证清晰：菜单切换为原生GDI TextRenderer；卡片使用同原生文字后端，在不透明24位RGB小图层画字，再按原像素贴入分层PArgb画面。主体乳白底不透明，仅圆角边缘/阴影透明，避免直接GDI写ARGB破坏alpha。绘制/测量使用同一按DPI取整的物理字号，坐标含滚动偏移并对齐整数像素；没有把旧文字位图再缩放
- 独立复核确认字层alpha策略、字号测量一致、双列/时间/百分号无布局冲突、资源释放正确；按建议在内容后重画边框，防止滚动字层覆盖细边。0.2.2 TokenLogonSid启动修复及接管/菜单关闭/自启逻辑未改动
- **547/547回归通过（27.49秒）**，新增精确尺寸、单次DPI尺度、日期无损拆分与多窗口行数检查。整solution双目标、FrameworkSmoke、预览工具和最终轻量EXE编译0警告0错误
- **74张PNG**全部解码，另验证关键预览主体alpha255；查看96/120/144/192DPI的Pro/Plus、100%、跨年日期、缺失、过期与长列表末尾。云端菜单示例320×319px与新卡片做并排尺寸比较。图片已明确标注Linux字体示意，Windows实际TextRenderer字形不由该预览证明
- FrameworkSmoke新增精确生产NativeHoverTextPainter/GDI路径的Pro/Plus多DPI离屏尺寸、alpha无空洞、数字有墨迹检查，保留0.2.2原生登录身份/精确构造器检查；仅编译，尚未在Windows执行。用户取消本机读取后没有重试；本轮没有宣称Windows文字实际已验证变清晰
- 最终EXE **1,398,272字节**，版本 **0.2.3.0**，x64/CLR4及嵌入依赖验证通过。SHA256：`b5310525fcbeba1aad896634cfd6e6e3f7772676fe6016c072ff3efbfddd17bf`

## 2026-10-08 0.2.2 修复无进程却提示旧版运行

- 用户真实启动0.2.1截图复现误导提示，任务管理器无托盘进程。Windows子任务核对F盘实际EXE版本/散列与交付0.2.1一致
- 根因：.NET `WindowsIdentity.Groups` 明确过滤登录SID。新增协调器却从该集合查找 `S-1-5-5-`，导致构造器身份初始化抛错，通用捕获错误地暗示有旧实例。依据：[Microsoft Framework实现](https://github.com/microsoft/referencesource/blob/main/mscorlib/system/security/principal/windowsidentity.cs)
- Windows受限与正常shell只读最小探测均确认：托管Groups无登录SID，而原生GetTokenInformation(TokenLogonSid)成功，登录SID实际存在。探测未显示/保存SID或令牌内容，也未操作账号、注册表或当前进程
- 当前/对端身份检查统一改用同一个原生TokenLogonSid helper。校验TOKEN_GROUPS单条登录组、属性、长度/指针边界；借用WindowsIdentity拥有的SafeAccessTokenHandle，分配缓冲始终释放。独立复核确认x64布局及调用点，无剩余阻塞
- 区分身份初始化、单实例协调、真正发现已占用实例之后的交接失败；前两个阶段不再声称旧程序正在运行。新增3项回归，**540/540通过（UTC，27.65秒）**；整solution双目标、FrameworkSmoke与最终EXE编译0警告0错误
- FrameworkSmoke直接链接正式SingleInstanceCoordinator.cs和LogonSessionIdentity.cs，执行真实helper/重复读、精确by-ref参数构造器及其初始化字段一致性。构造/Dispose不调用Enter/StartServer、不取得互斥锁所有权、不显示托盘、不改自启、不输出SID/token。此前的纯协议/编译检查没有覆盖这个Windows初始化边界，本版已补执行入口
- EXE **1,395,200字节**，版本 **0.2.2.0**，x64/CLR4与内嵌依赖验证通过。SHA256：`f04c048966b4ba472ba97e74c9534c1313d7aefa82ec2e0764a6b1a900d37eab`
- 自检EXE **1,344,512字节**；SHA256：`f98fd8e2c3d3b30a542e2103cf33afa409fec6780a7e76bd0ba67c303fdf9b29`。保存时已完成Windows根因API探测，但修复后完整生产启动/精确构造器自检及菜单交互仍待本机执行；不把云端构建当成运行通过

## 2026-10-08 追加 0.2.1 同风格右键菜单

- 用户确认追加统一右键菜单。保留原生ContextMenuStrip及所有操作，仅加入乳白浅色、圆角区域、柔和选中态、薄荷绿勾选与加大行距；请求标准系统菜单阴影，其可用性随Windows/主题/会话变化
- 首次菜单激活、SetForegroundWindow/Focus、AutoClose、Esc、重复打开代际保护、外部点击焦点归属及图标所属屏幕定位代码未改变
- 独立复核发现并修正WinForms每次布局重置Padding的问题（改用DefaultPadding并保留原生勾选栏）；额外行距后的文字/勾选垂直居中；Region随尺寸/DPI替换并释放。没有假称DWM圆角在自定义Region下生效
- 共享菜单生产绘制基础生成16张四缩放/勾选开关/高对比预览；合并悬浮卡预览共62张PNG全部解码。查看浅色选中、勾选/无勾选及高对比预览。菜单PNG布局为模拟fixture，不是运行中的Windows ToolStrip
- **最终537/537回归通过（UTC，27.74秒）**；整solution双目标、Framework自检、预览工具及最终轻量EXE构建0警告0错误。0.2.0阶段同逻辑已在UTC/上海/洛杉矶通过
- EXE **1,394,176字节**，版本 **0.2.1.0**，x64 / CLR4，内嵌私有依赖/许可；SHA256：`2059a7dd392518285807018a9021e5d289c26aa33cbeaf33c1b643498c72ad6d`
- 本记录保存时，Windows运行验证尚待执行。特别是net48的实际控件混合DPI、首次外部点击、圆角/阴影、键盘与真实菜单布局，不能以PNG/编译替代；后续实机结果另行补充

## 2026-10-08 iOS 悬浮卡与 0.2.0 版本接管

- 只使用用户确认的 CodexQuotaTray-iOS-hover-concept.png，已在实现执行器读取实际像素；排除用户随后说明下载不完整的另一张图。生产数据没有硬编码效果图中的百分比、卡数或日期
- 原生 GDI+ 分层卡：乳白轻微半透明、柔和阴影、圆角进度与清晰层级。无真正背景模糊/Acrylic；分层失败回退不透明圆角，高对比采用系统配色。托盘紧凑圆环/数字渲染器未修改
- Plus双窗口、Pro周窗口、实际卡数、部分/缺失明细、不过期/未知/已过期、旧快照均保留真实语义。额度>30绿、10–30黄、<10红，过期/错误灰；卡剩余≤7天黄、≤24小时红。注入时钟测试边界，定时唤醒精确覆盖阈值
- 非激活卡片、鼠标跨越间隙、离开/外部点击/Esc/右键/前台变化/生命周期关闭；键盘切入鼠标生命周期、长列表可见行绘制与细滚动条。独立复核修复了不透明回退缩放裁剪、拖动出界、Plus回退标题环及时区缓存问题
- 同一个生产 C# 绘制器生成 **45张 PNG**：10种模拟状态×4缩放、4张长列表末尾及高对比。全部Pillow解码通过，查看正常/Plus/红色临期/错误/未知/高对比与第10000行；另有三状态实际渲染合成图。Linux替代字体明确标注，**不是Windows截图**
- **537/537 回归测试在 UTC、Asia/Shanghai、America/Los_Angeles 分别通过（27.54 / 27.58 / 27.62秒）**。全solution双目标、Framework自检、生产renderer预览工具及最终轻量EXE构建均通过，0警告0错误
- 0.2.0新增本机当前登录会话受限的双向版本握手，更高数值版本等待旧实例自行关闭/回收读取并退出，随后取得原互斥锁；同版保留原进程，旧版不能替换新版。没有进程枚举/强杀/文件覆盖。保留CLI路径与自启明确关闭状态，启用路径按既有规则迁移。独立复核修正管道响应回执、重复启动后的端点复用与有限重建竞态
- 原v6/0.1.0不支持握手，首次升级必须手动退出旧版；未来发布须递增Version/AssemblyVersion/FileVersion，Library版本号不参与判断。相同版本换路径不会接管当前实例
- 最终EXE **1,389,568字节**；PE确认Amd64 Windows GUI、CLR v4.0.30319、Assembly/File **0.2.0.0**、Product/OriginalFilename匹配。Core/JSON/私有依赖及许可内嵌，未增加运行时外置文件。SHA256：`03df18638de5bae0f9811b08e8e6df533fb981973d0d0d1922b49b98eb635883`
- **未执行**Windows实际分层合成、系统字体、鼠标/键盘/滚动/焦点/混合DPI/Explorer交互、真实账号卡数据及命名管道接管；Framework自检仅编译，未在Windows运行。不能把模拟回归、同渲染器PNG、API签名检查或静态复核等同实机通过。详见 design-qa.md、IOS-HOVER.md 和 LIGHTWEIGHT-QA.md

## 2026-10-08 重置卡到期日

- 已查看用户重置卡截图像素。采用实际返回卡数，逐张追加简短日期；未把截图中的三张或日期写入实现，没有重新引入已取消的每日 token 功能
- 从本机官方 Codex CLI 0.159.2 在空白 `CODEX_HOME` 下生成非实验/完整协议类型，确认既有 `account/rateLimits/read` 默认包含 `rateLimitResetCredits`；公开源码交叉核对见 [RESET-CARDS.md](RESET-CARDS.md)。没有连接用户账号、读取凭据或调用兑换卡接口
- 解析权威数量、可空/截短明细、可用/兑换中/已兑换/未知状态、未知类型及日期；同日不同卡保持多行，重复 ID 不重复计数。日期使用系统本地时区并处理跨年、无有效期、缺失/无效及已到期。未返回与零张保持区分
- 保留原 Pro 周额度/Plus 双窗口的额度与重置行；正常短文字仍为原生提示。仅超过127 UTF-16字符时启用不抢焦点的溢出悬停浮层，所有行保留，超屏内容滚动，位置归属图标的屏幕。使用已有悬停计时器处理离开/Esc/点击/窗口切换，菜单、Explorer恢复及退出均关闭；没有全局输入钩子或新的空闲轮询
- 独立静态复核发现并修复两处边界：无额度窗口但有卡数据的成功时间独立保留；键盘触发且鼠标在别处的超长提示由Shell事件管理生命周期，不会被鼠标位置阻止。复核确认无剩余阻塞项
- **457/457 回归测试在 UTC、Asia/Shanghai、America/Los_Angeles 三个时区分别通过（27.56 / 27.85 / 28.22秒）**。新增89项覆盖0/1/多张（含1000张解析、10000行展示策略）、空/缺失/截短/非法数据、状态变化、日期边界、只读RPC顺序、127字符边界、卡独立时间戳、键盘与鼠标关闭策略、工作区限制
- 最终整方案（Core双目标、Windows UI双目标、测试/预览）及 Framework 自检均构建成功，0警告0错误；轻量 EXE 为 x64 Windows GUI / CLR程序集，大小 **1,338,368 字节**。嵌入 Core/JSON/私有依赖与许可资源仍存在，未引入外置运行文件
- **未运行**本版真实Windows进程/浮层、滚动/焦点/键盘、混合DPI或Explorer交互，也未验证真实账号返回的卡详情。Framework自检只构建、未在Windows执行；上述模拟与几何测试不替代实机验收。详细清单见 [LIGHTWEIGHT-QA.md](LIGHTWEIGHT-QA.md)

## 2026-10-08 换目录兼容与自启路径跟随

- 用户报告进程运行但图标缺失，并确认已更换 EXE 目录；截图只显示一个进程，不能据此证明运行版本或排除隐藏图标区。微软明确记录未签名 EXE 换路径可能导致固定 GUID 注册被拒，这与现象一致；本次没有取得该 Windows 实机的 Shell 返回值，不声称已直接复现该系统上的失败
- 确认上一版启动与图标注册代码未改动，菜单定位只在右键时启用；发现并修复原注册失败被静默忽略的缺口。优先保留原 GUID；只有首次注册失败才尝试 Windows 支持的 HWND+ID 身份。成功后固定身份，所有更新、取图标矩形、焦点返回、删除、Explorer恢复一致使用它，不清除系统托盘偏好
- 新注册策略使用可注入 Shell 调用的生产代码测试：GUID拒绝场景先出现3项失败，加入回退后通过。额外覆盖双身份失败、临时Modify/Add失败后恢复、重复删除、Explorer重启和回调版本设置，避免只重试Add导致原图标仍存在时无法恢复
- 注册失败的重试间隔为1/2/5/10/30秒，普通刷新或动画不能绕过；仍失败则显示一次提示，用户关闭提示后正常退出当前程序。日志只增加固定诊断码，无路径、邮箱、原始响应或异常文本
- 用户明确要求换目录后不再手动重新勾选自启：已启用且仍由本程序管理的旧路径会在新位置运行时自动迁移，菜单继续勾选。首次运行默认开启保持；显式取消、外部移除、外来同名项、Windows禁用或未知状态均保留。只改EXE路径，CLI参数变化仍需明确设置；迁移写入失败不假装成功，并覆盖部分写入后恢复
- 文件夹只需单 EXE；所需私有程序集与许可内嵌，日志和自启状态均存于当前用户位置，不依赖相邻README、散列文件、DLL、config或旧版备份。仍需既有Codex安装/登录与Windows内置Framework4.8
- **368/368 测试通过（27.54秒）**。相对于菜单修复版新增15项注册/重试测试与11项自启迁移测试，并更新原有移位测试。双屏接缝/负坐标/各方向菜单等26项布局测试继续通过
- .NET48轻量版、.NET8 Windows UI与Framework自检均成功构建，0警告0错误；独立静态复核参与检查注册身份、重试、清理与迁移条件。当前未运行Windows实机启动、移动目录、注册表或混合DPI视觉验收，验收清单见 [LIGHTWEIGHT-QA.md](LIGHTWEIGHT-QA.md)

## 2026-10-08 菜单跟随托盘所属屏幕

- 已查看用户提供的双屏接缝截图；用户确认竖任务栏位于两屏中间，要求菜单在自己所在屏幕弹出，而不是写死向左
- 原代码对右键直接调用 `menu.Show(anchor)`，没有把完整菜单限制到图标所属屏幕。新代码每次从 `Shell_NotifyIconGetRect` 取实际图标矩形，通过 `Screen.FromRectangle` 选择工作区；无图标矩形时才使用鼠标所在屏幕作为降级
- 新 `TrayContextMenuStrip` 在每次 `SetBoundsCore`（包括打开时内容变化和后续自动尺寸调整）先约束完整矩形，再交给 WinForms；显示后以及 DPI 变化后再次按实际像素尺寸校正，不重复乘 DPI。负坐标完整保留
- 左/右/上/下任务栏优先按图标所在的工作区保留条带向内展开；溢出托盘与自动隐藏按最近边界选择，极小工作区会限制菜单尺寸。菜单关闭后释放本次锚点，屏幕/任务栏设置变化后重开重新选择
- 保留既有焦点、空白点击/Esc关闭、代际保护与 Shell 焦点返回。圆环、数字大小、套餐窗口选择、简短悬停、刷新和开机启动代码均未改动；已取消的每日 token 功能不在发布源码或 EXE 中
- **342/342 回归通过（27.35秒）**，包括新增26项生产布局逻辑测试，覆盖接缝两侧、四边任务栏、负X/Y、上下排列与竖屏、溢出/自动隐藏、鼠标降级、键盘图标锚点、内容尺寸变化、96/120/144/192/240/288 DPI对应的实际尺寸，以及4000组固定种子随机边界场景
- .NET48轻量UI、.NET8 Windows UI和Framework自检均构建成功，0警告0错误。轻量版仍为单 EXE，约1.3 MiB；源码以当前稳定提交为基础，不包含本地取消功能实验目录
- 独立静态代码复核未发现阻塞项。实际 WinForms HWND 行为与测试几何逻辑不同：本次在云端 Linux 构建，**没有运行 Windows 实机/混合DPI视觉验收**，没有把编译或纯几何测试当成截图问题已实机确认。验收清单见 [LIGHTWEIGHT-QA.md](LIGHTWEIGHT-QA.md)

## 2026-10-05 精简原生悬停提示

- 已查看用户真实Windows截图：正常提示重复套餐、窗口、刷新说明。改为周额度2行（剩余+重置）；Plus5小时和周各2行，不在正常提示里重复套餐/规则/更新时间/频率
- 仅错误或过期才增加带日期的上次成功时间与简短原因；缺失窗口/重置时间仍明确未知，实际回退周期仍准确标注
- tooltip内部按单个窗口判定重置到期，避免5小时到期时把仍有效的周额度误标过期；图标/菜单状态规则与选择逻辑不变
- 新增30项精简提示测试，覆盖三种Pro变体、Plus及槽位交换、精确2/4行、时区转换、缺项、独立过期、异常恢复及127UTF16字符限制
- 正常和极端长周期的提示均保留重置日期；异常上次成功在所有压缩级别保留日期，不以截断掩盖信息
- 原生Shell tooltip继续使用；本次没有修改菜单、动画、配色、轮询、自启或协议/套餐选择。图标预览未改变
- **316/316 测试通过（27.10秒）**；.NET48和.NET8 Windows UI及Framework自检均构建成功，0警告0错误
- 云端文案与逻辑复核通过；新两/四行提示在Windows实际悬停与200%DPI的最终显示仍需替换后验证

## 2026-10-05 套餐窗口与额度颜色

- 本轮依据 CLI0.159.2 的 `app-server generate-ts` 生成类型（仅生成本地模式，不连接账号）读取 `account.planType` 和 Codex 额度快照 `planType`，按窗口时长而非 primary/secondary 槽位选择；对应官方 [Account](https://raw.githubusercontent.com/openai/codex/rust-v0.159.2/codex-rs/app-server-protocol/schema/typescript/v2/Account.ts)、[RateLimitSnapshot](https://raw.githubusercontent.com/openai/codex/rust-v0.159.2/codex-rs/app-server-protocol/schema/typescript/v2/RateLimitSnapshot.ts) 与 [RateLimitWindow](https://raw.githubusercontent.com/openai/codex/rust-v0.159.2/codex-rs/app-server-protocol/schema/typescript/v2/RateLimitWindow.ts)
- Plus 主图优先5小时，提示同时列5小时和周；Pro只显示周，缺周数据即未知。缺失/未知套餐及服务未返回项均明确标注
- 9%为红、10%和30%为黄、31%为绿；失败/过期统一灰色虚线，动效跟随当前真实读数的颜色
- 预览生成65张静态PNG、13个多尺寸ICO及21张轻扫光帧；Pillow独立解码87张PNG（含总览）和全部ICO尺寸通过，已查看配色总览
- 上一视觉版 Windows 自检23/23、32px / 192DPI读取Ready通过；用户手动确认右键正常显示、点空白正常关闭。远程会话关闭系统客户端动画时不显示扫光属于尊重系统偏好；未修改其设置
- **286/286 回归通过（27.12秒）**；包含11项颜色边界/动效测试和69项套餐解析、窗口选择、协议与提示测试。极端周期值的127字符预算回归已修正并通过
- .NET48与.NET8 Windows UI、更新后的Framework自检均构建成功，0警告0错误；新版自检新增Plus及三种Pro家族的选择/提示检查
- 解析/状态/提示/菜单及动效边界经过独立静态复核，无新增阻塞项
- 本轮套餐/颜色更新仍需Windows替换后验收；不把模拟协议覆盖称为多个真实套餐账号验证

## 2026-10-05 矢量数字、短动效与菜单加固

- 当前实机基础环境报告：上一轻量版在 Windows CLR4 自检21/21通过，实际额度读取为Ready；本次更新的原生视觉/菜单仍须另验
- 新数字使用有许可的 Inter4.1 数字矢量子集、灰阶采样、16px光学间距；100保持字高并横向压缩
- 成功扫光420ms、额度弧约200ms过渡；文字静止。约15fps，仅活动期间开动画定时器，结束立即停止；后台改为下一事件的单次唤醒
- 故障、未知、过期、零额度不产生假成功/满环效果，尊重系统动画偏好
- 已读实际截图并确定弹层为右键ContextMenuStrip；补充首次激活、关闭后WM_NULL、代际保护、拒绝后台孤立弹层及不抢走外部点击焦点
- **206/206 回归通过（26.25秒）**，包括24项矢量/单帧ICO、10项动画有限状态和6项菜单生命周期测试
- .NET48与.NET8 UI最终构建0警告0错误；Framework自检已更新到包含新动画ICO/空闲检查，但新版本Windows阶段尚待运行
- 字形与预览经独立视觉复核；代码生成图片不是Windows截图。原生32px在实际200%会话及真实右键首开/空白/Escape/重开必须验收

## 2026-10-05 轻量版

- 新增 net48 / x64 单 EXE，复用 Win10 1903+ / Win11 内置 Framework4.8 和已安装Codex，不打包或下载Codex，不新增登录流程
- 正式 EXE 实测 1,279,488 字节；Core及私有托管依赖作为程序集资源内嵌，PE元数据检查身份匹配，CLR标记v4.0.30319
- Core双目标和轻量UI构建通过，最终串行构建0警告0错误
- net8回归 **167/167通过（26.81秒）**，包含新增16项Codex桌面安装探测测试
- Framework专用单EXE自检已编译，覆盖真实Windows私有job/管道及根进程与后代回收，尚待Windows运行；云端未声称通过该运行阶段
- 原.NET8构建仍保留为兼容回退；当前默认交付轻量单EXE，不需要旧分包
- Windows替换与检查步骤见 [LIGHTWEIGHT-QA.md](LIGHTWEIGHT-QA.md)

## 2026-10-02 历史版本

日期：2026-10-02。云端构建环境：Debian 13 / Linux x64。本记录区分云端验证与 Windows 修复回传结果；本次云端同步未操作用户电脑。

## 已通过

- 使用微软官方 .NET SDK **8.0.425**、MSBuild **17.11.48**、目标运行时 **8.0.31**
- `dotnet build CodexQuotaTray.sln -c Release -m:1`：**0 警告，0 错误**
- `dotnet publish src/CodexQuota.Tray -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`：成功生成 Windows GUI x86-64 PE 可执行文件
- 独立控制台测试：最终结果见下方。测试不调用真实 Codex 账号，协议采用模拟响应
- 图标生成器自检：6 种状态 × 5 尺寸，共 30 PNG 与 6 个多尺寸 ICO
- Pillow 独立解码全部 PNG 及 ICO 帧；人工查看同一生产 C# 渲染器的 16/20/24/32 像素预览及明暗背景
- 协议实现与官方 app-server 文档、云端 Codex CLI 0.159.2 生成的类型核对
- 包含已观察到的 CLI 0.159.0 `primary=10080 分钟 / secondary=null` 形状回归样本；以窗口时长识别周额度
- 静态复查了凭据访问边界、异步 I/O、超时、进程清理；修正了服务端请求回复期间 StreamWriter 的 dispose 竞争并增加回归测试

## 测试范围

- 多桶 Codex 优先、旧字段兼容、primary / secondary 周窗口选择、非周窗口不冒充周额度
- 未知 / 0% 区分、越界比例裁剪、小数保守取整、可空/越界 Unix 秒数
- 未登录、API Key 和未知账号类型；退出登录/账号类型变化后清除旧值
- 失败时保留旧数据并明确过期；超过 10 分钟、重置时间已到标记过期；成功后恢复
- 127 字符 tooltip 限制
- 进入悬停一次、停留合并、离开重入、并发触发合并；30 秒成功间隔、失败退避、HTTP 429 冷却不被手动/悬停绕过
- 自启首次默认、取消后保留、外部移除、路径迁移、同名冲突保护、写入失败、Windows 禁用状态、Win32 参数引用
- initialize 响应先于 initialized、account/read 明确 refreshToken:false、无对话/模型 turn
- 并发请求 ID 分流、穿插通知、乱序响应、迟到响应、未知 ID、错误对象不泄露原文
- 超时、取消、EOF、非法 JSON、超大输入行、意外服务端请求只拒绝、不执行
- CLI 可执行路径带特殊字符时无 shell 展开；npm JS 入口由 Node 直接启动
- 真实 Linux OS 子进程测试：测试程序自身充当假 app-server，成功/取消/超时重试后回收所有测试子进程；未调用真实 Codex

## Windows 修复回传结果

- 用户要求去掉图标 `%`、保留圆环并放大加粗数字；云端同步的 TrayIconRasterizer 与 Windows 已安装修复源码一致
- Windows 10 上原有 149 项测试通过（27.48 秒），修复版框架依赖单文件使用已有 Microsoft.WindowsDesktop.App 8.0.22，启动后记录 Ready，菜单仍包含完整百分比
- 当地分包合并曾遇到 Get-FileHash 缺失，脚本已改为 .NET SHA256 / ZipFile；云端用模板和当前 ZIP 自动散列生成分包，禁止混用旧散列
- Windows 桌面会话在替换后断开，因此最终通知区清晰度、真实悬停提示与最终截图未确认；代码 PNG 预览不能代替该验证

## 仍需 Windows 实机验收

下面各项在云端 Linux **未运行**，不能用编译或 PNG 预览代替：

1. Windows 10 22H2 通知区域：启动、隐藏图标区、右键刷新、双击、退出、重复运行只保留一个实例、首次默认开机启动、取消后重启仍关闭
2. 100% / 125% / 150% / 200% 系统缩放、跨屏 DPI、浅/深色任务栏，特别是 16 像素粗体数字和 `100` 的可读性
3. 真实系统 tooltip 的换行、显示时长与本地时区
4. 使用该 Windows 用户已有 ChatGPT 登录获取额度；断网/重新联网、CLI 升级/退出、登录切换
5. Explorer 重启后的托盘恢复、睡眠唤醒后刷新、快速重复刷新与正在读取时退出
6. 横向/右侧竖向任务栏及隐藏图标弹出区：每次进入触发、停留不重复、离开重入触发、限流时悬停不绕过冷却
7. 首次启动自包含运行时解压以及未签名程序的安全提示

## 最终结果

**云端同步后 151/151 测试通过（26.82 秒）**。包含 20 项悬停/刷新调度测试、20 项开机启动策略/命令引用测试，以及真实模拟子进程的超时/回收测试。新增两项回归检查确认 16px 使用不含百分号的粗体字形，且 tooltip 保留百分号。Windows 完整视觉与注册表异常路径仍待实机确认。

预览是代码生成的 artwork，不是 Windows 截图。发布包未做代码签名。

云端受限环境的并行 solution restore 曾无具体诊断退出；采用 MSBuild 单节点 `-m:1` 后完整 solution 构建通过。构建脚本直接针对项目发布，不依赖并行 solution 构建。

修复后另验证 30 个 PNG、6 个 ICO 的全部帧独立解码；分包生成器以独立样本验证当前散列替换、逐字节重组与拒绝覆盖。放大字形在 16px 个别数字上会触及约 1 个圆环像素，这是与已安装修复版一致的极小尺寸取舍。
