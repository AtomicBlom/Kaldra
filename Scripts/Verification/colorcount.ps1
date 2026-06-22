# Count distinct RGB colours in a PNG (ignores alpha). Fast: LockBits + a HashSet over packed RGB.
param([Parameter(Mandatory=$true)][string]$Path)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Path)
$rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$len = [Math]::Abs($data.Stride) * $bmp.Height
$bytes = New-Object byte[] $len
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $len)
$bmp.UnlockBits($data)
$set = New-Object 'System.Collections.Generic.HashSet[int]'
for ($i = 0; $i -lt $len; $i += 4) {
  # Cast each channel to int before shifting: PowerShell's -shl on a [byte] truncates the result back to a byte,
  # which would drop the high bytes and collapse the key down to the blue channel alone.
  [void]$set.Add(([int]$bytes[$i+2] -shl 16) -bor ([int]$bytes[$i+1] -shl 8) -bor [int]$bytes[$i])
}
$w = $bmp.Width; $h = $bmp.Height
$bmp.Dispose()
"{0}: {1} distinct RGB colours ({2}x{3})" -f (Split-Path $Path -Leaf), $set.Count, $w, $h
