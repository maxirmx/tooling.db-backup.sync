# Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
# All rights reserved.

#Requires -Version 7.0

[CmdletBinding()]
param(
    [string] $SourceImage = (Join-Path $PSScriptRoot '..\assets\db-backup-remote-sync.png')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = [System.IO.Path]::GetFullPath($SourceImage)
$assetsDirectory = [System.IO.Path]::GetDirectoryName($sourcePath)
$source = [System.Drawing.Bitmap]::FromFile($sourcePath)

function New-ResizedPngBytes([System.Drawing.Image] $image, [int] $size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($image, 0, 0, $size, $size)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $bitmap.Dispose()
    }
}

function New-Icon([System.Drawing.Image] $image, [string] $outputPath) {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $images.Add((New-ResizedPngBytes $image $size))
    }

    $stream = [System.IO.File]::Create($outputPath)
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16] 0)
            $writer.Write([uint16] 1)
            $writer.Write([uint16] $sizes.Count)
            $offset = 6 + (16 * $sizes.Count)
            for ($index = 0; $index -lt $sizes.Count; $index++) {
                $size = $sizes[$index]
                $writer.Write([byte] $(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte] $(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte] 0)
                $writer.Write([byte] 0)
                $writer.Write([uint16] 1)
                $writer.Write([uint16] 32)
                $writer.Write([uint32] $images[$index].Length)
                $writer.Write([uint32] $offset)
                $offset += $images[$index].Length
            }

            foreach ($bytes in $images) {
                $writer.Write($bytes)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function New-InstallerBitmap(
    [System.Drawing.Image] $image,
    [string] $outputPath,
    [int] $width,
    [int] $height,
    [int] $imageX,
    [int] $imageY,
    [int] $imageSize,
    [bool] $drawPanel) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            if ($drawPanel) {
                $panel = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(236, 248, 254))
                try {
                    $graphics.FillRectangle($panel, 0, 0, 184, $height)
                }
                finally {
                    $panel.Dispose()
                }
            }

            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($image, $imageX, $imageY, $imageSize, $imageSize)
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        $bitmap.Dispose()
    }
}

try {
    New-Icon $source (Join-Path $assetsDirectory 'db-backup-remote-sync.ico')
    New-InstallerBitmap $source (Join-Path $assetsDirectory 'installer-banner.bmp') 493 58 431 3 52 $false
    New-InstallerBitmap $source (Join-Path $assetsDirectory 'installer-dialog.bmp') 493 312 27 86 130 $true
}
finally {
    $source.Dispose()
}
