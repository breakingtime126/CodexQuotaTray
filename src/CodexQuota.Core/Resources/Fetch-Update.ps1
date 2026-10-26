# Fixed public GitHub update feed only. No credentials, proxy/TLS overrides or elevation.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('manifest', 'asset')][string]$Operation,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$handler = $null; $client = $null; $response = $null; $request = $null
$inputStream = $null; $outputStream = $null; $deadline = $null
$output = Join-Path $PSScriptRoot 'download.bin'
$partial = Join-Path $PSScriptRoot 'download.partial'
function Assert-SafePath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe update path.' }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Assert-DownloadUri([Uri]$Uri) {
    if (-not $Uri.IsAbsoluteUri -or $Uri.Scheme -cne 'https' -or -not $Uri.IsDefaultPort -or
        -not [string]::IsNullOrEmpty($Uri.UserInfo) -or -not [string]::IsNullOrEmpty($Uri.Fragment)) { throw 'Unsafe update redirect.' }
    if (@('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com', 'github-releases.githubusercontent.com') -cnotcontains $Uri.DnsSafeHost.ToLowerInvariant()) { throw 'Unsafe update host.' }
    if ($Uri.DnsSafeHost -ieq 'github.com' -and -not $Uri.AbsolutePath.StartsWith('/breakingtime126/CodexQuotaTray/releases/', [StringComparison]::Ordinal)) { throw 'Unexpected update repository.' }
}
try {
    if ($PSVersionTable.PSVersion -lt [Version]'5.1') { throw 'PowerShell 5.1 or later required.' }
    Add-Type -AssemblyName System.Net.Http
    Assert-SafePath $PSScriptRoot
    Assert-SafePath $output
    Assert-SafePath $partial
    if ([IO.File]::Exists($output) -or [IO.File]::Exists($partial)) { throw 'Output already exists.' }
    if ($Operation -ceq 'manifest') {
        if (-not [string]::IsNullOrEmpty($Version)) { throw 'Unexpected version.' }
        $uri = [Uri]'https://github.com/breakingtime126/CodexQuotaTray/releases/latest/download/update.json'
        $limit = 32768
    } else {
        if ($Version -cnotmatch '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$') { throw 'Invalid update version.' }
        $parsed = [Version]::Parse($Version)
        if ($parsed.ToString(4) -cne $Version) { throw 'Noncanonical update version.' }
        $uri = [Uri]('https://github.com/breakingtime126/CodexQuotaTray/releases/download/v' + $Version + '/CodexQuotaTray.exe')
        $limit = 31457280
    }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $handler.Credentials = $null
    $handler.DefaultProxyCredentials = $null
    # Fail rather than silently forwarding credentials configured on a system proxy.
    if ($PSVersionTable.PSVersion.Major -ge 7) { $proxy = [Net.Http.HttpClient]::DefaultProxy }
    else { $proxy = [Net.WebRequest]::DefaultWebProxy }
    if ($null -ne $proxy -and $null -ne [Net.IWebProxy].GetProperty('Credentials').GetValue($proxy)) { throw 'Credentialed proxy not supported.' }
    # Windows PowerShell uses .NET Framework; pin this handler to TLS 1.2 only.
    # Never change ServicePointManager/global settings or certificate validation.
    if ($PSVersionTable.PSVersion.Major -lt 7) { $handler.SslProtocols = [Security.Authentication.SslProtocols]::Tls12 }
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(120))
    for ($redirects = 0; $redirects -le 5; $redirects++) {
        Assert-DownloadUri $uri
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
        $request.Headers.UserAgent.ParseAdd('CodexQuotaTray-Updater/1')
        $request.Headers.CacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
        $request.Headers.CacheControl.NoCache = $true
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        if ($status -in @(301, 302, 303, 307, 308)) {
            if ($redirects -eq 5 -or $null -eq $response.Headers.Location) { throw 'Too many redirects.' }
            $next = $response.Headers.Location
            if (-not $next.IsAbsoluteUri) { $next = [Uri]::new($uri, $next) }
            Assert-DownloadUri $next
            $uri = $next
            $response.Dispose(); $response = $null
            $request.Dispose(); $request = $null
            continue
        }
        if ($status -ne 200) { throw 'Update download failed.' }
        break
    }
    if ($null -eq $response -or [int]$response.StatusCode -ne 200) { throw 'Missing update response.' }
    $expectedLength = $response.Content.Headers.ContentLength
    if ($null -ne $expectedLength -and ($expectedLength -le 0 -or $expectedLength -gt $limit)) { throw 'Invalid response size.' }
    $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $outputStream = [IO.FileStream]::new($partial, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $buffer = [byte[]]::new(16384); [long]$total = 0
    while ($true) {
        $count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()
        if ($count -eq 0) { break }
        $total += $count
        if ($total -gt $limit) { throw 'Response too large.' }
        $outputStream.Write($buffer, 0, $count)
    }
    if ($total -eq 0 -or ($null -ne $expectedLength -and $total -ne $expectedLength)) { throw 'Truncated update response.' }
    $outputStream.Flush($true); $outputStream.Dispose(); $outputStream = $null
    Assert-SafePath $output
    [IO.File]::Move($partial, $output)
    exit 0
} catch {
    # Do not print signed CDN URLs, filesystem paths or environment details.
    exit 1
} finally {
    if ($null -ne $outputStream) { $outputStream.Dispose() }
    if ($null -ne $inputStream) { $inputStream.Dispose() }
    if ($null -ne $response) { $response.Dispose() }
    if ($null -ne $request) { $request.Dispose() }
    if ($null -ne $client) { $client.Dispose() } elseif ($null -ne $handler) { $handler.Dispose() }
    if ($null -ne $deadline) { $deadline.Dispose() }
}
