# Stat actions (`lp` / `gold`) — design

## Objetivo

Adicionar duas actions novas ao `RoguelikeActionEngine` que aplicam delta
em stats de run (LP e gold), com animação visual no client (floating
label central deslizando até a HUD + tween de contador no HUD).

Essas actions são pré-requisito pra implementar non-combat nodes (event,
shop, reward, treasure, rest, etc) — sem actions ricas, esses nodes
ficariam limitados a `options`/`message`/`openpack` (que só dão/escolhem
cartas).

## Princípios

- **Server-authoritative.** Server aplica delta no `RoguelikeRun`,
  clampa, decide se mata. Cliente só anima.
- **Sem ack do player.** Stat actions são side-effects no pump loop —
  não pausam o engine. `pendingAction` continua sendo "engine pausou,
  cliente PRECISA agir"; stat actions não se encaixam nesse uso.
- **Animação por diff client-side.** Cliente compara snapshot de
  `(lp, gold)` entre `WriteRun` ticks, anima a diferença. Mesmo path
  serve pra mudança pós-combat (LP cai depois do duel).
- **YAGNI.** 2 actions discretas, schemas explícitos, sem abstração de
  "stat genérico" (vira útil só com 3+ stats com regras semelhantes).

## Hoje (baseline)

- `RoguelikeActionEngine` roda `options` / `message` / `openpack`. Cada
  uma pausa via `PendingAction` (cliente ack pra avançar).
- `RoguelikeRun` tem `Lp` (int) e `Currency` (int — gold acumulado
  por vitórias).
- HUD do `RoguelikeMapScreen` reusa slot `HeaderButtonGroup.DeckNum`
  pra mostrar `"LP X / Y"`. Gold não é exibido.
- LP muda hoje só via `SendDuelResult` (post-combat). Sem animação —
  número snap pro novo valor.

## Schema

### Action `lp`

```json
{ "type": "lp",
  "delta": -500,                 // OPCIONAL — int absoluto
  "delta_percent": -0.30 }       // OPCIONAL — fração do MaxLp (-1.0..+1.0)
```

- **XOR** dos dois campos: exatamente um deve estar presente. Ambos
  ausentes ou ambos presentes → warn no loader + drop da action.
- `delta_percent` aplicado sobre `MaxLp` (não sobre LP atual).
  Ex.: `-0.30` num `maxLp = 8000` = `-2400`. Round half-even pra int.
- `delta_percent` fora de `[-1, 1]` → warn + clamp ao range.
- **Clamp superior:** `min(novo, MaxLp)` — heal não estoura max.
- **Clamp inferior:** `0`. Se `novo <= 0`: game over (mesmo path da
  derrota em combat — reusa `run.IsDead()` / state já existente).
- Sem ack do player; engine roda direto e vai pro `next` (se houver).
- Campo `next` (já suportado pelo engine) funciona normal.

### Action `gold`

```json
{ "type": "gold",
  "delta": +200,                 // OPCIONAL
  "delta_percent": -0.20 }       // OPCIONAL — fração do gold ATUAL
```

- Mesma estrutura XOR.
- `delta_percent` é fração do **gold atual** (não tem maxGold). Ex.:
  gold=1000, `delta_percent=-0.20` = `-200`.
- **Clamp inferior:** `0`. Se delta exigir mais do que tem, clampa em 0;
  **não bloqueia** a action; sem game over.
- **Sem clamp superior.**
- Sem ack, sem game over.

## Server

### `RoguelikeActionEngine`

Dois `Step` novos paralelos a `StepMessage` / `StepOpenPack`:

- `ParseLp(node)`, `ParseGold(node)` — valida XOR, retorna node tipado.
  Type field aceito no loader: `"lp"`, `"gold"`.
- `StepLp(node)`, `StepGold(node)` — aplica delta na run, clampa,
  decide game over (LP), avança pra `next`. Não cria `PendingAction`.
- Pump loop existente (que já encadeia até bater num node de input)
  funciona inalterado.

### `RoguelikeRun`

Helpers novos:

```csharp
// Retorna (newLp, killed). 'killed' = true se newLp <= 0.
public (int, bool) ApplyLpDelta(int? absDelta, double? pctDelta);

// Retorna newGold.
public int ApplyGoldDelta(int? absDelta, double? pctDelta);
```

Sem campo `PendingGameOver` novo — game over por LP=0 marca state já
existente (mesma path da derrota em combat).

### `WriteRun` wire + disk format

- **Renomear** `currency` → `gold` no `ToDictionary()`/`FromDictionary()`.
  Field interno `Currency` mantém o nome.
- `FromDictionary` lê `gold` primeiro, fallback pra `currency` (back-compat
  pra `roguelike.json` antigos no disco).
- **Não emitir** delta/lastDeltas — animação é diff client-side.
- Sem campo `pendingAction` pra lp/gold.

### `RoguelikeEncounters.ParseAction`

- Aceita `"lp"` e `"gold"` como `type` válido.
- Valida XOR no parse; warn + drop se inválido.
- Valida `delta_percent` range; warn + clamp.
- Campo `next` parseado como já é hoje.

### Encadeamento

Engine pump roda múltiplos steps até bater num node com ack. Sequência
`message → lp → message` faz:

1. Pausa no 1º message (PendingAction)
2. Player ack
3. Aplica lp
4. Avança pra `next` = 2º message
5. Pausa no 2º message (PendingAction)

Diff de state entre passos 2 e 5 cobre o lp da animação.

Standalone (action `lp` direto no encounter, sem message antes) funciona
do mesmo jeito — só não tem pausa nenhuma; cliente diff e anima.

## Client

### HUD (`RoguelikeMapScreen`)

**Novo label `GoldNum`:**

- Clonar GO `HeaderButtonGroup.DeckNum`, renomear root pra `GoldNum`,
  manter estrutura interna (TMP child).
- Posicionar à direita do `DeckNum` (offset X positivo); ajuste fino
  depois de rodar.
- Const `HeaderGold = "HeaderButtonGroup.GoldNum.TextGoldNumValue"`.
- Cache no field `_goldLabelGo` — clone só roda 1x via `EnsureGoldLabel()`
  no `OnCreatedView`.
- Esconder quando `!RoguelikeApi.IsRunActive`.

**Label format** (`RoguelikeLabels`):
```
"map.gold" = "GOLD {0}"      // texto puro; ícone depois
"map.lp"   = "LP {0} / {1}"  // já existe
```

**`RoguelikeApi`:**
- Getter novo `Gold()` lê `$.Roguelike.run.gold`.

### Animação

**Diff detection:**
- `RoguelikeMapScreen` mantém `_lastHudSnapshot { int Lp, int Gold }`
  (default `null`).
- A cada `Update`, compara com `(RoguelikeApi.Lp(), RoguelikeApi.Gold())`.
- Se snapshot `null` → adota baseline, **não anima** (primeira carga).
- Senão, pra cada stat com `delta != 0`: enfileira `FloatingDelta` +
  `HudCounter`.

**`FloatingDelta` (label central que desliza pra HUD):**

```csharp
class FloatingDelta {
    IntPtr Go;            // TMP clone parented no canvas root
    Vector3 StartPos;     // screen center (+offset Y por stat)
    Vector3 EndPos;       // world pos do label HUD correspondente
    float T;              // 0..1
    const float Duration = 0.7f;
    Color Color;          // verde (#4ADE80) se delta>0, vermelho (#F87171) se <0
    int TargetStat;       // 0=lp 1=gold (pra ativar HudCounter no final)
}
```

- Spawn: clona GO do `HeaderLp` ou `HeaderGold`, reparent no `Canvas`
  root, posiciona em screen center, set TMP text = `"+500 LP"` /
  `"-200 GOLD"`, set cor por sinal.
- Update loop: `T += dt / Duration`,
  `pos = Lerp(Start, End, EaseOutCubic(T))`, `alpha = 1 - T` (fade),
  `scale = Lerp(1.4f, 1.0f, T)` (encolhe enquanto vai).
- Quando `T >= 1`: `Destroy(Go)`, remove da lista, **dispara
  HudCounter correspondente**.

**`HudCounter` (label HUD anima de old → new):**

```csharp
class HudCounter {
    string LabelPath;     // HeaderLp ou HeaderGold
    int From, To;         // valores
    float T;
    const float Duration = 0.5f;
}
```

- Update: `T += dt / Duration`,
  `cur = (int)Math.Round(Lerp(From, To, EaseOutCubic(T)))`.
- `SetTmpText(LabelPath, Format(cur))` — pra LP formata com maxLp também.
- Quando `T >= 1`: snap `cur = To`, remove da lista.

**Sequencing por stat:**

Server roda action → state muda → diff detecta → spawn FloatingDelta +
cria HudCounter pendente **mas não inicia** → FloatingDelta termina
(T=1) → ativa HudCounter → HUD anima até valor novo.

Garante que HUD não atualiza ANTES do floater chegar (parece "snap").

**Múltiplos diffs simultâneos** (ex.: `lp -300 + gold +200` no mesmo
tick): 2 floaters spawnam ao mesmo tempo, no centro, com offset Y
(`±60px`): LP em cima, GOLD embaixo. Constantes simples no MapScreen.

**Múltiplos diffs em sequência** (ex.: 2 ticks seguidos do mesmo stat):
novo floater spawna mesmo se anterior ainda no ar — convivem. Se já tem
`HudCounter` em andamento pro stat, novo counter **substitui o anterior
no estado**: `From = cur` (valor atual exibido), `To = novoValor`, `T = 0`.
Sem snap; o lerp continua da posição visual atual pro novo target.

**Cores (constantes no MapScreen):**
- Positive: `#4ADE80` (verde)
- Negative: `#F87171` (vermelho)

**Edge cases:**

- **Game over (LP → 0):** cliente recebe state `isDead`, mas modal de
  game over **aguarda** `_pendingFloaters.Count == 0 &&
  _hudCounters.Count == 0` antes de abrir. Wait simples no `Update`.
- **Hot-reload no meio:** snapshot reseta, animações em curso canceladas
  (`Destroy` em GOs pendentes), próximo diff = baseline → sem animação
  fantasma.
- **`IsRunActive == false`:** skip diff/spawn, esvazia listas, esconde
  GoldNum.

## Testing

Smokes em `Encounters.json` (`act: {min:99}` pra ficarem off-list, igual
`smoke_openpack_*`):

```json
{ "id": "smoke_lp_heal_absolute",  "name": "Smoke LP +500",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "lp", "delta": 500 } },

{ "id": "smoke_lp_damage_percent", "name": "Smoke LP -30%",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "lp", "delta_percent": -0.30 } },

{ "id": "smoke_gold_gain",         "name": "Smoke Gold +200",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "gold", "delta": 200 } },

{ "id": "smoke_lp_lethal",         "name": "Smoke LP lethal",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "lp", "delta": -99999 } },

{ "id": "smoke_chain_msg_lp_msg",  "name": "Smoke msg->lp->msg",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": {
    "type": "message", "title": "Antes", "message": "Vai perder 500 LP.",
    "next": { "type": "lp", "delta": -500,
      "next": { "type": "message", "title": "Depois", "message": "Perdeu." }
    }
  }},

{ "id": "smoke_multi_stat_burst",  "name": "Smoke lp+gold mesmo tick",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": {
    "type": "lp", "delta": -300,
    "next": { "type": "gold", "delta": 200,
      "next": { "type": "lp", "delta": -200 }
    }
  }}
```

Disparo via `rgencounter <id>` no console.

**Checks por smoke:**
- HUD anima
- Floating label cor correta + slide até HUD
- Chain encadeia direito (modal → animação → modal)
- Lethal abre game over depois do tween (não simultâneo)
- Multi-stat consolidado (1 floater por stat, não 3)

**Pós-combat regression:** rodar 1 duel normal e confirmar que LP cai
com mesma animação (mesmo path de diff).

## Docs

- `Docs/config/roguelike/encounters.md` — nova seção "Actions de stat
  (`lp` / `gold`)" depois de openpack, descrevendo schema XOR, clamping,
  game over, encadeamento.
- `Docs/config/roguelike/settings.md` — atualizar referência ao gold
  (`reward` sai do wire, `gold` entra).
- Sem doc novo separado (mantém tudo em `encounters.md`, igual openpack).

## Não-objetivos (out of scope)

- Tipos novos de node (`event`/`shop`/`reward`/`treasure`/`rest`) — essas
  actions são pré-requisito; nodes virão depois.
- Outras actions (`cardop` remove/upgrade/transform, `summonchest`,
  `riskreward`, etc).
- Sound effects, particle effects.
- Ícone visual no GOLD label (espaço reservado, sprite vem depois).
- Source-tagged labels ("Maldição: -1000 LP" em vez de "-1000 LP") —
  requer campo delta explícito no wire; refatora se precisar.
- Unit tests automatizados (codebase não tem framework server-side).
