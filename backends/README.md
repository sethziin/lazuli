# Backends (plugins) do Executor

O app não conhece nenhum backend em tempo de compilação. Ele procura pastas em `backends\<id>\` ao lado do `Executor.exe`,
escolhe uma **uma vez, ao abrir**, e fala com ela só pela interface `IRunSession` (`src\Executor.Contracts`).
Não existe troca com o app aberto: mudou o backend, reabra o app.

## Como o app escolhe
1. `Configurações → Sobre → Backend (próxima abertura)` grava `BackendId` em `%APPDATA%\Executor\settings.json`.
2. Sem `BackendId` (Automático): usa o **único** backend instalado; com zero ou vários, usa a demonstração.
3. `demo` = a demonstração embutida (`MockRunSession`). Ela também é o plano B: se o backend escolhido não existe, está
   corrompido ou não carrega, o app abre na demonstração e mostra o motivo no Output.
4. Se o backend exige administrador (`requiresAdmin`) e o app não está elevado, o app se relança uma vez pedindo o UAC.
   Recusar o UAC não impede de abrir; o backend avisa que falta permissão. (O build do canal `xeno` já pede administrador no manifesto.)

## Layout de uma pasta de backend
```
backends\xeno\
  backend.json                  manifesto, lido ANTES de carregar qualquer código
  Executor.Backend.Xeno.dll     o plugin (a DLL do "assembly" do manifesto)
  Executor.Backend.Xeno.deps.json
  Xeno.dll, libcurl.dll, ...    nativos do backend, na mesma pasta
```
Os arquivos de um backend ficam **todos** na pasta dele; o backend recebe o caminho em `BackendContext.BackendDirectory`
(nunca use `AppContext.BaseDirectory`). O `Executor.Contracts.dll` **não** vai na pasta: vem do app.

`backend.json` (gerado no build por `backends\Backend.targets`):
```json
{ "id": "xeno", "displayName": "Xeno", "assembly": "Executor.Backend.Xeno.dll",
  "factory": "Executor.XenoBackend.XenoFactory", "contractVersion": 1, "requiresAdmin": true,
  "files": { "Executor.Backend.Xeno.dll": "<sha256>", "Xeno.dll": "<sha256>" } }
```
`factory` é um tipo público, com construtor sem parâmetros, que implementa `IBackendFactory`. Quem manda é o código dele:
`Info.Id` e `Info.RequiresAdmin` têm que bater com o manifesto, senão o backend é recusado.

## Criar um backend novo
1. Projeto `net10.0` em `backends\Executor.Backend.<Nome>\`, com `ProjectReference` ao `Executor.Contracts` e `Private="false"`.
2. Defina no csproj `BackendId`, `BackendDisplayName`, `BackendFactory`, `BackendRequiresAdmin` e termine com
   `<Import Project="..\Backend.targets" />` (veja `Executor.Backend.Xeno.csproj`).
3. Implemente `IBackendFactory` e `IRunSession`. `Create` não deve carregar DLL nativa nem tocar no alvo: isso fica para o primeiro Attach.
4. Declare só as `BackendCapabilities` que o backend realmente tem; a UI esconde o resto (ex.: sem `KillTarget` some o "Encerrar Roblox").
5. Para entrar no app: `dotnet build src\Executor.UI -p:Backend=xeno` copia o plugin Xeno; para outro backend, copie a pasta de saída
   do projeto para `backends\<id>\` ao lado do exe. (Hoje só o Xeno tem esse passo no build da UI; o canal `xeno` é o único que leva plugin.)

## Backend Solara (`Executor.Backend.Solara`)
Faz o papel do `SolaraV3.exe` oficial com a `SolaraV3.dll` original: `SetEventSink`, `Start(guid)` e a ponte HTTP local
(`scan_clients`, `Attach`, `execute_script`).
- Build: `dotnet build src\Executor.UI -c Debug -p:Backend=solara` (ou `publish -c Release`). A `SolaraV3.dll` é de terceiros e não está neste repositório: o app a baixa do site oficial na primeira abertura\n  (ou você pode empacotar uma cópia local com `-p:SolaraDir=<pasta>`).
- **CET desligado (`CetCompat=false`) só neste canal:** o código protegido da DLL é incompatível com o shadow stack que o apphost
  do .NET liga por padrão; com ele o processo morre com `0xC0000409` logo depois do Attach. O `SolaraV3.exe` oficial também não usa CET.
- **Arquivos da DLL:** ela grava `CHRS`, `PLTBL`, `DLCTBL_FRTS` e `DEBUG.txt` em `bin\` ao lado do `Executor.exe` (como o oficial).
- **Atualização da DLL:** antes do `Start`, lê `https://getsolara.dev/download/v3/manifest.json` (espelhos `.gg` e `solara.gg`), o mesmo
  manifesto do atualizador oficial. Se a `SolaraV3.dll` dele for diferente, baixa de `/download/v3/SolaraV3.dll`, confere tamanho e
  SHA-256 contra o manifesto e guarda em `%APPDATA%\Executor\backends\solara\dll\`. Sem rede, usa a última DLL conferida ou a empacotada.
  Como a DLL só aceita um `Start` por processo, uma versão lançada com o app aberto é avisada no Output (checagem a cada 10 min) e entra ao reabrir.
  O Attach também avisa quando a pasta de versão do Roblox aberto difere do `supportedClient` do manifesto.
- Diagnóstico: `EXECUTOR_SOLARA_TRACE=1` grava `bin\host-trace.txt` (requisições à ponte e eventos da DLL).
- Não precisa de administrador. A DLL continua mandando telemetria e consultando as listas de banimento do Solara, como no app oficial.

## Segurança: o que a conferência de SHA-256 faz e o que NÃO faz
- **Faz (integridade):** antes de carregar, o app recalcula o SHA-256 de cada arquivo do manifesto. Pega arquivo corrompido,
  incompleto ou trocado por engano. Também recusa qualquer `.dll/.exe/.sys/.winmd` na pasta que não conste no manifesto
  (uma DLL nativa solta seria carregada sem conferência) e links simbólicos.
- **Não faz (autenticação):** o manifesto e os hashes ficam **na mesma pasta** que os arquivos. Quem consegue gravar ali consegue
  gerar um manifesto novo com hashes novos, e também trocar o `Executor.exe`. O hash **não prova quem criou** o backend nem que ele é seguro.
- Um backend carregado roda **dentro do processo do app**, com as permissões dele (administrador, se o backend exigir),
  e uma DLL nativa dele nunca é descarregada. Instale só backends de origem confiável.
- Há uma janela entre conferir e carregar (o arquivo poderia ser trocado nesse intervalo por quem já tem acesso à pasta).
- Autenticar de verdade exigiria assinar o manifesto (como o atualizador já faz com ECDSA) e embutir a chave pública no app. Não está feito.

## Testes
`EXECUTOR_SELFTEST=<pasta temporária>` com o build Debug roda o autoteste inteiro, inclusive `src\Executor.UI\BackendTests.cs`,
que carrega de verdade o backend de teste `Executor.Backend.TestFixture` (copiado para `test-fixtures\fixture\` só no Debug)
e, se existir `backends\xeno\` ao lado do exe, também o plugin Xeno real (sem carregar o `Xeno.dll`).
