<#
.SYNOPSIS  Builds EasyLive-User-Guide.html (single file, images embedded) and .pdf (via Edge headless) from EasyLive-User-Guide.md.
           Understands only the small Markdown subset used by the guide.
#>
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$lines = Get-Content (Join-Path $dir 'EasyLive-User-Guide.md') -Encoding utf8
Add-Type -AssemblyName System.Web

function Inline([string]$t) {
    $t = [System.Web.HttpUtility]::HtmlEncode($t)
    $t = [regex]::Replace($t, '!\[([^\]]*)\]\(([^)]+)\)', {
        param($m)
        $f = Join-Path $dir $m.Groups[2].Value
        $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f))
        "<figure><img alt=`"$($m.Groups[1].Value)`" src=`"data:image/png;base64,$b64`"><figcaption>$($m.Groups[1].Value)</figcaption></figure>"
    })
    $t = [regex]::Replace($t, '\*\*(.+?)\*\*', '<strong>$1</strong>')
    $t = [regex]::Replace($t, '(?<![\w*])\*([^*\s][^*]*?)\*(?![\w*])', '<em>$1</em>')
    $t = [regex]::Replace($t, '`([^`]+)`', '<code>$1</code>')
    $t
}

$html = New-Object System.Text.StringBuilder
$i = 0; $inUl = $false; $inOl = $false
function CloseLists {
    if ($script:inUl) { [void]$html.AppendLine('</ul>'); $script:inUl = $false }
    if ($script:inOl) { [void]$html.AppendLine('</ol>'); $script:inOl = $false }
}
function Cells([string]$r) { ($r.Trim().Trim('|') -split '\|') | ForEach-Object { $_.Trim() } }

while ($i -lt $lines.Count) {
    $l = $lines[$i]
    if ($l -match '^\|') {
        CloseLists
        $rows = @(); while ($i -lt $lines.Count -and $lines[$i] -match '^\|') { $rows += $lines[$i]; $i++ }
        [void]$html.AppendLine('<table><thead><tr>' + ((Cells $rows[0] | ForEach-Object { "<th>$(Inline $_)</th>" }) -join '') + '</tr></thead><tbody>')
        foreach ($r in $rows | Select-Object -Skip 2) { [void]$html.AppendLine('<tr>' + ((Cells $r | ForEach-Object { "<td>$(Inline $_)</td>" }) -join '') + '</tr>') }
        [void]$html.AppendLine('</tbody></table>'); continue
    }
    if ($l -match '^(#{1,3}) (.*)$') { CloseLists; $n = $Matches[1].Length; [void]$html.AppendLine("<h$n>$(Inline $Matches[2])</h$n>") }
    elseif ($l -match '^- (.*)$') {
        if ($inOl) { [void]$html.AppendLine('</ol>'); $inOl = $false }
        if (-not $inUl) { [void]$html.AppendLine('<ul>'); $inUl = $true }
        [void]$html.AppendLine("<li>$(Inline $Matches[1])</li>")
    }
    elseif ($l -match '^  - (.*)$' -and $inUl) { [void]$html.AppendLine("<li class=`"sub`">$(Inline $Matches[1])</li>") }
    elseif ($l -match '^\d+\. (.*)$') {
        if ($inUl) { [void]$html.AppendLine('</ul>'); $inUl = $false }
        if (-not $inOl) { [void]$html.AppendLine('<ol>'); $inOl = $true }
        [void]$html.AppendLine("<li>$(Inline $Matches[1])")
        # indented continuation lines (paragraphs, images) belong to this item
        while ($i + 1 -lt $lines.Count -and ($lines[$i + 1] -match '^\s{2,}\S' -or ($lines[$i + 1] -eq '' -and $i + 2 -lt $lines.Count -and $lines[$i + 2] -match '^\s{2,}\S'))) {
            $i++
            if ($lines[$i] -ne '') { [void]$html.AppendLine("<p>$(Inline $lines[$i].Trim())</p>") }
        }
        [void]$html.AppendLine('</li>')
    }
    elseif ($l.Trim() -eq '') { }
    else { CloseLists; [void]$html.AppendLine("<p>$(Inline $l)</p>") }
    $i++
}
CloseLists

$logo = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $dir '..\..\assets\logo.png')))
$css = @'
:root{--bg:#f6f7fb;--card:#fff;--ink:#1c2033;--mute:#5b6280;--acc:#e0245e;--acc2:#7c3aed;--line:#dfe3ee;--code:#eef0f7;--th:#eceffa;--alt:#fafbff}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:17px/1.65 "Segoe UI",system-ui,sans-serif}
main{max-width:820px;margin:0 auto;padding:24px 20px 60px}
header.hero img{width:72px;height:72px;display:block}
h1{font-size:2.1rem;margin:.2em 0 0;background:linear-gradient(90deg,var(--acc),var(--acc2));-webkit-background-clip:text;background-clip:text;color:transparent;display:inline-block}
h2{margin:2.2em 0 .6em;font-size:1.5rem;padding-bottom:.25em;border-bottom:3px solid var(--line)}
h3{margin:1.6em 0 .4em;font-size:1.2rem;color:var(--acc2)}
p{margin:.7em 0} li{margin:.4em 0} ul,ol{padding-left:1.4em}
li.sub{margin-left:1.2em}
code{background:var(--code);padding:.1em .4em;border-radius:5px;font:.92em Consolas,monospace;word-break:break-all}
table{width:100%;border-collapse:collapse;margin:1em 0;background:var(--card);font-size:.95rem;border:1px solid var(--line)}
th,td{padding:9px 12px;text-align:left;vertical-align:top;border-bottom:1px solid var(--line)}
th{background:var(--th)} tr:nth-child(even) td{background:var(--alt)}
figure{margin:1em 0;text-align:center;break-inside:avoid}
figure img{max-width:340px;width:100%;border-radius:14px;box-shadow:0 6px 24px rgba(30,30,70,.25)}
figcaption{color:var(--mute);font-size:.88rem;margin-top:.5em}
@media(prefers-color-scheme:dark){:root{--bg:#12141f;--card:#1b1e2e;--ink:#e8eaf4;--mute:#9aa1c0;--line:#2c3050;--code:#262a42;--th:#23273d;--alt:#1f2235}}
@media print{:root{--bg:#fff;--card:#fff;--ink:#1c2033;--mute:#5b6280;--line:#dfe3ee;--code:#eef0f7;--th:#eceffa;--alt:#fafbff}
body{font-size:12.5px;line-height:1.5}main{padding:0}h2{break-after:avoid}h3{break-after:avoid}tr{break-inside:avoid}figure img{max-width:230px;box-shadow:none;border:1px solid #ccc}}
'@
$doc = "<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><meta name=`"viewport`" content=`"width=device-width,initial-scale=1`"><title>EasyLive User Guide</title><style>$css</style></head><body><main><header class=`"hero`"><img alt=`"EasyLive logo`" src=`"data:image/png;base64,$logo`"></header>$($html.ToString())</main></body></html>"
$out = Join-Path $dir 'EasyLive-User-Guide.html'
[IO.File]::WriteAllText($out, $doc, (New-Object Text.UTF8Encoding($false)))
'{0}  {1:N0} KB' -f $out, ((Get-Item $out).Length / 1KB)

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge) {
    $pdf = Join-Path $dir 'EasyLive-User-Guide.pdf'
    $tmp = Join-Path $env:TEMP 'el-edge-profile'
    & $edge --headless=new --disable-gpu "--user-data-dir=$tmp" --no-pdf-header-footer "--print-to-pdf=$pdf" ([Uri]$out).AbsoluteUri | Out-Null
    Start-Sleep 3
    if (Test-Path $pdf) { '{0}  {1:N0} KB' -f $pdf, ((Get-Item $pdf).Length / 1KB) } else { Write-Warning 'PDF not produced' }
} else { Write-Warning 'Edge not found - PDF skipped' }
