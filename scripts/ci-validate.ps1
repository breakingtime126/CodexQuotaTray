$ErrorActionPreference = 'Stop'
[xml]$props = Get-Content Directory.Build.props
$version = [string]$props.Project.PropertyGroup.FileVersion
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid FileVersion' }
if ([string]$props.Project.PropertyGroup.AssemblyVersion -ne $version) { throw 'AssemblyVersion mismatch' }
if ($env:GITHUB_EVENT_NAME -eq 'push') {
  if ($env:GITHUB_REF -ne "refs/tags/v$version") { throw 'Tag must match FileVersion' }
} elseif ($env:GITHUB_EVENT_NAME -eq 'workflow_dispatch') {
  if ($env:GITHUB_REF -ne 'refs/heads/main' -or $env:REQUESTED_VERSION -ne $version) { throw 'Manual release must run main with its exact FileVersion' }
} else { throw 'Unsupported event' }
$sha = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw 'Invalid HEAD' }
git merge-base --is-ancestor $sha origin/main
if ($LASTEXITCODE -ne 0) { throw 'Source is not in main history' }
$tagSha = git rev-parse --verify "refs/tags/v$version^{commit}" 2>$null
if ($LASTEXITCODE -eq 0 -and $tagSha.Trim() -ne $sha) { throw 'Existing release tag points elsewhere' }
"version=$version" >> $env:GITHUB_OUTPUT
"sha=$sha" >> $env:GITHUB_OUTPUT

# A missing tag is expected on first manual release; do not propagate git exit 128.
exit 0
