#requires -Version 5.1
<#
    Draws the SharpMind Studio mark (the shape in wwwroot\sharpmind-icon.svg) at
    any size. Dot-source this file; both the MSIX packager and the Store asset
    generator use it so the icon has one definition.

        . "$PSScriptRoot\StudioMark.ps1"
        Save-StudioMark -Path 'Square150x150Logo.png' -Size 150
#>

Add-Type -AssemblyName System.Drawing

function New-StudioRoundedPath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [single]$Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $Radius * 2
    $path.AddArc($Rect.X, $Rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rect.Right - $diameter, $Rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rect.Right - $diameter, $Rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-StudioMarkBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # The source viewBox is 256x256.
        $s = [single]($Size / 256.0)
        $panel = New-Object System.Drawing.RectangleF((10 * $s), (10 * $s), (236 * $s), (236 * $s))
        $panelPath = New-StudioRoundedPath -Rect $panel -Radius (54 * $s)

        $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $panel,
            [System.Drawing.Color]::FromArgb(255, 12, 20, 29),
            [System.Drawing.Color]::FromArgb(255, 18, 31, 42),
            [single]45)
        $graphics.FillPath($background, $panelPath)

        $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(140, 48, 68, 86), [single](4 * $s))
        $graphics.DrawPath($edge, $panelPath)

        $spoke = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(122, 94, 234, 212), [single](9 * $s))
        $spoke.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $spoke.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

        $nodes = @(@(128, 52), @(205, 183), @(51, 191))
        $centre = 128 * $s
        foreach ($node in $nodes) {
            $graphics.DrawLine($spoke, $centre, $centre, [single]($node[0] * $s), [single]($node[1] * $s))
        }

        $tip = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 94, 234, 212))
        foreach ($node in $nodes) {
            $x = [single](($node[0] - 14) * $s)
            $y = [single](($node[1] - 14) * $s)
            $graphics.FillEllipse($tip, $x, $y, [single](28 * $s), [single](28 * $s))
        }

        $core = New-Object System.Drawing.RectangleF((100 * $s), (100 * $s), (56 * $s), (56 * $s))
        $coreBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $core,
            [System.Drawing.Color]::FromArgb(255, 165, 243, 252),
            [System.Drawing.Color]::FromArgb(255, 94, 234, 212),
            [single]90)
        $graphics.FillEllipse($coreBrush, $core)

        $coreEdge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(191, 230, 255, 252), [single](5 * $s))
        $graphics.DrawEllipse($coreEdge, $core)

        $background.Dispose()
        $edge.Dispose()
        $spoke.Dispose()
        $tip.Dispose()
        $coreBrush.Dispose()
        $coreEdge.Dispose()
        $panelPath.Dispose()
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Save-StudioMark {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Size
    )

    $bitmap = New-StudioMarkBitmap -Size $Size
    try {
        $folder = Split-Path -Parent $Path
        if ($folder -and -not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Save-StudioCanvas {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height,
        [Parameter(Mandatory)][int]$MarkSize,
        [string]$Background = '#0C141D'
    )

    $colour = [System.Drawing.ColorTranslator]::FromHtml($Background)
    $canvas = New-Object System.Drawing.Bitmap($Width, $Height)
    $canvas.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $mark = New-StudioMarkBitmap -Size $MarkSize

    try {
        $graphics.Clear($colour)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($mark, [int](($Width - $MarkSize) / 2), [int](($Height - $MarkSize) / 2), $MarkSize, $MarkSize)

        $folder = Split-Path -Parent $Path
        if ($folder -and -not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
        $canvas.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $mark.Dispose()
        $graphics.Dispose()
        $canvas.Dispose()
    }
}
