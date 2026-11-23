# Gera uma atualização do Lazuli: compila (Release, backend Solara), empacota, assina e deixa tudo em dist\main\ pronto para subir.
#
#   scripts\make-update.ps1 -Version 1.0.1 -Notes "Melhoria A;Correção B"
#   scripts\make-update.ps1 -Version 1.0.1 -Mandatory
#
# Canal único: "main" — todo mundo recebe por ele. O zip sai SEM arquivos de terceiros (SolaraV3.dll): o app baixa a DLL do site
# oficial do Solara na primeira abertura e a mantém atualizada, conferindo tamanho e SHA-256.
# Depois suba dist\ para o Cloudflare (veja README-UPDATE.md). Rode com:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-update.ps1 -Version 1.0.1
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Notes = '',
    [switch] $Mandatory,
    [string] $MinVersion = '',
    [string] $PackageBase = ''   # ex.: https://pub-xxxx.r2.dev  (se o zip fica fora do Cloudflare Workers, que limita 25 MiB por arquivo)
)
$ErrorActionPreference = 'Stop'
$channel = 'main'
$root = Split-Path $PSScriptRoot -Parent
$key = Join-Path $PSScriptRoot 'keys\update-private.pem'
if (-not (Test-Path $key)) { throw "Falta $key. Gere com: dotnet run --project tools\UpdateTool -- keygen scripts\keys" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a versão no formato 1.2.3' }
$dist = Join-Path $root 'dist'
$proj = Join-Path $root 'src\Executor.UI\Executor.UI.csproj'
$out = Join-Path $root "publish-$channel"

# o app precisa estar fechado para não travar a publicação
Get-Process Lazuli -ErrorAction SilentlyContinue | ForEach-Object { try { $_ | Stop-Process -Force -ErrorAction Stop } catch { throw "Feche o Lazuli (PID $($_.Id)) e rode de novo." } }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Output "== Compilando $channel $Version"

# 1) plugin do backend limpo (sem a DLL de terceiros) -> vai embutido no exe (o exe se instala sozinho; veja Installer.cs)
$pluginProj = Join-Path $root 'backends\Executor.Backend.Solara\Executor.Backend.Solara.csproj'
$pluginDir = Split-Path $pluginProj -Parent
Remove-Item (Join-Path $pluginDir 'bin'), (Join-Path $pluginDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
dotnet build $pluginProj -c Release -p:BundleSolaraDll=false --nologo -v q | Select-String ' error ' | ForEach-Object { throw $_.Line }
if ($LASTEXITCODE -ne 0) { throw 'build do plugin falhou' }
$emb = Join-Path $root 'src\Executor.UI\Embedded\solara'
if (Test-Path $emb) { Remove-Item $emb -Recurse -Force }
New-Item $emb -ItemType Directory | Out-Null
Get-ChildItem (Join-Path $pluginDir 'bin\Release\net10.0') -File | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -notlike 'Executor.Contracts.*' } | Copy-Item -Destination $emb

# 2) o app, com o plugin embutido
dotnet publish $proj -c Release -p:Backend=solara -p:BundleSolaraDll=false -p:EmbedSolara=true "-p:Version=$Version" -o $out --nologo | Select-String ' error ' | ForEach-Object { throw $_.Line }
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }
Copy-Item (Join-Path $PSScriptRoot 'LEIA-ME.txt') $out   # vai dentro do zip (quem recebe lê os requisitos)

Remove-Item (Split-Path $emb -Parent) -Recurse -Force   # pasta temporária de embutir
# o que está embutido tem que ser idêntico ao plugin da pasta publicada
# garante que nada de terceiros foi junto
$stray = Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -match '^SolaraV3|DLCTBL|\.seed$' }
if ($stray) { throw "Arquivo de terceiros no pacote: $($stray.FullName -join ', ')" }

# confere que o manifesto do plugin bate com o que está no pacote (arquivo faltando = o app cai no modo demonstração)
foreach ($bj in Get-ChildItem (Join-Path $out 'backends') -Recurse -Filter backend.json) {
    $m = Get-Content $bj.FullName -Raw | ConvertFrom-Json
    foreach ($e in $m.files.PSObject.Properties) {
        $p = Join-Path $bj.DirectoryName $e.Name
        if (-not (Test-Path $p)) { throw "backend.json lista $($e.Name), mas o arquivo não está no pacote" }
        if ((Get-FileHash $p -Algorithm SHA256).Hash -ne $e.Value) { throw "hash diferente em $($e.Name)" }
    }
    $extra = Get-ChildItem $bj.DirectoryName -Recurse -File | Where-Object { $_.Name -ne 'backend.json' -and $_.Extension -ne '.pdb' -and -not $m.files.PSObject.Properties[$_.Name] }
    if ($extra) { throw "arquivo no pacote que o backend.json não lista: $($extra.Name -join ', ')" }
    Write-Output "   backend $($m.id): manifesto confere ($(@($m.files.PSObject.Properties).Count) arquivos)"
}

# assina o exe (certificado autoassinado "CN=Executor" do usuário, se existir)
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq 'CN=Executor' } | Select-Object -First 1
if ($cert) {
    $sig = Set-AuthenticodeSignature -FilePath (Join-Path $out 'Lazuli.exe') -Certificate $cert -HashAlgorithm SHA256
    if ($sig.Status -eq 'HashMismatch') { throw 'assinatura inválida' }
    Write-Output "   assinado ($($cert.Thumbprint.Substring(0,8))...) status: $($sig.Status)"
} else { Write-Output '   AVISO: sem certificado "CN=Executor"; exe sem assinatura' }

# versões antigas do pacote não precisam ficar no servidor (os apps só baixam a última)
Remove-Item (Join-Path $dist $channel) -Recurse -Force -ErrorAction SilentlyContinue
$a = @('run', '--project', (Join-Path $root 'tools\UpdateTool'), '-c', 'Release', '--', 'pack',
    '--dir', $out, '--out', $dist, '--version', $Version, '--channel', $channel, '--key', $key, '--notes', $Notes)
if ($Mandatory) { $a += '--mandatory' }
if ($MinVersion) { $a += @('--min-version', $MinVersion) }
if ($PackageBase) { $a += @('--package-base', $PackageBase) }
dotnet @a
if ($LASTEXITCODE -ne 0) { throw 'empacotar falhou' }
# exe único para mandar a outras pessoas: se instala sozinho em %LOCALAPPDATA%\Programs\Lazuli ao ser aberto
$rel = Join-Path $root 'release'
New-Item $rel -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $out 'Lazuli.exe') (Join-Path $rel 'Lazuli.exe') -Force
Copy-Item (Join-Path $PSScriptRoot 'LEIA-ME.txt') (Join-Path $rel 'LEIA-ME.txt') -Force
Write-Output "   exe único: $(Join-Path $rel 'Lazuli.exe')"
Write-Output "`nPronto. Suba a pasta dist\ para o Cloudflare:  $dist"
