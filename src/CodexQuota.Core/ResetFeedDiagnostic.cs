using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.ComponentModel;

namespace CodexQuota.Core;

public enum ResetFeedStage { Waiting, Request, Headers, Body, Parse, Complete, Unknown }
public enum ResetFeedTransport { Unknown, Injected, PowerShell7Json }
public enum ResetFeedFailure
{
    None, HttpStatus, InvalidCache, MissingBody, BodyTooLarge, ContentType, InvalidSchema,
    Timeout, Cancelled, Dns, ProxyDns, Connect, Proxy, Tls, Transport, Io, Dependency,
    InvalidOperation, Unexpected
}

/// <summary>Only closed labels and numbers. Never stores exception messages, response text, or addresses.</summary>
public sealed record ResetFeedDiagnostic(ResetFeedStage Stage, ResetFeedFailure Failure,
    int? HttpStatus, long ElapsedMilliseconds, int? NativeCode = null, WebExceptionStatus? WebStatus = null,
    ResetFeedTransport Transport = ResetFeedTransport.Unknown, ResetFeedFileFailure LocalFailure = ResetFeedFileFailure.None,
    int? ChildExitCode = null, ResetFeedExceptionType ExceptionType = ResetFeedExceptionType.None)
{
    public static ResetFeedDiagnostic Unexpected(Exception error, long elapsedMilliseconds = 0) =>
        new(ResetFeedStage.Unknown, Classify(error), null, elapsedMilliseconds, NumericCode(error), WebStatusOf(error));

    public static ResetFeedFailure Classify(Exception error)
    {
        // Bound the chain, and classify by runtime type/code only, never by exception text.
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            if (current is AuthenticationException) return ResetFeedFailure.Tls;
            if (current is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException or MissingMethodException)
                return ResetFeedFailure.Dependency;
            if (current is WebException web)
            {
                switch (web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure: return ResetFeedFailure.Dns;
                    case WebExceptionStatus.ProxyNameResolutionFailure: return ResetFeedFailure.ProxyDns;
                    case WebExceptionStatus.ConnectFailure: return ResetFeedFailure.Connect;
                    case WebExceptionStatus.TrustFailure:
                    case WebExceptionStatus.SecureChannelFailure: return ResetFeedFailure.Tls;
                    case WebExceptionStatus.RequestProhibitedByProxy: return ResetFeedFailure.Proxy;
                    case WebExceptionStatus.Timeout: return ResetFeedFailure.Timeout;
                }
            }
            if (current is SocketException socket)
                return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                    ? ResetFeedFailure.Dns : ResetFeedFailure.Connect;
        }
        return error is HttpRequestException or WebException ? ResetFeedFailure.Transport :
            error is IOException ? ResetFeedFailure.Io :
            error is InvalidOperationException ? ResetFeedFailure.InvalidOperation : ResetFeedFailure.Unexpected;
    }

    public static int? NumericCode(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; current = current.InnerException, depth++)
            if (current is Win32Exception native) return native.NativeErrorCode;
        return error.HResult;
    }

    public static WebExceptionStatus? WebStatusOf(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; current = current.InnerException, depth++)
            if (current is WebException web) return web.Status;
        return null;
    }
}

public sealed record ResetFeedAttemptDiagnostic(long Attempt, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null, ResetFeedReadResult? Result = null);

/// <summary>Replaced atomically by the UI thread. Current and last completed are never combined.</summary>
public sealed record ResetFeedDiagnosticSnapshot(ResetFeedAttemptDiagnostic? Current,
    ResetFeedAttemptDiagnostic? LastCompleted, DateTimeOffset? NextPollAt)
{
    public static readonly ResetFeedDiagnosticSnapshot Initial = new(null, null, null);
}

public static class ResetFeedDiagnosticText
{
    public static string Format(ResetFeedDiagnosticSnapshot snapshot, ResetFeedPresentation presentation,
        DateTimeOffset now)
    {
        var lines = new List<string> { "公共重置信息（独立匿名请求；打开此窗口不发起请求）：" };
        lines.Add(snapshot.Current is { } current
            ? "resetFeed current attempt=" + Number(current.Attempt) + " status=InFlight started=" + Time(current.StartedAt)
            : "resetFeed current status=" + (snapshot.LastCompleted is null ? "NotStarted" : "Idle"));
        if (snapshot.LastCompleted is { } completed)
        {
            var result = completed.Result;
            var diagnostic = result?.Diagnostic;
            lines.Add("resetFeed completed attempt=" + Number(completed.Attempt) +
                " started=" + Time(completed.StartedAt) + " completed=" + Time(completed.CompletedAt) +
                " status=" + (result is null ? "Unknown" : Label(result.Status)) +
                " stage=" + (diagnostic is null ? "Unknown" : Label(diagnostic.Stage)) +
                " failure=" + (diagnostic is null ? "Unknown" : Label(diagnostic.Failure)) +
                " http=" + (diagnostic?.HttpStatus is >= 100 and <= 599 ? Number(diagnostic.HttpStatus.Value) : "Unknown") +
                " elapsedMs=" + (diagnostic is null ? "Unknown" : Number(Math.Max(0, Math.Min(120000, diagnostic.ElapsedMilliseconds)))) +
                " code=" + (diagnostic?.NativeCode is { } code ? code.ToString(CultureInfo.InvariantCulture) : "None") +
                " webStatus=" + (diagnostic?.WebStatus is { } webStatus ? Label(webStatus) : "None") +
                " transport=" + (diagnostic is null ? "Unknown" : Label(diagnostic.Transport)) +
                " local=" + (diagnostic is null ? "None" : Label(diagnostic.LocalFailure)) +
                " childExit=" + (diagnostic?.ChildExitCode is { } childExit ? childExit.ToString(CultureInfo.InvariantCulture) : "None") +
                " exceptionType=" + (diagnostic is null ? "None" : Label(diagnostic.ExceptionType)));
            lines.Add("resetFeed data parse=" + (result?.Status == ResetFeedReadStatus.NotModified ? "RevalidatedCache" :
                result?.IsSuccess == true ? "Passed" : diagnostic?.Failure == ResetFeedFailure.InvalidSchema ? "Failed" : "NotReached") +
                " latestPresent=" + Flag(result?.Snapshot?.LatestReset is not null) +
                " scheduledPresent=" + Flag(result?.Snapshot?.ScheduledReset is not null) +
                " watchPresent=" + Flag(result?.Snapshot?.ActiveWatch is not null) +
                " latestVisible=" + Flag(presentation.Latest is not null) +
                " forecastVisible=" + Flag(presentation.Forecast is not null) +
                " hiddenReason=" + HiddenReason(result, presentation, now) +
                " latestReason=" + LatestReason(result, presentation, now));
        }
        else lines.Add("resetFeed completed status=None");
        lines.Add("resetFeed nextPoll=" + (snapshot.Current is not null ? "AfterCurrent" : Time(snapshot.NextPollAt)));
        return string.Join("\r\n", lines);
    }

    private static string HiddenReason(ResetFeedReadResult? result, ResetFeedPresentation presentation, DateTimeOffset now)
    {
        if (result is null) return "NotRead";
        if (!result.IsSuccess) return "ReadFailed";
        if (presentation.IsVisible) return "None";
        var data = result.Snapshot!;
        if (data.GeneratedAt > now && data.GeneratedAt - now > ResetFeedPresentation.AllowedClockSkew)
            return "GeneratedTimeAhead";
        if (data.LatestReset is null && data.ScheduledReset is null && data.ActiveWatch is null) return "EmptyFeed";
        if (data.LatestReset?.AnnouncedAt > now) return "LatestTimeAhead";
        if (data.ScheduledReset is { } scheduled)
            return scheduled.AnnouncedAt > now && scheduled.AnnouncedAt - now > ResetFeedPresentation.AllowedClockSkew
                ? "ForecastTimeAhead" : "ForecastExpired";
        if (data.ActiveWatch is { } watch) return watch.ExpiresAt <= now ? "ForecastExpired" : "ForecastTimeAhead";
        return "NoEligibleRows";
    }

    private static string LatestReason(ResetFeedReadResult? result, ResetFeedPresentation presentation, DateTimeOffset now)
    {
        if (result is null) return "NotRead";
        if (!result.IsSuccess) return "ReadFailed";
        if (presentation.Latest is not null) return "Visible";
        var data = result.Snapshot!;
        if (data.GeneratedAt > now && data.GeneratedAt - now > ResetFeedPresentation.AllowedClockSkew)
            return "GeneratedTimeAhead";
        if (data.LatestReset is null) return "NotProvided";
        return data.LatestReset.AnnouncedAt > now ? "LatestTimeAhead" : "NotRendered";
    }

    private static string Label<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? value.ToString() : "Unknown";
    private static string Number(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    private static string Flag(bool value) => value ? "true" : "false";
    private static string Time(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "None";
}
