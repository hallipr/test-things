#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Directory,
    [Parameter(Mandatory)][string] $StorageAccountName,
    [string] $Repository = 'hallipr/test-things',
    [string] $Ref = 'main',
    [string] $Subscription = 'd0c05057-7972-46ff-9bcf-3c932250155e'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$source = Get-Item -LiteralPath $Directory -Force
if (-not $source.PSIsContainer) { throw 'Directory must be a local directory.' }
$items = @(Get-ChildItem -LiteralPath $source.FullName -Recurse -Force)
if ($source.LinkType -or ($items | Where-Object LinkType)) {
    throw 'Symbolic links and junctions are not supported. Supply a directory of regular files.'
}
$files = @($items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
if ($files.Count -eq 0) { throw 'Directory must contain at least one file.' }
$expectedFile = [IO.Path]::GetRelativePath($source.FullName, $files[0].FullName).Replace('\', '/')
$expectedHash = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$requestId = [guid]::NewGuid().ToString('N')
$staging = "_staging/$requestId"
$storage = @('--account-name', $StorageAccountName, '--auth-mode', 'login', '--only-show-errors')

# Upload outside the authorization namespace; only the final rename grants access.
az storage blob upload-batch --subscription $Subscription @storage --destination runs `
    --destination-path $staging --source $source.FullName --overwrite false --output none
Write-Host "Uploaded $($files.Count) files to runs/$staging."

gh workflow run directory-read.yml --repo $Repository --ref $Ref `
    --raw-field "request_id=$requestId" --raw-field "expected_file=$expectedFile" `
    --raw-field "expected_sha256=$expectedHash"
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
$run = $null
do {
    $runs = gh run list --repo $Repository --workflow directory-read.yml --event workflow_dispatch `
        --limit 100 --json databaseId,displayTitle | ConvertFrom-Json
    $matches = @($runs | Where-Object displayTitle -EQ "Directory read $requestId")
    if ($matches.Count -gt 1) { throw "Ambiguous workflow correlation ID: $requestId" }
    if ($matches.Count -eq 1) { $run = $matches[0]; break }
    Start-Sleep -Seconds 3
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if (-not $run) { throw "Run not found. Staged data remains at runs/$staging; correlation ID $requestId." }

$repositoryId = gh api "repos/$Repository" --jq '.id'
$attempt = gh api "repos/$Repository/actions/runs/$($run.databaseId)" --jq '.run_attempt'
if ($repositoryId -notmatch '^[1-9][0-9]*$' -or $attempt -notmatch '^[1-9][0-9]*$') {
    throw 'GitHub returned invalid repository/run metadata.'
}
$runPath = "$repositoryId/$($run.databaseId)/$attempt"
foreach ($parent in @("$repositoryId", "$repositoryId/$($run.databaseId)")) {
    az storage fs directory create --subscription $Subscription @storage --file-system runs `
        --name $parent --output none
}
$exists = az storage fs directory exists --subscription $Subscription @storage --file-system runs `
    --name $runPath --query exists --output tsv
if ($exists -eq 'true') { throw "Refusing to overwrite an existing authorization directory: $runPath" }
az storage fs directory move --subscription $Subscription @storage --file-system runs `
    --name $staging --new-directory "runs/$runPath" --output none
$url = "https://github.com/$Repository/actions/runs/$($run.databaseId)"
Write-Host "Authorized runs/$runPath using $($files.Count) uploaded files."
Write-Host $url
gh run watch $run.databaseId --repo $Repository --exit-status
