# Pixel-diff two PNGs (same size). Crops the top strip (Vulkan's ImGui HUD bar). Writes a heatmap and prints
# mean/max per-channel abs difference + the fraction of pixels differing by more than a threshold.
param(
  [Parameter(Mandatory=$true)][string]$A,
  [Parameter(Mandatory=$true)][string]$B,
  [Parameter(Mandatory=$true)][string]$Out,
  [int]$CropTop = 28,
  [int]$Threshold = 16
)
Add-Type -AssemblyName System.Drawing

function Read-Argb($path) {
  $bmp = [System.Drawing.Bitmap]::FromFile($path)
  $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $len = [Math]::Abs($data.Stride) * $bmp.Height
  $bytes = New-Object byte[] $len
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $len)
  $bmp.UnlockBits($data)
  return [pscustomobject]@{ Bytes = $bytes; W = $bmp.Width; H = $bmp.Height; Stride = $data.Stride; Bmp = $bmp }
}

$ia = Read-Argb $A
$ib = Read-Argb $B
$w = [Math]::Min($ia.W, $ib.W)
$h = [Math]::Min($ia.H, $ib.H)

$diff = New-Object System.Drawing.Bitmap $w, $h
$dd = $diff.LockBits((New-Object System.Drawing.Rectangle 0,0,$w,$h), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$dbytes = New-Object byte[] ([Math]::Abs($dd.Stride) * $h)

[double]$total = 0; [int]$count = 0; [int]$maxd = 0; [int]$over = 0
for ($y = $CropTop; $y -lt $h; $y++) {
  $ra = $y * $ia.Stride; $rb = $y * $ib.Stride; $rd = $y * $dd.Stride
  for ($x = 0; $x -lt $w; $x++) {
    $oa = $ra + $x*4; $ob = $rb + $x*4; $od = $rd + $x*4
    $db = [Math]::Abs($ia.Bytes[$oa]   - $ib.Bytes[$ob])
    $dg = [Math]::Abs($ia.Bytes[$oa+1] - $ib.Bytes[$ob+1])
    $dr = [Math]::Abs($ia.Bytes[$oa+2] - $ib.Bytes[$ob+2])
    $d = [int](($dr + $dg + $db) / 3)
    $total += $d; $count++
    if ($d -gt $maxd) { $maxd = $d }
    if ($d -gt $Threshold) { $over++ }
    $v = [byte][Math]::Min(255, $d * 3)
    $dbytes[$od]   = $v; $dbytes[$od+1] = $v; $dbytes[$od+2] = $v; $dbytes[$od+3] = 255
  }
}
[System.Runtime.InteropServices.Marshal]::Copy($dbytes, 0, $dd.Scan0, $dbytes.Length)
$diff.UnlockBits($dd)
$diff.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$ia.Bmp.Dispose(); $ib.Bmp.Dispose(); $diff.Dispose()

$mean = if ($count -gt 0) { $total / $count } else { 0 }
$pct = if ($count -gt 0) { 100.0 * $over / $count } else { 0 }
"{0} vs {1}: mean={2:F2}/255  max={3}  pixels>{4}: {5:F1}%  -> {6}" -f (Split-Path $A -Leaf), (Split-Path $B -Leaf), $mean, $maxd, $Threshold, $pct, (Split-Path $Out -Leaf)
