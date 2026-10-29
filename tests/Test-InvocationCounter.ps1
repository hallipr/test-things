#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\scripts\InvocationStorage.ps1"

function Response($Status, $Content = '', $Code = '', $ETag = '') {
    [pscustomobject]@{
        StatusCode = $Status
        Content = $Content
        Headers = @{ 'x-ms-error-code' = $Code; ETag = $ETag }
    }
}

function Invoke-WebRequest {
    param($Uri, $Method, $Headers, $ContentType, $Body, [switch]$SkipHttpErrorCheck, $TimeoutSec)
    if ($script:Responses.Count -eq 0) { throw 'Unexpected storage request.' }
    $script:Requests.Add(@{ Method = $Method; Headers = $Headers.Clone(); Body = $Body })
    $script:Responses.Dequeue()
}

function Start-Sleep { param($Milliseconds) }

function Test-Allocation($Name, $Responses, $ExpectedId, $ExpectedError = '') {
    $script:Responses = [Collections.Generic.Queue[object]]::new()
    foreach ($response in $Responses) { $script:Responses.Enqueue($response) }
    $script:Requests = [Collections.Generic.List[object]]::new()
    $errorMessage = ''
    try {
        $id = New-InvocationId -StorageRoot 'https://testaccount.blob.core.windows.net/runs' `
            -Headers @{ Authorization = 'Bearer test'; 'x-ms-version' = '2023-11-03' }
    } catch { $errorMessage = $_.Exception.Message }
    if ($ExpectedError) {
        if ($errorMessage -notlike $ExpectedError) { throw "${Name}: unexpected error '$errorMessage'." }
    } elseif ($errorMessage -or $id -cne $ExpectedId) {
        throw "${Name}: expected ID $ExpectedId; got '$id', error '$errorMessage'."
    }
    if ($script:Responses.Count -ne 0) { throw "${Name}: not all expected requests were made." }
    Write-Host "PASS: $Name"
}

Test-Allocation 'initial allocation is conditionally created' @(
    (Response 404 -Code BlobNotFound), (Response 201)
) '1'
if ($script:Requests[1].Headers['If-None-Match'] -ne '*') { throw 'Missing conditional creation.' }

Test-Allocation 'sequential allocation uses the observed ETag' @(
    (Response 200 '{"lastInvocationId":"41"}' -ETag '"v1"'), (Response 201)
) '42'
if ($script:Requests[1].Headers['If-Match'] -ne '"v1"') { throw 'Missing ETag precondition.' }

Test-Allocation 'concurrent update reloads counter after 412' @(
    (Response 200 '{"lastInvocationId":"4"}' -ETag '"v1"'), (Response 412),
    (Response 200 '{"lastInvocationId":"5"}' -ETag '"v2"'), (Response 201)
) '6'
if ($script:Requests[3].Headers['If-Match'] -ne '"v2"') { throw 'Retry reused a stale ETag.' }

Test-Allocation 'concurrent initialization does not duplicate ID 1' @(
    (Response 404 -Code BlobNotFound), (Response 409 -Code BlobAlreadyExists),
    (Response 200 '{"lastInvocationId":"1"}' -ETag '"v1"'), (Response 201)
) '2'

foreach ($invalid in @('{"lastInvocationId":"01"}', '{"lastInvocationId":1}',
    '{"lastInvocationId":"9223372036854775807"}', '{"lastInvocationId":"-1"}', '{}')) {
    Test-Allocation "invalid counter $invalid fails closed" @(
        (Response 200 $invalid -ETag '"v1"')
    ) '' 'Invalid or exhausted invocation counter*'
}
Test-Allocation 'missing container is not a new counter' @(
    (Response 404 -Code ContainerNotFound)
) '' 'Cannot read invocation counter: HTTP 404.'
Test-Allocation 'storage outage is not a new counter' @(
    (Response 503)
) '' 'Cannot read invocation counter: HTTP 503.'
Test-Allocation 'counter write failure is surfaced' @(
    (Response 200 '{"lastInvocationId":"2"}' -ETag '"v1"'), (Response 403)
) '' 'Cannot advance invocation counter: HTTP 403.'
