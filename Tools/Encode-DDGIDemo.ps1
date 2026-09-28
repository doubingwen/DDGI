param(
    [string]$Ffmpeg = 'ffmpeg'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$frames = Join-Path $root 'Library/DDGI.VideoFrames'
$output = Join-Path $root 'pictures/ddgi'
$status = Join-Path $root 'Library/DDGI.VideoCapture.status'
if (-not (Test-Path -LiteralPath $status) -or
    (Get-Content -LiteralPath $status -Raw).Trim() -ne 'completed: 360 frames') {
    throw 'Complete Dou DDGI > Record README Light Rotation in Unity before encoding.'
}
$captureInfo = Join-Path $output 'VideoCaptureInfo.txt'
if (-not (Test-Path -LiteralPath $captureInfo) -or
    -not (Get-Content -LiteralPath $captureInfo -Raw).Contains('Motion: camera-left -> camera-right -> camera-left')) {
    throw 'Capture the current left-right-left demo before encoding; the previous center-start sweep is outdated.'
}
foreach ($mode in @('Composite', 'Indirect')) {
    for ($index = 0; $index -lt 360; $index++) {
        $path = Join-Path $frames ($mode + '/frame_{0:D4}.png' -f $index)
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing frame: $path" }
    }
}
$null = Get-Command $Ffmpeg -ErrorAction Stop
New-Item -ItemType Directory -Path $output -Force | Out-Null
$font = (Join-Path $env:WINDIR 'Fonts/arial.ttf').Replace('\', '/').Replace(':', '\:')
$composite = Join-Path $frames 'Composite/frame_%04d.png'
$indirect = Join-Path $frames 'Indirect/frame_%04d.png'
$video = Join-Path $output 'DDGI_LightRotation.mp4'
$sceneVideo = Join-Path $output 'DDGI_LightRotation_Scene.mp4'
$preview = Join-Path $output 'DDGI_LightRotation.gif'
$filter = "[0:v]scale=960:540,pad=960:580:0:40:color=0x15191c," +
    "drawtext=fontfile='$font':text='DDGI Composite':x=20:y=8:fontsize=24:fontcolor=white[left];" +
    "[1:v]scale=960:540,pad=960:580:0:40:color=0x15191c," +
    "drawtext=fontfile='$font':text='Indirect Only':x=20:y=8:fontsize=24:fontcolor=white[right];" +
    '[left][right]hstack=inputs=2[v]'
& $Ffmpeg -hide_banner -loglevel warning -y -framerate 30 -i $composite -framerate 30 -i $indirect `
    -filter_complex $filter -map '[v]' -frames:v 360 -c:v libx264 -preset medium -crf 20 `
    -pix_fmt yuv420p -movflags +faststart $video
if ($LASTEXITCODE -ne 0) { throw 'Comparison video encoding failed.' }
& $Ffmpeg -hide_banner -loglevel warning -y -framerate 30 -i $composite -frames:v 360 `
    -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -movflags +faststart $sceneVideo
if ($LASTEXITCODE -ne 0) { throw 'Scene video encoding failed.' }
& $Ffmpeg -hide_banner -loglevel warning -y -i $video `
    -filter_complex 'fps=10,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3' `
    -loop 0 $preview
if ($LASTEXITCODE -ne 0) { throw 'GIF preview encoding failed.' }
Get-Item $video, $sceneVideo, $preview | Select-Object Name, Length
