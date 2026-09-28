$ErrorActionPreference = 'Stop'

# CPU reference for renderer-owned ranges in a shared static-batch mesh, not a DXR test.
function Get-SubMeshIndices([int]$total, [int]$first, [int]$materials) {
    if ($first -lt 0 -or $first -ge $total) { return @() }
    $count = [Math]::Min($total - $first, $materials)
    for ($i = 0; $i -lt $count; ++$i) { $first + $i }
}

$cases = @(
    @{ Total=3; First=0; Materials=3; Expected='0,1,2' },
    @{ Total=20; First=7; Materials=2; Expected='7,8' },
    @{ Total=20; First=19; Materials=2; Expected='19' },
    @{ Total=20; First=20; Materials=1; Expected='' },
    @{ Total=20; First=-1; Materials=1; Expected='' },
    @{ Total=20; First=7; Materials=0; Expected='' }
)
foreach ($case in $cases) {
    $actual = @(Get-SubMeshIndices $case.Total $case.First $case.Materials) -join ','
    if ($actual -ne $case.Expected) { throw "Wrong submesh range: $actual vs $($case.Expected)" }
}
$firstRenderer = @(Get-SubMeshIndices 4 0 2)
$secondRenderer = @(Get-SubMeshIndices 4 2 2)
if (($firstRenderer + $secondRenderer -join ',') -ne '0,1,2,3') {
    throw 'Static-batch renderers did not cover distinct submesh ranges.'
}
Write-Output 'Passed 7 renderer submesh-range reference checks. Play-mode DXR still requires Unity validation.'
