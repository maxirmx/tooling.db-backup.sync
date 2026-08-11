# Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
# All rights reserved.

#Requires -Version 7.0

[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\installer\License.ru-ru.rtf')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$paragraphs = @(
    'Лицензия MIT',
    'Copyright (c) 2026 Максим Самсонов',
    'Настоящим предоставляется бесплатное разрешение любому лицу, получившему копию данного программного обеспечения и сопутствующей документации (далее — «Программное обеспечение»), использовать Программное обеспечение без ограничений, включая, помимо прочего, право на использование, копирование, изменение, объединение, публикацию, распространение, сублицензирование и/или продажу копий Программного обеспечения, а также лицам, которым предоставляется данное Программное обеспечение, при соблюдении следующих условий:',
    'Указанное выше уведомление об авторском праве и настоящее уведомление о разрешении должны быть включены во все копии или значительные части Программного обеспечения.',
    'ПРОГРАММНОЕ ОБЕСПЕЧЕНИЕ ПРЕДОСТАВЛЯЕТСЯ «КАК ЕСТЬ», БЕЗ КАКИХ-ЛИБО ГАРАНТИЙ, ЯВНЫХ ИЛИ ПОДРАЗУМЕВАЕМЫХ, ВКЛЮЧАЯ, ПОМИМО ПРОЧЕГО, ГАРАНТИИ ТОВАРНОЙ ПРИГОДНОСТИ, ПРИГОДНОСТИ ДЛЯ КОНКРЕТНЫХ ЦЕЛЕЙ И НЕНАРУШЕНИЯ ПРАВ. НИ В КОЕМ СЛУЧАЕ АВТОРЫ ИЛИ ПРАВООБЛАДАТЕЛИ НЕ НЕСУТ ОТВЕТСТВЕННОСТИ ПО КАКИМ-ЛИБО ИСКАМ, ЗА УЩЕРБ ИЛИ ПО ИНЫМ ТРЕБОВАНИЯМ, В РЕЗУЛЬТАТЕ ДЕЙСТВИЯ КОНТРАКТА, ДЕЛИКТА ИЛИ ИНОГО ПРОИСХОЖДЕНИЯ, ВОЗНИКАЮЩИМ ИЗ-ЗА ИСПОЛЬЗОВАНИЯ ПРОГРАММНОГО ОБЕСПЕЧЕНИЯ ИЛИ ИНЫХ ДЕЙСТВИЙ С ПРОГРАММНЫМ ОБЕСПЕЧЕНИЕМ.'
)

function ConvertTo-RtfText([string] $text) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $text.ToCharArray()) {
        switch ($character) {
            '\' { [void] $builder.Append('\\') }
            '{' { [void] $builder.Append('\{') }
            '}' { [void] $builder.Append('\}') }
            default {
                $value = [int] $character
                if ($value -ge 32 -and $value -le 126) {
                    [void] $builder.Append($character)
                }
                else {
                    $signed = if ($value -gt 32767) { $value - 65536 } else { $value }
                    [void] $builder.Append("\u${signed}?")
                }
            }
        }
    }

    return $builder.ToString()
}

$body = ($paragraphs | ForEach-Object { (ConvertTo-RtfText $_) + '\par\par' }) -join "`r`n"
$rtf = @"
{\rtf1\ansi\deff0\uc1
{\*\comment Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)\line All rights reserved.}
{\fonttbl{\f0 Segoe UI;}}
\fs20
$body
}
"@

Set-Content -LiteralPath $OutputPath -Value $rtf -Encoding ascii -NoNewline
