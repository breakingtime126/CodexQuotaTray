# 公共重置数据镜像

从 0.2.36 开始，客户端先匿名读取固定原站 `https://codex-resets.com/api/v1/status`；超时、请求失败或严格数据验证失败时，自动尝试本仓库 `reset-data` 分支的 `status.json`：

`https://raw.githubusercontent.com/breakingtime126/CodexQuotaTray/reset-data/status.json`

原始公共数据来自 [Codex Resets](https://codex-resets.com/api/docs)，不是 OpenAI 官方数据，也不是个人账号的实际重置时间。界面保留来源署名与经过验证的公告链接；第三方预测不保证发生。没有有效预测时只显示有数据的行。

## 同步与有效期

- GitHub Actions `Mirror public reset status` 匿名读取固定公共 API，每小时第 7、17、27、37、47、57 分钟计划运行，也支持手动触发。计划运行可能排队或延迟，并非实时服务。
- 仅发布经过大小、结构、类型及链接验证的数据；网络、限流或格式错误时保留上一次成功结果，不修改成功时间。
- `upstreamSuccessAtUtc` 表示镜像最近一次成功读取并验证原始 API 的时间。客户端只接受该时间在过去 60 分钟以内（最多容忍本机时钟落后 5 分钟）的结果；HTTP 缓存响应不会延长有效期。
- 历史 `latest_reset.announced_at` 是事件发生时间，不是镜像新鲜度，过去的重置事件仍可显示。
- 原站成功时不请求镜像。两个地址都固定且拒绝重定向，不使用跨来源 ETag／304 缓存；原站数据以其 `meta.generated_at` 验证新鲜度，镜像仍以 `upstreamSuccessAtUtc` 验证，不将下载时间冒充上游成功时间。
- 每路 HTTP 请求最多 8 秒，每路客户端等待最多 12 秒，两路顺序尝试最多约 24 秒；取消请求后不继续尝试镜像。若外部进程无法及时清理，仍保留进程所有权与文件锁保护，不强行并行绕过。
- 两路都失败、过期或无效时隐藏公共行，但不影响个人额度、重置卡和 TPS。

## 隐私与运行要求

镜像仅含公共 API 数据、来源和同步时间，不接收用户账号、个人额度或日志。同步任务只使用当前仓库的临时 GitHub Actions 权限，不需要个人令牌或更新签名私钥。

客户端仍通过标准位置安装的 PowerShell 7.5+ 读取镜像并输出本地 JSON，再由主程序严格验证。它不修改系统代理、证书、安全软件或公司策略。GitHub Releases 自动更新的 PowerShell 5.1 支持不代表公共重置功能也支持 5.1。

GitHub 的公共仓库计划任务可能因长期无活动被停用；维护者应检查本仓库 Actions 中同步任务的状态。过期保护会避免客户端长期展示停止更新的镜像。
