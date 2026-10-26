# Offline producer -> actual compiled Core protocol/parser/presentation checks. No request is sent.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$CoreDll,
      [string]$SourcePath = (Join-Path $PSScriptRoot '../src/CodexQuota.Core/Resources/Fetch-ResetState.ps1'))
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($CoreDll))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($SourcePath), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Invalid producer syntax.' }
foreach ($function in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$RequestId = '3e3c00fd-e1b4-43cf-a25a-63474ec451d5'
$limit = 65536; $envelopeLimit = 98304
$now = [DateTimeOffset]::UtcNow
$request = [CodexQuota.Core.ResetFeedFileRequest]::new($RequestId, $now, $null)
$generated = $now.ToString('o')
$api = '{"data":{"latest_reset":{"id":"observed-history","reset_type":"regular","announced_at":"2020-01-01T00:00:00Z","text":"Historical observation.","source":{"type":"observed"}},"scheduled_reset":null,"active_watch":null,"stats":{"total":1,"last_reset_at":"2020-01-01T00:00:00Z","days_since_last":1,"avg_interval_days":null}},"meta":{"api_version":"v1","generated_at":"' + $generated + '"}}'
$state = Convert-ApiState ([Text.Encoding]::UTF8.GetBytes($api))
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($state | ConvertTo-Json -Depth 16 -Compress))
$response = [CodexQuota.Core.ResetFeedFileProtocol]::Parse($bytes, $request, [DateTimeOffset]::UtcNow)
try {
    $body = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $snapshot = $null
    if (-not [CodexQuota.Core.ResetFeedParser]::TryParse($body, [ref]$snapshot)) { throw 'Producer body was rejected by Core.' }
    $result = [CodexQuota.Core.ResetFeedReadResult]::new([CodexQuota.Core.ResetFeedReadStatus]::Success, $snapshot, $null, $null)
    $view = [CodexQuota.Core.ResetFeedPresentation]::Create($result, [DateTimeOffset]::UtcNow)
    if ($null -eq $view.Latest -or $null -ne $view.Forecast -or $null -ne $view.EventIdentity) { throw 'Historical latest-only presentation failed.' }
} finally { $response.Dispose() }
$state = New-State 'NotModified' $null $null $null 304 '"fixture"'
$bytes = [Text.Encoding]::UTF8.GetBytes(($state | ConvertTo-Json -Compress))
$response = [CodexQuota.Core.ResetFeedFileProtocol]::Parse($bytes, $request, [DateTimeOffset]::UtcNow)
try { if ([int]$response.StatusCode -ne 304 -or $response.Headers.ETag.ToString() -ne '"fixture"') { throw '304 metadata mismatch.' } }
finally { $response.Dispose() }
$state = New-State 'Error' $null $null 'FetchFailed' 429 $null 1200
$bytes = [Text.Encoding]::UTF8.GetBytes(($state | ConvertTo-Json -Compress))
$response = [CodexQuota.Core.ResetFeedFileProtocol]::Parse($bytes, $request, [DateTimeOffset]::UtcNow)
try { if ([int]$response.StatusCode -ne 429 -or $response.Headers.RetryAfter.Delta.TotalSeconds -ne 1200) { throw '429 metadata mismatch.' } }
finally { $response.Dispose() }
$state = New-FailureState ([IO.IOException]::new('PRIVATE_NOT_FOR_OUTPUT')) 'ReadFailed' 200 $null $null
$json = $state | ConvertTo-Json -Compress
if ($json.Contains('PRIVATE_NOT_FOR_OUTPUT')) { throw 'Diagnostic privacy failure.' }
$rejected = $false
try { $null = [CodexQuota.Core.ResetFeedFileProtocol]::Parse([Text.Encoding]::UTF8.GetBytes($json), $request, [DateTimeOffset]::UtcNow) }
catch { if ($_.Exception.InnerException.Failure.ToString() -eq 'ReadFailed' -and $_.Exception.InnerException.HttpStatus -eq 200) { $rejected = $true } else { throw } }
if (-not $rejected) { throw 'Closed diagnostic mapping failed.' }
'PASS: actual producer -> Core success/null diagnostic fields/historical latest/no forecast, 304, 429, and sanitized read failure. No network.'
