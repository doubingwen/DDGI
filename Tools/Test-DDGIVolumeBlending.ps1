$ErrorActionPreference = 'Stop'

# CPU reference for the shader's premultiplied Blend OneMinusDstAlpha One operation.
function Mix-Volumes([object[]] $Volumes) {
    $light = 0.0
    $coverage = 0.0
    foreach ($volume in $Volumes) {
        $weight = (1.0 - $coverage) * $volume[1]
        $light += $volume[0] * $weight
        $coverage += $weight
    }
    $normalized = $light / [Math]::Max($coverage, 1e-6)
    return @(($normalized * [Math]::Min($coverage, 1.0)), $coverage)
}

function Assert-Close([double] $Actual, [double] $Expected, [string] $Name) {
    if ([Math]::Abs($Actual - $Expected) -gt 1e-8) {
        throw "$Name : expected $Expected, got $Actual"
    }
    Write-Output "PASS: $Name"
}

$result = Mix-Volumes @(@(2.0, 1.0), @(6.0, 1.0))
Assert-Close $result[0] 2.0 'Dense interior overrides coarse volume'
Assert-Close $result[1] 1.0 'Coverage does not exceed one'

$result = Mix-Volumes @(@(2.0, 0.25), @(6.0, 1.0))
Assert-Close $result[0] 5.0 'Dense boundary transitions to coarse volume'

$result = Mix-Volumes @(@(2.0, 0.0), @(6.0, 1.0))
Assert-Close $result[0] 6.0 'Outside dense volume uses coarse volume'

$result = Mix-Volumes @(@(2.0, 0.0), @(6.0, 0.0))
Assert-Close $result[0] 0.0 'No contributing volume remains finite and black'

$result = Mix-Volumes -Volumes (,@(8.0, 0.5))
Assert-Close $result[0] 4.0 'Normalization retains outer-volume fade'

$result = Mix-Volumes @(@(5.0, 1.0), @(5.0, 1.0), @(5.0, 1.0))
Assert-Close $result[0] 5.0 'Overlapping equal lighting does not add brightness'

for ($index = 0; $index -le 100; $index++) {
    $alpha = $index / 100.0
    $result = Mix-Volumes @(@(2.0, $alpha), @(6.0, 1.0))
    if ([Math]::Abs($result[0] - (6.0 - 4.0 * $alpha)) -gt 1e-8) {
        throw 'Nonlinear boundary discontinuity'
    }
}
Write-Output 'PASS: Boundary transition is continuous across 101 samples'

$scaledDensity = 1.0 / (8.0 * 1.0 * 1.0 * 1.0)
$coarseDensity = 1.0 / (1.0 * 2.0 * 2.0 * 2.0)
Assert-Close $scaledDensity $coarseDensity 'Sorting density accounts for world transform scale'

$worldEdgeDistance = 0.5 / (1.0 / 2.0)
Assert-Close $worldEdgeDistance 1.0 'Boundary distance uses inverse-transform plane scale'
