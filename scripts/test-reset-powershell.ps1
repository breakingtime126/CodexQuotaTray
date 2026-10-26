# Offline contract tests. Production entry-point tests stop at invalid setup before any network send.
[CmdletBinding()]
param([string]$SourcePath = (Join-Path $PSScriptRoot '../src/CodexQuota.Core/Resources/Fetch-ResetState.ps1'))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [version]'7.5') { throw 'PowerShell 7.5 or newer is required.' }
$SourcePath = [IO.Path]::GetFullPath($SourcePath)
$source = [IO.File]::ReadAllText($SourcePath)
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Production PowerShell syntax validation failed.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('reset-ps-tests-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($work)
$RequestId = '51f3a993-adcf-4d8f-8ce2-f97b4aa92e6f'
$limit = 65536; $envelopeLimit = 98304
$expectedOutput = Join-Path $work 'reset-state.json'
$passed = 0
function Assert-True([bool]$Condition, [string]$Name) { if (-not $Condition) { throw "FAIL: $Name" }; $script:passed++ }
function Assert-Rejected([scriptblock]$Action, [string]$Name) { $rejected = $false; try { $null = & $Action } catch { $rejected = $true }; Assert-True $rejected $Name }
function New-TestResponse([int]$Status = 200, [byte[]]$Bytes = ([Text.Encoding]::UTF8.GetBytes('{"mirrorSchemaVersion":1,"upstreamSuccessAtUtc":"2026-10-26T09:00:00Z","source":"https://codex-resets.com/api/v1/status","data":{},"meta":{}}'))) {
    $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]$Status)
    $response.Content = [Net.Http.ByteArrayContent]::new($Bytes)
    return $response
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
public sealed class ResetTestProxy : IWebProxy {
    public bool ThrowOnCredentials;
    public ICredentials Value;
    public ICredentials Credentials { get { if (ThrowOnCredentials) throw new IOException("PRIVATE_PROXY_DETAILS"); return Value; } set { Value = value; } }
    public Uri GetProxy(Uri u) { throw new InvalidOperationException("Proxy routing must not be inspected."); }
    public bool IsBypassed(Uri u) { throw new InvalidOperationException("Proxy routing must not be inspected."); }
}
public sealed class ResetTestCredentials : ICredentials {
    public NetworkCredential GetCredential(Uri uri, string authType) { throw new InvalidOperationException("Credentials must not be inspected."); }
}
public sealed class ResetTestStream : Stream {
    private readonly MemoryStream bytes;
    public bool FailRead;
    public ResetTestStream(byte[] data) { bytes = new MemoryStream(data); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) { if (FailRead) throw new IOException("PRIVATE_BODY_DETAILS"); return bytes.Read(buffer, offset, count); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) bytes.Dispose(); base.Dispose(disposing); }
}
'@
try {
    # Import only parsed function definitions into an isolated temporary directory.
    # No production arguments, request, output, proxy settings, or network path run.
    $functions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
    $functionPath = Join-Path $work 'offline-functions.ps1'
    [IO.File]::WriteAllText($functionPath, (($functions | ForEach-Object { $_.Extent.Text }) -join "`n"))
    . $functionPath
    Assert-True ($functions.Count -ge 12) 'Production function extraction'
    $sends = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $node.Member.Value -eq 'SendAsync' }, $true)
    Assert-True ($sends.Count -eq 1) 'Exactly one SendAsync call site'
    Assert-True ($source -notmatch '(?i)\b(Start-Sleep|Invoke-WebRequest|Invoke-RestMethod|Set-Acl|Set-ExecutionPolicy|Add-Type)\b|ServerCertificateCustomValidationCallback|SslProtocols|\.UseProxy\s*=|\.Proxy\s*=|DefaultProxy\s*=|GetProxy\(') 'No retries, alternate transport, TLS/proxy override or policy changes'
    foreach ($setting in @('AllowAutoRedirect = $false', 'UseCookies = $false', 'UseDefaultCredentials = $false', 'Credentials = $null', 'DefaultProxyCredentials = $null', 'PreAuthenticate = $false')) {
        Assert-True ($source.Contains('$handler.' + $setting)) "Anonymous handler setting: $setting"
    }
    Assert-True ($source.Contains('[IO.File]::Replace(') -and $source.Contains('[IO.FileMode]::CreateNew')) 'Atomic replace and exclusive temporary creation'
    Assert-True ($source.Contains("if (`$_.Exception.Data['ResetEnvelopeTooLarge'] -ne `$true) { exit 2 }")) 'No I/O retry after denied write'
    Assert-True (-not $source.Contains('.Exception.Message')) 'No exception message diagnostics'

    foreach ($etag in @('"abc"', 'W/"abc"', '""', ('"' + ('a' * 254) + '"'))) { Assert-True (Test-EntityTag $etag) 'Valid single ETag' }
    foreach ($etag in @('*', 'abc', 'w/"abc"', '"a", "b"', "`"a`"`n", "`"a`r`nb`"", ' "a"', ('"' + ('a' * 255) + '"'), 123)) { Assert-True (-not (Test-EntityTag $etag)) 'Invalid ETag rejected' }
    $request = New-StatusRequest 'W/"cached"'
    try {
        Assert-True ($request.Method.Method -ceq 'GET' -and $request.RequestUri.AbsoluteUri -ceq 'https://raw.githubusercontent.com/breakingtime126/CodexQuotaTray/reset-data/status.json' -and $request.RequestUri.Query -ceq '') 'Fixed GET endpoint without query'
        Assert-True ($request.Headers.IfNoneMatch.ToString() -ceq 'W/"cached"') 'If-None-Match retained'
        Assert-True ($request.Headers.CacheControl.NoCache -and $request.Headers.CacheControl.MaxAge -eq [TimeSpan]::Zero) 'Cache-Control no-cache/max-age=0'
        Assert-True ($request.Headers.Pragma.ToString() -ceq 'no-cache' -and $request.Headers.Accept.ToString() -ceq 'application/json') 'Pragma and Accept headers'
        Assert-True ($null -eq $request.Headers.Authorization -and -not $request.Headers.Contains('Cookie') -and -not $request.Headers.Contains('Proxy-Authorization')) 'No authentication or cookies'
    } finally { $request.Dispose() }
    $savedSource = $source
    $Source = 'Upstream'
    $request = New-StatusRequest $null
    try {
        Assert-True ($request.RequestUri.AbsoluteUri -ceq 'https://codex-resets.com/api/v1/status') 'Direct source is fixed upstream'
        Assert-True ($request.Headers.UserAgent.ToString() -ceq 'CodexQuotaTray/0.2.36') 'Honest fixed application User-Agent'
        Assert-True ($request.Headers.IfNoneMatch.Count -eq 0 -and $null -eq $request.Headers.Authorization) 'Direct source is unconditional anonymous GET'
    } finally { $request.Dispose(); $Source = $savedSource }
    $savedSource = $source
    $Source = 'Upstream'
    $response = New-TestResponse 200 ([Text.Encoding]::UTF8.GetBytes('{"data":{},"meta":{}}'))
    try {
        $state = Convert-HttpResponse $response ([Threading.CancellationToken]::None)
        Assert-True ($state.status -ceq 'Success' -and -not $state.Contains('mirror')) 'Direct body retains distinct provenance for strict Core parsing'
        $response.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('text/plain')
        Assert-True ((Convert-HttpResponse $response ([Threading.CancellationToken]::None)).errorCode -ceq 'ContentType') 'Direct source rejects text/plain while mirror accepts GitHub raw'
    } finally { $response.Dispose(); $Source = $savedSource }
    $request = New-StatusRequest $null
    try { Assert-True ($request.Headers.IfNoneMatch.Count -eq 0) 'No cache input omits conditional header' } finally { $request.Dispose() }
    Assert-Rejected { New-StatusRequest '"x", "y"' } 'No raw ETag header injection'

    $requestPath = Join-Path $work ('.reset-request.' + $RequestId + '.json')
    function Write-TestRequest([string]$Json) { [IO.File]::WriteAllText($requestPath, $Json, [Text.UTF8Encoding]::new($false)) }
    Write-TestRequest ('{"schemaVersion":1,"requestId":"' + $RequestId + '","etag":"W/\"tag\""}')
    Assert-True ((Read-RequestETag $requestPath) -ceq 'W/"tag"') 'Bounded request-file ETag input'
    Write-TestRequest ('{"schemaVersion":1,"requestId":"' + $RequestId + '","etag":null}')
    Assert-True ($null -eq (Read-RequestETag $requestPath)) 'Nullable request-file ETag'
    Assert-True ($null -eq (Read-RequestETag '')) 'Optional request path'
    Assert-Rejected { Read-RequestETag (Join-Path $work 'arbitrary.json') } 'Noncanonical request filename rejected'
    foreach ($json in @(
        ('{"schemaVersion":2,"requestId":"' + $RequestId + '","etag":null}'),
        ('{"schemaVersion":true,"requestId":"' + $RequestId + '","etag":null}'),
        ('{"schemaVersion":1.0,"requestId":"' + $RequestId + '","etag":null}'),
        '{"schemaVersion":1,"requestId":"00000000-0000-0000-0000-000000000000","etag":null}',
        ('{"schemaVersion":1,"requestId":"' + $RequestId + '","etag":"*"}'),
        ('{"schemaVersion":1,"requestId":"' + $RequestId + '","etag":null,"url":"https://other.invalid"}'),
        ('{"schemaVersion":1,"requestId":"' + $RequestId + '","etag":null,"Keys":["schemaVersion","requestId","etag"]}'),
        ('{"schemaVersion":1,"requestId":"' + $RequestId + '","etag":null,"etag":null}'),
        (' ' * 2049)
    )) { Write-TestRequest $json; Assert-Rejected { Read-RequestETag $requestPath } 'Invalid request-file envelope rejected' }

    $proxy = [ResetTestProxy]::new()
    Assert-True ($null -eq (Get-ProxyFailureCode $proxy)) 'Anonymous default proxy accepted without routing inspection'
    $proxy.Value = [ResetTestCredentials]::new()
    Assert-True ((Get-ProxyFailureCode $proxy) -ceq 'ProxyCredentialsPresent') 'Proxy credential presence fails closed without inspecting secrets'
    $proxy.ThrowOnCredentials = $true
    Assert-True ((Get-ProxyFailureCode $proxy) -ceq 'ProxyInspectionFailed') 'Proxy credential getter failure fails closed'

    $fixture = '{"data":{"latest_reset":{"at":"2020-01-01T00:00:00Z","future":42},"scheduled_reset":null,"active_watch":{"unknownFutureShape":[1,2]}},"meta":{"api_version":"v1","future":{"ok":true}}}'
    $state = Convert-ApiState ([Text.Encoding]::UTF8.GetBytes($fixture))
    Assert-True ($state.status -ceq 'Success' -and $state.data.latest_reset.at -is [string] -and $state.data.latest_reset.at -ceq '2020-01-01T00:00:00Z') 'Historical reset timestamp retained as string'
    Assert-True ($state.data.latest_reset.future -eq 42 -and $state.data.active_watch.unknownFutureShape.Count -eq 2 -and $state.meta.future.ok) 'Whole original data/meta preserved'
    Assert-True ($null -eq $state.errorCode -and $null -eq $state.exceptionType -and $null -eq $state.nativeCode) 'Success has no error diagnostics'
    foreach ($json in @('{"data":{},"data":{},"meta":{}}', '{"data":{"a":{"x":1,"x":2}},"meta":{}}', '{"data":{"a":[{"x":1,"\u0078":2}]},"meta":{}}', '{"data":{},"meta":{"x":1,"x":2}}', '{"data":null,"meta":{}}', '{"Data":{},"Meta":{}}', '{"data":{},"meta":{},"unknownRoot":true}', '{"data":{},"meta":{},"Keys":["data","meta"]}', '{"data":{},"meta":[]}', '{"data":{},"meta":{}} trailing', '{"data":{},"meta":{},}', '[]', ('{"data":' + ('{"x":' * 12) + '0' + ('}' * 12) + ',"meta":{}}'))) {
        Assert-Rejected { Convert-ApiState ([Text.Encoding]::UTF8.GetBytes($json)) } 'Invalid/duplicate/deep JSON rejected'
    }
    Assert-Rejected { Convert-ApiState ([byte[]]@(0xff, 0xfe)) } 'Invalid UTF-8 rejected'
    Assert-Rejected { Convert-ApiState ([byte[]]::new(65537)) } 'Direct 64 KiB body bound'
    $exact = '{"data":{"x":"' + ('a' * (65536 - 27)) + '"},"meta":{}}'
    Assert-True ([Text.Encoding]::UTF8.GetByteCount($exact) -eq 65536) 'Exact-bound fixture size'
    Assert-True ((Convert-ApiState ([Text.Encoding]::UTF8.GetBytes($exact))).status -ceq 'Success') 'Exactly 64 KiB accepted'

    $mirrorFixture = '{"mirrorSchemaVersion":1,"upstreamSuccessAtUtc":"2026-10-26T09:00:00Z","source":"https://codex-resets.com/api/v1/status",' + $fixture.Substring(1)
    $mirrorState = Convert-MirrorState ([Text.Encoding]::UTF8.GetBytes($mirrorFixture))
    Assert-True ($mirrorState.mirror.upstreamSuccessAtUtc -ceq '2026-10-26T09:00:00Z') 'Actual upstream success timestamp preserved, not replaced with local time'
    Assert-True ($mirrorState.data.latest_reset.at -ceq '2020-01-01T00:00:00Z') 'Mirror preserves historical reset independently'
    foreach ($invalidMirror in @($fixture, $mirrorFixture.Replace('"mirrorSchemaVersion":1', '"mirrorSchemaVersion":2'), $mirrorFixture.Replace('"mirrorSchemaVersion":1', '"mirrorSchemaVersion":1,"mirrorSchemaVersion":1'), $mirrorFixture.Replace('https://codex-resets.com/api/v1/status', 'https://invalid.example/status'))) {
        Assert-Rejected { Convert-MirrorState ([Text.Encoding]::UTF8.GetBytes($invalidMirror)) } 'Mirror schema/provenance mandatory'
    }
    foreach ($status in @(304, 429, 500)) {
        $response = New-TestResponse $status
        $response.Headers.ETag = [Net.Http.Headers.EntityTagHeaderValue]::Parse('W/"response"')
        $response.Headers.RetryAfter = [Net.Http.Headers.RetryConditionHeaderValue]::new([TimeSpan]::FromSeconds(60))
        try {
            $state = Convert-HttpResponse $response ([Threading.CancellationToken]::None)
            Assert-True ($state.httpStatus -eq $status -and $state.etag -ceq 'W/"response"' -and $state.retryAfterSeconds -eq 60 -and $null -eq $state.data -and $null -eq $state.meta) 'HTTP status/metadata preserved without error body'
            if ($status -eq 304) { Assert-True ($state.status -ceq 'NotModified' -and $null -eq $state.errorCode) '304 normalization' }
            else { Assert-True ($state.status -ceq 'Error' -and $state.errorCode -ceq 'FetchFailed') '429/non-200 error normalization' }
        } finally { $response.Dispose() }
    }
    $response = New-TestResponse
    try {
        $now = [DateTimeOffset]::Parse('2026-10-22T06:00:00Z')
        $response.Headers.RetryAfter = [Net.Http.Headers.RetryConditionHeaderValue]::new($now.AddHours(2).ToOffset([TimeSpan]::FromHours(8)))
        Assert-True ((Get-RetryAfterSeconds $response $now) -eq 7200) 'Retry-After date uses UTC difference'
        $response.Headers.RetryAfter = [Net.Http.Headers.RetryConditionHeaderValue]::new($now.AddSeconds(-1))
        Assert-True ((Get-RetryAfterSeconds $response $now) -eq 0) 'Past Retry-After date is zero'
        $response.Headers.RetryAfter = [Net.Http.Headers.RetryConditionHeaderValue]::new([TimeSpan]::FromDays(7))
        Assert-True ((Get-RetryAfterSeconds $response) -eq 604800) 'Seven-day Retry-After accepted'
        $response.Headers.RetryAfter = [Net.Http.Headers.RetryConditionHeaderValue]::new([TimeSpan]::FromSeconds(604801))
        Assert-True ($null -eq (Get-RetryAfterSeconds $response)) 'Overlong Retry-After ignored'
        $null = $response.Headers.TryAddWithoutValidation('ETag', '"a", "b"')
        Assert-True ($null -eq (Get-ResponseETag $response)) 'Multiple response ETags ignored'
    } finally { $response.Dispose() }
    foreach ($type in @($null, 'application/json', 'application/json; charset=utf-8', 'text/plain; charset=utf-8', 'text/html')) {
        $response = New-TestResponse
        if ($null -ne $type) { $response.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse($type) }
        try {
            $state = Convert-HttpResponse $response ([Threading.CancellationToken]::None)
            if ($type -eq 'text/html') { Assert-True ($state.errorCode -ceq 'ContentType' -and $null -eq $state.data) 'Unsupported content type rejected' }
            else { Assert-True ($state.status -ceq 'Success') 'JSON/absent content type accepted' }
        } finally { $response.Dispose() }
    }
    foreach ($invalidType in @('not a media type', 'application/json, text/html')) {
        $response = New-TestResponse
        $null = $response.Content.Headers.TryAddWithoutValidation('Content-Type', $invalidType)
        try { Assert-True ((Convert-HttpResponse $response ([Threading.CancellationToken]::None)).errorCode -ceq 'ContentType') 'Malformed Content-Type rejected instead of treated as absent' } finally { $response.Dispose() }
    }
    $response = New-TestResponse 200 ([byte[]]::new(65537))
    try { Assert-True ((Convert-HttpResponse $response ([Threading.CancellationToken]::None)).errorCode -ceq 'ResponseTooLarge') 'Content-Length body bound' } finally { $response.Dispose() }
    $response = New-TestResponse 200 ([Text.Encoding]::UTF8.GetBytes('{"data":{"x":1,"x":2},"meta":{}}'))
    try { Assert-True ((Convert-HttpResponse $response ([Threading.CancellationToken]::None)).errorCode -ceq 'ParseFailed') 'Duplicate key response error classification' } finally { $response.Dispose() }
    foreach ($failRead in @($false, $true)) {
        $response = New-TestResponse
        $stream = [ResetTestStream]::new([byte[]]::new(65537)); $stream.FailRead = $failRead
        $response.Content.Dispose(); $response.Content = [Net.Http.StreamContent]::new($stream)
        try {
            Assert-True ($null -eq $response.Content.Headers.ContentLength) 'Unknown body length test path'
            $state = Convert-HttpResponse $response ([Threading.CancellationToken]::None)
            $expectedCode = if ($failRead) { 'ReadFailed' } else { 'ResponseTooLarge' }
            Assert-True ($state.errorCode -ceq $expectedCode -and $null -eq $state.data) 'Streaming bound/read failure classification'
        } finally { $response.Dispose() }
    }
    $response = New-TestResponse; $cts = [Threading.CancellationTokenSource]::new(); $cts.Cancel()
    try { Assert-True ((Convert-HttpResponse $response $cts.Token).errorCode -ceq 'DeadlineExceeded') 'Deadline error classification' } finally { $response.Dispose(); $cts.Dispose() }

    $private = 'PRIVATE_TOKEN PRIVATE_PATH PRIVATE_IP PRIVATE_BODY_DETAILS PRIVATE_PROXY_DETAILS'
    $exception = [Net.Http.HttpRequestException]::new($private, [Net.Sockets.SocketException]::new(10061))
    $state = New-FailureState $exception 'FetchFailed' $null $null $null
    $serialized = $state | ConvertTo-Json -Compress
    Assert-True ($state.exceptionType -ceq 'SocketException' -and $state.nativeCode -eq $exception.InnerException.NativeErrorCode) 'Nested socket native code with closed type'
    Assert-True ($serialized -notmatch 'PRIVATE_|Message|StackTrace|TargetSite|InnerException') 'Privacy-safe diagnostics contain no exception/raw details'
    $exception = [Exception]::new($private, [ComponentModel.Win32Exception]::new(5, $private))
    $state = New-FailureState $exception 'FetchFailed' $null $null $null
    Assert-True ($state.exceptionType -ceq 'Win32Exception' -and $state.nativeCode -eq 5) 'Nested Win32 native code'
    $exception = [Exception]::new($private)
    $state = New-FailureState $exception 'SetupFailed' $null $null $null
    Assert-True ($state.exceptionType -ceq 'Other' -and $state.nativeCode -eq $exception.HResult) 'Outer HResult fallback'

    $state = Convert-ApiState ([Text.Encoding]::UTF8.GetBytes($fixture))
    Publish-State $state
    $readBack = [IO.File]::ReadAllText($expectedOutput) | ConvertFrom-Json -AsHashtable -DateKind String
    Assert-True ($readBack.requestId -ceq $RequestId -and $readBack.status -ceq 'Success') 'Atomic initial publication'
    Publish-State (New-State 'Error' $null $null 'FetchFailed' 429 '"retained"' 30)
    $readBack = [IO.File]::ReadAllText($expectedOutput) | ConvertFrom-Json -AsHashtable
    Assert-True ($readBack.status -ceq 'Error' -and $readBack.httpStatus -eq 429 -and $null -eq $readBack.data) 'Atomic replacement publication'
    Assert-True (@(Get-ChildItem -LiteralPath $work -Filter '*.tmp').Count -eq 0) 'No leftover publication temporary file'
    Assert-Rejected { Publish-State (New-State 'Success' @{ x = ('a' * 98304) } @{} $null 200) } '96 KiB envelope bound before I/O'
    $readBack = [IO.File]::ReadAllText($expectedOutput) | ConvertFrom-Json -AsHashtable
    Assert-True ($readBack.status -ceq 'Error' -and $readBack.httpStatus -eq 429) 'Oversize never damages existing envelope'
    $collisionPath = Join-Path $work ('.reset-state.' + $RequestId + '.tmp')
    [IO.File]::WriteAllText($collisionPath, 'existing-owner')
    Assert-Rejected { Publish-State $state } 'Temporary-file collision aborts publication'
    Assert-True ([IO.File]::ReadAllText($collisionPath) -ceq 'existing-owner') 'Failed CreateNew never deletes someone else''s temporary file'
    [IO.File]::Delete($collisionPath)

    # End-to-end failure paths deliberately provide an invalid request file, so
    # the production script cannot reach its SendAsync call in these child runs.
    $entry = Join-Path $work 'entry'; $null = [IO.Directory]::CreateDirectory($entry)
    $entryScript = Join-Path $entry 'Fetch-ResetState.ps1'
    [IO.File]::Copy($SourcePath, $entryScript)
    $entryOutput = Join-Path $entry 'reset-state.json'
    $entryRequest = Join-Path $entry ('.reset-request.' + $RequestId + '.json')
    [IO.File]::WriteAllText($entryRequest, '{"schemaVersion":0,"requestId":"invalid","etag":null}')
    function Invoke-OfflineEntry([bool]$SelfTestEntry = $false) {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($arg in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $entryScript, '-RequestId', $RequestId, '-OutputPath', $entryOutput)) { $start.ArgumentList.Add($arg) }
        if ($SelfTestEntry) { $start.ArgumentList.Add('-SelfTest') }
        else { $start.ArgumentList.Add('-RequestPath'); $start.ArgumentList.Add($entryRequest) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $outTask = $process.StandardOutput.ReadToEndAsync(); $errTask = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Offline entry test timed out.' }
            return @{ code = $process.ExitCode; stdout = $outTask.GetAwaiter().GetResult(); stderr = $errTask.GetAwaiter().GetResult() }
        } finally { $process.Dispose() }
    }
    $entryResult = Invoke-OfflineEntry
    Assert-True ($entryResult.code -eq 1 -and $entryResult.stdout -ceq '' -and $entryResult.stderr -ceq '') 'Setup failure exits 1 with no raw output'
    $entryState = [IO.File]::ReadAllText($entryOutput) | ConvertFrom-Json -AsHashtable
    Assert-True ($entryState.errorCode -ceq 'SetupFailed' -and $entryState.requestId -ceq $RequestId -and $null -eq $entryState.httpStatus) 'Setup failure writes bounded diagnostic envelope before sending'
    $entryOld = [IO.File]::ReadAllBytes($entryOutput)
    $entryCollision = Join-Path $entry ('.reset-state.' + $RequestId + '.tmp')
    [IO.File]::WriteAllText($entryCollision, 'do-not-delete')
    $entryResult = Invoke-OfflineEntry
    Assert-True ($entryResult.code -eq 2 -and $entryResult.stdout -ceq '' -and $entryResult.stderr -ceq '') 'Write failure exits 2 without fallback or raw output'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($entryOutput)) -ceq [Convert]::ToBase64String($entryOld) -and [IO.File]::ReadAllText($entryCollision) -ceq 'do-not-delete') 'Failed write leaves previous state and unowned temporary file unchanged'
    [IO.File]::Delete($entryCollision); [IO.File]::Delete($entryOutput)
    $entryResult = Invoke-OfflineEntry $true
    Assert-True ($entryResult.code -eq 0 -and $entryResult.stdout.StartsWith('PASS: offline normalization') -and $entryResult.stderr -ceq '') 'Built-in offline SelfTest entry point passes'
    Assert-True (-not [IO.File]::Exists($entryOutput)) 'Built-in SelfTest cleans up isolated output'
    "PASS: $passed offline PowerShell checks; no network requests."
} finally { [IO.Directory]::Delete($work, $true) }
