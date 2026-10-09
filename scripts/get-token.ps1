<#
.SYNOPSIS
  Prints a local Keycloak access token for a demo user (submitter | viewer | auditor).
  Paste it into Swagger's "Authorize" dialog, or use it with curl / Postman.
.EXAMPLE
  ./scripts/get-token.ps1 submitter | Set-Clipboard
#>
param(
    [ValidateSet('submitter', 'viewer', 'auditor')]
    [string]$User = 'submitter',
    [string]$IdpUrl = 'http://localhost:8080/realms/careflow'
)

$body = @{ grant_type = 'password'; client_id = 'careflow-cli'; username = $User; password = $User }
(Invoke-RestMethod -Method Post -Uri "$IdpUrl/protocol/openid-connect/token" -Body $body).access_token
