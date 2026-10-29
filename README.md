# GitHub OIDC directory access POC

A GitHub Actions job exchanges its signed OIDC token for a **60-minute,
HTTPS-only, read/list directory SAS**. No Azure credentials are stored in GitHub.
The Function validates GitHub's signature, issuer, audience, expiry, repository
`hallipr/test-things` (immutable ID `1437763204`), run ID and run attempt.

Authorization is the existence of an actual ADLS Gen2 directory:

```text
runs/{repository_id}/{run_id}/{run_attempt}/
```

Only an operator creates that directory. All jobs in that run attempt may read
its descendants. Other runs/attempts and writes are denied. A file with the same
name is not authorization. The Function derives the path from signed claims,
never from a caller-supplied path.

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
or able to create authorization directories is inside this POC's trust boundary.
Do not grant that identity broader write roles on the run-storage account.

## Upload a directory and run

```powershell
.\scripts\Start-DirectoryRun.ps1 -Directory C:\path\to\input `
  -StorageAccountName $outputs.runStorageAccountName.value
```

The script uploads all files (including hidden files) to a unique `_staging/`
directory, dispatches the workflow, correlates its run ID using a unique input,
then atomically renames staging into that run attempt's authorization directory.
The workflow waits up to ten minutes, downloads the directory into runner
temporary storage (including paginated listings), checks a supplied file's SHA256,
and verifies that sibling run/attempt reads and lists and writes fail. The script
waits for the workflow result and fails if it fails. Symlinks/junctions and empty
inputs are rejected. Only upload data intended for that workflow.

If dispatch/authorization fails, the script reports failure rather than granting
another run access; staged data may remain for operator cleanup. Rerunning an
existing GitHub run increments `run_attempt` and needs a new directory.

## Test locally

```powershell
dotnet test .\tests\DirectoryAccess.Tests -c Release
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
  There is no branch/environment restriction beyond the operator's directory grant.
- No cleanup/retention policy, private networking, rate limiter, or production
  approval service is included.
