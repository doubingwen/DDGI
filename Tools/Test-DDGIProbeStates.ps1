$ErrorActionPreference = 'Stop'

# CPU reference for the GPU scheduler. This does not execute DXR or inspect rendered pixels.
$Off = 0; $Vigilant = 1; $Uninitialized = 2; $Sleep = 3
$Awake = 4; $NewAwake = 5; $NewVigilant = 6
$Update = 1; $Reset = 2; $Classify = 4; $DynamicNearby = 8; $StaticNearby = 16
$passed = 0

function Prepare([int]$old, [int]$nearby, [bool]$changed = $false,
    [bool]$recheck = $false, [bool]$resetAll = $false, [bool]$enabled = $true) {
    $dormant = $old -eq $Off -or $old -eq $Sleep
    if (-not $enabled) {
        $flags = $Update
        if ($dormant -or $old -eq $Uninitialized) { $flags = $flags -bor $Reset }
        return @($Vigilant, $flags)
    }
    $dynamic = ($nearby -band $DynamicNearby) -ne 0
    if ($resetAll -or $old -eq $Uninitialized -or ($dormant -and ($changed -or $recheck -or $dynamic))) {
        return @($Uninitialized, ($nearby -bor $Update -bor $Classify -bor $Reset))
    }
    if ($old -eq $Off) { return @($Off, 0) }
    $static = $old -eq $Vigilant -or $old -eq $NewVigilant
    $state = if ($dynamic) { $Awake } elseif ($static) { $Vigilant } else { $Sleep }
    $waking = ($dynamic -and $old -ne $Awake -and $old -ne $NewAwake) -or
        (-not $dynamic -and $static -and $old -ne $Vigilant -and $old -ne $NewVigilant)
    if ($waking) { $state = if ($dynamic) { $NewAwake } else { $NewVigilant } }
    $flags = $nearby
    if ($state -ne $Sleep) { $flags = $flags -bor $Update }
    if ($waking -or ($changed -and -not $dynamic)) { $flags = $flags -bor $Reset }
    if (-not $dynamic -and ($changed -or $old -eq $Awake -or $old -eq $NewAwake)) {
        $flags = $flags -bor $Classify -bor $Update -bor $Reset
    }
    return @($state, $flags)
}

function ClassifyResult($prepared, [int]$backfaces, [double]$nearest,
    [double]$radius = 3.4641, [int]$rays = 64, [double]$threshold = 0.95) {
    if (($prepared[1] -band $Classify) -eq 0) { return $prepared }
    $dynamic = ($prepared[1] -band $DynamicNearby) -ne 0
    $static = ($prepared[1] -band $StaticNearby) -ne 0
    $interior = -not $dynamic -and $static -and $backfaces -ge [Math]::Ceiling($rays * $threshold)
    $state = if ($interior) { $Off } elseif ($dynamic) { $NewAwake } elseif ($nearest -le $radius) {
        $NewVigilant
    } else { $Sleep }
    return @($state, $prepared[1])
}

function AssertState($value, [int]$expected, [int]$requiredFlags, [string]$name) {
    if ($value[0] -ne $expected -or ($value[1] -band $requiredFlags) -ne $requiredFlags) {
        throw "$name failed: actual state/flags $value, expected $expected / $requiredFlags"
    }
    $script:passed++
}

$init = Prepare $Uninitialized $StaticNearby
AssertState $init $Uninitialized ($Update -bor $Reset -bor $Classify) 'Initial capture'
AssertState (ClassifyResult $init 61 0.3) $Off $Update 'Static interior: 61/64 backfaces'
AssertState (ClassifyResult $init 60 0.3) $NewVigilant $Reset 'Conservative threshold: 60/64 remains usable'
AssertState (ClassifyResult $init 0 1) $NewVigilant $Reset 'Static surface initialization'
AssertState (ClassifyResult $init 0 100) $Sleep $Update 'Empty tile initializes once'
AssertState (Prepare $Sleep 0) $Sleep 0 'Sleep persists'
if (((Prepare $Sleep 0)[1] -band $Update) -ne 0) { throw 'Sleeping probe updated unexpectedly' }
$passed++
AssertState (Prepare $Off $StaticNearby) $Off 0 'Interior stays disabled'
if (((Prepare $Off $StaticNearby)[1] -band $Update) -ne 0) { throw 'Off probe updated unexpectedly' }
$passed++
AssertState (Prepare $NewVigilant $StaticNearby) $Vigilant $Update 'NewVigilant matures'
AssertState (Prepare $NewAwake $DynamicNearby) $Awake $Update 'NewAwake matures'
AssertState (Prepare $Vigilant $DynamicNearby $true) $NewAwake $Reset 'Static probe switches to dynamic mode'
$wake = Prepare $Sleep $DynamicNearby $true
AssertState (ClassifyResult $wake 64 0.1) $NewAwake $Reset 'Dynamic proximity wakes without interior rejection'
AssertState (Prepare $Vigilant $StaticNearby) $Vigilant $Update 'Static geometry still responds to changing lights'
$depart = Prepare $Awake 0 $true
AssertState $depart $Sleep ($Classify -bor $Reset -bor $Update) 'Dynamic departure requests capture'
AssertState (ClassifyResult $depart 0 100) $Sleep $Reset 'Empty after departure'
AssertState (ClassifyResult $depart 0 1) $NewVigilant $Reset 'Static surface after departure'
AssertState (Prepare $Off $StaticNearby $true) $Uninitialized $Classify 'Changed old bounds rechecks interior'
AssertState (Prepare $Sleep 0 $false $true) $Uninitialized $Update 'Periodic safety recheck'
AssertState (Prepare $Vigilant $StaticNearby $false $false $true) $Uninitialized $Reset 'Resource reset reinitializes'
AssertState (Prepare $Off 0 $false $false $false $false) $Vigilant ($Update -bor $Reset) 'Disabling classification restores full updates'
$moving = Prepare $Awake $DynamicNearby $true
AssertState $moving $Awake $Update 'Moving geometry remains active'
if (($moving[1] -band $Reset) -ne 0) { throw 'Moving active probe lost history every frame' }
$passed++

$root = Split-Path $PSScriptRoot -Parent
$stateSource = Get-Content -Raw (Join-Path $root 'Assets/DDGI/Shaders/DDGIProbeState.hlsl')
foreach ($entry in @{ OFF=0; VIGILANT=1; UNINITIALIZED=2; SLEEP=3; AWAKE=4; NEW_AWAKE=5; NEW_VIGILANT=6 }.GetEnumerator()) {
    if ($stateSource -notmatch "#define DDGI_PROBE_$($entry.Key) $($entry.Value)u") {
        throw "GPU state enum mismatch: $($entry.Key)"
    }
}
$passed++
Write-Output "Passed $passed DDGI probe state reference checks. GPU rendering still requires Unity validation."
