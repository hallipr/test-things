// Existing resource group: pahallis-sandbox; subscription d0c05057-7972-46ff-9bcf-3c932250155e.
using './main.bicep'

param namePrefix = 'rundirectory'
param location = 'eastus2'
param githubOidcAudience = 'api://hallipr-test-things-directory-access'
param managedIdentityName = 'pahallis-sandbox-identity'
param applicationInsightsName = 'rundirectory-d5eq7yh3lf7by'
