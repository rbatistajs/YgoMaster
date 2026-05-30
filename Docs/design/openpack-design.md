# Roguelike — Open Pack action (design)

## 1. Objetivo

Adicionar uma ação `openpack` ao motor de ações do roguelike (M5): durante a
run, uma action node abre 1+ pacotes de cartas (com sorteio ponderado, pity, e
modos *keep all* ou *pick X de N*) e adiciona o resultado ao pool/deck da run.

Reaproveita a infraestrutura existente:

- `RoguelikeActionEngine` (M5) — `options` / `message` ganham um terceiro tipo.
- `RoguelikeCardPool` (CardPool.json, rarity weights, rate groups, filtros) —
  já calcula pools/pesos; vamos só adicionar um `DrawN` reutilizando o miolo
  do `Resolve` dos modifiers.
- VC nativo `CardPack/CardPackOpen` → `CardPack/CardPackOpenResult` — animação
  e grid de resultado prontos; hooka o Result pra customizar texto/seleção.

## 2. Princípios

- **Server-authoritative** sempre. O cliente nunca decide raridades, cartas,
  nem adiciona pro pool — só renderiza, envia índices de seleção e popa o VC.
- **Pool é fonte da verdade.** Toda carta no `Deck` da run existe no
  `Cards` (pool). Cartas obtidas vão no pool e (em *keep all*) também são
  somadas ao deck.
- **Determinístico no seed da run.** Mesma run + mesma sequência de ações
  = mesmas cartas (replays consistentes).
- **Single source of truth** pra pesos: `CardPool.json` (global → asc →
  action) com merge por chave em `rarityRates`.

## 3. Arquitetura geral

```
Server (RoguelikeActionEngine)            Client (driver no Pump)
┌──────────────────────────────┐         ┌──────────────────────────────┐
│ Enter "openpack" node:       │         │ MapScreen.Update -> Pump:    │
│  - DrawN por pack do batch   │         │  - lê $.Roguelike.pendingPack│
│  - aplica pity por pack      │         │  - dedup por token            │
│  - run.PendingPack = staged  │  --->   │  - push CardPack/CardPackOpen │
│  - $.Gacha.drawInfo = vanilla│  $.     │     (args {ForwardResultArgs} │
│  - $.Roguelike.pendingPack   │ ClientWork│ ─── animação nativa ───      │
│                              │         │  Result VC -> nosso hook:    │
│                              │         │   * relabel título           │
│                              │         │   * OK -> "Confirmar"        │
│                              │         │   * pick mode: SelectionButton│
│                              │         │     gate em |sel|==pick      │
│                              │         │                              │
│ Act_RoguelikePackFinalize   │  <---   │ Confirmar -> Roguelike.pack_  │
│  - valida token + picks      │  Request │   finalize { token, picks } │
│  - run.AddCard + Deck.add    │ .Entry  │  - pop VC                    │
│  - PendingPack = null        │         │                              │
│  - ActionEngine.Advance(next)│         │                              │
└──────────────────────────────┘         └──────────────────────────────┘
```

Lifecycle resiliente: se o player sair da run mid-pack, `PendingPack`
persiste em `roguelike.json`. Ao reentrar, `WriteRun` reprojeta `$.Gacha`
e o pump abre `CardPackOpen` de novo (token dedup evita re-push se o
cliente ainda tiver o VC).

## 4. Schema da action

Terceiro `kind` do motor:

```jsonc
{
  "kind": "openpack",

  // Quantos pacotes abrir em sequência (default 1). Vanilla anima N vezes.
  "packs": 3,

  // 0  -> keep all (pega tudo)
  // >0 -> pick X (seleciona exatamente X do total mostrado)
  "pick": 0,

  // Lista ordenada de pulls dentro de UM pacote. size do pacote = sum(count).
  // (Cada um dos `packs` pacotes roda esses pulls.)
  "pulls": [
    { "count": 6, "pool": { "source": "any", "random": "monster" } },
    { "count": 1, "pool": { "source": "any", "random": "spell"   } },
    { "count": 1, "chance": 0.5,
      "pool": { "source": "any", "random": "monster", "rarity": "UR" } }
  ],

  // Override LOCAL de pity (merge por raridade com global do CardPool.json).
  // "pity": false  -> desliga pity nesse action (não soma bônus, não atualiza contadores).
  "pity": { "UR": { "increment": 20 } },

  // Labels opcionais; {0}=pick, {1}=size_total. Defaults via RoguelikeLabels.
  "title_keep":    "Cartas obtidas",
  "title_pick":    "Selecione {0} de {1} cartas",
  "confirm_label": "Confirmar",

  // Próximo nó da árvore (igual aos children do "options"); null = termina.
  "next": null
}
```

### 4.1 Pool spec (dentro de `pulls[].pool`)

Mesma shape do random spec dos modifiers (sem inventar chaves):

| Chave            | Tipo      | Valores / função                                                                   |
|------------------|-----------|-------------------------------------------------------------------------------------|
| `source`         | string    | `"any"` / `"link"` / `"deck"` (default). Define o universo via `BuildPool`.        |
| `deck_owner`     | string    | `"player"` / `"opponent"` / `"self"`. Só importa em `source` `"deck"`/`"link"`.    |
| `random`         | string    | filtro kind (`monster`, `main_monster`, `extra_monster`, `spell`, `trap`, ...).    |
| `subtype`        | string    | monster: `normal`/`effect`/`ritual`/`fusion`/`synchro`/`xyz`/`link`; spell/trap: `normal`/`counter`/`field`/`equip`/`continuous`/`quickplay`/`ritual`. |
| `minAtk`/`maxAtk`/`minDef`/`maxDef`/`minLevel`/`maxLevel` | int | filtros numéricos (já em `PassesNumeric`). |
| `rarity` / `rarities` | string / [string] | **NOVO** filtro hard (`"UR"` ou `["SR","UR"]`). Compara contra `CardListRarity`. |
| `rarityRates`    | {N\|R\|SR\|UR: weight} | **NOVO** override local dos pesos de raridade (merge por chave; ver §5). |

### 4.2 Pulls — semântica

- `count`: quantas cartas desenhar desse pool dentro de **um** pacote.
- `chance` (opcional, default 1.0): probabilidade da pull INTEIRA acontecer.
  Falha → pull pulada (pacote fica com menos cartas). Rolado por-pacote
  (cada um dos `packs` rola independente).
- `pool`: spec acima.
- O `used` (anti-duplicata) é **local ao pacote**: mesma carta pode aparecer
  em pacotes diferentes do mesmo batch (igual `NoDuplicatesPerPack` vanilla).

### 4.3 Pick & multi-pack

- `pick` é sobre o **total** mostrado no Result.
  Ex.: `packs:3`, pulls com `count` total 8 → 24 cartas no Result.
  `pick:5` → escolhe 5 das 24.
- Em `keep all`, o cliente envia `picks=[0..size-1]`. O server valida.

### 4.4 Validação no load (Encounters.json)

Mesmo lugar onde `options`/`message` são validados:

- `packs >= 1`, `size_total = sum(pulls.count) >= 1`, `0 <= pick <= size_total`.
- Cada pull tem `count >= 1` e `pool` resolvível.
- `pool.rarityRates` keys ∈ {N,R,SR,UR}; values > 0.
- `pool.rarity`/`rarities` valores ∈ {N,R,SR,UR}.
- `pity` (se presente) tem chaves ∈ {N,R,SR,UR}, `increment >= 0`, `max >= 0`.

Action inválida → falha o load do Encounters.json (log + skip), igual hoje.

## 5. Layers do card pool e do rarityRates

```
1. CardList.json + regulation banlist   ← universo base
2. CardPool.json (global)               ← include/exclude, rarityRates, rateGroups, pity
3. CardPool.json byAscension[asc]       ← overlay por ascensão (mesmas chaves)
4. Pool spec na action                  ← filtros locais + rarityRates local + pity local
```

### 5.1 Universo (cardpool)
- Define-se em (1) + (2) + (3). A action **não** mexe (só filtra).
- `source: "any"` → `RoguelikeCardPool.AnyPool(asc)`.
- `source: "link"` → cartas relacionadas (via `CARD_Link`) das cartas do deck do owner,
  ∩ `AnyPool(asc)`.
- `source: "deck"` → cartas do deck do owner.

### 5.2 Filtros (Matches)
- Aplicados em (4) — narrow do universo via `RoguelikeCardPool.Matches`.
- Inclui o filtro hard `rarity`/`rarities` (extensão nova).

### 5.3 rarityRates (pesos de raridade)
- Merge **por chave** das 3 camadas: global → asc → action.
  Ex.: global `{N:60,R:30,SR:9,UR:1}` + asc `{UR:5}` + action `{UR:50, SR:30}`
  → efetivo `{N:60, R:30, SR:30, UR:50}`.
- Aplicado em sources `"any"`/`"link"` (em `"deck"` o draw é uniforme).

### 5.4 rateGroups
- Só (2) + (3). Action **não** sobrescreve em v1 (yagni).
- Multiplica cumulativamente no Weight de cada cid afetado.

### 5.5 Weight efetivo
```
Weight(cid, asc, action_rarityRates, pity[r]) =
   rarityRates_effective[rarity(cid)] × ∏(rateGroups containing cid)

onde rarityRates_effective[r] =
   layered_rarityRates[r] + min(pity[r] × pity_increment[r], pity_max[r])
```

`layered_rarityRates` é o merge das 3 camadas. O bônus de pity entra somando
DEPOIS do merge (pity é por-run, não por-config).

## 6. Pity (chance crescente por raridade)

### 6.1 Mecânica
- Contador `pity[r]` na run, persistido em `roguelike.json`. Cresce **sem cap**
  no contador.
- A cada **pacote** aberto:
  - Pra cada raridade `r` configurada em `pity`:
    - Se nenhuma carta do pacote tem raridade ∈ `pity[r].reset_on` (default = `[r]`):
      `pity[r]++`.
    - Caso contrário: `pity[r] = 0`.
- Bônus aplicado **antes** do sort do próximo pacote, com cap no bônus (não
  no contador): `bonus = min(pity[r] × increment, max)` — ver §5.5.

### 6.2 Config (CardPool.json)
```jsonc
{
  "pity": {
    "UR": { "increment": 10, "max": 50, "reset_on": ["UR"] },
    "SR": { "increment": 5,  "max": 30, "reset_on": ["SR", "UR"] }
  },
  "byAscension": [
    null, null,
    { "pity": { "UR": { "increment": 15 } } }     // override per-asc (merge por raridade)
  ]
}
```

### 6.3 Override por action
- `action.pity` faz merge **por raridade** com global+asc, e **por campo**
  dentro de cada raridade. Ex.: global tem `UR: {increment:10, max:50, reset_on:["UR"]}`
  e action manda `UR: {increment:20}` → efetivo
  `UR: {increment:20, max:50, reset_on:["UR"]}`.
- `action.pity = false` → **desliga** pity inteiro pra esse action:
  não soma bônus, não incrementa nem reseta contadores. (Pity continua igual
  no que estava antes.)

### 6.4 Edge cases
- Pull com `chance` que falhou: o pacote ainda aconteceu (ticka pity
  normalmente); só ficou com menos slots.
- Pacote vazio (todas as pulls com `chance` falharam): conta como pacote
  aberto → pity ticka como "sem nenhuma raridade".

## 7. Estado da run

Adições em `RoguelikeRun`:

```csharp
class RoguelikeRun
{
    // ... campos existentes ...
    public Dictionary<string, int> Pity;                  // {"UR":7, "SR":2} (persistido)
    public Dictionary<string, object> PendingPack;        // staging do pacote (persistido)
}
```

Shape do `PendingPack`:

```jsonc
{
  "token":  <int>,                    // = ActionToken atual (dedup cliente)
  "mode":   "keep" | "pick",
  "pick":   <int>,                    // 0 em keep; X em pick
  "size":   <int>,                    // total de cards exibidos
  "cards":  [                         // flat na ordem de exibição (ordem dos packs/pulls)
    { "cid": 12345, "rarity": 4, "new": true, "premium": 0, "packIdx": 0 },
    ...
  ],
  "labels": { "title_keep": "...", "title_pick": "...", "confirm": "..." },
  "next":   <action node | null>     // pra ActionEngine.Advance depois do finalize
}
```

`ActionToken` continua sendo o token canônico de prompt/pack (bumpa toda vez
que o engine entra num novo nó que requer interação).

## 8. Projeção (WriteRun)

Quando `PendingPack != null`, o server **adiciona** ao response:

### 8.1 `$.Gacha` — shape vanilla, pro `CardPackOpen` consumir

```jsonc
"Gacha": {
  "drawInfo": {
    "packs": [
      {
        "packInfo": [ { "effects": { ... }, "cardInfo": [
          { "mrk": <cid>, "rarity": <1..4>, "backSideRarity": 1,
            "foundSecrets": [], "extendSecrets": [],
            "new": <bool>, "premiumType": <int> }
          // 1 entrada por carta DESSE pack
        ]} ],
        "effects": { "isPickup": false, "imageName": "<random>", "smokeType": 1 }
      }
      // 1 entrada por pack do batch
    ],
    "options": { "skippable": true }
  },
  "resultInfo": { "isSendGift": false, "showSecretFoundResult": false,
                  "isNextFinalizedUR": false, "setItems": [], "buyCardFile": 0 }
}
```

### 8.2 `$.Roguelike.pendingPack` — sinal compacto pro driver

```jsonc
"pendingPack": {
  "token":  <int>,
  "mode":   "keep" | "pick",
  "pick":   <int>,
  "size":   <int>,
  "labels": { "title_keep": "...", "title_pick": "...", "confirm": "..." }
}
```

Quando `PendingPack == null`, server **remove** `$.Gacha` e
`$.Roguelike.pendingPack` (cleanup explícito, mesmo padrão do action prompt M5).

### 8.3 Mutual exclusion
- `pendingPack` é mutuamente exclusivo com `pendingAction` (M5) e
  `pendingDuelNode`. O ActionEngine garante isso: ao entrar num openpack
  node, limpa `pendingAction`; ao terminar (finalize), pode setar novo
  `pendingAction` ou `pendingPack` conforme o `next`.

## 9. Integração com `RoguelikeActionEngine`

### 9.1 Step do engine

```
ActionEngine.Step(run, cursor):
  switch (cursor.kind):
    case "options":   projeta $.Roguelike.action {options}; espera action_respond
    case "message":   projeta $.Roguelike.action {message}; espera action_respond
    case "openpack":  NOVO
      - run.ActionToken++
      - cards = []; pa = action.packs (default 1)
      - pra cada packIdx em 0..pa-1:
          packCards = []
          pra cada pull em action.pulls:
            if pull.chance < 1 && Rng < pull.chance falhou: continue
            drawn = RoguelikeCardPool.DrawN(pull.pool, pull.count, rng,
                                            asc, action.rarityRates_merged,
                                            run.Pity, pity_config_merged)
            packCards += drawn
            (usedSet por-pack pra evitar repetição dentro do pack)
          cards += packCards (com packIdx anotado)
          updatePity(run.Pity, packCards, pity_config_merged)
      - run.PendingPack = { token, mode, pick, size=cards.count, cards, labels, next: cursor.next }
      - $.Gacha + $.Roguelike.pendingPack via WriteRun
      - aguarda Roguelike.pack_finalize (não toca em run.Cards/Deck ainda)
```

### 9.2 `RoguelikeCardPool.DrawN` (helper novo)

```csharp
public static List<DrawResult> DrawN(
    Dictionary<string, object> spec,
    int n,
    Random rng,
    int ascension,
    Dictionary<string, object> regulation,
    HashSet<int> usedSet,                          // por-pack
    Dictionary<int, double> rarityRatesOverride,   // merged (global+asc+action), pode ser null
    Dictionary<int, int> pityCounters,             // run.Pity (parsed por r int)
    Dictionary<int, PityConfig> pityConfig);       // merged, pode ser null

class DrawResult { public int Cid; public int Rarity; public bool IsNew; public int PremiumType; }
```

Internamente: refatora o miolo do `RoguelikeModifiers.Resolve` (BuildPool +
Matches + weighted pick) pra um helper reutilizável. O `Resolve` chama esse
helper passando seu `used` (escope-modifier) — sem mudança de comportamento.

### 9.3 Novo act: `Roguelike.pack_finalize`

Params: `{ token: <int>, picks: [<int>] }`.

```
Act_RoguelikePackFinalize(req):
  if run.PendingPack == null: ignore (stale)
  if req.token != run.PendingPack.token: ignore (stale)
  validate picks:
    - sem duplicatas
    - todos ∈ [0, run.PendingPack.size)
    - mode==keep: picks.count == size (todos)
    - mode==pick: picks.count == run.PendingPack.pick
  if invalid: set ResultCode = INVALID; return (sem mexer no estado)

  pra cada idx em picks:
    card = run.PendingPack.cards[idx]
    run.AddCard(card.cid, 1)                            // pool
    AddToDeck(run.Deck, card.cid, IsExtraDeck(card))    // deck (Extra se card.IsExtraDeck)

  run.PendingPack = null
  ActionEngine.Advance(run, savedNext)                  // próximo nó (ou termina)
  WriteRun(run, req)                                     // projeta sem $.Gacha agora
  SavePlayer(req.Player)
```

`AddToDeck` é helper que põe o cid na seção certa do `run.Deck` (`Main` ou
`Extra`), criando estrutura mínima se ainda não existir. Detecção
extra-deck reusa `YdkHelper.GameCardInfo` (mesmo do `RoguelikeCardPool`).

### 9.4 GameServer dispatch
Caso novo em `GameServer.cs::HandleRequest`:
```csharp
case "Roguelike.pack_finalize":  Act_RoguelikePackFinalize(req); break;
```

## 10. Cliente — driver + hook

### 10.1 Arquivos

```
YgoMasterClient/Roguelike/Actions/
├── RoguelikePackDriver.cs          // pump: detecta pendingPack -> push CardPack/CardPackOpen
└── RoguelikePackResultHook.cs      // hook em CardPackOpenResultViewController.OnCreatedView
```

Extensões:
- `Roguelike/RoguelikeApi.cs` — `GetPendingPack()`, `PackFinalize(int token, int[] picks)`.
- `Roguelike/RoguelikeMapScreen.cs::Update()` — chama `RoguelikePackDriver.Pump()` ao lado
  do `RoguelikeActionDriver.Pump()` (mesmo gate `IsActive(_go)`).

### 10.2 RoguelikePackDriver (padrão M5)

```csharp
static class RoguelikePackDriver
{
    static int _shownToken = -1;
    public static void Pump()
    {
        var p = RoguelikeApi.GetPendingPack();
        if (p == null) { _shownToken = -1; return; }
        if (p.Token == _shownToken) return;
        _shownToken = p.Token;
        var manager = ContentViewControllerManager.GetManager();
        var args = NewDict(); DictAdd(args, "ForwardResultArgs", IntPtr.Zero);
        ViewControllerManager.PushChildViewControllerArgs(manager, "CardPack/CardPackOpen", args);
    }
}
```

Pump só roda no MapScreen.Update (mesma razão do M5: gate pelo mapa visível).

### 10.3 RoguelikePackResultHook

Hook (MinHook, um por método) em `CardPackOpenResultViewController.OnCreatedView`:

```csharp
static void OnCreatedView(IntPtr thisPtr)
{
    _hook.Original(thisPtr);
    var p = RoguelikeApi.GetPendingPack();
    if (p == null) return;                   // vanilla shop pack: passa intacto

    var go = Component.GetGameObject(thisPtr);

    // (1) Renomeia título.
    string title = p.Mode == "pick"
        ? string.Format(p.Labels.TitlePick, p.Pick, p.Size)
        : p.Labels.TitleKeep;
    SetBindingText(go, "TitleSafeArea.TitleGroup.NameText", title);

    // (2) Renomeia OK -> "Confirmar" + intercepta onClick.
    var okBtn = GameObject.FindGameObjectByPath(go,
        "FooterArea.BaseAll.BaseRight.Button/OKButton");
    SetBindingText(okBtn, "TextTMP", p.Labels.Confirm);
    WireButton(okBtn, OnConfirmClick);

    if (p.Mode == "keep") return;

    // (3) Pick: ativa seleção + gate em |sel|==pick.
    EnableSelectionMode(go, p);
}
```

### 10.4 Modo `pick` — seleção e gate

- Itera cada `PackCardTemplate(Clone)` na grid (ordem == índices em
  `PendingPack.cards`).
- Em cada um, hooka o `SelectionButton` do `CardPict` pra toggle do
  índice no set `_selected`.
- Atualiza `OKButton.interactable = (|_selected| == pick)`.
- Visual:
  - selecionada → `SelectCursorCardForPC.SelectCursorPlate` ativo (TweenAlpha já existe).
  - desselecionada → desativa.
- Click acima do limite: ignora (alternativa "derruba o mais antigo" considerada,
  mas mais complexo — v1 fica em ignorar).

### 10.5 Confirmar → finalize → pop

```csharp
static void OnConfirmClick()
{
    var p = RoguelikeApi.GetPendingPack();
    if (p == null) return;

    int[] picks;
    if (p.Mode == "keep") picks = Enumerable.Range(0, p.Size).ToArray();
    else if (_selected.Count == p.Pick) picks = _selected.ToArray();
    else return; // botão devia estar disabled; defesa em profundidade

    RoguelikeApi.PackFinalize(p.Token, picks);

    var manager = ContentViewControllerManager.GetManager();
    if (manager != IntPtr.Zero) ViewControllerManager.PopChildViewController(manager);
}
```

### 10.6 API

```csharp
public class PendingPack
{
    public int Token; public string Mode; public int Pick, Size;
    public Labels Labels;
    public class Labels { public string TitleKeep, TitlePick, Confirm; }
}

public static PendingPack GetPendingPack();                  // lê $.Roguelike.pendingPack
public static void PackFinalize(int token, int[] picks);     // Request.Entry("Roguelike.pack_finalize", ...)
```

## 11. Reentrada / robustez

- Sair da run mid-pack: `PendingPack` persiste no `roguelike.json`.
  Ao reentrar (login + ir pro mapa):
  - `WriteRun` reprojeta `$.Gacha` + `$.Roguelike.pendingPack` (mesmo token).
  - `RoguelikePackDriver.Pump` (1ª chamada após reentrada) vê token novo
    (porque `_shownToken=-1` na inicialização) → re-push do `CardPackOpen`.
  - Animação roda igual; user finaliza igual.

- Cancelar via back/ESC: o VC nativo não tem cancel visível, mas joystick "B"
  pode mapear pra pop. Caso vire problema:
  - Hook em `OnReleaseView` re-pushca enquanto `pendingPack` existe (mesma
    estratégia do action prompt M5). Tratamento fora-de-escopo v1; adiciona
    se observado em teste.

- Token validation defensiva: server ignora `pack_finalize` com token != atual.
  Cliente nunca pode escolher cid arbitrário (envia índices; server resolve).

## 12. Determinismo / RNG

- `RoguelikeRun.Seed` já existe. ActionEngine usa um RNG seedado por
  `(run.Seed, run.ActionToken)` antes de cada `openpack` Step.
- Pity é parte do estado da run → também determinístico.
- `chance` por-pull e `rarityRates` rolls usam o mesmo RNG.

## 13. Sem inventar — o que o card pool ganha

Mudanças em `RoguelikeCardPool` (uma adição contida):

1. `Matches` aceita `rarity` (string) ou `rarities` (lista) como filtro hard.
2. `DrawN(spec, n, rng, asc, regulation, usedSet, rarityRatesOverride,
   pityCounters, pityConfig)` — novo helper, refatora o miolo do
   `Resolve` dos modifiers. **Sem mudar comportamento** do `Resolve`.
3. Carregador do `CardPool.json` reconhece o bloco `pity` (global e
   `byAscension[asc].pity`), com defaults vazios.

## 14. Out of scope (v1)

- Garantias **entre pacotes** estilo "1 UR em 10" (vanilla
  `IsUltraRareGuaranteed`). Pity já cobre o caso comum. Adicionar
  no v2 só se necessário.
- `chance` variável por pull baseado em estado (ex.: 1ª vez 80%, depois
  50%). Yagni.
- `pulls[].pool.rateGroups` (override per-action de grupos). Yagni.
- "Derruba o mais antigo ao selecionar acima do limite" (pick mode UX).
- Indicador visual "X/Y selecionadas" custom — título já comunica.
- Animação especial de seleção/desseleção — usa o que tiver no nativo.
- Hover/preview ampliado da carta no Result — vanilla já tem.
- Joystick "B" → re-push (cancela). Adiciona se observado.

## 15. Plano de teste (manual, in-game)

1. `Encounters.json` recebe um encontro com action openpack (`packs:1`, `pulls`
   simples, `pick:0`).
2. Disparar via `rgencounter <id>` (comando existente do M5).
3. Verifica: VC `CardPackOpen` abre, anima, transiciona pro Result com
   título "Cartas obtidas", botão "Confirmar". OK → cartas no pool (`run.Cards`)
   + no deck (`run.Deck`).
4. Action openpack `pick:3` de `size:8`: Result mostra "Selecione 3 de 8",
   botão desabilitado, ativa após 3ª seleção, OK finaliza com só os 3 escolhidos.
5. Action openpack `packs:3, pulls c/ rarityRates UR-high`: 3 animações,
   1 result com 24 cards. Pity persiste no JSON; abrir outro openpack
   depois reflete o pity carregado.
6. Sair da run mid-pack (alt+F4 ou abandonar). Reentrar → o pack reabre
   automaticamente com as mesmas cartas (determinístico).

## 16. Resumo das mudanças por arquivo

**Server (novo)**
- `Roguelike/Actions/Action_OpenPack.cs` (ou bloco em `RoguelikeActionEngine.cs`) — Step do `openpack`.
- `Roguelike/Actions/PackFinalize.cs` (ou em `GameServer.Roguelike.cs`) — `Act_RoguelikePackFinalize`.

**Server (modificado)**
- `Roguelike/RoguelikeRun.cs` — campos `Pity` + `PendingPack`, serialização.
- `Roguelike/RoguelikeCardPool.cs` — filtro `rarity`, `DrawN`, carrega `pity` do CardPool.json.
- `Roguelike/RoguelikeModifiers.cs` — refatora `Resolve` pra usar `DrawN` (sem mudar contrato).
- `Roguelike/RoguelikeActionEngine.cs` — Step do `openpack`.
- `Roguelike/GameServer.Roguelike.cs` — `WriteRun` projeta `$.Gacha` + `pendingPack`; cleanup.
- `GameServer.cs` — case `Roguelike.pack_finalize`.
- `Roguelike/RoguelikeEncounters.cs` — parsing/validação do action openpack.

**Client (novo)**
- `Roguelike/Actions/RoguelikePackDriver.cs`.
- `Roguelike/Actions/RoguelikePackResultHook.cs`.

**Client (modificado)**
- `Roguelike/RoguelikeApi.cs` — `PendingPack`/`GetPendingPack`/`PackFinalize`.
- `Roguelike/RoguelikeMapScreen.cs` — Update chama `RoguelikePackDriver.Pump`.

**Config / dados**
- `DataLE/Roguelike/CardPool.json` — bloco `pity` opcional (defaults vazios).
- `DataLE/Roguelike/Encounters.json` — exemplos de action openpack (testes).
