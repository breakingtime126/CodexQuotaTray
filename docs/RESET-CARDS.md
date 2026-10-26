# 重置卡只读数据与显示

## 官方依据

本次从已安装的官方 **Codex CLI 0.159.2** 在空白 `CODEX_HOME` 下运行 `app-server generate-ts`，同时检查含实验字段和非实验版本生成类型；仅生成协议文件，没有连接账号、读取凭据、调用额度或兑换接口。`rateLimitResetCredits` 已包含在非实验响应中。

对应公开实现：

- [官方账号协议](https://github.com/openai/codex/blob/529cd6b8602f77ce946502d2e67df6b41bc795af/codex-rs/app-server-protocol/src/protocol/v2/account.rs)
- [官方重置卡列表读取](https://github.com/openai/codex/blob/529cd6b8602f77ce946502d2e67df6b41bc795af/codex-rs/backend-client/src/client/rate_limit_resets.rs)
- [官方明细映射与失败降级](https://github.com/openai/codex/blob/529cd6b8602f77ce946502d2e67df6b41bc795af/codex-rs/app-server/src/request_processors/account_processor/rate_limit_resets.rs)
- [官方 TUI 显示规则](https://github.com/openai/codex/blob/529cd6b8602f77ce946502d2e67df6b41bc795af/codex-rs/tui/src/chatwidget/reset_credits.rs)
- [app-server 文档](https://learn.chatgpt.com/docs/app-server#6-rate-limits-chatgpt)

## 响应合同

仍只使用既有 `account/rateLimits/read` 请求，不增加网络端点、不调用兑换方法。参数 `excludeResetCreditDetails` 省略或为 `false` 时默认读取明细。本程序继续省略，以兼容原请求。

`rateLimitResetCredits` 为 `null` 或 `{ availableCount, credits }`：

- `availableCount`：可用数量（整数）。明细可能受后台数量上限约束，以该数量为准
- `credits: null`：只知道数量；不是零张
- `credits: []`：明细请求没有返回可用行；若数量仍大于零，日期不完整
- 每项包括 `id`、`resetType`、`status`、`grantedAt`、`expiresAt`、`title`、`description`
- `expiresAt`：Unix 秒数；显式 `null` 意为无有效期。缺字段、格式错误或超界不能解释成无有效期
- `resetType`：`codexRateLimits` 或 `unknown`
- `status`：`available`、`redeeming`、`redeemed`、`unknown`。官方未定义 `expired`；已到期文案仅按 `expiresAt <= now` 推导

公开的 app-server 合同和后台客户端列表调用没有分页或游标；未找到可保证拿到截短列表剩余日期的只读机制。因此已知卡逐张显示，缺失部分只报数量，不伪造日期。

## 本程序规则

1. 仅显示 `available` 且类型是 `codexRateLimits` 的行；未知类型/状态不当作可用卡展示。已兑换或正在兑换的行不展示到期日
2. 按到期时间排序，明确无有效期与日期未知放后面；不同 ID 的同日卡保持多行。重复 ID 不重复计数
3. 最多使用权威数量所允许的明细；少于数量时简短提示「另N张日期未返回」。数量无效显示数据异常，不推断零张
4. 到期时刻显示 `M月dd日 HH:mm:ss`，跨年为 `yyyy年M月dd日 HH:mm:ss`，按电脑本地时区转换官方 `expiresAt` Unix秒；卡片与可访问/备用文字共用同一格式。明确无有效期为「不过期」；无效/缺失日期为「日期未知」；已到时间为「已到期」
5. 成功的新快照替换旧卡数据，包括字段缺失或零张；读取失败时与额度一起保留上次快照，并显示既有失败/上次成功提示；退出或不支持的账号类型清除卡数据
6. 日期为系统本地时区。卡行追加在原额度/重置行后面，不为了多张卡删除既有额度信息；超过原生提示容量时采用溢出悬停显示，不固定卡数

任何示例或测试日期仅来自合成测试数据，不代表实际账号。程序不展示或记录卡 ID、原始账号响应、后台标题/描述，也不读取认证文件。

## 验证边界

云端可验证解析、协议顺序、模拟的0/1/多张、截短/缺失/异常、已使用状态、本地日期、跨年及长度边界；能够交叉编译 Windows EXE。没有用户电脑或真实账号会话，因此本次未验证用户账号实际返回的卡明细，也没有运行 Windows 悬停浮层、滚动、混合 DPI 或 Explorer 重启交互。
