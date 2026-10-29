# GitHub OIDC directory access POC

A GitHub Actions job exchanges its signed OIDC token for a **60-minute,
HTTPS-only, read/list directory SAS**. No Azure credentials are stored in GitHub.
The Function validates GitHub's signature, issuer, audience, expiry, repository
`hallipr/test-things` (immutable ID `1437763204`), run ID and run attempt.

Files are staged before the workflow is queued. Authorization is a trusted blob
mapping the invocation to the run, not the directory's existence or the ID a job
supplies:

```text
runs/
  _control/invocation-counter.json
  1/
    .authorization.json
    ...input files...
```

For example, `1/.authorization.json` contains:

```json
{"invocationId":"1","repositoryId":"1437763204","runId":"2342423","runAttempt":"1"}
```

Only the local operator writes that blob, after GitHub returns the queued run's
ID. The job requests `POST /api/directory-access/1`. The Function checks the
mapping against signed OIDC claims before issuing a SAS for directory `1`
(`sr=d`, `sdd=1`). Supplying another invocation ID does not authorize access.
All jobs in the mapped run attempt may read its descendants; other runs/attempts
and writes are denied. Missing mappings deny access; corrupt mappings and service
failures fail closed with an upstream error.

## Deploy

Requires PowerShell 7, Azure CLI with Bicep, GitHub CLI, and .NET SDK 10.0.403.
Use `az login` and `gh auth login` first.

```powershell
$subscription = 'd0c05057-7972-46ff-9bcf-3c932250155e'
$group = 'pahallis-sandbox'
az deployment group create --subscription $subscription --resource-group $group `
  --name github-oidc-blob-poc --template-file .\infra\main.bicep `
  --parameters .\infra\sandbox.bicepparam
$outputs = az deployment group show --subscription $subscription --resource-group $group `
  --name github-oidc-blob-poc --query properties.outputs | ConvertFrom-Json
dotnet publish .\src\DirectoryAccess.Functions -c Release -o .\publish
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path .\publish), "$PWD\function.zip")
az functionapp deployment source config-zip --subscription $subscription --resource-group $group `
  --name $outputs.functionAppName.value --src .\function.zip --build-remote false
gh variable set DIRECTORY_BROKER_URL --repo hallipr/test-things --body $outputs.brokerUrl.value
```

The template uses the **existing `pahallis-sandbox-identity`** user-assigned
managed identity in that group. It receives Blob Data Reader on `runs` and Blob
Delegator on the HNS account. Separate non-HNS host storage receives the runtime
and deployment roles. Both accounts disallow shared-key access and anonymous
blobs. Public HTTPS endpoints are necessary for the GitHub-hosted runner.
Hosting is Linux Flex Consumption, .NET 10 isolated.

The local operator needs **Storage Blob Data Contributor** on the run-storage
account (management-plane Contributor alone does not grant data access). Allow
time for new RBAC assignments to propagate. Anyone sharing the managed identity
or able to write authorization blobs is inside this POC's trust boundary.
Do not grant that identity broader write roles on the run-storage account.

## Upload a directory and run

```powershell
.\scripts\Start-DirectoryRun.ps1 -Directory C:\path\to\input `
  -StorageAccountName $outputs.runStorageAccountName.value
```

The script allocates a storage-backed sequential invocation ID, uploads all
files (including hidden files) to that directory, then dispatches the workflow
with the invocation ID. GitHub's dispatch API returns the actual run ID using
`return_run_details`; there is no display-name polling or self-reported run ID.
The script then conditionally creates `.authorization.json`, refusing to
overwrite an existing mapping. That reserved root name cannot appear in inputs.
The workflow waits up to ten minutes, downloads the directory into runner
temporary storage (including paginated listings), checks a supplied file's SHA256,
and verifies that arbitrary invocation requests, sibling/control reads and lists,
and writes fail. The authorization blob is checked but excluded from input files.
The script
waits for the workflow result and fails if it fails. Symlinks/junctions and empty
inputs are rejected. Only upload data intended for that workflow.

If dispatch/authorization fails, the script reports failure rather than granting
another run access; staged data may remain for operator cleanup. The counter uses
ETag compare-and-swap and conditional creation, so concurrent operators cannot
allocate the same ID. IDs are never reused; failures can leave gaps. Never delete
or reset the counter. Rerunning an existing GitHub run increments `run_attempt`
and is not authorized by the original mapping; stage a new invocation/run.

## Test locally

```powershell
dotnet test .\tests\DirectoryAccess.Tests -c Release
pwsh -File .\tests\Test-InvocationCounter.ps1
az bicep build --file .\infra\main.bicep
```

For local Function hosting, copy
`src/DirectoryAccess.Functions/local.settings.example.json` to
`local.settings.json` in the same folder and set the real DFS endpoint.
Azure Functions Core Tools and host storage/Azurite are additionally required.
`Development` explicitly allows developer Azure credentials; production requires
`AZURE_CLIENT_ID` and uses only that user-assigned managed identity.

## Limits

- The SAS is a bearer credential: do not log it or store it in artifacts. Workflow
  masking and `Cache-Control: no-store` reduce accidental disclosure.
- JWT expiry ends the ability to exchange that JWT, not the issued SAS lifetime.
  Directory deletion is not reliable SAS revocation. Delegation-key revocation
  revokes all affected SAS tokens; otherwise grants expire after 60 minutes.
- This proves run-level authorization, not individual job-level authorization.
  There is no branch/environment restriction beyond the operator's run mapping.
- No cleanup/retention policy, private networking, rate limiter, or production
  approval service is included.
