<#
.SYNOPSIS
    Publishes the gh-pages folder, as committed on main, to the gh-pages branch that GitHub Pages serves.
    Commit and push documentation changes to main first.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# subtree split is deterministic, so each run extends the previous gh-pages history and the push fast-forwards.
$commit = git -C $root subtree split --prefix gh-pages main
if ($LASTEXITCODE -ne 0) { throw 'git subtree split failed.' }
git -C $root push origin "${commit}:refs/heads/gh-pages"
if ($LASTEXITCODE -ne 0) { throw 'Pushing the gh-pages branch failed.' }
Write-Host "Published $commit to gh-pages."
