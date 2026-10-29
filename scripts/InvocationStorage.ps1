function New-InvocationId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $StorageRoot,
        [Parameter(Mandatory)][hashtable] $Headers
    )

    $uri = "$StorageRoot/_control/invocation-counter.json"
    for ($retry = 0; $retry -lt 30; $retry++) {
        $requestHeaders = $Headers.Clone()
        $requestHeaders['x-ms-date'] = [DateTime]::UtcNow.ToString('R')
        $current = Invoke-WebRequest -Uri $uri -Method Get -Headers $requestHeaders `
            -SkipHttpErrorCheck -TimeoutSec 30
        if ($current.StatusCode -eq 404 -and $current.Headers['x-ms-error-code'] -eq 'BlobNotFound') {
            $next = 1L
            $requestHeaders['If-None-Match'] = '*'
        } elseif ($current.StatusCode -eq 200) {
            $counter = $current.Content | ConvertFrom-Json
            $last = 0L
            if ($counter.lastInvocationId -isnot [string] -or
                -not [long]::TryParse($counter.lastInvocationId, [ref]$last) -or $last -lt 1 -or
                $last.ToString([Globalization.CultureInfo]::InvariantCulture) -cne $counter.lastInvocationId -or
                $last -eq [long]::MaxValue -or -not $current.Headers['ETag']) {
                throw 'Invalid or exhausted invocation counter; refusing to reset it.'
            }
            $next = $last + 1L
            $requestHeaders['If-Match'] = @($current.Headers['ETag'])[0]
        } else {
            throw "Cannot read invocation counter: HTTP $($current.StatusCode)."
        }
        $body = @{ lastInvocationId = $next.ToString([Globalization.CultureInfo]::InvariantCulture) } | ConvertTo-Json -Compress
        $requestHeaders['x-ms-blob-type'] = 'BlockBlob'
        $result = Invoke-WebRequest -Uri $uri -Method Put -Headers $requestHeaders -ContentType 'application/json' `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) -SkipHttpErrorCheck -TimeoutSec 30
        if ($result.StatusCode -eq 201) { return $next.ToString([Globalization.CultureInfo]::InvariantCulture) }
        if ($result.StatusCode -ne 412 -and
            -not ($result.StatusCode -eq 409 -and $result.Headers['x-ms-error-code'] -eq 'BlobAlreadyExists')) {
            throw "Cannot advance invocation counter: HTTP $($result.StatusCode)."
        }
        Start-Sleep -Milliseconds (Get-Random -Minimum 50 -Maximum 250)
    }
    throw 'Invocation counter remained busy after 30 conditional-write attempts.'
}
