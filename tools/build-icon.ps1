$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$project=Split-Path $PSScriptRoot -Parent
$source=[Drawing.Image]::FromFile((Join-Path $project 'branding\yspotify-avatar-v1.png'))
$entries=New-Object 'System.Collections.Generic.List[byte[]]'
$sizes=@(16,24,32,48,64,128,256)
try{
 foreach($size in $sizes){
  $bitmap=New-Object Drawing.Bitmap $size,$size
  $graphics=[Drawing.Graphics]::FromImage($bitmap)
  try{$graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$graphics.DrawImage($source,0,0,$size,$size)}finally{$graphics.Dispose()}
  $buffer=New-Object IO.MemoryStream
  try{$bitmap.Save($buffer,[Drawing.Imaging.ImageFormat]::Png);$entries.Add($buffer.ToArray())}finally{$buffer.Dispose();$bitmap.Dispose()}
 }
 $output=[IO.File]::Create((Join-Path $project 'branding\yspotify.ico'))
 $writer=New-Object IO.BinaryWriter $output
 try{
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i -lt $sizes.Count;$i++){
   $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
   $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
   $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$entries[$i].Length);$writer.Write([uint32]$offset)
   $offset+=$entries[$i].Length
  }
  foreach($entry in $entries){$writer.Write([byte[]]$entry)}
 }finally{$writer.Dispose();$output.Dispose()}
}finally{$source.Dispose()}
