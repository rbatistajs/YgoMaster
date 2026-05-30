# Roguelike Mode — Design

Data: 2026-05-20

Um modo roguelike de Yu-Gi-Oh dentro do YgoMaster (fork Goat). Lógica isolada em
módulos `Roguelike/` próprios (server e client), separada do `Goat/`.

---

## Visão

Botão "Roguelike" na Home → tela do modo → continuar a run salva ou começar nova.
Numa run:
- Escolher 1 de 3 decks (de uma pool nossa).
- Navegar um **mapa estilo dungeon** (grid próprio, sem a limitação do solo de "1 pai
  por node").
- Nodes de tipos variados (duelo / elite / boss / evento / loja / reward).
- Ganhar duelo dá recompensa: **booster** (escolher até 3 cartas pra pool da run) +
  **moeda da run** (substitui gemas; usada em loja/eventos).
- **Editor de deck** filtrado pela pool de cartas da run.
- **Boss final** destranca após vencer **2 de 3 elites**. Vence a run ao derrotá-lo.
- Persistência por player em `roguelike.json`.
- Haverá 2 tipos de jogo; começamos pelo **"deck base"** (escolhe 1 de 3 decks). O 2º
  tipo pluga no mesmo modelo depois.

## Decisões de arquitetura

- **Server dono do estado e da lógica.** A run (persistência, geração de mapa,
  rewards, progressão) vive no server, como o resto dos dados do jogador. Client = só
  UI, conversa via **Acts custom** (`Roguelike.*`).
- **Persistência em arquivo separado** `Players/<id>/roguelike.json` (não dentro do
  `Player.json`), auto-contido — não mexe no `Player.cs`/`GameServer.State.cs`.
- **UI custom via overlay nosso** (GameObject root parenteado no canvas), clonando
  prefabs/componentes do jogo. Telas 100% novas não têm prefab pro
  `PushChildViewController`, então o padrão do modo é overlay reutilizável.
- **Módulos isolados:** `YgoMasterServer/Roguelike/` e `YgoMasterClient/Roguelike/`.
  Únicos toques no core: rotear `Roguelike.*` no dispatch do `GameServer.cs`, e
  registrar as classes do client em `Program.cs` + `.csproj`.

## Roadmap (milestones)

| # | Subsistema | Depende de |
|---|---|---|
| **M1** ✅ | Fundação: modelo+persistência da run, Acts, botão Home, overlay de entrada (Nova/Continuar/Abandonar) | — |
| **M2** ✅ | Início de run: pool de decks iniciais + escolher 1 de 3 | M1 |
| M3 | Mapa: grid próprio + UI do mapa + tipos de node + navegação | M1, M2 |
| M4 | Duelo + reward básico: iniciar duelo do node, vitória/derrota, moeda, avançar | M3 |
| M5 | Booster + pool de cartas: abrir pacote, escolher até 3 cartas, salvar na run | M4 |
| M6 | Editor de deck da run: estender o editor pra mostrar só a pool da run | M5 |
| M7 | Progressão: gating elite/boss (2/3 elites → boss), vitória/derrota da run | M3, M4 |
| M8 | Loja & eventos: nodes que gastam a moeda | M4 |

Cada milestone vira seu próprio ciclo spec → plano → implementação.

---

## M1 — Fundação (spec)

Objetivo: provar o **encanamento ponta-a-ponta** (Home → overlay → Act → persiste) e
estabelecer a base de UI/dados reutilizável. Nova/Continuar levam a placeholders.

### Fluxo
```
Home (botão "Roguelike")
   └─ clica → overlay "Roguelike" (GameObject nosso)
        ├─ ao abrir → Act "Roguelike.get_state"  → { active, gameType, ... }
        ├─ "Nova Run"     → (se ativa: confirma) → Act "Roguelike.start_run"  → toast "Run criada"
        ├─ "Continuar"    → (só se ativa)        → toast "abrindo run…"  (M3 abre o mapa)
        ├─ "Abandonar Run"→ (só se ativa) confirma → Act "Roguelike.abandon_run"
        └─ "Fechar"
```

### Modelo de dados (`roguelike.json`, M1 mínimo e extensível)
```json
{ "version": 1, "active": true, "gameType": "base_deck", "seed": 123456, "createdAt": "ISO-8601" }
```
Campos futuros (adicionados nos milestones): `deck`, `cardPool`, `coins`, `map`,
`position`, `progress`.

### Acts (server, `Roguelike.*`)
- `Roguelike.get_state` → lê o arquivo; responde `{ active, gameType, ... }` (ou
  `active:false` se não existe).
- `Roguelike.start_run` → cria/sobrescreve a run (sorteia `seed`, grava), responde o
  estado novo.
- `Roguelike.abandon_run` → desativa/apaga a run, responde `active:false`.

### Botão na Home (client)
- Hook em `HomeViewController.UpdateHome` → injeta botão "Roguelike" no `RootMenu`
  (clona botão existente, troca label TMP, liga `onClick` via
  `_UnityAction.CreateUnityAction`). Idempotente.

### Overlay de entrada (client) — base reutilizável das telas do modo
- GameObject root parenteado no canvas da Home: título + Nova Run + Continuar
  (habilitado só se `active`) + Abandonar Run (só se `active`) + Fechar.
- Botões clonados da UI do jogo, posicionados via `RectTransform`. Confirmações via
  `CommonDialog`.

### Organização do código
- **Server `YgoMasterServer/Roguelike/`:** `RoguelikeRun.cs` (modelo + load/save), 
  `RoguelikeActs.cs` (`Handle(request)`).
- **Client `YgoMasterClient/Roguelike/`:** `RoguelikeApi.cs` (chama os Acts),
  `RoguelikeHomeButton.cs`, `RoguelikeEntryOverlay.cs`.
- **Core (mínimo):** `GameServer.cs` roteia `Roguelike.*` → `RoguelikeActs.Handle`;
  `Program.cs` + `.csproj` registram as classes do client.

### Fora do M1 (stubs)
Deck-select (M2), mapa (M3), duelo+rewards (M4/M5), editor (M6), progressão (M7),
loja/eventos (M8). Nova/Continuar levam a placeholders no M1.

### Riscos / spikes
1. **Client → server (Act custom):** confirmar como o client dispara um Act e lê a
   resposta (o jogo já faz HTTP local; precisamos do helper). **Primeiro spike.**
2. Pegar o canvas certo pro overlay + limpeza ao trocar de cena.
3. Setar texto TMP nos botões clonados.

### Pronto quando (M1)
Da Home: clicar Roguelike abre o overlay; "Nova Run" cria e persiste `roguelike.json`;
reabrir mostra "Continuar"/"Abandonar" habilitados; "Abandonar" limpa o estado.

> **M1 concluído (2026-05-21).** Implementado com ActionSheet/CommonDialog (em vez do
> overlay custom) — pragmático e reaproveitável. O modo segue com ActionSheet até a UI
> do mapa (M3), quando construiremos telas mais ricas.

---

## M2 — Início de run: escolher 1 de 3 decks (spec)

Objetivo: ao iniciar uma run `base_deck`, o **server sorteia 3 decks** de um pool e o
jogador **escolhe 1**, que vira o deck da run (persistido). Reaproveita
ActionSheet/CommonDialog do M1. Sem arte de carta ainda (vem com a UI do mapa).

### Fluxo
```
Home → botão "ROGUELIKE" → menu:
  - Sem run:                 "Nova Run" → start_run (server sorteia 3)
                               → ActionSheet "Escolha seu deck" [Deck A / B / C]
                               → tap → choose_deck(i) → toast "Deck: X"
  - Run sem deck (deckChosen=false): "Continuar Run" reabre a seleção (resumível)
  - Run com deck:            "Continuar Run" → toast "Run em andamento (mapa: M3)"
                             "Abandonar Run" → confirma → abandon_run
```

### Pool de decks (nosso, fora do Goat)
- Fonte: **`DataLE/Roguelike/StartingDecks/*.json`** — decks iniciais nossos (formato
  player), curados/editáveis. **Não** reaproveitamos `DataLE/decks/` nem o
  `DeckPoolLoader` da pasta `Goat` (regra: nada de código do Goat). Em vez disso, um
  loader próprio `RoguelikeDeckPool` (em `YgoMasterServer/Roguelike/`, inspirado no
  DeckPoolLoader) lista e carrega os JSONs.
- Nome de exibição = nome do arquivo (sem extensão), pra curadoria simples. `bossCard` =
  campo `boss_card` do JSON, ou o 1º id do main como fallback.
- Seleção: RNG semeado pelo `seed` da run → 3 arquivos distintos; `file` salvo como
  caminho relativo ao `dataDirectory` (ex.: `Roguelike/StartingDecks/Chaos Turbo.json`).

### Modelo (`roguelike.json`, adições ao M1)
```json
{
  "version": 1, "active": true, "gameType": "base_deck", "seed": 123, "createdAt": "...",
  "deckChosen": false,
  "deckOffers": [
    {"name":"Chaos Turbo","bossCard":5835,"file":"Roguelike/StartingDecks/Chaos Turbo.json"},
    {"name":"Cat Control","bossCard":4456,"file":"Roguelike/StartingDecks/Cat Control.json"},
    {"name":"Final Countdown","bossCard":4321,"file":"Roguelike/StartingDecks/Final Countdown.json"}
  ],
  "deck": null
}
```
- `deckChosen=false`: `deckOffers` tem 3, `deck=null`.
- Após `choose_deck`: `deck` = `{name, bossCard, deck:<deck formato-player m/e/s>}`.
  Guardamos no formato player (não convertemos pro shape SoloDuel agora — isso é trabalho
  do M4, com conversor nosso/`DeckInfo` original); `deckOffers` esvaziado.

### Acts (server)
- `start_run` (evolui): cria run `active`, `deckChosen=false`, sorteia `seed` + 3 offers,
  persiste, responde o estado (com offers).
- `choose_deck { index }` (novo): valida `0..2` e run pendente;
  `RoguelikeDeckPool.LoadOne(offers[index].file)` → grava `deck`, `deckChosen=true`, limpa
  offers; responde estado. Idempotente (se já escolhido, no-op).
- `abandon_run`: inalterado.
- get_state (home piggyback): inclui `deckChosen`, `deck` (name/bossCard), `deckOffers`.

### Client
- `RoguelikeApi`: + `IsDeckChosen()`, `GetDeckOfferNames()` (lê `$.Roguelike.deckOffers`
  via `ClientWork.SerializePath`), `ChooseDeck(int index)` (act `choose_deck`).
- Reação assíncrona: `RoguelikeFlow.OnNetworkComplete(cmd)` chamado do hook
  `RequestStructure.Complete` (`DuelStarter.cs`, junto do `ProfileReplayViewController
  .OnNetworkComplete`). `start_run` completo → abre ActionSheet de seleção a partir do
  `$.Roguelike` já atualizado; `choose_deck` completo → toast confirmando.
- `RoguelikeHomeButton.OnMenuSelect`: "Nova Run" agora só dispara `start_run` (a seleção
  vem pela completion). "Continuar": se `active && !deckChosen` reabre a seleção; se deck
  escolhido, toast "mapa: M3".

### Fora do M2 (stubs)
Mapa (M3), uso do `deck` em duelo (M4). O `deck` salvo só é consumido no M4. UI rica com
arte de carta fica pra UI do mapa (M3).

### Pronto quando (M2)
"Nova Run" mostra 3 decks; escolher 1 grava `deck` + `deckChosen=true` no `roguelike.json`;
reabrir antes de escolher reabre a seleção; após escolher, "Continuar" indica run em
andamento; "Abandonar" limpa tudo.

> **M2 concluído (2026-05-21).** Pool próprio em `DataLE/Roguelike/StartingDecks/` (sem
> reuso de código do `Goat`); loader `RoguelikeDeckPool`. Verificado in-game: Nova Run →
> escolher 1 de 3 → persiste o deck.

---

## M2.5 — Tela de seleção de deck (UI rica) (spec)

Objetivo: trocar o ActionSheet do M2 por uma **tela própria** mostrando os 3 decks como
cards com a **carta principal em destaque**; clicar num card abre um **drawer** pra
**visualizar o deck** (reusando o viewer nativo) e **selecionar**, com **descrição**.

### Spike (concluído 2026-05-21)
Provado in-game que dá pra empurrar uma tela do jogo standalone e renderizar nosso
conteúdo nela (fundo + arte de carta). Ferramentas de dev criadas (mantidas):
`vcdump [depth]` (despeja hierarquia da top VC em `_tmp/vc_<classe>.txt`), `vclog`
(loga prefab-paths), `rgpush <path>` (empurra tela). Spike `rgproof` é descartável
(removido quando o M2.5 entrar).

### Base: `Solo/SoloMode`
Abre com **fundo animado** + header/back nativos e hospeda a `SoloPortalViewController`,
cujo conteúdo já tem **tiles de card prontas** (estrutura confirmada via `vcdump 12`):
```
SoloPortalUI(Clone) → Root
  TitleSafeArea/TitleGroup/NameText                 ← título  → "Escolha seu Deck"
  ButtonArea/MainGroup/RecommendGroup [HLayout]     ← seção dos cards
    RecommendText                                   ← label  → "Decks"
    RecommendButton1 / RecommendButton2             ← TILES (clonar p/ ter 3)
      Button [SelectionButton]                      ← clicável (onClick)
        …/ImageGate/SoloCardThumbMask/SoloCardThumbImage [RawImage, AutoReleaseCardIllust]  ← arte
        …/NameArea/…/TextGateName [ExtendedTextMeshProUGUI]                                  ← nome
  ButtonArea/MainGroup/LastPlayGroup                ← esconder
  ButtonArea/GateListGroup                          ← esconder (Histórias/Treinamento)
```

### Fluxo
```
Nova Run → start_run → push Solo/SoloMode → customiza SoloPortal:
  - título "Escolha seu Deck"; esconde LastPlayGroup + GateListGroup
  - 3 tiles (RecommendButton clonadas): arte da carta principal + nome do deck
  - tap numa tile → drawer:  [Ver deck]  [Selecionar]  + descrição
       Ver deck   → viewer nativo de deck via DeckView.SetCards (cartas + descrição)
       Selecionar → Roguelike.choose_deck(index) → fecha a tela
Continuar (deck não escolhido) → reabre essa tela.
```

### Mecanismo (tudo confirmado no spike)
- Empurrar/customizar: `ViewControllerManager.PushChildViewController("Solo/SoloMode")` →
  `GetViewController(manager, SoloPortalViewController)` + hook `OnCreatedView` → manipular
  GameObjects (clonar tiles, set texto, esconder grupos, wire onClick).
- Arte de carta: `AssetHelper.LoadImmediateAsset("Card/Images/Illust/tcg/<cid>")` +
  `AssetHelper.SpriteFromTexture` → `Image.sprite` (core, não-Goat). Alternativa: o binding
  nativo `BindingSoloCardThumb` da própria tile.
- Grid "ver deck": `YgomGame.Deck.DeckView.SetCards(ptr, mainColl, extraColl)` (DeckEditorUtils, core).

### Server / dados (adições ao M2)
- Enriquecer cada item de `deckOffers` com `description` + listas `main/extra/side` (ids),
  pra alimentar o viewer sem round-trip. Após `choose_deck`, `deck` também leva `description`.
- Novo campo `description` (e opcional `archetype`) nos JSONs de `Roguelike/StartingDecks`;
  `RoguelikeDeckPool.LoadOne` lê.

### Organização (código novo, isolado em `Roguelike/`)
- `RoguelikeCardImage` (client) — carrega/cacheia sprite de carta por cid (reusa AssetHelper).
- `RoguelikeDeckSelectScreen` (client) — push SoloMode + customiza SoloPortal + 3 tiles + drawer.
- `RoguelikeFlow` — passa a abrir essa tela (em vez do ActionSheet) na conclusão do `start_run`.
- Server: `RoguelikeDeckPool` + offers ganham `description` + card ids.

### Fora do M2.5
Uso do deck em duelo real (M4), mapa (M3). Tile "Ver deck" só visualiza.

### Pronto quando (M2.5)
Nova Run abre a tela SoloMode com 3 cards (arte + nome); tap abre drawer; "Ver deck" mostra
o deck no viewer nativo (com descrição); "Selecionar" grava o deck e fecha; "Continuar" sem
deck reabre a tela.

> **M2.5 concluído (2026-05-21).** Verificado in-game: Home → Nova Run abre a tela
> (`Solo/SoloMode` reaproveitado; hook em `SoloPortalViewController.OnCreatedView` gated por
> flag — Solo normal intacto); 3 tiles clonadas de `RecommendButton` com arte da carta
> principal (overlay `RoguelikeCardImage`: sprite próprio gchandle'd + DontDestroyOnLoad +
> AspectRatioFitter EnvelopeParent — preenche e sobrevive ao viewer); drawer (ActionSheet)
> com "Ver deck" (abre o `DeckBrowser` nativo via args IL2CPP **tipados**, evitando o cast
> Int64→Int32) e "Selecionar" (`choose_deck` + fecha). Dev tools mantidas:
> `vcdump`/`vclog`/`rgpush`/`rgdeck`.
>
> Pendências (não bloqueiam): `boss_card` por deck nas StartingDecks (Buster Blader/Cat
> Control caem no mesmo cid 5655 = 1º id do main); painel lateral de descrição do viewer
> (`shortcutSettings`); fundo `Base` do RecommendGroup escondido (revisitar o painel).

---

## M3 — Mapa (spec)

Objetivo: **gerar** (server, seedado) + **renderizar** (tela custom) + **navegar** um mapa
estilo **Slay-the-Spire**. Layout configurável via `DataLE/Roguelike/Settings.json`, com
classe abstrata extensível pra outros formatos depois (multi-parent, sem o limite de 1 pai
do Goat).

### Config: `DataLE/Roguelike/Settings.json` (data-driven, editável sem recompilar)
```json
{
  "layout": "slay_the_spire",
  "floors": 8,
  "width": 4,
  "typeWeights": { "duel": 0.6, "elite": 0.12, "event": 0.12, "shop": 0.08, "reward": 0.08 }
}
```
- `layout` escolhe a impl (factory). `floors`/`width`/`typeWeights` são params do layout.
  `duel` é forçado na fileira 0; `boss` é fixo no topo. Defaults no código se o arquivo
  faltar.

### Modelo (`roguelike.json`, adições)
```json
"map": { "nodes": [ {"id":0,"type":"duel","row":0,"col":1,"next":[3,4]}, ... ], "rows": 8 },
"position": null,     // id do nó atual (null = na entrada, antes da fileira 0)
"visited": []         // ids já percorridos
```
Multi-parent é natural: vários nós podem listar o mesmo id em `next`.

### Geração (server, `Roguelike/`)
- `RoguelikeSettings` — lê/cacheia `DataLE/Roguelike/Settings.json` (defaults se ausente).
- `RoguelikeMapLayout` (abstrata): `Build(seed, settings) → RoguelikeMap`; factory
  `Create(settings.layout)`.
- `SlayTheSpireLayout : RoguelikeMapLayout` (1ª impl): N andares × até W nós, arestas
  andar→andar (1-2 por nó, conectividade garantida, sem cruzamento de linhas), boss no
  topo, fileira 0 = entrada, tipos por `typeWeights`.
- Gerado no `choose_deck` (usa o `seed` da run → determinístico).

### Tipos de nó (M3 = só visual; ações no M4+)
`duel, elite, event, shop, reward, boss` — ícone/cor por tipo. Clicar num nó alcançável só
**move** (marca visitado, revela vizinhos).

### Acts (server)
- `Roguelike.move { nodeId }` — valida (conectado ao atual + alcançável) → atualiza
  `position`/`visited` → responde estado.

### Client — tela custom (`RoguelikeMapScreen`)
- Empurra uma base + constrói os **nós** (posicionados por row/col, de baixo pra cima) +
  **linhas** entre conectados (Image fino rotacionado) + destaca o atual + clicáveis = os
  próximos alcançáveis. Click → `move` → re-renderiza.
- Nós = ícone/cor por tipo (v1 simples). Reaproveita os padrões do M2.5 (push base + clonar
  GameObjects + onClick via `_UnityAction`).

### Navegação
- Início: `position=null` → toda a fileira 0 alcançável. De um nó: alcançáveis = seus
  `next`. Sem voltar. Chegar no boss (topo) = fim do mapa (vitória da run vem no M7).

### Fora do M3
Ações dos nós (M4: duelo/reward), gating do boss por 2/3 elites (M7), loja/eventos (M8).

### Pronto quando (M3)
Escolher o deck gera o mapa; "Continuar" abre a tela do mapa (StS) reaproveitando a base;
dá pra navegar de baixo até o boss com `position`/`visited` persistidos; trocar o
`Settings.json` (ex.: `floors`/`width`/`typeWeights`) muda o layout sem recompilar.

> **Revisão de UI (2026-05-21):** base única `DeckEdit/DeckSelect` (`DeckSelectViewController2`)
> em vez de `SoloMode`. UMA tela (`RoguelikeRunScreen`) que mostra **escolha de deck OU
> mapa** conforme o estado, trocando o conteúdo **in-place** (sem flicker). Reaproveita o
> tile `DeckGroup/.../Template(Clone)[DeckBox]` (caixa de deck com cartas + nome +
> `Body[SelectionButton]`): nas escolhas mostra os 3 decks; no mapa vira nó (menor, ícone de
> tipo) com **GridLayoutGroup desligado + posição manual** + linhas. Botão **ROGUELIKE** abre
> direto (nova/continua conforme estado, sem o menu). Botão do topo (`HeaderButtonGroup`) →
> editor do deck da run; footer → Abandonar. As telas em SoloMode (deck-select/map) são
> substituídas.

---

## M4 — Duelo a partir do nó + reward básico (spec)

Clicar num nó de **combate** (`duel`/`elite`/`boss`) inicia um **duelo de verdade** com o
deck da run contra um oponente. Vitória dá recompensa e avança; **derrota encerra a run**.
Nós não-combate (`reward`/`shop`/`event`) ainda só movem (ações deles = M5/M8).

### Decisões (2026-05-22)
- **Oponentes**: pasta própria `DataLE/Roguelike/Opponents/*.json` (decks de inimigo,
  formato deck do projeto). Sorteio por seed da run. (boss/elite podem ganhar sub-pools
  depois — M7.)
- **Derrota encerra a run** (`active=false`), antecipando parte do M7.

### Fluxo
- Mapa → clicar nó de combate alcançável → em vez de só `move`, abre o **duelo**.
- Reusa o fluxo Solo do jogo: `Solo/SoloStartProduction` + injeção de `duelStarterData`
  no hook `Duel_begin` (mesmo mecanismo que o projeto já usa pra duelos custom). Player =
  deck da run; oponente = deck sorteado de `Opponents`.
- Fim do duelo (`Duel.end`): client avisa o server do resultado.
  - **Vitória** → marca nó visitado, `position = nó`, credita moeda (reward por tipo),
    volta ao mapa.
  - **Derrota** → `active=false` (run encerrada), volta à Home.

### Spike (validar primeiro)
Iniciar um duelo a partir da tela do mapa com **player + opponent decks arbitrários** via
o fluxo Solo, e **detectar o resultado** (win/lose) roteando de volta ao mapa roguelike.
O Goat fez algo assim (CpuContest/solo gates) mas não está neste clone; a base Solo
(`Act_Solo`, `SoloStartProductionViewController`, injeção em `Duel_begin`) está.

### Acts (server)
- `Roguelike.start_duel { nodeId }` — valida nó de combate alcançável → escolhe oponente →
  monta os dados do duelo (player=run deck, opp=opponent) → responde o que o client precisa
  pra abrir o duelo.
- `Roguelike.duel_result { nodeId, win }` — vitória: visited+position+moeda; derrota: encerra.

### Modelo (`roguelike.json`, adições)
- `currency` (int) — moeda da run, creditada por vitória.
- (opcional) `pendingDuelNode` — nó em duelo, pra validar o `duel_result`.

### Client
- `RoguelikeMapScreen`: clique em nó de combate → `start_duel` em vez de `move`.
- Reusar/estender a ponte de início de duelo do `DuelStarter` (Solo) com os decks da run.
- No `Duel.end`, mandar `duel_result`; reabrir o mapa (ou Home se derrota).

### Fora do M4
Booster/escolha de cartas (M5), editor do deck da run (M6), LP/gating boss e vitória da run
(M7), loja/eventos (M8). Reward = só moeda por enquanto.

### Pronto quando (M4)
Clicar num nó de duelo abre um duelo real (run deck vs oponente); vencer credita moeda,
marca o nó e volta ao mapa avançado; perder encerra a run. Trocar decks em
`DataLE/Roguelike/Opponents` muda os inimigos sem recompilar.
