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

# ---- assinatura (certificado autoassinado "Lazuli", criado por scripts\make-cert.ps1) ----
# Autoassinado dá a assinatura e a identidade "Lazuli" ao arquivo, mas o Windows só confia em certificados de autoridades reconhecidas.
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq 'CN=Lazuli' -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) { throw 'Falta o certificado de assinatura "CN=Lazuli". Crie com: powershell -File scripts\make-cert.ps1' }
function Sign-File($path) {
    # com carimbo de hora (serviço gratuito da DigiCert): a assinatura continua datada mesmo depois de o certificado vencer; sem rede, assina sem carimbo
    $sig = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction SilentlyContinue
    if (-not $sig -or -not $sig.SignerCertificate) { $sig = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 }
    if ($sig.Status -eq 'HashMismatch' -or -not $sig.SignerCertificate) { throw "falha ao assinar $path" }
    Write-Output "   assinado: $(Split-Path $path -Leaf) ($($cert.Thumbprint.Substring(0,8))...)"
}
$proj = Join-Path $root 'src\Lazuli.UI\Lazuli.UI.csproj'
$out = Join-Path $root "publish-$channel"

# o app precisa estar fechado para não travar a publicação
Get-Process Lazuli -ErrorAction SilentlyContinue | ForEach-Object { try { $_ | Stop-Process -Force -ErrorAction Stop } catch { throw "Feche o Lazuli (PID $($_.Id)) e rode de novo." } }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Output "== Compilando $channel $Version"

# 1) plugin do backend limpo (sem a DLL de terceiros) -> vai embutido no exe (o exe se instala sozinho; veja Installer.cs)
$pluginProj = Join-Path $root 'backends\Lazuli.Backend.Solara\Lazuli.Backend.Solara.csproj'
$pluginDir = Split-Path $pluginProj -Parent
Remove-Item (Join-Path $pluginDir 'bin'), (Join-Path $pluginDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
dotnet build $pluginProj -c Release -p:BundleSolaraDll=false --nologo -v q | Select-String ' error ' | ForEach-Object { throw $_.Line }
if ($LASTEXITCODE -ne 0) { throw 'build do plugin falhou' }
$emb = Join-Path $root 'src\Lazuli.UI\Embedded\solara'
if (Test-Path $emb) { Remove-Item $emb -Recurse -Force }
New-Item $emb -ItemType Directory | Out-Null
Get-ChildItem (Join-Path $pluginDir 'bin\Release\net10.0') -File | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -notlike 'Lazuli.Contracts.*' } | Copy-Item -Destination $emb
# O plugin NÃO é assinado de propósito: testado, com assinatura autoassinada o Controle Inteligente de Aplicativos do Windows BLOQUEIA o carregamento
# da DLL (o app cai no modo demonstração); sem assinatura ela carrega. Quem garante a integridade do plugin é o SHA-256 do backend.json.

# 2) o app, com o plugin embutido
dotnet publish $proj -c Release -p:Backend=solara -p:BundleSolaraDll=false -p:EmbedSolara=true "-p:Version=$Version" -o $out --nologo | Select-String ' error ' | ForEach-Object { throw $_.Line }
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }
Copy-Item (Join-Path $PSScriptRoot 'LEIA-ME.txt') $out   # vai dentro do zip (quem recebe lê os requisitos)

# o plugin publicado é o mesmo que foi embutido no exe (garante hashes idênticos)
$pubPlugin = Join-Path $out 'backends\solara'
Get-ChildItem $emb -File | Copy-Item -Destination $pubPlugin -Force
foreach ($f in Get-ChildItem $emb -File) { if ((Get-FileHash $f.FullName).Hash -ne (Get-FileHash (Join-Path $pubPlugin $f.Name)).Hash) { throw "plugin embutido difere do publicado: $($f.Name)" } }
Remove-Item (Split-Path $emb -Parent) -Recurse -Force   # pasta temporária de embutir
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

# assina o exe
Sign-File (Join-Path $out 'Lazuli.exe')

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
