# Сборка AICompanions на Windows встроенным компилятором .NET Framework 4 (SDK не нужен).
#   powershell -ExecutionPolicy Bypass -File build.ps1 -BepInEx "<профиль>\BepInEx" [-Valheim "<папка игры>"] [-Install]
param(
  [Parameter(Mandatory = $true)][string]$BepInEx,
  [string]$Valheim = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
  [switch]$Install
)
$ErrorActionPreference = "Stop"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$m = Join-Path $Valheim "valheim_Data\Managed"
$core = Join-Path $BepInEx "core"
$out = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $out | Out-Null

function Refs($dir, $names) { $names | ForEach-Object { "/reference:" + (Join-Path $dir "$_.dll") } }

$game = Refs $m @("assembly_valheim", "assembly_utils", "assembly_guiutils", "netstandard", "SoftReferenceableAssets",
  "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.TextRenderingModule", "UnityEngine.IMGUIModule",
  "UnityEngine.InputLegacyModule", "UnityEngine.PhysicsModule", "UnityEngine.AnimationModule", "UnityEngine.ImageConversionModule")
& $csc /nologo /target:library /nowarn:618 "/out:$out\AICompanions.dll" @game @(Refs $core @("BepInEx", "0Harmony")) "$PSScriptRoot\AICompanions\*.cs"
if ($LASTEXITCODE) { throw "AICompanions: ошибка сборки" }

$ldr = (Refs $m @("UnityEngine", "UnityEngine.CoreModule", "netstandard")) + (Refs $core @("BepInEx", "Mono.Cecil"))
& $csc /nologo /target:library /nowarn:618 "/out:$out\AICompanionsLoader.dll" @ldr "$PSScriptRoot\AICompanionsLoader\AICompanionsLoader.cs"
if ($LASTEXITCODE) { throw "AICompanionsLoader: ошибка сборки" }

if ($Install) {
  $p = New-Item -ItemType Directory -Force (Join-Path $BepInEx "plugins\AICompanionsLoader")
  $c = New-Item -ItemType Directory -Force (Join-Path $BepInEx "AICompanionsCore")
  Copy-Item "$out\AICompanionsLoader.dll" $p -Force
  Copy-Item "$out\AICompanions.dll" $c -Force
  "OK: установлено в $BepInEx"
} else { "OK: $out" }
