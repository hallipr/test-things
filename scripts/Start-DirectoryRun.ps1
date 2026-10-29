#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Directory,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{3,24}$')][string] $StorageAccountName,
    [string] $Repository = 'hallipr/test-things',
    [string] $Ref = 'main',
    [string] $Subscription = 'd0c05057-7972-46ff-9bcf-3c932250155e'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot\InvocationStorage.ps1"
$source = Get-Item -LiteralPath $Directory -Force
if (-not $source.PSIsContainer) { throw 'Directory must be a local directory.' }
$items = @(Get-ChildItem -LiteralPath $source.FullName -Recurse -Force)
if ($source.LinkType -or ($items | Where-Object LinkType)) {
    throw 'Symbolic links and junctions are not supported. Supply a directory of regular files.'
}
$files = @($items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
if ($files.Count -eq 0) { throw 'Directory must contain at least one file.' }
if (Get-ChildItem -LiteralPath $source.FullName -Force | Where-Object Name -IEQ '.authorization.json') {
    throw 'The root name .authorization.json is reserved for the operator-written run mapping.'
}
$expectedFile = [IO.Path]::GetRelativePath($source.FullName, $files[0].FullName).Replace('\', '/')
$expectedHash = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$repositoryId = gh api "repos/$Repository" --jq '.id'
if ($repositoryId -ne '1437763204') { throw 'The broker trusts only repository ID 1437763204 (hallipr/test-things).' }
$blobRoot = "https://$StorageAccountName.blob.core.windows.net/runs"
$token = az account get-access-token --subscription $Subscription --resource https://storage.azure.com/ --query accessToken -o tsv
$headers = @{ Authorization = "Bearer $token"; 'x-ms-version' = '2023-11-03' }
$invocationId = New-InvocationId -StorageRoot $blobRoot -Headers $headers
$storage = @('--account-name', $StorageAccountName, '--auth-mode', 'login', '--only-show-errors')
$exists = az storage fs directory exists --subscription $Subscription @storage --file-system runs `
    --name $invocationId --query exists --output tsv
if ($exists -eq 'true') { throw "Invocation directory runs/$invocationId already exists. The counter must not be reset." }

# A directory alone no longer grants access; the mapping is written only after dispatch.
Write-Host "Allocated invocation $invocationId. Its ID is never reused, even if a later operation fails."
az storage blob upload-batch --subscription $Subscription @storage --destination runs `
    --destination-path $invocationId --source $source.FullName --overwrite false --output none
Write-Host "Uploaded $($files.Count) files to runs/$invocationId (not yet authorized)."

$dispatch = @{
    ref = $Ref
    return_run_details = $true
    inputs = @{ invocation_id = $invocationId; expected_file = $expectedFile; expected_sha256 = $expectedHash }
} | ConvertTo-Json -Depth 3 -Compress
$created = $dispatch | gh api "repos/$Repository/actions/workflows/directory-read.yml/dispatches" `
    --method POST --input - | ConvertFrom-Json
$runId = [string]$created.workflow_run_id
if ($runId -notmatch '^[1-9][0-9]*$') { throw "Dispatch returned no run ID. Invocation $invocationId remains unauthorized." }
# Use the dispatch response, never a run's self-reported ID or a matching display name.
$run = gh api "repos/$Repository/actions/runs/$runId" | ConvertFrom-Json
if ([string]$run.repository.id -ne $repositoryId -or $run.event -ne 'workflow_dispatch' -or $run.run_attempt -ne 1) {
    throw "Unexpected metadata for dispatched run $runId. Invocation $invocationId remains unauthorized."
}
$authorization = @{
    invocationId = $invocationId; repositoryId = $repositoryId; runId = $runId; runAttempt = '1'
} | ConvertTo-Json -Compress
$token = az account get-access-token --subscription $Subscription --resource https://storage.azure.com/ --query accessToken -o tsv
$headers = @{
    Authorization = "Bearer $token"; 'x-ms-version' = '2023-11-03'; 'x-ms-date' = [DateTime]::UtcNow.ToString('R')
    'x-ms-blob-type' = 'BlockBlob'; 'If-None-Match' = '*'
}
$result = Invoke-WebRequest -Uri "$blobRoot/$invocationId/.authorization.json" -Method Put -Headers $headers `
    -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($authorization)) -SkipHttpErrorCheck -TimeoutSec 30
if ($result.StatusCode -ne 201) { throw "Cannot authorize invocation $invocationId for run ${runId}: HTTP $($result.StatusCode)." }
$url = "https://github.com/$Repository/actions/runs/$runId"
Write-Host "Authorized invocation $invocationId for repository $repositoryId, run $runId, attempt 1."
Write-Host $url
gh run watch $runId --repo $Repository --exit-status
