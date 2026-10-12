# Release tags of this fork: bare X.Y.Z tags that are not part of the upstream history we are based on (upstream has bare tags of
# its own, 0.45.1 and older, which are ancestors of the base tag and do not count here).
function Get-CairnReleaseTags([string]$Root, [string]$Exclude) {
    $strict = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
    $base = "v" + ((Get-Content (Join-Path $Root "UPSTREAM.md") -TotalCount 1) -replace '[^0-9.]', '').Trim('.')
    # upstream's main can be ahead of its newest tag (a version not tagged yet): then the mirror branch of its history is the base
    & git -C $Root rev-parse --verify --quiet "$base^{commit}" *> $null
    if ($LASTEXITCODE -ne 0) { $base = if (& git -C $Root rev-parse --verify --quiet refs/heads/upstream) { 'refs/heads/upstream' } else { 'refs/remotes/origin/upstream' } }
    @(& git -C $Root tag --list | Where-Object { $_ -cmatch $strict -and $_ -ne $Exclude } | Where-Object {
        & git -C $Root merge-base --is-ancestor $_ $base 2>$null
        $LASTEXITCODE -ne 0
    } | Sort-Object { [version]$_ })
}