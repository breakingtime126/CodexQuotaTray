using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>Only public cache metadata crosses the local PowerShell boundary, never an arbitrary URL.</summary>
public sealed record ResetFeedFileRequest(string RequestId, DateTimeOffset StartedAtUtc, string? ETag);

public enum ResetFeedFileFailure
{
    None, MissingPowerShell, LaunchDenied, LaunchFailed, OutputWriteDenied, OutputMissing,
    OutputTooLarge, OutputInvalid, OutputStale, RequestFailed, RequestTimeout, CleanupPending,
    OutputWriteFailed, ChildFailed, ScriptMismatch, FetchFailed, ReadFailed, ParseFailed,
    ContentType, ProxyCredentialsPresent, ProxyInspectionFailed, SetupFailed
}

public enum ResetFeedExceptionType
{
    None, SocketException, Win32Exception, HttpRequestException, IOException, OperationCanceledException,
    JsonException, InvalidDataException, InvalidOperationException, UnauthorizedAccessException, NotSupportedException, Other
}

/// <summary>A closed failure label and an optional numeric code; never carries child output or paths.</summary>
public sealed class ResetFeedFileException : HttpRequestException
{
    public ResetFeedFileFailure Failure { get; }
    public int? NativeCode { get; }
    public int? ChildExitCode { get; }
    public ResetFeedExceptionType ExceptionType { get; }
    public int? HttpStatus { get; }

    public ResetFeedFileException(ResetFeedFileFailure failure, int? nativeCode = null, int? childExitCode = null,
        ResetFeedExceptionType exceptionType = ResetFeedExceptionType.None, int? httpStatus = null)
        : base("Optional reset file transport failed.")
    {
        Failure = Enum.IsDefined(typeof(ResetFeedFileFailure), failure) ? failure : ResetFeedFileFailure.OutputInvalid;
        NativeCode = nativeCode;
        ChildExitCode = childExitCode;
        ExceptionType = Enum.IsDefined(typeof(ResetFeedExceptionType), exceptionType) ? exceptionType : ResetFeedExceptionType.Other;
        HttpStatus = httpStatus is >= 100 and <= 599 ? httpStatus : null;
    }
}

/// <summary>
/// Bounded, correlated local result envelope. Transport timestamps are checked independently of
/// the announcement timestamps: an old latest_reset remains valid historical information.
/// </summary>
public static class ResetFeedFileProtocol
{
    public const int MaximumEnvelopeBytes = 96 * 1024;
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumAttemptAge = TimeSpan.FromMinutes(2);
    private static readonly string[] Fields =
    {
        "schemaVersion", "requestId", "status", "fetchedAtUtc", "httpStatus", "etag", "retryAfterSeconds", "data", "meta", "errorCode"
    };

    public static HttpResponseMessage Parse(byte[] envelope, ResetFeedFileRequest request, DateTimeOffset now)
    {
        if (envelope.Length > MaximumEnvelopeBytes) throw Failure(ResetFeedFileFailure.OutputTooLarge);
        if (envelope.Length == 0) throw Failure(ResetFeedFileFailure.OutputInvalid);
        try
        {
            using var document = JsonDocument.Parse(envelope, new JsonDocumentOptions { MaxDepth = 14 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Failure(ResetFeedFileFailure.OutputInvalid);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!names.Add(field.Name) || !Fields.Contains(field.Name) && field.Name is not ("exceptionType" or "nativeCode"))
                    throw Failure(ResetFeedFileFailure.OutputInvalid);
            if (Fields.Any(name => !names.Contains(name)) || names.Contains("exceptionType") != names.Contains("nativeCode") ||
                !root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1)
                throw Failure(ResetFeedFileFailure.OutputInvalid);
            var id = Text(root, "requestId", 36);
            if (!Guid.TryParseExact(id, "D", out var guid) || guid.ToString("D") != id ||
                !string.Equals(id, request.RequestId, StringComparison.Ordinal))
                throw Failure(ResetFeedFileFailure.OutputStale);
            var fetchedText = Text(root, "fetchedAtUtc", 64);
            if (!(fetchedText.EndsWith("Z", StringComparison.Ordinal) || fetchedText.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(fetchedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fetched) ||
                fetched.Offset != TimeSpan.Zero)
                throw Failure(ResetFeedFileFailure.OutputInvalid);
            if (fetched - request.StartedAtUtc < -ClockTolerance || fetched - now > ClockTolerance ||
                now - request.StartedAtUtc > MaximumAttemptAge || request.StartedAtUtc - now > ClockTolerance)
                throw Failure(ResetFeedFileFailure.OutputStale);

            var status = Text(root, "status", 16);
            var errorCode = NullableText(root, "errorCode", 32);
            var exceptionType = ResetFeedExceptionType.None;
            int? nativeCode = null;
            if (names.Contains("exceptionType"))
            {
                var label = NullableText(root, "exceptionType", 32);
                if (label is not null && (!Enum.TryParse(label, out exceptionType) ||
                    !Enum.IsDefined(typeof(ResetFeedExceptionType), exceptionType) || exceptionType.ToString() != label))
                    throw Failure(ResetFeedFileFailure.OutputInvalid);
                nativeCode = NullableInt(root.GetProperty("nativeCode"));
            }
            var http = NullableInt(root.GetProperty("httpStatus"));
            var etag = NullableText(root, "etag", 256);
            var retry = NullableInt(root.GetProperty("retryAfterSeconds"));
            if (http is not null && http is < 100 or > 599 || retry is < 0 or > 604800)
                throw Failure(ResetFeedFileFailure.OutputInvalid);
            EntityTagHeaderValue? tag = null;
            if (etag is not null && !EntityTagHeaderValue.TryParse(etag, out tag))
                throw Failure(ResetFeedFileFailure.OutputInvalid);
            var data = root.GetProperty("data");
            var meta = root.GetProperty("meta");
            if (status == "Error")
            {
                if (data.ValueKind != JsonValueKind.Null || meta.ValueKind != JsonValueKind.Null)
                    throw Failure(ResetFeedFileFailure.OutputInvalid);
                var local = errorCode switch
                {
                    "DeadlineExceeded" => ResetFeedFileFailure.RequestTimeout,
                    "EnvelopeTooLarge" or "ResponseTooLarge" => ResetFeedFileFailure.OutputTooLarge,
                    "FetchOrValidationFailed" => ResetFeedFileFailure.RequestFailed,
                    "FetchFailed" => ResetFeedFileFailure.FetchFailed,
                    "ReadFailed" => ResetFeedFileFailure.ReadFailed,
                    "ParseFailed" => ResetFeedFileFailure.ParseFailed,
                    "ContentType" => ResetFeedFileFailure.ContentType,
                    "ProxyCredentialsPresent" => ResetFeedFileFailure.ProxyCredentialsPresent,
                    "ProxyInspectionFailed" => ResetFeedFileFailure.ProxyInspectionFailed,
                    "SetupFailed" => ResetFeedFileFailure.SetupFailed,
                    _ => throw Failure(ResetFeedFileFailure.OutputInvalid)
                };
                if (local is not (ResetFeedFileFailure.RequestFailed or ResetFeedFileFailure.FetchFailed) || http is null)
                    throw new ResetFeedFileException(local, nativeCode, exceptionType: exceptionType, httpStatus: http);
                if (http is 200 or 304) throw Failure(ResetFeedFileFailure.OutputInvalid);
            }
            else if (errorCode is not null || exceptionType != ResetFeedExceptionType.None || nativeCode is not null)
                throw Failure(ResetFeedFileFailure.OutputInvalid);
            if (!(status == "Success" && http == 200 || status == "NotModified" && http == 304 ||
                status == "Error" && http is not null and not (200 or 304)))
                throw Failure(ResetFeedFileFailure.OutputInvalid);

            byte[]? body = null;
            if (status == "Success")
            {
                if (data.ValueKind != JsonValueKind.Object || meta.ValueKind != JsonValueKind.Object)
                    throw Failure(ResetFeedFileFailure.OutputInvalid);
                var json = "{\"data\":" + data.GetRawText() + ",\"meta\":" + meta.GetRawText() + "}";
                if (Encoding.UTF8.GetByteCount(json) > ResetFeedParser.MaximumBodyBytes)
                    throw Failure(ResetFeedFileFailure.OutputTooLarge);
                body = Encoding.UTF8.GetBytes(json);
            }
            else if (data.ValueKind != JsonValueKind.Null || meta.ValueKind != JsonValueKind.Null)
                throw Failure(ResetFeedFileFailure.OutputInvalid);

            var response = new HttpResponseMessage((HttpStatusCode)http!.Value);
            if (tag is not null) response.Headers.ETag = tag;
            if (retry is { } seconds) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            if (body is not null)
            {
                response.Content = new ByteArrayContent(body);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            return response;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or
            ArgumentException or KeyNotFoundException or OverflowException)
        {
            throw Failure(ResetFeedFileFailure.OutputInvalid);
        }
    }

    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null :
        value.TryGetInt32(out var number) ? number : throw Failure(ResetFeedFileFailure.OutputInvalid);

    private static string Text(JsonElement value, string name, int maximum) =>
        NullableText(value, name, maximum) ?? throw Failure(ResetFeedFileFailure.OutputInvalid);

    private static string? NullableText(JsonElement value, string name, int maximum)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.String) throw Failure(ResetFeedFileFailure.OutputInvalid);
        var text = item.GetString()!;
        if (text.Length == 0 || text.Length > maximum || text.Any(char.IsControl))
            throw Failure(ResetFeedFileFailure.OutputInvalid);
        return text;
    }

    private static ResetFeedFileException Failure(ResetFeedFileFailure failure) => new(failure);
}
