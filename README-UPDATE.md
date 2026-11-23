# Atualização automática do Lazuli

Na abertura (durante a splash) o app consulta `<URL>/main/latest.json`. Se houver versão maior, abre a janela
**Update** (novidades, tamanho, "Update now" / "Later"). Ao aceitar: baixa com progresso, confere, troca os
arquivos e reabre sozinho já na versão nova. Cobre o app todo: UI e plugin do backend (`Lazuli.exe` + `backends\solara\`).

**Canal único: `main`.** Todo build recebe as atualizações por ele, qualquer que seja o backend.
(Os canais antigos `lite` e `xeno` ficam no servidor só para quem ainda usa o `Executor.exe` antigo; não são mais gerados.)

**Servidor atual:** `https://executor-updates.lpn688c.workers.dev` (Cloudflare Workers com arquivos estáticos; a URL
está em `src/Executor.UI/Updates/UpdateConfig.cs`, `DefaultBaseUrl`).

## O que vai (e o que não vai) no pacote
O zip leva o `Lazuli.exe`, o `LEIA-ME.txt` e o plugin `backends\solara\` (só código nosso). **Não leva a `SolaraV3.dll`
nem outros arquivos do Solara**: o app baixa a DLL do site oficial na primeira vez que o usuário clica em Attach
(confere tamanho e SHA-256 do manifesto oficial) e a mantém atualizada. O script de release falha se algum arquivo do
Solara entrar no pacote por engano.

## Segurança (o que garante que ninguém troca o seu app)
- O manifesto é **assinado** (ECDSA P-256). A chave pública está no app (`Updates/UpdateConfig.cs`); a privada fica só em
  `scripts\keys\update-private.pem` (fora do git). Sem a assinatura correta, nada é baixado nem instalado.
- O manifesto traz o **SHA-256** do zip; o app confere durante o download. Zip adulterado ou corrompido é descartado.
- Só HTTPS (HTTP apenas em `localhost`, para teste). Caminhos do zip são validados (sem sair da pasta do app).
- A troca de arquivos é **desfeita por completo** se qualquer arquivo falhar (ex.: preso por outro programa).
- Se o servidor estiver fora do ar ou o PC offline, o app abre normal (a verificação nunca trava a abertura: até ~2,5 s
  na splash; se demorar mais, o app abre e a oferta aparece depois).

> **Faça backup de `scripts\keys\update-private.pem`.** Quem tem esse arquivo publica atualizações no seu nome; se perder,
> os exes já enviados não aceitam mais atualizações e você precisa mandar um exe novo (nova chave).

## Publicar uma atualização (2 comandos)
1. Gerar o pacote (compila, empacota e assina; o app precisa estar fechado):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-update.ps1 -Version 1.0.1 -Notes "Novidade A;Correção B"
```
   Saída: `publish-main\` (o app dessa versão) e **`dist\main\`** (zip + `latest.json` assinados). Escreva as notas em inglês:
   elas aparecem na janela de atualização.
2. Subir `dist\` para o Cloudflare (o Wrangler está instalado só neste projeto, em `tools\cloudflare`):
```powershell
cd tools\cloudflare; npx --no-install wrangler deploy --assets ..\..\dist --name executor-updates --compatibility-date 2026-11-01
```
   O comando mostra a URL no final. Quem abrir o app numa versão menor recebe a oferta na próxima abertura.
   **Não apague `dist\lite` e `dist\xeno`**: o deploy publica a pasta inteira, e apagá-las tira os canais antigos do ar.

Opções do `make-update.ps1`: `-Mandatory` (sem botão "Later"), `-MinVersion 1.0.0` (quem está abaixo é forçado a
atualizar), `-PackageBase https://...` (zip em outro HTTPS).

- A versão vem do `-Version` (vira o `<Version>` do exe). **Sempre suba o número**; versão igual ou menor é ignorada.
- Limite de **25 MiB por arquivo** nos arquivos estáticos do Cloudflare. O zip está em ~21 MiB. Se passar,
  hospede o zip em outro HTTPS (ex.: R2 público) e use `-PackageBase`; o manifesto continua no Workers.

## Primeira vez / outra máquina (login no Cloudflare)
```powershell
cd tools\cloudflare; npx --no-install wrangler login      # abre o navegador; você autoriza (a senha não passa pelo Claude)
npx --no-install wrangler whoami                           # confere a conta
```
Se `tools\cloudflare\node_modules` não existir: `npm install` nessa pasta. Para trocar de servidor, mude o `--name` do
deploy e o `DefaultBaseUrl`, e gere o exe de novo.
A autorização do Wrangler pode ser revogada em *Cloudflare → My Profile → API Tokens*.

## Testar sem internet
```powershell
$env:EXECUTOR_UPDATE_URL = 'http://localhost:8099'   # serve a pasta dist\ nessa porta; a assinatura continua exigida
```
`EXECUTOR_NOUPDATE=1` desliga a verificação. Em build Debug, `EXECUTOR_UPDATE_AUTO=1` aceita a atualização sozinho.

## Observações
- O app precisa poder gravar na própria pasta (por isso, não instale em `Program Files`; uma pasta sua funciona).
- Arquivos antigos viram `*.old` durante a troca e são apagados na abertura seguinte.
- Atualização não apaga arquivos que a versão nova deixou de ter (só troca/adiciona).
- Quem usa o `Executor.exe` antigo (canais `lite`/`xeno`) **não** migra sozinho para o Lazuli: o exe antigo só enxerga o
  próprio canal. Esses usuários baixam o zip do Lazuli uma vez; dali em diante atualizam pelo canal `main`.
- A pasta de dados continua `%APPDATA%\Executor` (abas e configurações são mantidas).
