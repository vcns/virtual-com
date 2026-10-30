<#
.SYNOPSIS
    Sets up the GitHub repository for CI and releases. Run once, as a repository admin, with gh logged in (PowerShell
    7). Safe to rerun: each run replaces the CI certificate.

    - Development: only main can use it. Holds the CI test-signing certificate that signs every build from main.
    - Production: only main and v* tags can use it, and each use needs the reviewer's approval. Holds the release
      signing certificate. Until VCNS Tech Ltd has a publicly trusted one, that is the same CI certificate, so releases
      are test-signed and their notes say so.
    - Private vulnerability reporting, which SECURITY.md points to.

    The CI certificate's key exists only in GitHub: the PFX never touches the disk, and the local copy of the key is
    deleted once it's stored. Your own development certificate isn't used.

.PARAMETER Reviewer
    GitHub login of the person who approves releases. Defaults to you.
#>
param(
    [string] $Repository = 'vcns/virtual-com',
    [string] $Reviewer
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$environments = "repos/$Repository/environments"
$reviewerId = if ($Reviewer) { gh api "users/$Reviewer" --jq .id } else { gh api user --jq .id }

function Set-Environment([string] $name, [object[]] $reviewers, [string[]] $policies) {
    @{
        reviewers                = $reviewers
        deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true }
    } | ConvertTo-Json -Depth 4 | gh api -X PUT "$environments/$name" --input - | Out-Null
    $existing = gh api "$environments/$name/deployment-branch-policies" --jq '.branch_policies[] | .type + ":" + .name'
    foreach ($policy in $policies | Where-Object { $_ -notin $existing }) {
        $type, $pattern = $policy -split ':', 2
        gh api -X POST "$environments/$name/deployment-branch-policies" -f name=$pattern -f type=$type | Out-Null
    }
}

Set-Environment Development @() 'branch:main'
Set-Environment Production @(@{ type = 'User'; id = [int]$reviewerId }) 'branch:main', 'tag:v*'

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=VCNS Virtual COM (CI test signing)' `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
try {
    $password = [guid]::NewGuid().ToString('N')
    $pfx = [Convert]::ToBase64String($cert.Export('Pfx', $password))
    gh secret set VCOM_TEST_SIGNING_PFX --repo $Repository --env Development --body $pfx
    gh secret set VCOM_TEST_SIGNING_PASSWORD --repo $Repository --env Development --body $password
    # Replace these two with VCNS Tech Ltd's certificate when there is one.
    gh secret set VCOM_RELEASE_SIGNING_PFX --repo $Repository --env Production --body $pfx
    gh secret set VCOM_RELEASE_SIGNING_PASSWORD --repo $Repository --env Production --body $password
}
finally {
    Remove-Item $cert.PSPath -DeleteKey
}

gh api -X PUT "repos/$Repository/private-vulnerability-reporting" | Out-Null

foreach ($name in 'Development', 'Production') {
    $rules = gh api "$environments/$name" --jq '[.protection_rules[].type] | join(", ")'
    $policies = gh api "$environments/$name/deployment-branch-policies" --jq '[.branch_policies[] | .type + " " + .name] | join(", ")'
    "${name}: $policies; rules: $(if ($rules) { $rules } else { 'none' })"
    gh secret list --repo $Repository --env $name
}
