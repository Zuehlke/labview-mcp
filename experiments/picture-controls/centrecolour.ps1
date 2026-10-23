param([string]$Dir, [string]$Filter = '*.png')
# Prints the centre pixel of each screenshot and which state image (solidpng.py colours) it is.
Add-Type -AssemblyName System.Drawing
$names = @{ 'FF0000' = '0 red'; '00FF00' = '1 green'; '0000FF' = '2 blue'; 'FFFF00' = '3 yellow'; 'FF00FF' = '4 magenta'; '00FFFF' = '5 cyan' }
Get-ChildItem $Dir -Filter $Filter | Sort-Object Name | ForEach-Object {
    $b = [Drawing.Bitmap]::FromFile($_.FullName)
    $p = $b.GetPixel([int]($b.Width / 2), [int]($b.Height / 2))
    $hex = '{0:X2}{1:X2}{2:X2}' -f $p.R, $p.G, $p.B
    $b.Dispose()
    '{0,-28} {1}  {2}' -f $_.BaseName, $hex, ($(if ($names.ContainsKey($hex)) { $names[$hex] } else { '?' }))
}
