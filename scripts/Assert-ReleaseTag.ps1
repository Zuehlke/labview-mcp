<#
.SYNOPSIS
    Refuse a release tag that is not exactly vX.Y.Z, and say precisely what is wrong with it.

.DESCRIPTION
    The release workflow reads the tag and stamps it into three places that all need a numeric
    three-part version: the assembly's FileVersion/ProductVersion, plugin.json's `version`, and
    VERSION.txt inside the archive. A tag MSBuild cannot parse either fails the build halfway
    through or - worse - is silently coerced, which is how two different tags come to stamp the
    same version.

    WHY THIS EXISTS. The tags in this repository do not agree on a format. Measured 2026-09-11:
    v1.3.0, v1.1.1 and v1.0.7 are lower-case; V1.2.8, V1.2.5, V1.2.2, V1.2.0 and V1.1.5 are
    upper-case; and v10.4 has only two components. That last one also sorts ABOVE every
    three-part tag in `git tag --sort=-v:refname`, so for ten days the newest-looking tag in the
    list was neither the newest release nor a valid version - which is how a release process
    loses track of what it has shipped.

    THE RULE: `v`, lower-case, then three decimal components separated by dots, nothing else.
    Leading zeros are refused as well, because MSBuild reads 02 as 2, so `v1.02.3` would be
    indistinguishable from `v1.2.3` in every artefact this stamps while remaining a different
    git ref.

.PARAMETER Tag
    The tag to check. A `refs/tags/` prefix is accepted and stripped, so the workflow may pass
    either $env:GITHUB_REF or $env:GITHUB_REF_NAME without caring which it has.

.OUTPUTS
    On success: prints the accepted tag and its version and, under GitHub Actions, writes `tag`
    and `version` to $GITHUB_OUTPUT for later steps. Exit code 0.
    On failure: prints the diagnosis and the remedy. Exit code 1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Assert-ReleaseTag.ps1 -Tag v1.3.0

.EXAMPLE
    # What a maintainer should run BEFORE pushing a tag.
    powershell -ExecutionPolicy Bypass -File scripts\Assert-ReleaseTag.ps1 -Tag v1.4.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$Tag
)

$ErrorActionPreference = 'Stop'

# refs/tags/v1.2.3 -> v1.2.3. Accepted rather than refused: which of the two GitHub hands you
# depends on the variable the workflow happened to use, and that is not a tagging mistake.
$raw = $Tag
$tag = $Tag
if ($tag -like 'refs/tags/*') { $tag = $tag.Substring('refs/tags/'.Length) }

# THE rule, in one place, quoted by every message below so the text cannot drift from the regex.
$Rule = 'vX.Y.Z - a lower-case "v" followed by exactly three decimal numbers separated by dots, and nothing else. Examples: v1.0.0, v1.3.0, v12.7.31'
$Pattern = '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'

function Deny {
    param([string]$Problem, [string]$Why, [string]$Suggested)

    # Everything a reader needs without opening a file: what arrived, what is wrong with it, why
    # the rule exists, and the commands that fix an already-pushed tag. A release that fails with
    # "invalid tag" and nothing else is how a check like this gets worked around instead of obeyed.
    $lines = @(
        '',
        '=====================================================================',
        ' RELEASE REFUSED - the tag is not a valid version',
        '=====================================================================',
        '',
        "  tag pushed    :  $raw",
        "  problem       :  $Problem",
        "  why it matters:  $Why",
        '',
        "  required form :  $Rule"
    )
    if ($Suggested) { $lines += @('', "  you probably meant:  $Suggested") }
    $lines += @(
        '',
        '  Nothing has been built or published. Delete the bad tag and push a correct one:',
        '',
        "      git tag -d $tag",
        "      git push origin :refs/tags/$tag"
    )
    $lines += if ($Suggested) {
        @("      git tag $Suggested <commit>", "      git push origin $Suggested")
    } else {
        @('      git tag vX.Y.Z <commit>', '      git push origin vX.Y.Z')
    }
    $lines += @(
        '',
        '  Check a tag before pushing it and this never fires:',
        '',
        '      powershell -ExecutionPolicy Bypass -File scripts\Assert-ReleaseTag.ps1 -Tag vX.Y.Z',
        '',
        '====================================================================='
    )
    $lines | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}

# ---------------------------------------------------------------------------------------------
# The specific diagnoses, ordered so the most informative one wins. A single "does not match the
# pattern" would be correct and useless: the value of this check is telling the maintainer WHICH
# of the plausible mistakes they made.
# ---------------------------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($tag)) {
    Deny 'the tag is empty' `
         'there is no version to stamp into the assembly, plugin.json or VERSION.txt' `
         ''
}

if ($tag -ne $tag.Trim()) {
    Deny "the tag has leading or trailing whitespace ('$tag')" `
         'git accepts it and every string comparison downstream does not' `
         $tag.Trim()
}

if ($tag -cmatch '^V') {
    # First, because it is the mistake this repository actually keeps making - five of the nine
    # existing tags - and because a case-insensitive listing shows no sign of it.
    $fixed = 'v' + $tag.Substring(1)
    Deny "the tag starts with an upper-case 'V'" `
         ('git refs are case-sensitive, so V1.2.3 and v1.2.3 are two different tags that can both ' +
          'exist, on different commits, pointing at different releases - and a listing sorted ' +
          'case-insensitively gives no hint that both are there') `
         $(if ($fixed -cmatch $Pattern) { $fixed } else { '' })
}

if ($tag -cnotmatch '^v') {
    $fixed = 'v' + $tag
    Deny "the tag does not start with a lower-case 'v'" `
         ('the release workflow triggers on tags matching v*, so a tag without the prefix builds ' +
          'and publishes nothing at all, with no failed run to notice') `
         $(if ($fixed -cmatch $Pattern) { $fixed } else { '' })
}

$body = $tag.Substring(1)

if ($body -match '[-+]') {
    $split = $body -split '[-+]', 2
    Deny "the tag carries a pre-release or build suffix ('$($split[1])')" `
         ('MSBuild accepts a suffix in InformationalVersion but not in FileVersion, so the stamped ' +
          'assembly and the published tag would no longer say the same thing; this project has no ' +
          'pre-release channel') `
         $(if ("v$($split[0])" -cmatch $Pattern) { "v$($split[0])" } else { '' })
}

$parts = $body.Split('.')

if ($parts.Count -ne 3) {
    $suggested = switch ($parts.Count) {
        1 { "v$body.0.0" }
        2 { "v$body.0" }
        default { 'v' + ($parts[0..2] -join '.') }
    }
    $why = if ($parts.Count -lt 3) {
        'a version needs three components. A two-part tag such as v10.4 also sorts ABOVE every ' +
        'three-part tag in `git tag --sort=-v:refname`, so it permanently masks the real newest ' +
        'release in every listing'
    } else {
        'FileVersion has a fourth field that MSBuild fills in itself, so supplying it means the ' +
        'tag and the assembly disagree about the version'
    }
    Deny "the tag has $($parts.Count) component(s) rather than 3 ('$body')" $why `
         $(if ($suggested -cmatch $Pattern) { $suggested } else { '' })
}

$names = @('major', 'minor', 'patch')

$nonNumeric = @(0..2 | Where-Object { $parts[$_] -notmatch '^[0-9]+$' })
if ($nonNumeric.Count -gt 0) {
    $named = $nonNumeric | ForEach-Object { "$($names[$_]) = '$($parts[$_])'" }
    Deny "a version component is not a decimal number ($($named -join ', '))" `
         ('every component is stamped into a numeric FileVersion field, which takes digits only, ' +
          'so a non-numeric component cannot be built at all') `
         ''
}

$leadingZero = @(0..2 | Where-Object { $parts[$_].Length -gt 1 -and $parts[$_].StartsWith('0') })
if ($leadingZero.Count -gt 0) {
    $trimmed = 'v' + (($parts | ForEach-Object { [string][int]$_ }) -join '.')
    Deny "a version component has a leading zero ('$body')" `
         ('MSBuild reads 02 as 2, so this tag and its zero-free twin stamp an IDENTICAL version ' +
          'into the assembly, plugin.json and VERSION.txt while remaining two different git refs - ' +
          'two releases no installed copy could tell apart') `
         $trimmed
}

if ($tag -cnotmatch $Pattern) {
    # Unreachable given the diagnoses above. Kept so that a later edit narrowing $Pattern fails
    # loudly here instead of letting an unchecked tag through.
    Deny "the tag is not a valid version ('$tag')" `
         'the version is stamped into the assembly, plugin.json and VERSION.txt' ''
}

$version = $body

Write-Host "release tag OK: $tag  ->  version $version" -ForegroundColor Green

# Hand the parsed version to the following workflow steps. Guarded, so a local run - the way a
# maintainer is meant to check a tag before pushing it - works with no GitHub environment.
if ($env:GITHUB_OUTPUT) {
    "tag=$tag"         | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}

exit 0
