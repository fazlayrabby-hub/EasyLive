<#
.SYNOPSIS
  Builds the distributable EasyLive package: artifacts\EasyLive-<version>\ and artifacts\EasyLive-<version>.zip

.DESCRIPTION
  1. dotnet publish (Release, win-x64, self-contained; NOT single-file, NOT trimmed - WPF and FFmpeg.AutoGen break).
  2. Copies the FFmpeg LGPL shared DLLs (only the ones the app needs, plus any FFmpeg DLL they import) into <out>\ffmpeg\.
     ffmpeg.exe / ffprobe.exe / ffplay.exe are NOT shipped.
  3. Copies LICENSES\ and THIRD-PARTY-NOTICES.txt.
  4. Verifies the FFmpeg build is LGPL-only (configuration string + binary scan) and prints DLL dependents.
  5. Zips the folder.

.PARAMETER Version   Version stamped into the assembly and the folder/zip name.
.PARAMETER FfmpegBin Folder with the FFmpeg LGPL shared DLLs. Default: newest tools\ffmpeg\*\bin.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$FfmpegBin
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $repo "artifacts\EasyLive-$Version"
$zip  = "$out.zip"

if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

# FFmpeg DLLs the app binds (FFmpeg.AutoGen 8.1.0 <-> FFmpeg 8.1). Must match src\EasyLive.Core\Streaming\FfmpegRuntime.cs.
$required = 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swscale-9.dll', 'swresample-6.dll'

if (-not $FfmpegBin) {
    $FfmpegBin = Get-ChildItem (Join-Path $repo 'tools\ffmpeg') -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'bin' } |
        Where-Object { Test-Path (Join-Path $_ 'avcodec-62.dll') } | Select-Object -First 1
}
if (-not $FfmpegBin -or -not (Test-Path (Join-Path $FfmpegBin 'avcodec-62.dll'))) {
    throw "FFmpeg LGPL shared build not found. See tools\README.md (expected tools\ffmpeg\<build>\bin\avcodec-62.dll)."
}
Write-Host "FFmpeg bin: $FfmpegBin"

# ---- PE helpers --------------------------------------------------------------------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
public static class PeTools
{
    static uint Rva2Off(uint rva, List<uint[]> secs)
    {
        foreach (var s in secs) if (rva >= s[0] && rva < s[0] + Math.Max(s[1], s[2])) return rva - s[0] + s[3];
        return 0;
    }
    public static List<string> Imports(string path)
    {
        var res = new List<string>();
        using (var f = File.OpenRead(path))
        using (var r = new BinaryReader(f))
        {
            f.Position = 0x3C; f.Position = r.ReadInt32();
            if (r.ReadUInt32() != 0x4550) return res;
            f.Position += 2; int nsec = r.ReadUInt16(); f.Position += 12; int optSize = r.ReadUInt16(); f.Position += 2;
            long opt = f.Position; ushort magic = r.ReadUInt16();
            int ddOff = magic == 0x20B ? 112 : 96;
            f.Position = opt + ddOff + 8; uint impRva = r.ReadUInt32();
            f.Position = opt + optSize;
            var secs = new List<uint[]>();
            for (int i = 0; i < nsec; i++)
            {
                f.Position += 8; uint vs = r.ReadUInt32(), va = r.ReadUInt32(), rs = r.ReadUInt32(), ro = r.ReadUInt32();
                f.Position += 16; secs.Add(new[] { va, vs, rs, ro });
            }
            if (impRva == 0) return res;
            uint off = Rva2Off(impRva, secs);
            while (off != 0)
            {
                f.Position = off + 12; uint nameRva = r.ReadUInt32();
                if (nameRva == 0) break;
                long next = off + 20; f.Position = Rva2Off(nameRva, secs);
                var sb = new StringBuilder(); int b;
                while ((b = f.ReadByte()) > 0) sb.Append((char)b);
                res.Add(sb.ToString()); off = (uint)next;
            }
        }
        return res;
    }
    // Finds ASCII needle in a file (streamed, 8 MB chunks). Returns the number of hits and fills up to maxSamples context strings.
    public static int Scan(string path, string needle, List<string> samples, int maxSamples)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle); int hits = 0;
        byte[] buf = new byte[8 * 1024 * 1024 + 256]; int keep = 0;
        using (var f = File.OpenRead(path))
        {
            int read;
            while ((read = f.Read(buf, keep, 8 * 1024 * 1024)) > 0)
            {
                int total = keep + read;
                for (int i = 0; i + n.Length <= total; i++)
                {
                    if (buf[i] != n[0]) continue;
                    int j = 1; while (j < n.Length && buf[i + j] == n[j]) j++;
                    if (j < n.Length) continue;
                    hits++;
                    if (samples != null && samples.Count < maxSamples)
                    {
                        int s = i, e = i;
                        while (s > 0 && i - s < 40 && buf[s - 1] >= 32 && buf[s - 1] < 127) s--;
                        while (e < total && e - i < 60 && buf[e] >= 32 && buf[e] < 127) e++;
                        samples.Add(Encoding.ASCII.GetString(buf, s, e - s));
                    }
                }
                keep = Math.Min(total, 256);
                Buffer.BlockCopy(buf, total - keep, buf, 0, keep);
            }
        }
        return hits;
    }
}
'@

function Get-Dependents([string]$file) {
    $dumpbin = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($dumpbin) {
        & $dumpbin.Source /nologo /dependents $file | Where-Object { $_ -match '^\s+\S+\.dll\s*$' } | ForEach-Object { $_.Trim() }
    } else {
        [PeTools]::Imports($file)
    }
}

# ---- 1. publish --------------------------------------------------------------------------------------------------
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "`n== dotnet publish ($Version) =="
& dotnet publish (Join-Path $repo 'src\EasyLive.App\EasyLive.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false `
    -p:DebugType=none -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# ---- 2. FFmpeg DLLs ----------------------------------------------------------------------------------------------
Write-Host "`n== FFmpeg DLLs =="
$ffOut = Join-Path $out 'ffmpeg'
New-Item -ItemType Directory -Force $ffOut | Out-Null
$todo = New-Object System.Collections.Generic.Queue[string]
$seen = @{}
$required | ForEach-Object { $todo.Enqueue($_) }
while ($todo.Count) {
    $name = $todo.Dequeue()
    if ($seen.ContainsKey($name.ToLower())) { continue }
    $seen[$name.ToLower()] = $true
    $src = Join-Path $FfmpegBin $name
    if (-not (Test-Path $src)) { throw "Required FFmpeg DLL missing: $src" }
    Copy-Item $src $ffOut
    # any other FFmpeg DLL (av*/sw*/postproc*) that this one imports must ship too
    foreach ($imp in [PeTools]::Imports($src)) {
        if ($imp -match '^(av|sw|postproc)[a-z]*-\d+\.dll$' -and (Test-Path (Join-Path $FfmpegBin $imp))) { $todo.Enqueue($imp) }
    }
}
Get-ChildItem $ffOut | ForEach-Object { '  {0,-20} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }

# ---- 3. licences -------------------------------------------------------------------------------------------------
Copy-Item (Join-Path $repo 'LICENSES') (Join-Path $out 'LICENSES') -Recurse
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.txt') $out

# ---- 4. verification ---------------------------------------------------------------------------------------------
Write-Host "`n== Licence verification =="
$fail = @()

$ffExe = Join-Path $FfmpegBin 'ffmpeg.exe'
if (Test-Path $ffExe) {
    $cfg = (& $ffExe -version | Where-Object { $_ -like 'configuration:*' }) -join ' '
    foreach ($bad in '--enable-gpl', '--enable-nonfree', '--enable-libx264', '--enable-libx265') {
        if ($cfg -match [regex]::Escape($bad) + '(\s|$)') { $fail += "FFmpeg configuration contains $bad" }
    }
    if ($cfg -notmatch '--enable-version3') { Write-Warning 'FFmpeg configuration has no --enable-version3 (expected for the BtbN LGPL build).' }
    Write-Host ("  configuration: --enable-gpl:{0}  --enable-nonfree:{1}  libx264:{2}  libx265:{3}  version3:{4}  libopenh264:{5}" -f
        ($cfg -match '--enable-gpl'), ($cfg -match '--enable-nonfree'), ($cfg -match '--enable-libx264'), ($cfg -match '--enable-libx265'),
        ($cfg -match '--enable-version3'), ($cfg -match '--enable-libopenh264'))
} else {
    Write-Warning 'ffmpeg.exe not found next to the DLLs - configuration string not checked (binary scan still runs).'
}

foreach ($dll in Get-ChildItem $ffOut -Filter *.dll) {
    $line = $dll.Name
    foreach ($needle in '--enable-gpl', '--enable-nonfree', '--enable-libx264', '--enable-libx265', 'x264_encoder', 'x265_encoder', 'x265_api') {
        $n = [PeTools]::Scan($dll.FullName, $needle, $null, 0)
        $line += "  ${needle}=$n"
        if ($n -gt 0) { $fail += "$($dll.Name) contains '$needle' ($n hits)" }
    }
    # The text "libx264"/"libx265" is allowed only inside the embedded configuration string as "--disable-libx26x".
    foreach ($lib in 'libx264', 'libx265') {
        $all = [PeTools]::Scan($dll.FullName, $lib, $null, 0)
        $dis = [PeTools]::Scan($dll.FullName, "--disable-$lib", $null, 0)
        $line += "  ${lib}=$all(disabled-in-config=$dis)"
        if ($all -ne $dis) { $fail += "$($dll.Name): '$lib' appears outside a --disable-$lib configuration flag" }
    }
    # informational: the h264 parser legitimately contains the text "x264 - core" (it reads x264's SEI banner); not the encoder
    $samples = New-Object 'System.Collections.Generic.List[string]'
    $x = [PeTools]::Scan($dll.FullName, 'x264', $samples, 3)
    $line += "  'x264'=$x"
    Write-Host "  $line"
    foreach ($s in $samples) { Write-Host "      sample: $s" }
}

$how = if (Get-Command dumpbin.exe -ErrorAction SilentlyContinue) { 'dumpbin' } else { 'PE import scan; dumpbin not installed' }
Write-Host "`n== Dependents ($how) =="
foreach ($dll in Get-ChildItem $ffOut -Filter *.dll) {
    Write-Host "  $($dll.Name): $((Get-Dependents $dll.FullName) -join ', ')"
}
Write-Host ("  EasyLive.exe: " + ((Get-Dependents (Join-Path $out 'EasyLive.exe')) -join ', '))

foreach ($must in 'EasyLive.exe', 'THIRD-PARTY-NOTICES.txt', 'LICENSES', 'ffmpeg\avcodec-62.dll') {
    if (-not (Test-Path (Join-Path $out $must))) { $fail += "missing from package: $must" }
}
foreach ($never in 'ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe') {
    if (Test-Path (Join-Path $ffOut $never)) { $fail += "$never must not be shipped" }
}
if ($fail.Count) { $fail | ForEach-Object { Write-Error $_ -ErrorAction Continue }; throw "Package verification FAILED." }
Write-Host "  OK: no GPL/nonfree/x264/x265 markers; package layout complete."

# ---- 5. zip ------------------------------------------------------------------------------------------------------
Write-Host "`n== Zip =="
# ZipFile (not Compress-Archive): Windows PowerShell 5.1's Compress-Archive writes backslash entry names.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
'{0}  {1:N1} MB' -f $zip, ((Get-Item $zip).Length / 1MB)

