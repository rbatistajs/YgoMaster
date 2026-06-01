# Roguelike — duelHooks (efeito ativo de relíquia via Lua) — design

## 1. Objetivo

Dar às relíquias **efeitos ativos** (evento de duelo → ação), via scripts **Lua**.
Hoje as relíquias só têm `modifiers` (board-spec) e `buffs` (ATK/DEF/level
passivo, avaliado por-card). Os duelHooks preenchem exatamente o que o
`relics-design.md` lista como **fora de escopo v1** (§13): *"Relíquias com
trigger ativo (efeito ao ativar, não contínuo)"* e *"Buffs condicionais por
estado de duelo"*.

Exemplo-alvo (o benchmark do design):

> Quando invocar um monstro **normal** do tipo **Dragão**, olhe o topo do deck;
> se for um **normal Dragão nível 4**, adiciona à mão; senão, embaralha o deck.

Isso exige trigger filtrado + *peek* + condição composta + branch — além do
declarativo. Lua resolve com a própria linguagem.

## 2. Relação com o sistema de relíquias

duelHooks é a **terceira camada** da relíquia, ao lado de `modifiers` e `buffs`:

| camada | o quê | onde roda |
|---|---|---|
| `modifiers` | board-spec (começar com X monstro, LP, etc.) | server → cmds |
| `buffs` | ATK/DEF/level passivo por-card (regra avaliada) | client (stat hooks) |
| **`duelhooks`** | **efeito ativo: evento → script Lua → primitiva** | **client (thread do duelo)** |

Continua **server-authoritative no inventário**: o server decide **quais**
relíquias o player/inimigo tem e manda os hooks ativos no payload do duelo
(igual já manda as regras de buff). O client apenas **executa** — os `.lua` e as
primitivas são conteúdo do client (como a `duel.dll` já é).

## 3. Princípios

- **Schema mínimo, lógica no script.** O hook declara só `{trigger, script,
  params}`; todo filtro/condição/branch vive no Lua.
- **Reuso via `params`.** Um `.lua` genérico serve várias relíquias variando o
  `params`.
- **Pega os dois lados.** Triggers vêm do barramento de **intenção**
  (`duelState` cmd, polling no `DLL_DuelSysAct`) — que captura player **e** CPU
  (ver `duel-action-primitives.md`, "Detecção de eventos").
- **ctx magro + funções.** O `ctx` traz identidade (ids); propriedades de carta
  vêm de `get_card_props(cid)` — a mesma função pra a carta do evento e pra
  qualquer outra (topo do deck, etc.).
- **Reusa a infra de relíquia.** Gating, sorteio, payload de duelo, baking
  server→client: tudo já existe pro `buffs`; duelhooks anexa no mesmo canal.

## 4. Arquitetura / fluxo

```
Server (assa o duelo)                    Client (duelo)
relíquias ativas (player+enemy)          recebe hooks no payload do duelo
  coleta duelhooks de cada uma           carrega/cacheia os .lua (MoonSharp)
  marca owner (player id 0/1)            registra por trigger
  → payload: [{script,trigger,    ─────► DLL_DuelSysAct detecta a borda do cmd
     params,owner}]                duel    (rgsys: summon/attack/activate/...)
                                  start     → on_event(ctx, params) por hook
                                            → leituras (get_card_props/deck_top)
                                            → ações (primitivas duel.dll)
```

## 5. Schema no `Relics.json`

```jsonc
"dragon_recycler": {
  "name": "Reciclador Dracônico",
  "icon": "card_...",
  "text": "Ao invocar um Dragão Normal, recicla o topo do deck.",
  "rarity": "SR",
  "act": { "min": 1 },                       // gating, igual aos outros campos
  "duelhooks": [
    { "trigger": "onSummon",
      "script": "peek_recycle.lua",
      "params": { "race": "Dragon", "level": 4 } }
  ]
}
```

- `duelhooks`: **array** → vários hooks por relíquia; cada um `{trigger, script,
  params}`.
- `trigger`: um dos valores do catálogo (§6).
- `script`: nome de arquivo em `DataLE/Roguelike/Scripts/`.
- `params`: dict livre (JSON), repassado ao script. Opcional.

Uma relíquia pode declarar o **mesmo** `script` em entradas com triggers
diferentes — `on_event` roda pros dois e ramifica por `ctx.event`.

## 6. Catálogo de triggers (v1) e semântica

Todos vêm do barramento de **intenção** (campos de comando no `duelState`,
polling no `DLL_DuelSysAct`). Já mapeados empiricamente e **pegam o CPU**:

| trigger | cmd (`0x3cf8`) | dispara quando |
|---|---|---|
| `onSummon` | 4 | invocação normal (face-up) conclui |
| `onSet` | 6 | set de monstro/carta conclui |
| `onAttack` | 0 | ataque é declarado (`pend!=0`, `pos<7`) |
| `onActivate` | 3 | efeito/magia/armadilha é ativado |
| `onDraw` | 13 | carta é sacada |
| `onPhase` | 17/18 | troca de fase (battle/main2) |
| `onTurn` | 19 + draw | virada de turno |

- **Borda**: o trigger dispara na transição do `cmd` (intenção declarada →
  `pend` volta a 0 = ação concluída), não a cada tick. Implementação espelha o
  rising/falling-edge que o `OnAttackSummonMask` já usa.
- **Owner**: o hook **dispara em todos os eventos** (player e CPU). O `ctx`
  carrega `me` (id do dono da relíquia, 0/1) e `player` (id do autor do evento,
  0/1); o **script decide** (`if ctx.player == ctx.me`). Não há filtro de lado
  no schema.

`onDestroy`/`onDamage`/`onLeaveField` (barramento de **resultado**, `rgop`)
ficam pra v2 — exigem mapear os opcodes do emitter primeiro.

## 7. Contrato do script

Cada `.lua` expõe **uma** função de entrada, chamada quando o trigger dispara:

```lua
function on_event(ctx, params)
  -- ctx.event  : string do trigger ("summon", "attack", ...)
  -- ctx.player : id do autor do evento (0/1)
  -- ctx.me     : id do dono da relíquia (0/1)
  -- ctx.card   : { cid, player, zone }  -- identidade da instância do evento
  -- params     : o dict do JSON (pode ser nil)
end
```

`on_event` única (não `on_summon`/`on_attack`): maximiza reuso — o mesmo script
serve qualquer trigger via `params`; quem quiser distinguir lê `ctx.event`.

`ctx.card` é a **identidade** (número + onde está) pra você **agir** naquela
carta. As **propriedades** vêm sempre de `get_card_props(cid)` (§8/§9).

Exemplo-alvo escrito:

```lua
-- peek_recycle.lua  (genérico, reusável via params)
function on_event(ctx, params)
  if ctx.player ~= ctx.me then return end                    -- "quando EU"
  local c = get_card_props(ctx.card.cid)
  if not (c.subtype == "normal" and c.race == params.race) then return end
  local top = deck_top(ctx.me)                                -- cid do topo
  local t = get_card_props(top)
  if t and t.subtype == "normal" and t.race == params.race and t.level == params.level then
    to_hand(ctx.me, "deck", 0)                                -- topo → mão
  else
    shuffle_deck(ctx.me)                                      -- senão embaralha
  end
end
```

## 8. API Lua (v1)

Enxuta de propósito; expande quando novas relíquias pedirem (decisão do user).
Todas rodam na thread do duelo (o dispatch é dentro do `DLL_DuelSysAct`):
leituras são imediatas; ações usam as primitivas já catalogadas (algumas
assíncronas, ex. special summon, resolvem nos ticks seguintes).

**Leitura**
- `get_card_props(cid)` → `{ cid, race, subtype, level, attr, atk, def }` (§9).
- `deck_top(player)` → cid do topo do deck (ou nil).
- `hand_count(player)` / `grave_count(player)` / `deck_count(player)` → int.

**Ações** (mapeiam pras primitivas de `duel-action-primitives.md`)
- `summon_from_deck(player, index)` → `FUN_180625c00` (base deck `0x3c4`).
- `summon_from_grave(player, index)` → `FUN_180625c00` (base grave `0x7fc`).
- `to_hand(player, location, index)` → debug cmd 6.
- `shuffle_deck(player)` → debug cmd 20.
- `draw(player)` → debug cmd 6 sobre `deck[0]`.
- `damage(player, amount)` → `FUN_180142750`.
- `heal(player, amount)` → `FUN_180148660`.
- `to_grave(player, location, index)` → `FUN_180153dd0` (mode 0).
- `destroy(player, zone)` → `FUN_1801548f0`.

`location` na API Lua é **nome** (`"deck"`, `"hand"`, `"grave"`, `"banish"`),
mapeado pelo binding pros codes do engine (13=mão, 15=deck, 16=cemitério, …);
`zone`/`index` são inteiros (zona 0–6 no campo, índice na pilha da zona).

## 9. Card-prop accessor (pré-requisito — "tijolo 1")

Função que, **a partir do cid**, devolve as propriedades estáticas da carta:

```csharp
struct CardProps { int Cid, Race, Level, Attr, Atk, Def; string SubType; }
static CardProps GetCardProps(int cid);   // lê CARD_Prop/card-data, cacheado por cid
```

- Lê do card-data do engine (`DAT_1811adbe8` / `DuelDll.CardPropMem` / CARD_Prop).
- Cacheado por cid (props são por-cid, não por-instância).
- **`subType`** (normal/effect/fusion/synchro/xyz/link/…): resolvido aqui — é o
  mesmo dado que hoje é **STUB** em `RoguelikeStatBuff.SubTypeMatches`.

**Por que é o tijolo 1:** serve **os dois** consumidores — o filtro dos hooks
Lua (`ctx.card`, `deck_top`) **e** o `subType` dos `buffs` passivos (pendente).
Por isso a fase 1 do build é fincar o accessor, antes da engine Lua.

## 10. Engine Lua

- **MoonSharp** (Lua 5.2 puro em C#, DLL gerenciada) — compatível com o client
  (.NET Framework 4.8), **sem dependência nativa** (ideal pro processo injetado).
- **Sandbox**: `CoreModules` restrito (sem `io`/`os`/`require`); só as funções
  expostas pela API (§8).
- Um interpretador (`Script`) por duelo; os `.lua` são carregados/cacheados e
  os globals da API registrados uma vez.

## 11. Lifecycle e dispatch

- **Carga**: ao entrar no duelo, o client lê os hooks do payload, carrega os
  `.lua` referenciados e registra `(trigger → [hook])`, cada hook com
  `owner`/`params`.
- **Dispatch**: o loop do `DLL_DuelSysAct` (que já lê os campos de comando pro
  `rgsys`/`OnAttackSummonMask`) detecta a borda do `cmd`, monta o `ctx` (lendo
  `player`/`pos`/descritor) e chama `on_event(ctx, params)` de cada hook do
  trigger. Exceções de Lua são capturadas e logadas (um hook que quebra não
  derruba o duelo).
- **Descarga**: ao sair do duelo, limpa os hooks e o interpretador.

## 12. Server → client (baking)

No ponto onde o server já assa o duelo (modifiers → cmds, buffs → regras):

1. Coleta os `duelhooks` de todas as relíquias ativas (player + enemy).
2. Marca cada hook com `owner` = id do dono (0 = player local, 1 = inimigo).
3. Anexa a lista `[{script, trigger, params, owner}]` no payload de início do
   duelo (mesmo canal das regras de buff).

Client, ao entrar: `RoguelikeDuelHooks.SetHooks(list)`; `Clear()` ao sair.

## 13. Determinismo / RNG

- O RNG usável dentro do script é **livre** (`math.random`): o duelo é jogado ao
  vivo e **não** é reproduzido pela run, então o determinismo seedado da run
  (que governa drops/sorteios) **não se aplica** dentro do duelo.
- O estado da run (inventário de relíquias) continua determinístico — só a
  execução in-duelo é livre.

## 14. Dev command

`rgduelhook` no `ConsoleHelper` (testar sem depender do server):
- `rgduelhook load <arquivo.lua> <trigger> [params-json]` — registra um hook
  manualmente no duelo atual.
- `rgduelhook list` — lista hooks ativos.
- `rgduelhook clear` — remove todos.
- `rgduelhook fire <trigger>` — dispara o trigger manualmente (ctx sintético)
  pra testar o script sem reproduzir a jogada.

## 15. Fora de escopo (v1)

- Triggers de **resultado** (`onDestroy`/`onDamage`/`onLeaveField`) → v2 (mapear
  o barramento de resultado `rgop`).
- **Estado persistente** entre disparos (memória do hook ao longo do duelo) → v2.
- Hook **alterar regras de buff** dinamicamente → v2.
- **Código Lua inline** no payload (em vez de arquivo no client) → descartado
  (incha payload, sem hot-reload, sem ganho num jogo offline local).
- Props **dinâmicas** de instância (atk modificado atual, posição face-up/down)
  no `get_card_props` → v1 só props estáticas por cid.

## 16. Riscos / a validar na implementação

- **Captura do `ctx.card.cid`**: pra alguns triggers o cid da carta do evento
  sai limpo do descritor (`desc[9]`/`desc[6]`) no momento do `cmd`; pra outros
  (ex. `onDraw`) pode não ser trivial. v1 garante `ctx.card` pra
  summon/set/attack/activate; onde a captura falhar, `ctx.card = nil` e o script
  trata (documentado por trigger).
- **`subType` no card-data**: confirmar o campo/máscara da categoria no
  CARD_Prop (o mesmo lookup destrava o STUB dos buffs).
- **Custo do dispatch**: Lua roda na thread do duelo; manter os scripts leves
  (sem laços pesados). A engine captura exceções e mede tempo em dev.

## 17. Fases de build

1. **Card-prop accessor** (`GetCardProps(cid)`): lê CARD_Prop, cacheia, resolve
   `subType`. Liga no `RoguelikeStatBuff` (mata o STUB). Testável isolado
   (`rgbuff peek` + um dump por cid). **Bloqueia o resto.**
2. **Engine + API de leitura**: MoonSharp no client; `RoguelikeDuelHooks`
   (carga/registro/dispatch stub); expõe `get_card_props`/`deck_top`/counts;
   `rgduelhook load/fire` pra rodar um script com ctx sintético.
3. **API de ações**: liga `summon_from_*`/`to_hand`/`shuffle_deck`/`draw`/
   `damage`/`heal`/`to_grave`/`destroy` nas primitivas existentes.
4. **Dispatch real**: detectar a borda dos triggers de intenção no
   `DLL_DuelSysAct`, montar o `ctx` (cid via descritor) e chamar os hooks.
5. **Server→client**: parse de `duelhooks` no `Relics.json`; baking coleta os
   hooks ativos (com `owner`) → payload; client `SetHooks`/`Clear`.
6. **Validação in-game**: o exemplo-alvo (`peek_recycle.lua`) como relíquia,
   player e CPU; e um 2º script reusando o mesmo `.lua` com `params` diferente.

## 18. Resumo das mudanças por arquivo

**Client (novo)**
- `Roguelike/RoguelikeDuelHooks.cs` — engine: carga/cacheia `.lua`, registro por
  trigger, dispatch, bindings da API Lua (sandbox MoonSharp).
- `Roguelike/RoguelikeCardProps.cs` — `GetCardProps(cid)` (lê a card-data via `DuelDll`, cacheia por cid).
- `DataLE/Roguelike/Scripts/peek_recycle.lua` — script de exemplo.
- referência a `MoonSharp.Interpreter.dll` no `YgoMasterClient.csproj` + cópia.

**Client (modificado)**
- `DuelDll.cs` — expõe as primitivas que faltam (revive-from-grave, to_hand,
  shuffle, damage, heal, to_grave, destroy) como métodos públicos pro wrapper;
  o `DLL_DuelSysAct` ganha o dispatch dos triggers.
- `Roguelike/RoguelikeStatBuff.cs` — `SubTypeMatches` usa o accessor (mata STUB).
- `ConsoleHelper.cs` — comando `rgduelhook`.
- carga do duelo (client) — `RoguelikeDuelHooks.SetHooks` no início, `Clear` no fim.

**Server (modificado)**
- `Roguelike/RoguelikeRelics.cs` — parse de `duelhooks` na relíquia.
- baking do duelo (`GameServer.Roguelike.cs` ou onde assa) — coleta hooks ativos
  (com `owner`) e anexa no payload.

**Config / dados**
- `DataLE/Roguelike/Relics.json` — exemplo com `duelhooks`.
- `Docs/config/roguelike/duelhooks.md` — doc de configuração (schema + catálogo
  de triggers + API Lua).
