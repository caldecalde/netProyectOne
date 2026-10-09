<#
.SYNOPSIS
  End-to-end check of a running CareFlow stack: gets real Keycloak tokens, uploads the sample files,
  and verifies storage, status, duplicate detection and authorization.
.EXAMPLE
  ./scripts/smoke-test.ps1
  ./scripts/smoke-test.ps1 -ApiUrl https://demo.example.com -IdpUrl https://auth.example.com/realms/careflow
#>
[CmdletBinding()]
param(
    [string]$ApiUrl = 'http://localhost:5080',
    [string]$IdpUrl = 'http://localhost:8080/realms/careflow'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = 0

function Get-Token([string]$User) {
    $body = @{ grant_type = 'password'; client_id = 'careflow-cli'; username = $User; password = $User }
    (Invoke-RestMethod -Method Post -Uri "$IdpUrl/protocol/openid-connect/token" -Body $body).access_token
}

function Invoke-Upload([string]$Token, [string]$Path) {
    # curl.exe ships with Windows 10+; Invoke-RestMethod -Form needs PowerShell 7.
    $out = & curl.exe -s -w "`n%{http_code}" -H "Authorization: Bearer $Token" -F "file=@$Path" "$ApiUrl/api/v1/files"
    $lines = $out -split "`n"
    [pscustomobject]@{ Status = [int]$lines[-1]; Body = ($lines[0..($lines.Length - 2)] -join "`n") }
}

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name $Detail" -ForegroundColor Red; $script:failures++ }
}

Write-Host "CareFlow smoke test against $ApiUrl"
$submitter = Get-Token 'submitter'
$viewer = Get-Token 'viewer'

$ready = Invoke-WebRequest -Uri "$ApiUrl/health/ready" -UseBasicParsing
Check 'readiness probe' ($ready.StatusCode -eq 200)

foreach ($sample in 'samples/hl7/adt-a01.hl7', 'samples/x12/837p-single-claim.x12') {
    $r = Invoke-Upload $submitter (Join-Path $root $sample)
    Check "upload $sample (202 new or 200 duplicate)" ($r.Status -in 200, 202) "got $($r.Status): $($r.Body)"
    if ($r.Status -in 200, 202) {
        $file = $r.Body | ConvertFrom-Json
        $status = Invoke-RestMethod -Uri "$ApiUrl/api/v1/files/$($file.id)" -Headers @{ Authorization = "Bearer $viewer" }
        Check "  viewer can read status of $($file.id) [$($status.format)/$($status.status)]" ($status.id -eq $file.id)
        $again = Invoke-Upload $submitter (Join-Path $root $sample)
        Check '  re-upload is detected as duplicate (200, same id)' ($again.Status -eq 200 -and ($again.Body | ConvertFrom-Json).id -eq $file.id)
    }
}

$forbidden = Invoke-Upload $viewer (Join-Path $root 'samples/hl7/adt-a01.hl7')
Check 'viewer cannot upload (403)' ($forbidden.Status -eq 403) "got $($forbidden.Status)"

$anonymous = & curl.exe -s -o NUL -w '%{http_code}' -F "file=@$(Join-Path $root 'samples/hl7/adt-a01.hl7')" "$ApiUrl/api/v1/files"
Check 'anonymous upload rejected (401)' ($anonymous -eq '401') "got $anonymous"

if ($failures -gt 0) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed' -ForegroundColor Green
