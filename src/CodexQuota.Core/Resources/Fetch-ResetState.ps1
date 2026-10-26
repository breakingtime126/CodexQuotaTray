# Fixed GitHub public mirror only; redirects are rejected, with no original-site fallback. Preserve system TLS/proxy routing; never send credentials.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')][string]$RequestId,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$RequestPath,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$limit = 65536
$envelopeLimit = 98304
$expectedOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'reset-state.json'))
# An invalid destination must not cause a write or expose filesystem diagnostics.
try { if ([IO.Path]::GetFullPath($OutputPath) -ne $expectedOutput) { exit 2 } } catch { exit 2 }

function New-State($Status, $Data, $Meta, $ErrorCode, $HttpStatus, $ETag = $null, $RetryAfterSeconds = $null, $ExceptionType = $null, $NativeCode = $null) {
    [ordered]@{ schemaVersion = 1; requestId = $RequestId; status = $Status
        fetchedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); httpStatus = $HttpStatus
        etag = $ETag; retryAfterSeconds = $RetryAfterSeconds
        data = $Data; meta = $Meta; errorCode = $ErrorCode
        exceptionType = $ExceptionType; nativeCode = $NativeCode }
}
function Throw-StateFailure([string]$Code) {
    $failure = [InvalidOperationException]::new('Reset state operation failed.')
    $failure.Data['ResetStateErrorCode'] = $Code
    throw $failure
}
function Test-EntityTag($Value) {
    if ($Value -isnot [string] -or $Value.Length -gt 256 -or $Value -cnotmatch '^(?:W/)?"[\x21\x23-\x7e\x80-\xff]*"$') { return $false }
    $parsed = $null
    return [Net.Http.Headers.EntityTagHeaderValue]::TryParse($Value, [ref]$parsed)
}
function Assert-UniqueJsonKeys([Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { Throw-StateFailure 'ParseFailed' }
            Assert-UniqueJsonKeys $property.Value
        }
    } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-UniqueJsonKeys $item }
    }
}
function Convert-StrictJson([byte[]]$Bytes) {
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 12
    $document = [Text.Json.JsonDocument]::Parse($json, $options)
    try {
        Assert-UniqueJsonKeys $document.RootElement
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { Throw-StateFailure 'ParseFailed' }
    } finally { $document.Dispose() }
    return ($json | ConvertFrom-Json -AsHashtable -Depth 12 -DateKind String)
}
function Read-RequestETag([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return $null }
    $expectedRequest = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('.reset-request.' + $RequestId + '.json')))
    if ([IO.Path]::GetFullPath($Path) -ne $expectedRequest) { Throw-StateFailure 'SetupFailed' }
    $stream = [IO.FileStream]::new($expectedRequest, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt 2048) { Throw-StateFailure 'SetupFailed' }
        $bytes = [byte[]]::new(2049)
        $length = 0
        while ($length -lt $bytes.Length) {
            $count = $stream.Read($bytes, $length, $bytes.Length - $length)
            if ($count -eq 0) { break }
            $length += $count
        }
        if ($length -gt 2048) { Throw-StateFailure 'SetupFailed' }
        $payload = [byte[]]::new($length)
        [Array]::Copy($bytes, $payload, $length)
    } finally { $stream.Dispose() }
    try { $inputState = Convert-StrictJson $payload } catch { Throw-StateFailure 'SetupFailed' }
    $keys = @($inputState.psbase.Keys)
    if ($keys.Count -ne 3 -or -not ($keys -ccontains 'schemaVersion') -or -not ($keys -ccontains 'requestId') -or -not ($keys -ccontains 'etag') -or
        ($inputState.schemaVersion -isnot [long] -and $inputState.schemaVersion -isnot [int]) -or $inputState.schemaVersion -ne 1 -or
        $inputState.requestId -isnot [string] -or $inputState.requestId -cne $RequestId -or
        ($null -ne $inputState.etag -and -not (Test-EntityTag $inputState.etag))) { Throw-StateFailure 'SetupFailed' }
    return $inputState.etag
}
function Get-ProxyFailureCode($Proxy) {
    # Invoke the CLR accessor explicitly: PowerShell property syntax can suppress getter failures.
    try {
        if ($null -ne $Proxy -and $null -ne [Net.IWebProxy].GetProperty('Credentials').GetValue($Proxy)) { return 'ProxyCredentialsPresent' }
    }
    catch { return 'ProxyInspectionFailed' }
    return $null
}
function New-StatusRequest($ETag) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, 'https://raw.githubusercontent.com/breakingtime126/CodexQuotaTray/reset-data/status.json')
    try {
        $cacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
        $cacheControl.NoCache = $true
        $cacheControl.MaxAge = [TimeSpan]::Zero
        $request.Headers.CacheControl = $cacheControl
        $request.Headers.Pragma.ParseAdd('no-cache')
        $request.Headers.Accept.ParseAdd('application/json')
        if ($null -ne $ETag) {
            if (-not (Test-EntityTag $ETag)) { Throw-StateFailure 'SetupFailed' }
            $request.Headers.IfNoneMatch.ParseAdd($ETag)
        }
        return $request
    } catch { $request.Dispose(); throw }
}
function Get-ResponseETag([Net.Http.HttpResponseMessage]$Response) {
    try {
        $values = $null
        if ($Response.Headers.TryGetValues('ETag', [ref]$values)) {
            $tags = @($values)
            if ($tags.Count -eq 1 -and (Test-EntityTag $tags[0])) { return $tags[0] }
        }
    } catch { }
    return $null
}
function Get-RetryAfterSeconds([Net.Http.HttpResponseMessage]$Response, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow) {
    try {
        $retry = $Response.Headers.RetryAfter
        if ($null -eq $retry) { return $null }
        if ($null -ne $retry.Delta) { $seconds = [Math]::Ceiling($retry.Delta.TotalSeconds) }
        elseif ($null -ne $retry.Date) { $seconds = [Math]::Max(0, [Math]::Ceiling(($retry.Date.ToUniversalTime() - $Now.ToUniversalTime()).TotalSeconds)) }
        else { return $null }
        if ($seconds -ge 0 -and $seconds -le 604800) { return [int]$seconds }
    } catch { }
    return $null
}
function New-FailureState([Exception]$Exception, [string]$FallbackCode, $HttpStatus, $ETag, $RetryAfterSeconds, [bool]$DeadlineExceeded = $false) {
    $code = $FallbackCode
    $exceptionType = 'Other'
    $nativeCode = [int]$Exception.HResult
    $nativeFound = $false
    $allowedCodes = @('FetchFailed', 'ReadFailed', 'ParseFailed', 'DeadlineExceeded', 'ResponseTooLarge', 'ContentType', 'ProxyCredentialsPresent', 'ProxyInspectionFailed', 'SetupFailed')
    $current = $Exception
    for ($depth = 0; $null -ne $current -and $depth -lt 16; $depth++) {
        if ($allowedCodes -ccontains $current.Data['ResetStateErrorCode']) { $code = $current.Data['ResetStateErrorCode'] }
        if ($current -is [Net.Sockets.SocketException]) {
            $exceptionType = 'SocketException'
            if (-not $nativeFound) { $nativeCode = [int]$current.NativeErrorCode; $nativeFound = $true }
        } elseif ($current -is [ComponentModel.Win32Exception]) {
            if (-not $nativeFound) { $exceptionType = 'Win32Exception'; $nativeCode = [int]$current.NativeErrorCode; $nativeFound = $true }
        } elseif (-not $nativeFound) {
            if ($current -is [OperationCanceledException]) { $exceptionType = 'OperationCanceledException' }
            elseif ($current -is [Text.Json.JsonException]) { $exceptionType = 'JsonException' }
            elseif ($current -is [Net.Http.HttpRequestException]) { $exceptionType = 'HttpRequestException' }
            elseif ($current -is [IO.InvalidDataException]) { $exceptionType = 'InvalidDataException' }
            elseif ($current -is [IO.IOException]) { $exceptionType = 'IOException' }
            elseif ($current -is [UnauthorizedAccessException]) { $exceptionType = 'UnauthorizedAccessException' }
            elseif ($current -is [InvalidOperationException]) { $exceptionType = 'InvalidOperationException' }
            elseif ($current -is [NotSupportedException]) { $exceptionType = 'NotSupportedException' }
        }
        $current = $current.InnerException
    }
    if ($DeadlineExceeded) { $code = 'DeadlineExceeded' }
    New-State 'Error' $null $null $code $HttpStatus $ETag $RetryAfterSeconds $exceptionType $nativeCode
}
function Convert-ApiState([byte[]]$Bytes) {
    if ($Bytes.Length -gt $limit) { Throw-StateFailure 'ResponseTooLarge' }
    $api = Convert-StrictJson $Bytes
    if (@($api.psbase.Keys).Count -ne 2 -or -not (@($api.psbase.Keys) -ccontains 'data') -or -not (@($api.psbase.Keys) -ccontains 'meta') -or $api.data -isnot [Collections.IDictionary] -or $api.meta -isnot [Collections.IDictionary]) { Throw-StateFailure 'ParseFailed' }
    # Preserve all original API data/meta; past reset timestamps remain valid.
    New-State 'Success' $api.data $api.meta $null 200
}
function Convert-MirrorState([byte[]]$Bytes) {
    if ($Bytes.Length -gt $limit) { Throw-StateFailure 'ResponseTooLarge' }
    $mirror = Convert-StrictJson $Bytes
    $required = @('mirrorSchemaVersion', 'upstreamSuccessAtUtc', 'source', 'data', 'meta')
    if (@($mirror.psbase.Keys).Count -ne 5) { Throw-StateFailure 'ParseFailed' }
    foreach ($key in $required) { if (-not (@($mirror.psbase.Keys) -ccontains $key)) { Throw-StateFailure 'ParseFailed' } }
    if (($mirror.mirrorSchemaVersion -isnot [long] -and $mirror.mirrorSchemaVersion -isnot [int]) -or
        $mirror.mirrorSchemaVersion -ne 1 -or $mirror.source -cne 'https://codex-resets.com/api/v1/status' -or
        $mirror.upstreamSuccessAtUtc -isnot [string] -or $mirror.upstreamSuccessAtUtc.Length -gt 64 -or
        $mirror.data -isnot [Collections.IDictionary] -or $mirror.meta -isnot [Collections.IDictionary]) { Throw-StateFailure 'ParseFailed' }
    # Core independently validates timestamp, TTL, provenance and complete API schema.
    $state = New-State 'Success' $mirror.data $mirror.meta $null 200
    $state['mirror'] = [ordered]@{ mirrorSchemaVersion = $mirror.mirrorSchemaVersion
        upstreamSuccessAtUtc = $mirror.upstreamSuccessAtUtc; source = $mirror.source }
    return $state
}
function Convert-HttpResponse([Net.Http.HttpResponseMessage]$Response, [Threading.CancellationToken]$Token) {
    $httpStatus = [int]$Response.StatusCode
    $etag = Get-ResponseETag $Response
    $retryAfter = Get-RetryAfterSeconds $Response
    if ($httpStatus -eq 304) { return (New-State 'NotModified' $null $null $null 304 $etag $retryAfter) }
    if ($httpStatus -ne 200) { return (New-State 'Error' $null $null 'FetchFailed' $httpStatus $etag $retryAfter) }
    $body = $null; $buffered = $null; $stage = 'ContentType'
    try {
        $typeValues = $null
        if ($Response.Content.Headers.TryGetValues('Content-Type', [ref]$typeValues)) {
            $types = @($typeValues)
            $contentType = $null
            if ($types.Count -ne 1 -or -not [Net.Http.Headers.MediaTypeHeaderValue]::TryParse($types[0], [ref]$contentType) -or
                (-not [string]::Equals($contentType.MediaType, 'application/json', [StringComparison]::OrdinalIgnoreCase) -and
                 -not [string]::Equals($contentType.MediaType, 'text/plain', [StringComparison]::OrdinalIgnoreCase))) { Throw-StateFailure 'ContentType' }
        }
        $stage = 'ReadFailed'
        if ($null -ne $Response.Content.Headers.ContentLength -and $Response.Content.Headers.ContentLength -gt $limit) { Throw-StateFailure 'ResponseTooLarge' }
        $body = $Response.Content.ReadAsStreamAsync($Token).GetAwaiter().GetResult()
        $buffered = [IO.MemoryStream]::new()
        $chunk = [byte[]]::new(4096)
        while (($count = $body.ReadAsync($chunk, 0, $chunk.Length, $Token).GetAwaiter().GetResult()) -gt 0) {
            if ($buffered.Length + $count -gt $limit) { Throw-StateFailure 'ResponseTooLarge' }
            $buffered.Write($chunk, 0, $count)
        }
        $Token.ThrowIfCancellationRequested()
        $stage = 'ParseFailed'
        $state = Convert-MirrorState $buffered.ToArray()
        $Token.ThrowIfCancellationRequested()
        $state.etag = $etag
        $state.retryAfterSeconds = $retryAfter
        return $state
    } catch { return (New-FailureState $_.Exception $stage $httpStatus $etag $retryAfter $Token.IsCancellationRequested) }
    finally { foreach ($resource in @($body, $buffered)) { if ($null -ne $resource) { $resource.Dispose() } } }
}
function Publish-State($State) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($State | ConvertTo-Json -Depth 16 -Compress))
    if ($bytes.Length -gt $envelopeLimit) {
        $failure = [InvalidOperationException]::new('Reset state envelope is too large.')
        $failure.Data['ResetEnvelopeTooLarge'] = $true
        throw $failure
    }
    $temp = Join-Path $PSScriptRoot ('.reset-state.' + $RequestId + '.tmp')
    $ownsTemp = $false
    try {
        $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $ownsTemp = $true
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ([IO.File]::Exists($expectedOutput)) { [IO.File]::Replace($temp, $expectedOutput, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temp, $expectedOutput) }
    } finally { if ($ownsTemp -and [IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}

if ($SelfTest) {
    # Offline fixtures only; never overwrite an existing real output during tests.
    if ([IO.File]::Exists($expectedOutput)) { throw 'SelfTest requires no existing reset-state.json.' }
    $fixture = '{"data":{"latest_reset":{"at":"2020-01-01T00:00:00Z"},"scheduled_reset":null,"active_watch":{"unknownFutureShape":[1,2]}},"meta":{"api_version":"v1"}}'
    $state = Convert-ApiState ([Text.Encoding]::UTF8.GetBytes($fixture))
    if ($state.status -ne 'Success' -or $state.data.latest_reset.at -ne '2020-01-01T00:00:00Z' -or $state.data.active_watch.unknownFutureShape.Count -ne 2) { throw 'Fixture validation failed' }
    $mirrorFixture = '{"mirrorSchemaVersion":1,"upstreamSuccessAtUtc":"' + [DateTimeOffset]::UtcNow.ToString('o') + '","source":"https://codex-resets.com/api/v1/status",' + $fixture.Substring(1)
    $state = Convert-MirrorState ([Text.Encoding]::UTF8.GetBytes($mirrorFixture))
    if ($state.mirror.mirrorSchemaVersion -ne 1 -or $state.data.latest_reset.at -ne '2020-01-01T00:00:00Z') { throw 'Mirror fixture failed' }
    foreach ($invalid in @(([byte[]]::new(65537)), ([Text.Encoding]::UTF8.GetBytes('{"data":null,"meta":{}}')), ([Text.Encoding]::UTF8.GetBytes('{"data":{"x":1,"x":2},"meta":{}}')))) {
        $rejected = $false
        try { $null = Convert-ApiState $invalid } catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid fixture was accepted' }
    }
    try {
        Publish-State $state
        $readBack = Get-Content -LiteralPath $expectedOutput -Raw | ConvertFrom-Json -AsHashtable
        if ($readBack.requestId -ne $RequestId -or $readBack.status -ne 'Success') { throw 'Readback failed' }
        Publish-State (New-State 'Error' $null $null 'FetchFailed' $null)
        $readBack = Get-Content -LiteralPath $expectedOutput -Raw | ConvertFrom-Json -AsHashtable
        if ($readBack.status -ne 'Error' -or $null -ne $readBack.data) { throw 'Atomic replacement failed' }
    } finally { if ([IO.File]::Exists($expectedOutput)) { [IO.File]::Delete($expectedOutput) } }
    'PASS: offline normalization, atomic creation/replacement/readback, past reset, unknown fields, body bound and duplicate rejection'
    exit 0
}

$handler = $null; $client = $null; $response = $null; $request = $null; $cts = $null
$state = $null; $exitCode = 0; $stage = 'SetupFailed'
try {
    if ($PSVersionTable.PSVersion -lt [version]'7.5') { Throw-StateFailure 'SetupFailed' }
    $etag = Read-RequestETag $RequestPath
    try { $proxyFailure = Get-ProxyFailureCode ([Net.Http.HttpClient].GetProperty('DefaultProxy').GetValue($null)) }
    catch { Throw-StateFailure 'ProxyInspectionFailed' }
    if ($null -ne $proxyFailure) { Throw-StateFailure $proxyFailure }
    $cts = [Threading.CancellationTokenSource]::new(12000)
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $handler.Credentials = $null
    $handler.DefaultProxyCredentials = $null
    $handler.PreAuthenticate = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $request = New-StatusRequest $etag
    $stage = 'FetchFailed'
    $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cts.Token).GetAwaiter().GetResult()
    $state = Convert-HttpResponse $response $cts.Token
} catch {
    $state = New-FailureState $_.Exception $stage $null $null $null ($null -ne $cts -and $cts.IsCancellationRequested)
} finally {
    foreach ($resource in @($request, $response, $client, $handler, $cts)) { if ($null -ne $resource) { $resource.Dispose() } }
}
if ($state.status -eq 'Error') { $exitCode = 1 }
try { Publish-State $state } catch {
    # Only a pre-I/O size failure may use a smaller envelope. Never retry denied I/O.
    if ($_.Exception.Data['ResetEnvelopeTooLarge'] -ne $true) { exit 2 }
    $exitCode = 1
    try { Publish-State (New-State 'Error' $null $null 'ResponseTooLarge' $state.httpStatus $state.etag $state.retryAfterSeconds) } catch { exit 2 }
}
exit $exitCode
