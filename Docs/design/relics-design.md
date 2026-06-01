# Roguelike — Sistema de Relíquias (design)

## 1. Objetivo

Adicionar **relíquias** ao roguelike: itens persistentes na run que combinam
os **modifiers** que já existem (spec de tabuleiro: começar com X monstro, etc.)
com uma camada nova de **buffs** (alterar ATK/DEF/estrelas de monstros por
tipo/subtipo/atributo/nível). O player coleta relíquias (via ações/drops) e
inimigos podem carregar relíquias (encounters mais difíceis).

Reaproveita a infraestrutura existente:

- `RoguelikeModifiers` — já faz **merge de camadas** `{ player, enemy }`. Cada
  relíquia contribui uma camada de `modifiers`.
- `RoguelikeStatBuff` (client) — o hook em `FUN_1800b0000` (campo) + o hook em
  `DLL_DuelGetCardBasicVal` (mão/indexado) já aplicam deltas de ATK/DEF. Vira um
  **motor de regras** que avalia os buffs das relíquias.
- `RoguelikeActionEngine` / `RoguelikeActions` — a aquisição é uma ação nova
  `addRelic` (específica ou sorteada).
- `RoguelikeCardPool` — peso/raridade/pity reusados pro sorteio de relíquia.
- `Encounter` (`Range` Act/Floor/Ascension) — relíquias herdam o mesmo gating.

## 2. Princípios

- **Server-authoritative.** O inventário de relíquias mora na run (server). O
  cliente nunca decide quais relíquias o player tem nem o que elas fazem — só
  recebe as regras de buff "assadas" e renderiza ícones.
- **Reusa, não inventa.** Modifiers de relíquia entram como camada no `Merge`
  existente; o sorteio reusa `RoguelikeCardPool`; a aquisição é uma action.
- **Determinístico no seed da run.** Drops/sorteios usam RNG seedado por
  `(run.Seed, run.ActionToken)` — reentrada reproduz o mesmo resultado.
- **Buff = regra avaliada por-card.** Não há mutação de estado do duelo; cada
  hook avalia as regras ativas contra o card que a engine está pedindo.

## 3. Arquitetura geral

```
Server (início do duelo)                  Client (RoguelikeStatBuff)
┌────────────────────────────────┐        ┌─────────────────────────────────┐
│ Coleta relíquias ativas:       │        │ Carrega regras de buff:         │
│  - player: run.Relics          │        │  RoguelikeStatBuff.SetRules(...) │
│  - enemy:  encounter.relics    │        │                                 │
│ Pra cada relíquia:             │  --->  │ Hooks avaliam por-card:         │
│  (a) modifiers -> camada Merge │  duel  │  FUN_1800b0000  (campo, 0-6)    │
│  (b) buffs -> regras + side    │  start │   -> regras scope=field         │
│ Manda regras compiladas no     │ payload│  DLL_DuelGetCardBasicVal (7+)   │
│ payload de início do duelo     │        │   -> regras scope=permanent     │
└────────────────────────────────┘        └─────────────────────────────────┘

Aquisição (durante a run)                 UI
┌────────────────────────────────┐        ┌─────────────────────────────────┐
│ action "addRelic" (specific |  │        │ Barra de ícones do player na    │
│  random+rarity+chance)         │        │  tela da run (tooltip name/text)│
│ encounter.relicDrop na vitória │        │ Relíquias do inimigo no detalhe │
│ dropConfig global (act/floor/  │        │  do node (node-detail-drawer)   │
│  ascension)                    │        │                                 │
└────────────────────────────────┘        └─────────────────────────────────┘
```

`side` é implícito: relíquia do player → regras `side=mine`; do inimigo →
`side=foe`. O motor casa `(player & 1) == MyID` (mesmo critério de hoje).

## 4. Modelo de dados — `DataLE/Roguelike/Relics.json`

```jsonc
{
  // ----- config global de drop (opcional) -----
  // Chance base de uma vitória de duelo dropar relíquia + pesos de raridade do
  // sorteio. Varia por act / floor / ascension (overlays que mergeiam por chave).
  "dropConfig": {
    "chance": 0.15,
    "rarityWeights": { "R": 60, "SR": 30, "UR": 10 },
    "byAct":       { "2": { "chance": 0.20 }, "3": { "chance": 0.25 } },
    "byFloor":     { },
    "byAscension": [ null, { "chance": 0.20 } ]   // índice = tier de ascensão
  },

  // ----- relíquias -----
  "dragon_soul": {
    "name": "Alma do Dragão",
    "icon": "card_88264978",            // "card_<cid>" ou "profile_<id>"
    "text": "Seus Dragões ganham +300 ATK enquanto em campo.",
    "rarity": "SR",                     // pro pool de sorteio
    "act":       { "min": 1 },          // gating (igual Encounter) — elegibilidade no sorteio
    "floor":     { "max": 5 },
    "ascension": { "min": 0 },
    "modifiers": { "player": { /* board-spec, opcional */ } },
    "buffs": [
      { "match": { "type": "Dragon" }, "scope": "field", "timing": "always", "atk": 300 }
    ]
  }
}
```

- `act`/`floor`/`ascension` usam o mesmo `Range { Min, Max }` dos Encounters
  (filtram quais relíquias entram no pool de sorteio num dado momento da run).
- `modifiers` é opcional e reusa 100% a shape do `RoguelikeModifiers`
  (entra como uma camada no `Merge`, lado `player` ou `enemy` conforme o dono).
- `buffs` é a lista de regras (§5).

## 5. Schema de regra de buff (motor novo no `RoguelikeStatBuff`)

```jsonc
{
  "match": {              // todos em AND; chave ausente/null = ignora esse eixo
    "type":    "Dragon",  // raça/espécie (struct offset 20) — direto do hook
    "subType": null,      // categoria (normal/effect/fusion/synchro/xyz/link/...) — via card-data
    "attr":    null,      // atributo (struct offset 22) — direto do hook
    "level":   null,      // nível exato (struct offset 26) — direto do hook
    "cid":     null       // carta específica (struct offset 0)
  },
  "scope":  "field",      // "field" | "permanent" — afeta SÓ `level` (permanent = nível + tributo).
                          // atk/def é SEMPRE field-only (efeito azul); off-field não existe no engine.
  "timing": "always",     // "always" | "damage"    (damage = flag bit3 / 0x9 no hook de campo)
  "atk":   300,           // delta de ATK
  "def":   0,             // delta de DEF
  "level": 0              // delta de estrelas
}
```

### 5.1 Onde cada efeito aplica (estado real implementado)

O `scope` (`field`/`permanent`) controla o alcance:

| efeito | scope | hooks | resultado |
|---|---|---|---|
| `atk`/`def` | qualquer | `FUN_1800b0000` (campo) | ATK/DEF efetivo no campo (azul, dano, IA) |
| `level` | `field` | `FUN_1800b0000` | level **só no campo** (display + checks de level em campo); **tributo inalterado** |
| `level` | `permanent` | `FUN_1800b0000` **+** `FUN_180095af0` | level no campo **e** na mão → **afeta o tributo** de invocação |
| `atk`/`def` off-field | `permanent` | — *reservado* | sem applier |

**`level` + `permanent` afeta o tributo de verdade.** A `FUN_180095af0` é a fonte do
**level efetivo da mão** (já aplica efeitos tipo Cost Down, percorrendo a lista de nós em
`DAT+0x2bec`; ids `0x1f6a`/`0x2df1`/`0x40a5`/`0x44f8` reduzem level), e a checagem de
**tributo** lê dela. O hook só aplica o delta ali pra regras `permanent` → uma regra
`level` **field** muda o nível no campo SEM baixar o custo de invocação; com `permanent`,
baixa. Confirmado in-game (level 5 com `-1 permanent` summona sem tributo). Delta clampado
em min 1; raça resolvida pelo card-data `DAT_1811adbe8`.

**`atk`/`def` é SEMPRE field-only (com azul).** Não há off-field: o engine **não calcula**
atk/def efetivo fora do campo (só o valor impresso da tabela `DAT_1811adbe8`), então não
existe função pra hookar — `permanent` é **ignorado** pra atk/def. A única alternativa
seria patchar a base na tabela (global por cid), mas isso **perde o azul** (`eff == org`)
e nem é per-instância → **descartado** (o azul vale mais). Ver §13.

### 5.2 Eixos de match

- **Diretos do hook** (sem lookup): `type` (raça), `attr`, `level`, `cid`.
  Na `FUN_1800b0000` saem em offsets 20/22/26/0; na `DLL_DuelGetCardBasicVal`
  saem em `pVal.Type/Attr/Level/EffectID`.
- **`subType`** (categoria do card): não está na struct de stats; resolve via
  card-data por cid (o client tem `DuelDll.CardPropMem`/CARD_Prop). Cacheado
  por cid no client.

### 5.3 Combinação

- Todas as regras ativas que casam um card **somam** seus deltas (atk/def/level).
- `timing=damage` só soma quando o hook de campo é chamado no contexto de dano
  (flag bit3 setada). `always` soma sempre (display estático incluso).

## 6. Estado da run

Adição em `RoguelikeRun`:

```csharp
class RoguelikeRun
{
    // ... campos existentes ...
    public List<object> Relics;   // lista de relic ids (strings), persistida
}
```

Serializa em `ToDictionary` (`"relics"`) e `FromDictionary`. `AddRelic(id)`
helper (dedup opcional — decisão: relíquias são **únicas** por id na run v1).

## 7. Encounter

Dois campos novos no `RoguelikeEncounters.Encounter`:

```csharp
public List<object> Relics;                 // relic ids carregadas pelo inimigo
public Dictionary<string, object> RelicDrop; // drop na vitória deste encounter
```

```jsonc
{
  "id": "...", "deck": "...",
  "relics": ["dragon_soul"],                       // inimigo ganha modifiers+buffs dessas
  "relicDrop": { "chance": 0.3, "relic": "dragon_soul" },        // específica
  // ou:
  "relicDrop": { "chance": 0.3, "random": true, "rarity": "SR" } // sorteada
}
```

`Encounter.Ascension` (Range) já existe — nada a adicionar lá.

## 8. Baking + entrega (server → client)

No ponto onde o server já assa o duelo (modifiers → `cmds`):

1. Junta as camadas de `modifiers` das relíquias ativas (player + enemy) com as
   camadas existentes do encounter → `RoguelikeModifiers.Merge(layers)`.
2. Compila os `buffs` de todas as relíquias ativas numa lista de regras, cada
   uma com `side` (`mine` pras do player, `foe` pras do inimigo).
3. Anexa as regras no payload de início do duelo (mesmo canal que já leva os
   cmds/config do duelo pro client).

Client, ao entrar no duelo: `RoguelikeStatBuff.SetRules(rules)` (substitui o
estado dev flat de hoje). `Clear()` ao sair do duelo.

## 9. Aquisição

### 9.1 Action `addRelic`

Novo `kind` no `RoguelikeActionEngine`:

```jsonc
{ "kind": "addRelic", "relic": "dragon_soul" }                          // específica
{ "kind": "addRelic", "random": true, "rarity": "SR", "chance": 0.5 }   // sorteada + chance
```

- `chance` (opcional, default 1.0): probabilidade de conceder. Falhou → não dá nada.
- `random`: sorteia do pool filtrado por `rarity` (opcional) + `act/floor/ascension`
  da relíquia vs estado atual da run, com peso/pity reusando `RoguelikeCardPool`.
- já-possuídas são excluídas do sorteio (relíquia única por run).

### 9.2 `relicDrop` no Encounter

Na vitória do duelo: rola `chance`; se passar, dispara um grant — `relic`
(específica) ou `random`+`rarity` (sorteada, mesmas regras do `addRelic`).

### 9.3 `dropConfig` global

Quando um encounter **não** define `relicDrop`, usa o `dropConfig` global do
`Relics.json` (chance + rarityWeights), com overlays `byAct`/`byFloor`/
`byAscension` mergeando por chave. Permite tunar o drop por progressão sem
tocar cada encounter.

## 10. Cliente — motor de buff

`RoguelikeStatBuff` deixa de ser o estado dev flat (`TestAtk`) e vira:

```csharp
static class RoguelikeStatBuff
{
    // Regra compilada (recebida do server).
    public class Rule
    {
        public string Side;     // "mine" | "foe"
        // match
        public int? Type, Attr, Level, Cid; public string SubType;
        // escopo / timing
        public bool Permanent;  // scope
        public bool DamageOnly; // timing
        // efeito
        public int Atk, Def, LevelDelta;
    }

    public static void SetRules(List<Rule> rules);
    public static void Clear();

    // Avaliado pelos hooks. location: field|hand. timing: normal|damage.
    public static bool Evaluate(int cid, int type, int attr, int level, bool mine,
                                bool permanentLocation, bool damageContext,
                                out int atk, out int def, out int levelDelta);
}
```

- O hook de campo (`FUN_1800b0000`) chama `Evaluate(..., permanentLocation:false,
  damageContext: (flags&8)!=0, ...)` e aplica atk/def/level.
- O hook indexado (`DLL_DuelGetCardBasicVal`, pos≥7) chama
  `Evaluate(..., permanentLocation:true, damageContext:false, ...)` e só soma
  regras `permanent`.
- `subType` resolvido por cid via card-data (cache local).

Comando dev **`rgrelic`** (no `ConsoleHelper`): `rgrelic give <id>` /
`rgrelic list` / `rgrelic clear` / `rgrelic rules` — pra testar regras in-game
sem depender do server (injeta regras direto no `SetRules`).

## 11. UI

- **Barra do player**: ícones das relíquias na tela da run
  (`RoguelikeRunScreen`/`RoguelikeMapScreen`), com tooltip (`name` + `text`).
  Lê `run.Relics` + metadados das relíquias (server projeta nome/icon/text no
  payload da run).
- **Inimigo**: ícones das relíquias do encounter no **node detail drawer**
  (já existe — ver `node-detail-drawer-design.md`), na seção do combate.

## 12. Determinismo / RNG

- Sorteios (`addRelic random`, `relicDrop random`, dropConfig) usam RNG seedado
  por `(run.Seed, run.ActionToken)`.
- `run.Relics` é parte do estado da run → reentrada reproduz o inventário.

## 13. Out of scope (v1)

- Relíquias **empilháveis** (mesma relíquia 2x). v1 = única por id.
- Buffs condicionais por estado de duelo (ex.: "+300 se você tem 3+ monstros").
  v1 = match estático (type/subType/attr/level/cid).
- Match por **arquétipo** (CARD_Same/linhagem). v1 não expande por arquétipo.
- Relíquias com **trigger ativo** (efeito ao ativar, não contínuo). v1 só
  modifiers + buffs passivos.
- Remover/trocar relíquia (eventos de "perder relíquia"). v2.
- `level` afetar deckbuilding FORA do duelo. v1 só dentro do duelo (dentro, `permanent`
  já afeta o tributo via `FUN_180095af0`).
- **atk/def off-field** (mostrar/aplicar fora do campo). O engine não computa atk/def
  efetivo off-field; o base upgrade (patch na tabela `DAT_1811adbe8`) perderia o azul
  (`eff == org`) e é global por cid → **descartado**. atk/def fica field-only com azul.

## 14. Plano de teste (manual, in-game)

1. `Relics.json` com 2-3 relíquias (uma só-buff field, uma só-modifier, uma
   permanent que mexe em estrelas).
2. `rgrelic give dragon_soul` → entra num duelo → Dragões do player com +300 ATK
   azul **no campo**; não-Dragões intactos. (atk/def é sempre field-only; na mão fica
   o ATK impresso.)
3. `timing:damage` → buff só aparece no cálculo de dano (declara ataque).
4. Encounter com `relics:["dragon_soul"]` → inimigo entra com os Dragões dele
   buffados; ícone aparece no node detail.
5. Encounter com `relicDrop` → vencer → `addRelic` dispara (specific/random),
   ícone aparece na barra do player; persiste no `roguelike.json`.
6. Action `addRelic` num evento (Encounters.json) → testa specific + random+chance.
7. Reentrar a run → inventário e buffs reproduzem.

## 15. Fases de build

1. **Dados + motor de buff**: `Relics.json` + loader; `RoguelikeStatBuff` vira
   motor de regras (match/scope/timing); comando `rgrelic`. Testável isolado.
2. **Aplicar no duelo**: `RoguelikeRun.Relics` + `Encounter.relics`; baking
   server→client (modifiers no Merge + regras de buff no payload).
3. **Aquisição**: action `addRelic` + `Encounter.relicDrop` + `dropConfig`.
4. **UI**: barra de ícones do player + relíquias do inimigo no node detail.

## 16. Resumo das mudanças por arquivo

**Server (novo)**
- `Roguelike/RoguelikeRelics.cs` — modelo + loader do `Relics.json` (relic +
  dropConfig), sorteio (reusa CardPool), gating act/floor/ascension.

**Server (modificado)**
- `Roguelike/RoguelikeRun.cs` — campo `Relics` + serialização + `AddRelic`.
- `Roguelike/RoguelikeEncounters.cs` — parse de `relics` + `relicDrop`.
- `Roguelike/RoguelikeActionEngine.cs` — `kind: "addRelic"` (specific/random/chance).
- `Roguelike/GameServer.Roguelike.cs` (ou onde o duelo é assado) — junta as
  camadas de modifiers das relíquias no `Merge`; compila os buffs em regras
  (com side) e anexa no payload de duelo; projeta metadados de relíquia
  (name/icon/text) na run pro client.
- vitória de duelo — rola `relicDrop`/`dropConfig`.

**Client (novo)**
- nenhum arquivo novo obrigatório (motor mora no `RoguelikeStatBuff`); UI pode
  reusar telas existentes.

**Client (modificado)**
- `Roguelike/RoguelikeStatBuff.cs` — vira motor de regras (`Rule`, `SetRules`,
  `Evaluate`), resolve `subType` por card-data.
- `DuelDll.cs` (hook `FUN_1800b0000`) + `DuelDll.ProxyFunctions.cs` (hook
  `DLL_DuelGetCardBasicVal`) — chamam `Evaluate` em vez do buff flat; aplicam
  delta de `level` também.
- `ConsoleHelper.cs` — comando `rgrelic`.
- carregamento do duelo (client) — `SetRules` no início, `Clear` no fim.
- `Roguelike/RoguelikeMapScreen.cs` / `RoguelikeRunScreen.cs` — barra de ícones.
- node detail drawer — relíquias do inimigo.

**Config / dados**
- `DataLE/Roguelike/Relics.json` — novo (relíquias + dropConfig).
- `DataLE/Roguelike/Encounters.json` — exemplos com `relics`/`relicDrop` (testes).
- `Docs/config/roguelike/relics.md` — doc de configuração (formato do JSON).
