$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$gac = "C:\Windows\Microsoft.NET\assembly\GAC_MSIL"
$gac64 = "C:\Windows\Microsoft.NET\assembly\GAC_64"

function Find-Gac([string]$name, [string]$root) {
    $hit = Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_MSIL" -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $hit) {
        $hit = Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_64" -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $hit) {
        $hit = Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_32" -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $hit) { throw "missing $name" }
    return $hit.FullName
}

$refs = @(
    (Find-Gac "System" $gac),
    (Find-Gac "System.Core" $gac),
    (Find-Gac "System.Xml" $gac),
    (Find-Gac "System.Data" $gac),
    (Find-Gac "System.Drawing" $gac),
    (Find-Gac "System.Windows.Forms" $gac),
    (Find-Gac "System.Web.Extensions" $gac),
    (Find-Gac "System.Net.Http" $gac),
    (Find-Gac "System.Runtime.Serialization" $gac),
    (Find-Gac "System.Xaml" $gac),
    (Find-Gac "WindowsBase" $gac),
    (Find-Gac "PresentationCore" $gac64),
    (Find-Gac "PresentationFramework" $gac)
) | ForEach-Object { "/r:`"$_`"" }

$out = Join-Path $PSScriptRoot "DSHPet.exe"
$src = Join-Path $PSScriptRoot "Pet.cs"
& $csc /nologo /noconfig /target:winexe /platform:anycpu /optimize+ "/out:$out" $refs $src
if ($LASTEXITCODE -ne 0) { throw "compile failed $LASTEXITCODE" }
"built $out"
