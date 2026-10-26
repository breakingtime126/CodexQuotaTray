using System.Globalization;

namespace CodexQuota.Core;

/// <summary>Completed metadata with explicit generation or whole-turn timing semantics.</summary>
public sealed record OutputSpeedView(string Value, string Model, string Detail, string AccessibleText,
    bool IsOld, bool IsUnavailable)
{
    public string SampleTime { get; init; } = "";
    public string SampleLabel { get; init; } = "";
    // Set only from reliable metadata for this completed response. An absent
    // reasoning effort stays absent; the UI must never infer it from the model.
    public string ReasoningLabel { get; init; } = "";
    public string CompactModel { get; init; } = "";
    public bool HasVerifiedTiming { get; init; }
    public OutputSpeedTimingReliability TimingReliability { get; init; }
    public ThreadRequestedSetting ThreadRequestedSetting { get; init; } = ThreadRequestedSetting.Unknown;
    public string HistoryPrefix { get; init; } = "";
    public string FastPrefix => ThreadRequestedSetting == ThreadRequestedSetting.Fast ? "⚡ " : "";
    public string CompactRate => HasVerifiedTiming ? "≈ " + Value : "";
    public string CompactTail => ReasoningLabel + (ReasoningLabel.Length > 0 && CompactRate.Length > 0 ? "  " : "") + CompactRate;
    public string CompactLine => HistoryPrefix + FastPrefix + CompactModel + (ReasoningLabel.Length > 0 ? " " + ReasoningLabel : "") +
        (CompactRate.Length > 0 ? "  " + CompactRate : "");

    public static string FormatModelName(string model)
    {
        // Typography only: preserve version/family/suffix tokens; never map a
        // raw model to a different product or a guessed friendly alias.
        if (!model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return model;
        var parts = model.Substring(4).Split('-');
        if (parts.Any(part => part.Length == 0) || !char.IsDigit(parts[0][0])) return model;
        return "GPT" + parts[0] + string.Concat(parts.Skip(1).Select(part => " " + char.ToUpperInvariant(part[0]) + part.Substring(1)));
    }

    public static string FormatReasoningEffort(string? effort) => effort switch
    {
        "none" => "无", "minimal" => "极低", "low" => "低", "medium" => "中",
        "high" => "高", "xhigh" => "超高", "max" => "最高", "ultra" => "极高", "persistent" => "持续",
        _ => "" // Unrecognized/missing metadata is not a default effort.
    };

    public static OutputSpeedView Create(OutputSpeedSample? sample, DateTimeOffset now,
        bool unavailable = false, bool historyLimited = false, bool pendingWork = false,
        RolloutModelMetadataStatus modelMetadataStatus = RolloutModelMetadataStatus.NotFound)
    {
        if (sample is null)
        {
            var detail = unavailable ? "暂时无法读取日志" : pendingWork ? "正在读取近期日志" : "暂无可靠样本";
            return new("—", "", detail, "输出速度（估算）：" + detail + "；只采用有可靠计时的完成响应，包含推理 token。", true, unavailable)
                { SampleLabel = detail };
        }
        var rate = sample.TokensPerSecond;
        var observedAt = rate.HasValue ? sample.CompletedAt : sample.RecordedAt ?? sample.CompletedAt;
        var completed = observedAt.ToLocalTime();
        var localNow = now.ToLocalTime();
        var format = completed.Date == localNow.Date ? "HH:mm:ss"
            : completed.Year == localNow.Year ? "M/dd HH:mm:ss" : "yyyy/M/dd HH:mm:ss";
        var value = rate is { } numericRate
            ? numericRate > 0 && numericRate < .1 ? "<0.1" : numericRate.ToString(numericRate >= 100000 ? "0.##E+0" : numericRate >= 1000 ? "0" : "0.0", CultureInfo.InvariantCulture)
            : "计时未核验";
        var hasModel = !string.IsNullOrWhiteSpace(sample.Model);
        var model = hasModel ? sample.Model! : "模型未知";
        var compactModel = hasModel ? FormatModelName(model) : modelMetadataStatus switch
        {
            RolloutModelMetadataStatus.PendingHistory => "模型回查中",
            RolloutModelMetadataStatus.HistoryLimit => "模型回查达上限",
            _ => model
        };
        var effortLabel = FormatReasoningEffort(sample.ReasoningEffort);
        var old = sample.IsHistoricalOtherThread || sample.IsRetainedTurnAverage || now - observedAt >= TimeSpan.FromMinutes(10);
        var historyFormat = completed.Date == localNow.Date ? "HH:mm" : completed.Year == localNow.Year ? "M/dd HH:mm" : "yyyy/M/dd HH:mm";
        var historyPrefix = old || unavailable ? "上次" + completed.ToString(historyFormat, CultureInfo.InvariantCulture) + " " : "";
        var detailText = "含推理·" + (old || unavailable ? "上次 " : "") + completed.ToString(format, CultureInfo.InvariantCulture);
        var description = (rate.HasValue
            ? sample.TimingReliability == OutputSpeedTimingReliability.ResponseRateEstimate
                ? "响应有效速度（估算）：" + value + " tok/s；同一模型响应的输出用量除以日志请求边界到响应完成记录的区间，包含首 token 等待、网络与落盘延迟；不是模型纯生成 TPS；模型 " + model + "；完成记录于 "
                : sample.TimingReliability == OutputSpeedTimingReliability.TurnAverage
                ? "回合均速（TurnAverage，估算）：" + value + " tok/s；最近完整正常完成回合；包含首 token 等待、工具执行、审批、网络与调度耗时，不是模型纯生成 TPS；模型 " + model + "；完成于 "
                : "输出速度（估算）：" + value + " tok/s；最近有效完成响应，模型 " + model + "；完成于 "
            : "输出速度：计时未核验，未显示 tok/s；模型 " + model + "；" + (sample.RecordedAt.HasValue ? "用量记录时间 " : "完成通知时间 ")) +
            completed.ToString("yyyy年M月dd日 HH:mm:ss zzz", CultureInfo.InvariantCulture) +
            "（电脑本地时间）；output_tokens 已包含推理 token，不重复相加；不是实时速度。" +
            (!rate.HasValue ? "当前日志中的项目起止可能包含生成完成后的生命周期通知，无法核验真实生成时长；未用这些时间计算速度。" : "") +
            (sample.ReasoningEffort is { } effort ? "该历史响应的推理档位：" + (effortLabel.Length > 0 ? effortLabel + "（" + effort + "）" : effort) + "。" : "该历史响应未返回可确认的推理档位。") +
            (!hasModel ? modelMetadataStatus switch
            {
                RolloutModelMetadataStatus.PendingHistory => "模型元数据正在按固定IO预算渐进回查。",
                RolloutModelMetadataStatus.HistoryLimit => "已达到历史元数据回查安全上限，未把范围以外当作模型缺失。",
                RolloutModelMetadataStatus.Conflict => "历史模型元数据有冲突，未猜测模型。",
                RolloutModelMetadataStatus.Boundary => "历史模型回查遇到无法跨越的可信边界，未猜测模型。",
                _ => "未找到可确认的历史模型元数据。"
            } : "") +
            (unavailable ? "当前日志读取暂不可用，保留上次结果。" : "") +
            (historyLimited ? "仅检查有界近期日志，不代表完整历史。" : "") +
            (sample.IsHistoricalOtherThread ? "独立历史有效样本，不归属于当前活动会话；型号、档位、数值与完成时设置快照整组保留。" :
                sample.IsRetainedTurnAverage ? "同会话较新记录尚无有效速度样本，保留上次样本的型号、档位、数值与完成时设置快照；没有将旧速度拼接到新响应型号。" : "") +
            "Fast标记来源：ThreadRequestedSetting，" + (sample.TimingReliability is OutputSpeedTimingReliability.TurnAverage or OutputSpeedTimingReliability.ResponseRateEstimate ? "完成时线程设置快照（CompletionSnapshot）" : "同会话最近明确请求设置") +
            "；本回合请求档位未知（turnTierUnknown），不代表服务器实际服务档位或实际加速。";

        return new(rate.HasValue ? value + " tok/s" : value, model,
            rate.HasValue ? detailText : "计时未核验·" + (old || unavailable ? "上次记录 " : "记录 ") + completed.ToString(format, CultureInfo.InvariantCulture),
            description, old, unavailable)
            { SampleTime = completed.ToString(format, CultureInfo.InvariantCulture), HistoryPrefix = historyPrefix,
                SampleLabel = rate.HasValue ? old || unavailable ? "上次" : "样本" : old || unavailable ? "上次记录" : "记录",
                CompactModel = compactModel, ReasoningLabel = effortLabel, HasVerifiedTiming = rate.HasValue, TimingReliability = sample.TimingReliability, ThreadRequestedSetting = sample.ThreadRequestedSetting };
    }
}
