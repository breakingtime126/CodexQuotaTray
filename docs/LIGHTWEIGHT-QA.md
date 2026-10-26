# 0.2.27 additional Windows acceptance (not cloud-executed)

本轮新增响应级速度与有界日志追赶。云端验证覆盖逐响应更新、短回复、工具等待、迟到副本、冷读补扫、旧日期发现、超长正文及取消；Windows 实际写入/轮询交互仍未实测。以下较早条目保留为历史验收背景；当前速度口径以 OUTPUT-SPEED.md 为准。

- 新回合未完成或另一会话仅开始时，最近有效完整历史均速应连同其模型、档位、完成时 Fast 设置一起保留，明确标「上次 HH:mm」，不能当成新回合的速度；从未获得完整证据仍留空。
- 定向旧回合的取消、矛盾或身份边界应撤回旧数值；后续设置变化不能把新 Fast 标记贴到历史完整均速。
- Desktop 0.2.21 已在一个用户安装中手动验证 Ready/退出0。本版冻结其后端代码；该结果不替代其他机器的兼容性验证。

- Desktop 验证预算失败应单独显示 desktopLimit、hashProvider、imageBytes/hashBytes、hashMs/phaseMs/verificationMs；不要把资源上限解释为签名拒绝。Windows Framework 生产哈希应为 WindowsCng；不允许提供程序失败后跳过哈希。
- FrameworkSmoke 新增生产哈希工厂 CNG 类型与空字节/abc SHA256 已知向量检查。云端只编译该 Windows 测试，未执行，也不要求用户另行运行。

- Desktop 发现失败应显示固定 API 步骤/安全位置类别及实际数字码；不能从 Discover 5 单独推断公司策略。当前发现不要求逐级打开全部祖先目录，仍拒绝重解析路径并验证 Store 包与签名。
- 在查询开始、异常结束、刷新和切换时打开诊断：当前轮与最近完成轮明确分开，每轮来源、策略、后端、协议和终态属于同一轮。过期回调不能写入新轮；入口异常不得伪造已经到达额度阶段。
- 本修订未在 Windows 实机执行，实际受保护路径权限兼容性仍未验证。

- Default Auto retains its policy checkmark; only its current valid successful source has an independent filled dot. Failure, expired quota, switching and cleanup failure remove the dot; a discovered candidate alone never gets one. Manual mode only checks its chosen policy.
- The compact menu retains refresh, source, startup, diagnostics, about/licenses and exit; quota/reset/model readouts stay in the hover card. Optional settings-warning actions remain accessible.
- On first upgrade from v1 settings, confirm Auto migration; fixing a manual source again persists that strict choice.
- Auto failure order is CLI, VSCode, Desktop; successful later refreshes use only its winner. Service rate limits stop the cycle.
- Rapid policy changes while reading must wait for owned cleanup and reject old results/diagnostics.
- At either side of a two-screen vertical taskbar seam, the source submenu stays inside the tray/parent monitor work area, choosing an inward side. Check 100/150/200% DPI, negative-coordinate monitors, keyboard/mouse open/close and reopening.
- CLI and VSCode were user-verified in 0.2.17. Desktop package Start/5 is historical evidence. The new existing-runtime route needs Windows validation; absent or unverified components must report a closed discovery failure, not manufacture a copied executable.

# Lightweight Framework Windows QA

Cloud build target: net48 / x64. The production EXE embeds Core plus its private managed dependencies and third-party notices. Windows 10 1903+ and Windows 11 include the required .NET Framework 4.8; it does not use an installed .NET 8 runtime.

Before replacing the running app:

1. Put **only** CodexQuotaFrameworkSmoke.exe into a fresh empty test folder. Do not copy any DLL, config, runtime, or other output files beside it.
2. Run it and capture stdout/exit code. Expect CLR v4, embedded assembly checks, PNG/ICO checks, hover/cooldown checks, a self-hosted fake-server read, and bounded cancellation. Both the fake server and its descendant must exit, including when the server exits before the descendant. It never invokes real Codex, accesses credentials, or uses the network.
3. Keep the old running executable as a backup before replacing it. Exit through its own menu. Place only the new CodexQuotaTray.exe at the same stable location and run without elevation.
4. Confirm single instance, known quota or an honest diagnostic, tooltip percentage/reset (last-success time and reason only on stale/error), right-click refresh/startup/exit, hover entry coalescing, and bold digits/ring.
5. In 0.2.17 select each source independently: Codex desktop / CLI / VSCode extension. Confirm exactly one checkmark, persistence after restart, immediate refresh, and no other-source fallback on discovery/start/handshake/quota failure. Rapidly switch during a pending read and confirm old quota/diagnostics never reappear. Current-user OpenAI.Codex AppX detection is best-effort. Never change WindowsApps permissions, copy auth.json, or force login to make the test pass.
6. Confirm there is no need for an extra DLL or .NET 8 installation, and that the original current-user startup opt-out/entry is preserved. This test is not permission to change system settings or create new login grants.

The net8 console regression suite is separate and does not prove Framework-native process behavior. Windows QA is required for this release path. No Windows visual pass is claimed by the cloud build.

## Reset-card expiry and overflow hover acceptance (2026-10-08)

- Use the existing signed-in Codex account, without reading/copying authentication files or redeeming any card. Compare the actually returned card count and local expiry dates with the official UI; do not seed screenshot dates or assume three cards
- Keep the existing Pro weekly or Plus 5-hour/weekly quota-reset rows, then append one expiry line per returned available card. Confirm duplicate dates still produce separate rows for separate cards, next-year dates include the year, no-expiry and missing-date cases remain distinct, and no verbose backend title/description is repeated
- Use fixtures for zero, one, many, consumed, count-only, capped detail list, malformed and past-expiry scenarios. Missing summary must say not returned, never zero; incomplete details must show how many dates were not returned. Do not change the real account to force these states
- Up to 127 UTF-16 units must still use the ordinary Shell tooltip. Above that threshold, check exactly one nonactivating overflow popup with every line available; no truncated native tooltip behind it. Test async transitions short→long→short while the pointer stays over the icon
- Check real mouse departure, crossing from icon to popup across taskbar padding, Escape, outside click, icon click, right-click menu, double-click refresh, foreground change and exit. A dismissed popup must not reopen until departure/reentry; hovering a scrollbar must not steal focus or trigger repeated refreshes
- With the mouse on another monitor, use Win+B and keyboard selection on this tray icon. A long readout must appear on the icon's monitor, survive unrelated pointer polling and an asynchronous short→long update, and close on Shell selection departure or Escape; moving the pointer onto the icon switches back to ordinary mouse lifetime
- For content taller than the work area, drag/click the scrollbar to reach the last row. Wheel scrolling on an inactive window depends on Windows input routing, so test it without changing the user's system preference. Mixed DPI, negative monitor coordinates, taskbar seams, overflow/auto-hide tray and Explorer restart need real Windows checks
- No Windows popup or account-data validation was run for this cloud build. Pure row/lifetime/geometry tests and cross-compilation are useful coverage, not a substitute for these acceptance steps

## Own-monitor menu placement acceptance (2026-10-08)

- With a right-edge vertical taskbar at a dual-screen seam, open the menu using a real right click. Its client/window rectangle must be wholly inside the icon's monitor work area, inward to the left. The neighboring monitor having more free space must not change this
- Mirror that setup: a left-edge taskbar on the right-hand monitor must open right. Top and bottom taskbars must open below/above respectively. Also test a monitor left of or above the primary (negative coordinates), portrait screens and offset monitor origins
- Repeat first-open, close/reopen, Win+B keyboard invocation with the pointer on another monitor, overflow tray and auto-hide taskbar. Position is determined by the actual Shell icon rectangle; only when that query fails does the implementation use the current pointer's screen
- Test 100%/125%/150%/200% mixed DPI and moving the taskbar between screens. Inspect the final menu window bounds, after text/visibility updates in Opening and after a quota refresh while the menu remains open. Physical coordinates are not multiplied by DPI again
- A menu larger than a small work area is capped. Verify vertical scroll buttons/navigation and horizontal clipping at extreme accessibility font sizes. A visual drop shadow is not part of the menu's window rectangle
- Confirm the established first-click focus path, outside-click dismissal, Escape, item selection, repeated activation and Explorer-restart recovery. Display/taskbar setting changes close a current popup so reopening uses current screen geometry
- Portable geometry tests and a cross-compiled EXE are not Windows visual acceptance. This update was built in Linux; native Windows behavior, mixed-DPI rendering and the screenshot's exact desktop layout still require real Windows testing

Implementation references: [Shell icon rectangle](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicongetrect), [monitor from rectangle](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromrect), [work-area/negative coordinates](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-monitorinfo), [DPI changes](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged).

## Portable-path registration recovery acceptance (2026-10-08)

- Run only the single EXE in one ordinary user-writable directory, exit normally, then move only the EXE to a second directory. Neither location requires an adjacent DLL, config, README, checksum file or license folder. Existing Codex installation/login and Framework 4.8 remain prerequisites
- Existing-path startup should log `TrayRegisteredGuid`. If Windows rejects the original GUID at the changed path, expect `TrayRegisteredWindowId`; the application does not reset Shell preferences or pretend it can force the icon out of overflow
- In either registration mode, inspect actual icon bounds and the same-monitor menu, hover, double-click, outside-click/Escape, exit cleanup and Explorer restart. GUID_NULL plus HWND+ID must be used for bounds lookup in fallback mode
- Do not deliberately damage Shell or alter its registry cache to simulate failures. Portable injected-Shell tests cover both registrations rejected, transient Modify/Add failure, recovery of an existing icon, retry gating and cleanup. Actual Windows acceptance remains necessary
- If both identity registrations fail, the initial attempt is followed by retries after 1/2/5/10/30 seconds. Ordinary Render/animation cannot bypass the backoff. At exhaustion, one visible diagnostic asks the user to dismiss it, then exits this app normally; it does not terminate unrelated Codex processes
- Moving the EXE and running it at the new path automatically updates an existing enabled entry only when its exact old command remains owned by this app and the only change is the executable path. The menu stays checked without another click. Explicit opt-out/external removal, foreign entries, and Windows-disabled or unknown approval remain untouched. Changed CLI arguments still require explicit updating
- `status.log` records only fixed tray diagnostic codes alongside existing quota status codes, without raw paths, exceptions, account details or Shell payloads

Microsoft documents the moved-unsigned-binary GUID restriction and the supported alternate identity in [NOTIFYICONDATA troubleshooting](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw#troubleshooting) and [NOTIFYICONIDENTIFIER](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyiconidentifier).

## Vector/motion/menu update acceptance

- Inspect the 32px native icon in the confirmed 200% session and compare 97/68/88/100. Use native and remote1:1 screenshots, not only a enlarged preview
- First real right-click after launch must close on blank desktop/taskbar or another application; test Escape, repeated reopen, pinned and overflow trays, and Win+B keyboard invocation
- Do not treat a synthetic WM_CONTEXTMENU callback alone as acceptance; Windows may deny foreground activation and the tool should then close that attempted popup
- Hover refresh preserves tooltip text; successful result has one short ring glint, unchanged stable digits, and no continuing idle animation. Error/stale data must not play success motion
- If Windows client-area animations are disabled, static rendering is intentional; do not change that system setting to force this test

## Plan windows and severity acceptance

- Plus with both windows selects 5 hours; Pro with both selects weekly. Actual API response governs available windows, not assumed subscription entitlements
- Confirm Plus shows both 5-hour and weekly remaining/reset, explicitly identifying missing data. Pro shows weekly only and hides any returned 5-hour quota; absent weekly means unknown. Unknown/other-plan fallback must be labeled, never silently called weekly or 5 hours
- Verify 9% red, 10% amber, 30% amber, 31% green in code fixtures. Failed/stale values use gray dashes and no success glint. At0 the neutral empty track intentionally has no fake progress arc
- Use self-hosted fixtures for unavailable plan variants; do not change the user account or start authentication merely to test another tier

## Minimal native tooltip acceptance

- The quota portion for fresh Pro-family shows exactly two lines: weekly remaining and local reset date/time. No Codex/plan/rule/refresh-cadence/update-time repetition; reset-card rows follow separately
- The quota portion for fresh Plus shows four lines: 5-hour value/reset, then weekly value/reset. Each missing window is explicitly unknown, never0%; absent reset says unknown
- Expired/failed data carries its stale marker, last-success date/time and concise reason. A reset reached in one window must not stale another otherwise valid window
- Native Shell tooltip remains in use when the complete text fits 127 UTF-16 units; check actual200% DPI hover and subsequent async update on Windows. Overflow follows the reset-card acceptance section above

## 0.2.0 原生悬浮卡与版本接管（本轮云端未运行）

1. 首次从 v6/0.1.0 升级先退出旧版，再打开新 EXE。检查真实字号、半透明边缘与阴影；确认没有重复系统 tooltip、Pro徽章或示例数据。
2. Windows 10/11、100/125/150/200%缩放、高对比模式、远程会话；分别检查正常、未知、低额度、错误/过期、Plus双窗口、Pro周窗口与卡片未返回。
3. 四边任务栏、双屏接缝左右竖任务栏、负坐标、混合DPI和隐藏图标区：卡片始终在图标所属工作区，窗口尺寸变化后仍完整。
4. 首次悬停、快速离开/重入、跨到卡片、空白处点击、Esc、Alt+Tab、右键卡片/图标、连续右键重开、Win+B键盘选择、Explorer重启、显示/DPI/时区变化与退出。卡片不抢焦点，不留下孤立浮窗。
5. 长列表滚轮、细滚动条拖动、拖到卡片外部后释放，检查最后一行可见且不意外关闭/卡住；异步更新、卡数变少、DPI变化后滚动位置合理。
6. 测试用时间边界：剩余恰好7天黄色、24小时红色、到点已到期；旧快照灰色且有上次成功时间，不以日期当实际剩余时长。
7. 从0.2.0开始，同版二次启动不重复；数值更高版本双击后旧实例先取消/回收自己查询再退出，新版再出现；旧数值版本不可替换新版本。不同目录、带CLI参数、临时读取中与快速并发启动分别验收。
8. 接管不能覆盖旧EXE文件或强杀进程。旧版不支持握手、IPC无响应/失效、用户/会话不匹配时应有说明，原实例保留。保留显式自启关闭；原来启用时随新位置迁移。

9. 0.2.1浅色菜单：首次右键打开即点击桌面/其他应用应关闭；Esc、点击项目、快速重开、键盘上下选择、开机启动真实勾选/不勾选显示。大/小DPI首次打开及跨屏重开，确认行中文字/勾选垂直居中、边角不裁字、长菜单在本屏工作区且可导航。阴影由系统决定，未强制改系统视觉设置。

## 0.2.2 登录身份回归

先在新空目录运行本版CodexQuotaFrameworkSmoke.exe：应通过native TokenLogonSid、exact production single-instance constructor等新增检查，再完成其余模拟协议/取消检查，退出码0。它不启动真实托盘、不改自启、不读取账号，无需关闭现有应用。随后按获准流程运行正式0.2.2，验证不存在旧实例时正常启动；有旧v5/v6才需要先退出旧版。0.2.0/0.2.1的错误与托盘缓存无关，不要用清缓存/删注册表代替升级。

## 0.2.3 小字清晰度与紧凑尺寸

在用户重新授权本机验证或自行运行后，确认96DPI三卡Pro约300×342/Plus300×350（含阴影），正文与右键菜单接近。检查中文小字、100%后缀、跨年日期、缺失/过期槽位和两列分隔；125/150/200%移动重开应重新画字而非放大旧图片。实际Windows字体清晰度必须看实机，不能用Linux布局图代替。可独立运行新版FrameworkSmoke测试离屏GDI alpha/尺寸和生产初始化；它不启动托盘、不改自启、不读账号。

## 0.2.7 系统主题、底部TPS与模型回查

Windows待验收：切换应用浅色/深色后重新悬停和右键；高对比开关优先覆盖配色；注册表偏好缺失、非整数或访问失败时安全回退浅色。检查100/125/150/200%及跨屏：Plus双额度、零卡/长列表、长年秒级日期、低额度红/黄、错误/过期灰、未知/旧TPS和长模型名。顶部「额度」刷新时刻与底部「样本」完成时刻应不同且各自真实。

模型回查只用同会话同轮的历史元数据，最多4MiB，计入既有增量预算并缓存终点。可在有可靠计时但启动tail丢失turn_context的合成日志中确认：先未知后补模型，TPS不变；中途设置冲突/损坏/跨轮不猜。不要为了验收读取或上传auth/正文。实际Windows日志跟随与主题交互本轮云端未运行。

## 0.2.8 长轮次模型与单行布局

本轮云端已用真实匿名元数据形状复现0.2.7的4MiB截断：同轮context距usage4,311,873字节，日志末尾距context约4.62MiB。新版渐进回查按原有IO预算继续，界面应先显示「模型回查中」再补真实模型/档位，TPS不改变；达到256MiB安全上限明确标记，历史缺失/冲突不猜。重复副本、取消、截断和持续追加用合成回归验证，不读取真实正文来制作fixture。

Windows待验收：实际较长turn中模型补回；已确认历史effort显示中文；额度单行相对/绝对时间、Plus两条全宽reset、每卡天+小时及完整秒级日期、跨年numeric日期、长模型省略和旧TPS「上次」。检查100/125/150/200%及高对比。该版本没有在Windows运行，云端像素检查不证明原生字体或交互通过。

## 0.2.9 后端来源与连接诊断

- 仅安装官方桌面、本地VSCode扩展或独立CLI的三种环境分别读取已有订阅登录；不能用一种来源的通过宣称全部通过。远程/WSL扩展主机不在本地发现保证内。
- 遇技术故障应在40秒共享截止前有限尝试下个候选；账号未登录/APIKey/无额度/限流应保持真实终态。显式路径不自动换源。
- 若额度始终失败，打开右键「查看连接诊断…」，窗口保持到关闭，截图应有来源、后端数字版本、Start/Initialize/RateLimitsRead阶段、数值码和固定错误类别，不含账号、路径或服务原文。RoutingDiscoveryFailed不推测为403，只有官方明确401固定字符串归类为RoutingUnauthorized401。
- Plus应有5小时和周额度，无重置卡或卡字段缺失不能让额度读取失败；Pro正常与Plus失败的对照不能排除来源/版本差异。
- 新版Windows实际后端进程/账号/窗口交互尚未在云端运行，合成协议通过不代替同事实机反馈。

- 正常查询只发送initialize、initialized、account/rateLimits/read；协议测试必须断言不存在account/read。套餐只取quota响应，缺失不拿本机配置猜。

## 0.2.10 管理员启动版

- 验证EXE嵌入清单为requireAdministrator、uiAccess=false，版本0.2.10.0；普通启动由Windows进行UAC，不使用静默提权。
- 取消UAC不应启动；同账号升级可以尝试现有版本交接；系统策略/跨权限身份验证拒绝时须手动退出旧版，不强杀、不降低IPC身份校验。若输入其他管理员账号，可能使用不同用户配置和Codex登录，不承诺跨账号接管。
- 普通Run自启菜单不可用，初始化/检查/勾选操作都不得读写原有注册状态；没有新增任务计划或服务。旧项若指向另一份旧版，仍可能启动旧版。
- Codex或Node子进程继承管理员权限。此选项不是Start(5)根因修复证明，也不是权限绕过。
- 本次仅云端测试/编译/嵌入manifest校验；UAC界面、登录启动和跨完整性级别握手未在Windows实机运行。

## 0.2.11 只读模块原型

- 云端合成回归和编译范围以 VERIFICATION.md 为准；不把编译成功当成 Windows 实机通过。
- Windows 后续可运行 FrameworkSmoke：只执行测试自己的合成子进程和临时目录，不加载真实认证或联网读取额度。检查原生目录句柄、文件校验、Job、System32优先加载和退出回收。
- 分别检查 file、Direct keyring、Secrets 已有登录；读取前后登录文件/系统密钥应保持不变。缺密钥不得生成；过期不得续期；宿主内存登录必须明确不可继承。
- 401仅同账户已更新令牌可重读重试一次；账户变化不得显示旧账户额度；重置明细缺失不冒充零张。
- 恢复普通用户自启，保留关闭/Windows禁用；升级已启用路径应迁移。高权限0.2.10如阻止普通进程接管，先由用户退出旧版。

## 0.2.12 网络兼容修正

- 在已有正常登录与网络配置下刷新额度；不要求更改代理、关闭证书校验或重新复制凭据。
- 若仍失败，查看连接诊断的固定 errorKind：DnsFailure / ConnectFailure / ProxyConfiguration / ProxyConnectFailure / ProxyAuthentication / TlsFailure / CertificateConfiguration / Timeout。截图不应含代理地址、证书路径、令牌或原始服务文本。
- 确认额度获取不再启动完整 Codex CLI；模块不执行登录续期或凭据写回。
- 云端未执行 Windows 实机网络验证。用户验证前不能把恢复默认网络语义等同于所有网络配置已验证。

## 0.2.13 网络 bootstrap 与计时可靠性

- 唯一已选 CODEX_HOME/.env 的网络 allowlist 可覆盖继承环境；不会改 home、登录或任意应用变量。不存在文件可正常继续；无效文件显示 NetworkConfiguration，不静默直连。
- 已有本地 Desktop/CLI/扩展的同一 home 登录可复用；WSL、远程扩展主机或仅宿主内存登录不能被本地 Windows 模块自动发现。
- 底栏保留模型与档位，当前 rollout 一律显示计时未核验，不显示旧 tok/s；辅助时间明确为记录/通知时间。检查浅深色、长模型、Plus 与混合 DPI。
- 无用户机器、真实登录或 Windows 网络验证。本次生产 builder 的代理测试仅用本地假代理、隔离 home 和合成数据。

## 0.2.14 安全诊断候选

- 本版仅增强证据，不应宣称ConnectFailure已经修复；不改现有网络、登录或TLS校验，不增加外网探测。
- 右键诊断窗口显示最近两次额度读取及当前本地模型元数据状态。网络字段只能是固定标签、布尔值或数字，不含URL/代理地址/凭据/路径/原响应。
- route=Unknown表示匹配点尚未被调用，Direct表示实际选了直连而非成功；NO_PROXY导致绕过时单独标记。HTTP code和OS osCode不能混为一谈。
- 模型未知应能区分NotFound、Boundary、Conflict、PendingHistory或HistoryLimit；诊断打开不触发日志扫描，不输出会话ID、原始模型值或日志内容。
- 本次无Windows实机、真实登录或用户网络验证。

## 0.2.16 system proxy acceptance (not run on Windows)

- Automatic detection, PAC-present and static-present are independent booleans.
- PAC -> enabled auto-detection -> static/bypass; Unavailable -> environment/direct.
- System Direct ignores environment proxies; NO_PROXY applies to environment fallback.
- Selected-route connection/HTTP/TLS/auth errors never advance to another route.
- Existing explicit settings only; no system changes or automatic domain authentication.
- Verify real company result without assuming 12180 or 10013 has been eliminated.

## 主题验收

- 主题子菜单跟随系统/浅色/深色互斥勾选，默认跟随系统，重启恢复有效保存值。
- 切换后菜单与悬浮卡同步使用已有色板；仅跟随系统受系统主题变更影响。高对比度优先保持可读性。
- 配置缺失/无效回默认；保存失败提示仅本次生效，不显示已保存。
- 主题子菜单复用同屏边界；在屏幕缝、高DPI及快速反复开闭时不得跨到邻屏。

## 启动时尝试显示托盘图标

- 默认每进程启动仅一次有限尝试，无菜单开关或额外配置；失败静默，可手动从托盘箭头拖出。
- 仅 Windows 10、精确自身图标；明确隐藏时不得改为显示。不循环重试、不骚扰提示。
- 关闭或版本交接必须取消并清理隔离 helper，不能让 COM 卡死主 UI。
- 合成身份/协议/故障测试与交叉编译不等于 Windows COM 或实际图标常驻已通过。
