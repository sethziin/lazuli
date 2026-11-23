# Cria o certificado de assinatura de código AUTOASSINADO "CN=Lazuli" no repositório de certificados do usuário (CurrentUser\My).
# Uso (uma vez por máquina):  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-cert.ps1
#
# O que isso dá: os arquivos ficam assinados e identificados como "Lazuli" (e qualquer alteração depois de assinar é detectada).
# O que NÃO dá: o Windows só confia em certificados de autoridades reconhecidas (pagos), então SmartScreen e o Controle Inteligente
# de Aplicativos continuam tratando o arquivo como de editor desconhecido. Este script NÃO instala o certificado como confiável
# no Windows (isso seria mexer na confiança do sistema).
$ErrorActionPreference = 'Stop'
$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq 'CN=Lazuli' -and $_.NotAfter -gt (Get-Date) }
if ($existing) { Write-Output "Já existe: $($existing[0].Thumbprint) (vale até $($existing[0].NotAfter.ToString('yyyy-MM-dd')))"; return }
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Lazuli' -FriendlyName 'Lazuli code signing (self-signed)' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
$dir = Join-Path $PSScriptRoot 'keys'
New-Item $dir -ItemType Directory -Force | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $dir 'lazuli-codesign-public.cer') | Out-Null   # só a parte pública
Write-Output "Criado: $($cert.Thumbprint) (vale até $($cert.NotAfter.ToString('yyyy-MM-dd')))"
Write-Output "Parte pública em scripts\keys\lazuli-codesign-public.cer (essa pasta não vai para o git)."
