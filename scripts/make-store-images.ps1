#Requires -Version 5.1
<#
.SYNOPSIS
    Generates the Store package's tile and icon images from src/Deskbar.App/Assets/deskbar-256.png.
.DESCRIPTION
    Writes every file Get-RequiredImages (StorePackaging.psm1) lists into
    src/Deskbar.App/Package/Images, overwriting what is there. The output is checked in; run this
    again only when the icon changes, and commit the result.

    Why the full set: under the package the shell draws the taskbar, Alt+Tab and Start icons from
    these files rather than from deskbar.ico, and with a single plain Square44x44Logo it plates
    them on a coloured tile, which looks worse than the direct build. See "Images and the resource
    index" in docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md.

    What each file holds, read from its name:
      Square44x44Logo.scale-<n>                      plated: the icon over the tile colour #1F1F1F,
                                                     44 px times n/100 square.
      Square44x44Logo.targetsize-<n>                 plated too: the plain target size is the
                                                     plated form by the shell's naming.
      ...targetsize-<n>_altform-unplated             unplated: the icon on transparency, n px. What
      ...targetsize-<n>_altform-lightunplated        the taskbar and Alt+Tab use. Light is the same
                                                     pixels: the icon carries its own background.
      Square150x150Logo.scale-<n>, Wide310x150Logo   the icon centred at half the tile's height on
                                                     transparency; the shell fills the manifest's
                                                     BackgroundColor (#1F1F1F) behind it.
      StoreLogo.scale-<n>                            the icon filling 50 px times n/100 on
                                                     transparency.

    Resizing is System.Drawing's high-quality bicubic, with the source's edge pixels clamped
    (TileFlipXY) so a downscale does not pull transparent black into the icon's border. A target
    the same size as the source (targetsize-256 unplated) is the source's own pixels.
.PARAMETER Source
    The square PNG to draw from. Defaults to the app's 256 px icon.
.PARAMETER OutputDir
    Where the images go. Defaults to src/Deskbar.App/Package/Images.
#>
[CmdletBinding()]
param(
    [string] $Source,
    [string] $OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Defaults are filled here, not in param(): Windows PowerShell 5.1 measured an empty
# $PSScriptRoot inside param() defaults when the script runs through -File.
if (-not $Source) { $Source = Join-Path $PSScriptRoot '..\src\Deskbar.App\Assets\deskbar-256.png' }
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot '..\src\Deskbar.App\Package\Images' }

Add-Type -AssemblyName System.Drawing
Import-Module (Join-Path $PSScriptRoot 'StorePackaging.psm1') -Force

# The Store tile colour; the manifest's BackgroundColor is the same value, so a plated icon and
# the tile behind it never disagree.
$plate = [System.Drawing.ColorTranslator]::FromHtml('#1F1F1F')

# Base sizes at scale 100, by the image's base name.
$baseSizes = @{
    Square44x44Logo   = @{ Width = 44; Height = 44 }
    Square150x150Logo = @{ Width = 150; Height = 150 }
    StoreLogo         = @{ Width = 50; Height = 50 }
    Wide310x150Logo   = @{ Width = 310; Height = 150 }
}

# Decides canvas size, icon size and plate for one file name. Returns a hashtable the drawing
# step reads. Throws on a name it does not understand, so a new entry in Get-RequiredImages
# cannot be skipped silently.
function Get-ImageSpec {
    param([string] $Name)

    if ($Name -match '^Square44x44Logo\.targetsize-(?<size>\d+)(?<alt>_altform-(un|lightun)plated)?\.png$') {
        $size = [int]$Matches.size
        # ContainsKey: an unmatched optional group is absent from $Matches, and StrictMode
        # throws on reading it as a property.
        return @{ Width = $size; Height = $size; Icon = $size; Plated = -not $Matches.ContainsKey('alt') }
    }
    if ($Name -match '^(?<base>\w+)\.scale-(?<scale>\d+)\.png$') {
        $base = $baseSizes[$Matches.base]
        if (-not $base) { throw "No base size for $Name." }
        $factor = [int]$Matches.scale / 100
        $width = [int][math]::Round($base.Width * $factor)
        $height = [int][math]::Round($base.Height * $factor)
        # Tiles show the icon at half their height with room around it; the 44 and the Store
        # logo are the icon itself.
        $isTile = $Matches.base -in @('Square150x150Logo', 'Wide310x150Logo')
        $icon = if ($isTile) { [int][math]::Round($height / 2) } else { $height }
        return @{ Width = $width; Height = $height; Icon = $icon; Plated = $Matches.base -eq 'Square44x44Logo' }
    }
    throw "Do not know how to draw $Name."
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$sourceImage = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).ProviderPath)
$clamp = New-Object System.Drawing.Imaging.ImageAttributes
$clamp.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

try {
    Get-RequiredImages | ForEach-Object {
        $name = $_
        $spec = Get-ImageSpec -Name $name
        $target = Join-Path $OutputDir $name

        # Same size, no plate: write the source's own pixels rather than a resample of them.
        if (-not $spec.Plated -and $spec.Width -eq $sourceImage.Width -and $spec.Height -eq $sourceImage.Height -and $spec.Icon -eq $sourceImage.Width) {
            Copy-Item -LiteralPath (Resolve-Path -LiteralPath $Source).ProviderPath -Destination $target -Force
            Write-Host "$name (copied)"
            return
        }

        $bitmap = New-Object System.Drawing.Bitmap $spec.Width, $spec.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.Clear($(if ($spec.Plated) { $plate } else { [System.Drawing.Color]::Transparent }))

            # Centred; integer offsets so the icon's edges land on whole pixels.
            $x = [int][math]::Floor(($spec.Width - $spec.Icon) / 2)
            $y = [int][math]::Floor(($spec.Height - $spec.Icon) / 2)
            $destination = New-Object System.Drawing.Rectangle $x, $y, $spec.Icon, $spec.Icon
            $graphics.DrawImage($sourceImage, $destination, 0, 0, $sourceImage.Width, $sourceImage.Height, [System.Drawing.GraphicsUnit]::Pixel, $clamp)
        }
        finally {
            $graphics.Dispose()
        }
        try { $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png) }
        finally { $bitmap.Dispose() }
        Write-Host "$name ($($spec.Width)x$($spec.Height), icon $($spec.Icon), $(if ($spec.Plated) { 'plated' } else { 'transparent' }))"
    }
}
finally {
    $clamp.Dispose()
    $sourceImage.Dispose()
}
