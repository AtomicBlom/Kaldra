# Where did content land? Reports the non-black bounding box and an NxN grid distribution for a PNG —
# instantly reveals "everything rendered into one corner" (a camera/transform bug) vs full-frame coverage,
# the signature that distinguishes a transform problem from a shading one. Skips the top HUD strip (-CropTop).
# A pixel counts as content if any RGB channel exceeds -Threshold.
param(
  [Parameter(Mandatory=$true)][string]$Path,
  [int]$Threshold = 18,
  [int]$CropTop = 28,
  [int]$Grid = 3
)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Path)
$rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$len = [Math]::Abs($data.Stride) * $bmp.Height
$bytes = New-Object byte[] $len
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $len)
$stride = $data.Stride
$w = $bmp.Width; $h = $bmp.Height
$bmp.UnlockBits($data); $bmp.Dispose()

$cells = New-Object 'int[]' ($Grid * $Grid)
$minx = $w; $miny = $h; $maxx = -1; $maxy = -1
[int]$content = 0; [int]$considered = 0
$bandH = $h - $CropTop
for ($y = $CropTop; $y -lt $h; $y++) {
  $row = $y * $stride
  for ($x = 0; $x -lt $w; $x++) {
    $o = $row + $x*4
    $considered++
    if ($bytes[$o] -gt $Threshold -or $bytes[$o+1] -gt $Threshold -or $bytes[$o+2] -gt $Threshold) {
      $content++
      if ($x -lt $minx) { $minx = $x }; if ($x -gt $maxx) { $maxx = $x }
      if ($y -lt $miny) { $miny = $y }; if ($y -gt $maxy) { $maxy = $y }
      # NB: [int] in PowerShell *rounds* (banker's), not truncates — use Floor so cell boundaries are even thirds.
      $gx = [Math]::Min($Grid-1, [int][Math]::Floor($x * $Grid / $w))
      $gy = [Math]::Min($Grid-1, [int][Math]::Floor(($y - $CropTop) * $Grid / $bandH))
      $cells[$gy * $Grid + $gx]++
    }
  }
}
$pct = if ($considered -gt 0) { 100.0 * $content / $considered } else { 0 }
"{0}: {1:F1}% content ({2}x{3}, skipped top {4}px)" -f (Split-Path $Path -Leaf), $pct, $w, $h, $CropTop
if ($maxx -lt 0) { "  (no content above threshold — blank/black frame)"; return }
"  bbox: x[{0}..{1}] y[{2}..{3}]  ({4}x{5} px)" -f $minx, $maxx, $miny, $maxy, ($maxx-$minx+1), ($maxy-$miny+1)
$per = ($w / $Grid) * ($bandH / $Grid)
"  {0}x{0} grid (% of each cell that is content):" -f $Grid
for ($gy = 0; $gy -lt $Grid; $gy++) {
  $line = "   "
  for ($gx = 0; $gx -lt $Grid; $gx++) {
    $cp = if ($per -gt 0) { 100.0 * $cells[$gy*$Grid+$gx] / $per } else { 0 }
    $line += ("{0,7:F1}" -f $cp)
  }
  $line
}
