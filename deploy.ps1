# deploy.ps1 - sestaví projekt pro Linux, nahraje ho přes SSH a (volitelně) spustí.
#
# Příklady (spouštět v PowerShellu v kořeni řešení):
#   .\deploy.ps1 -Target pc@192.168.1.240            první použití, adresa se si zapamatuje
#   .\deploy.ps1                                     příště stačí tohle
#   .\deploy.ps1 -ProgramArgs "--auto"               spustí s parametrem
#   .\deploy.ps1 -NoRun                              jen nahraje, nespouští
#   .\deploy.ps1 -Project PS150.Raspi -Runtime linux-arm64 -Target pi@raspi
#
# Poznámky:
#  - Na cílovém stroji musí být .NET 10 v ~/dotnet (jak jste nainstaloval).
#  - SSH bez hesla: ssh-keygen (jednou) a pak veřejný klíč přidat na cíl do
#    ~/.ssh/authorized_keys, jinak se heslo ptá při každém ssh/scp.
#  - Spouští se přes "ssh -t", aby fungovala klávesnice v programu.

param(
    [string]$Target = "",
    [string]$Project = "PS150.Asrock",
    [string]$Runtime = "linux-x64",
    [string]$RemoteDir = "",
    [string]$ProgramArgs = "",
    [switch]$NoRun
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$targetFile = Join-Path $root ".deploy-target-$Project.txt"

if ([string]::IsNullOrWhiteSpace($Target)) {
    if (Test-Path $targetFile) {
        $Target = (Get-Content $targetFile -Raw).Trim()
    }
    else {
        throw "Zadejte cíl, např.: .\deploy.ps1 -Target pc@192.168.1.240 (uloží se pro příště)"
    }
}
else {
    Set-Content -Path $targetFile -Value $Target -NoNewline
}

if ([string]::IsNullOrWhiteSpace($RemoteDir)) {
    $RemoteDir = "~/ps150/" + $Project.ToLower()
}

$publishDir = Join-Path $root "publish\$Project"

Write-Host "== Sestavuji $Project pro $Runtime ..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "$Project\$Project.csproj") -c Release -r $Runtime --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Sestavení selhalo." }

Write-Host "== Nahrávám na ${Target}:$RemoteDir ..." -ForegroundColor Cyan
ssh $Target "mkdir -p $RemoteDir"
if ($LASTEXITCODE -ne 0) { throw "SSH selhalo (adresa, uživatel nebo heslo?)." }

scp -r "$publishDir\*" "${Target}:$RemoteDir/"
if ($LASTEXITCODE -ne 0) { throw "Kopírování selhalo." }

if (-not $NoRun) {
    Write-Host "== Spouštím (konec = Q nebo Ctrl+C) ..." -ForegroundColor Cyan
    ssh -t $Target "~/dotnet/dotnet $RemoteDir/$Project.dll $ProgramArgs"
}
