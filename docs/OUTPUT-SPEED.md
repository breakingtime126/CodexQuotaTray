# 响应有效速度与历史样本 · 0.2.27

## 更新口径

优先使用官方 rollout 顶层 `token_usage_record`。每条属于当前文件自身 thread、明确 turn 与 response 的有效用量记录到达后，即可更新该模型响应的估算，不再等待整个工具回合的 `task_complete`。

数值是该 response 的 `usage.output_tokens` 除以有证据的日志起止区间。output 已包含 reasoning，不再相加；不同会话、回合或响应不累计。没有短回复 200 token 门槛、速度硬上限或滚动中位数，正常短回复也更新。

这是“响应有效速度估算”，不是实时解码速度：起点来自本轮开始、用户输入、前一响应完成或已观察到的工具结果；终点是该响应的用量完成记录。区间可能包含首 token 等待、网络、调度和日志落盘延迟。轮询时间不参与分母，`item_started/item_completed` 的通知间隔也不作为生成时长。

## 响应边界与去重

- 首个已观察的模型生成项锁定当前响应起点。随后并行到达的工具结果只影响下一响应，不移动已经锁定的起点。
- 已确认前一响应完成可作为下一响应的后备起点；随后明确工具结果或输入可以推进下一起点。工具等待不会有意拼入下一响应。
- 使用文件所属 `session_meta.id` 与用量的 `thread_id`、`turn_id`、`response_id` 绑定。谱系 `root_turn_id` 不是当前所有权，子线程复制的历史不能计作自己。
- 重复记录先去重，不能清空下一响应窗口。矛盾副本只撤回其依赖的样本，不能用重复用量再计一次。
- 型号、推理档位、请求 Fast 设置快照、数值、时间作为完整组保存，不给新型号配旧速度。
- 缺归属、缺起点、非正区间、坏用量、损坏边界时不杜撰数字。下一响应在新的可信边界可以恢复，不因一条缺字段记录永久报废整个正常回合。
- 本版新增响应数值针对 `token_usage_record`；只有旧 `token_count` 的日志不被当作第二份响应数据。既有完整 `TurnAverage` 可作为保守历史样本，不与响应样本相加。

正式 0.159.0 和 0.162.0 的官方源码均定义该新记录，但本工具仍按实际字段判断，不能仅凭版本承诺每份历史文件完整。

## 历史显示

保持用户指定的紧凑 `⚡ GPT6.1 Sol 中  ≈ 30.2 tok/s` 格式。当前没有可用新响应时保留最近有效整组，必要时显示 `上次HH:mm`；跨日/跨年补日期。其他会话或尚未完成的新记录不能把历史组拆开拼接。

同文件可靠追加顺序参与新旧判断，同时间的后续响应不能仅因 response ID 字典序落后而一直被遮住。历史补扫和归档副本不能仅凭“最后读取到”抢占更新。日志墙钟不是全局单调时钟，跨文件歧义仍须保守处理。

⚡表示 `ThreadRequestedSetting`：同会话最近明确 Fast 设置在样本完成时的快照，不是服务器实际服务档位，也不能证明该次请求未被单轮设置覆盖。

## 有界读取与追赶

读取 CODEX_HOME 或当前用户 `.codex` 下的 sessions / archived_sessions；悬停不触发扫描。发现采用可继续的有界游标，避免每次只扫描最新日期而永久遗漏恢复活动的旧会话。常规发现/空闲轮询为 3 秒。

保留每轮总 IO 2 MiB、每文件 512 KiB、最多 24 个跟踪文件等资源预算。冷启动仍先读近期尾部，缺失边界再按预算补扫冻结尾端前最多 256 MiB 的历史窗口；不反复滑窗或全量重读，窗口外缺证据仍留空。补扫可取消，不能阻塞前向追加。大工具正文通过有界结构投影处理，只保留必要元数据；非法 JSON、UTF-8、身份或必要字段不能作为正常正文跳过。跨读取缺口后的首个响应，必须有明确 task_started 或先用一次可信完成记录建立基线，不能仅凭半途工具结果伪造起点。

只有存在可实际前进的发现/前向/补扫游标才快速追赶：最多连续八次 150 ms 间隔，然后让出 1 秒。半行等待写入不持续快速轮询；正常空闲恢复 3 秒。单次读取完成后才调度下次，没有并行扫描。

文件替换、截断、取消和归档副本必须通过身份/冲突检查；不持久化正文或速度缓存，不输出路径、会话ID或日志内容。诊断使用固定状态与有界计数，区分响应边界缺失、发现待继续、前向积压和历史补扫。

## 证据与参考

- [官方 0.162.0 TokenUsageRecord 结构](https://github.com/openai/codex/blob/c1382380de69521303b416720a52f42d51af6248/codex-rs/protocol/src/protocol.rs#L2291-L2305)
- [官方流结束先持久化响应用量、再处理后续收尾](https://github.com/openai/codex/blob/c1382380de69521303b416720a52f42d51af6248/codex-rs/core/src/session/turn.rs#L2946-L2994)
- [官方记录持久化入口](https://github.com/openai/codex/blob/c1382380de69521303b416720a52f42d51af6248/codex-rs/core/src/session/mod.rs#L4720-L4752)
- [Tokrate 响应级估算参考，MIT](https://github.com/mattivilola/tokrate-clients/blob/9a741e2ec74046cc34e3576cee2860ebee6c6a15/shared/Sources/TokrateCore/CodexEventParser.swift#L294-L350)
- [adenta/codex-tps 旧格式边界参考，MIT](https://github.com/adenta/codex-tps/blob/be549617f50409c8c0e15a8a6838520ec12d2bb1/analysis.go#L191-L237)

本版采用独立状态机，不照搬外部项目的短回复门槛、中位数、过期回退或无归属的全局累计。

## 验证范围

以合成官方字段时间线测试：单轮多响应立即更新、短回复、工具等待、重复/冲突、跨会话与模型、冷读补扫、大正文、旧日期发现、积压与取消、文件替换、同时间追加及历史重放。云端编译、完整回归与合成渲染不能替代 Windows 实际交互和用户真实日志验证。本次未读取真实凭据、恢复用户日志或发送模型请求。

## 0.2.27 兼容性修正

0.2.26 的流式投影把官方 turn_context 的 summary 字段（0.159 必填，0.162 也会写兼容值）误判成不允许的正文位置，导致整条模型上下文被丢弃；有效用量仍能读到，却因 MissingModel 无法形成数值。小记录也会触发，与是否结束整轮无关。本版修正合法非计量字段的投影处理，并覆盖官方持久化记录形状，保留 JSON/UTF-8 校验、身份与用量校验和资源上限。诊断中的旧模型反向回查 LineTooLong 是另一条读取路径，不能直接当作这次投影拒绝的原因。

源码依据：[0.159 TurnContextItem.summary](https://github.com/openai/codex/blob/687a119f0fcaace47e1f1abcc77cec6c813fd6da/codex-rs/protocol/src/protocol.rs#L3301-L3357)、[官方持久化类型策略](https://github.com/openai/codex/blob/687a119f0fcaace47e1f1abcc77cec6c813fd6da/codex-rs/rollout/src/policy.rs#L9-L25)、[持久化 item_completed 类型](https://github.com/openai/codex/blob/687a119f0fcaace47e1f1abcc77cec6c813fd6da/codex-rs/rollout/src/policy.rs#L92-L123)。这些是公开schema依据；验证数据为云端自造合成数据，没有读取用户会话。
