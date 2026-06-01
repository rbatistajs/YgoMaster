# Roguelike — duelHooks (relíquia via Lua) — design + estado

## 1. Objetivo

Dar às relíquias **efeitos via scripts Lua** (MoonSharp), pendurados em **hooks**.
O primeiro hook implementado é o **`buff`** (substitui o antigo `buffs`
declarativo); os hooks de **evento** (`onSummon`/`onAttack`/…) vêm depois.

Exemplo-alvo (benchmark do design, futuro — depende dos eventos + ações):

> Quando invocar um monstro **normal Dragão**, olhe o topo do deck; se for
> **normal Dragão nível 4**, adiciona à mão; senão embaralha. (peek + condição +
> branch — Lua resolve com a própria linguagem.)

## 2. As camadas da relíquia

| camada | o quê | onde roda |
|---|---|---|
| `modifiers` | board-spec (começar com X monstro, LP…) | server → cmds |
| **`scripts` (hooks Lua)** | **buff (query) + eventos (efeito ativo)** | **client (thread do duelo)** |

O antigo `buffs` declarativo (regras `match→delta`) foi **removido**: buff agora
é `on("buff", …)` em Lua. `modifiers` continua server-side, é outra camada.

## 3. Estado da implementação

**Pronto (validado in-game):**
- Engine **MoonSharp** sandboxed (`RoguelikeLua`) + API de leitura + `card_props`.
- Classe de hooks **`RoguelikeDuelHooks`** (`on`/`EvalBuff`/`Fire`/`Clear`).
- Accessor **`RoguelikeCardProps`** (cid → props estáticas).
- Hook **`buff`**: `DuelGetFieldCardVal` chama `EvalBuff` por-card e aplica os
  deltas **com a regra do mirror** (anti-double no ataque — ver
  `duel-action-primitives.md`).
- Dev: `rghook load/clear/list`, `rglua`, `rgcardprops`.

**Futuro:**
- **Eventos** (`onSummon`/`onAttack`/…): reintroduzir a detecção de intenção
  (campos de comando no `DLL_DuelSysAct`, que pega o CPU) e ligar no `Fire`. §11.
- **Ações** na API Lua (summon/damage/heal/draw/destroy…). §9.
- **Server→client**: relíquia declara `scripts`, server manda no payload. §12.

## 4. Arquitetura

- **`RoguelikeLua`** — runtime: `Script` MoonSharp sandboxed (carregado uma vez),
  API global (leitura + `on` + `card_props`), `Call(fn,ctx,params)`,
  `EvalFieldBuff(...)` (ponte do hook de campo), `LoadScript(file,paramsJson)`.
- **`RoguelikeDuelHooks`** — registro `nome → [(fn, params)]`; `EvalBuff` (query),
  `Fire` (evento), `Clear`. As chamadas Lua passam pelo `RoguelikeLua`.
- **`RoguelikeCardProps`** — `cid → { race, attr, level, atk, def, subType,
  frame, kind, icon }`, lido do `YdkHelper.GameCardInfo` (CARD_Prop), cacheado.

## 5. Schema no `Relics.json`

```jsonc
"dragon_soul": {
  "name": "Alma do Dragão", "icon": "card_...", "text": "...", "rarity": "SR",
  "act": { "min": 1 },                                  // gating (server)
  "modifiers": { "player": { /* board-spec */ } },      // server-side, outra camada
  "scripts": [
    { "file": "race_buff.lua", "params": { "race": 18, "atk": 300 } }
  ]
}
```

- `scripts`: lista de `{ file, params }`. A relíquia só **aponta** o script; o
  script se pendura nos hooks via `on(...)`. `params` é dict livre (JSON).
- Reuso: um `.lua` genérico serve várias relíquias com `params` diferentes.

## 6. Registro: `on(name, fn)`

O script se registra num hook por **nome** (string, extensível):

```lua
on("buff", function(c, params) ... end)
```

- O `params` da relíquia (ativo na carga) é **capturado** e passado de volta como
  **2º argumento** `fn(ctx, params)` no dispatch — sem global, reuso limpo.
- Um script pode se pendurar em vários hooks (`on("buff",…)`, `on("summon",…)`).

## 7. ctx (live) vs `card_props` (estático)

Princípio para **todos** os hooks:

| origem | o que | exemplo |
|---|---|---|
| **ctx** | valores **da instância (live)** — só o state sabe, já com efeitos | `cid, zone, mine, race, attr, level, atk, def` (atuais) |
| **`card_props(c.cid)`** | **categoria/base estática** por cid | `subtype, frame, kind, icon` + stats impressos |

- `c.race`/`c.attr`/`c.level`/`c.atk`/`c.def` vêm direto do `outVal` do
  `DuelGetFieldCardVal` → **já alterados** por efeito (ex.: DNA Surgery muda
  `race`; modificador muda `level`). Use-os para "tipo/atributo/nível atual".
- `card_props(c.cid)` dá o que **não** está no state e **não** muda: a categoria
  (`subtype`/`frame`/`kind`/`icon`) + os stats impressos. Use para "é normal?",
  "é Tuner?".

## 8. Hook `buff` (implementado)

`on("buff", fn)` — **query por-card**. O `DuelGetFieldCardVal` (C#) chama, para
cada carta no campo (zona 0-6), todos os callbacks `buff`; cada um retorna
`{ atk?, def?, level? }` (deltas) ou `nil`; a soma é aplicada no `outVal`
**exceto** na leitura do mirror de batalha (anti-double). O Lua só decide os
números; a aplicação correta (mirror) fica no C#.

```lua
-- race_buff.lua
on("buff", function(c, params)
  if c.mine and c.race == params.race then
    return { atk = params.atk or 0, def = params.def or 0, level = params.level or 0 }
  end
end)
```

## 9. API Lua

**Leitura (pronto)**
- `card_props(cid)` → `{ cid, race, attr, level, atk, def, subtype, frame, kind, icon }` ou nil.
- `deck_top(p)` → cid (ou nil); `hand_count/deck_count/grave_count/extra_count/banish_count(p)` → int.
- `on(name, fn)`; `log(x)`.

**Ações (futuro)** — mapeiam pras primitivas de `duel-action-primitives.md`:
`summon_from_deck/grave`, `to_hand`, `shuffle_deck`, `draw`, `damage`, `heal`,
`to_grave`, `destroy`. (Special-summon foi removido do código até resolver o
mid-attack — ver a "Receita C#" no doc de primitivas.)

`location` na API será **nome** (`"deck"`/`"hand"`/`"grave"`/`"banish"`) mapeado
pros codes; `zone`/`index` inteiros.

## 10. Engine MoonSharp

- Lua 5.2 puro em C# (gerenciado), `lib/MoonSharp/MoonSharp.Interpreter.dll`
  (net40), compatível com o client .NET 4.8.
- **Sandbox** `Preset_HardSandbox` (sem `io`/`os`/`require`); só a API exposta.
- Um `Script` compartilhado, criado uma vez (estado persiste entre `rglua`).
- **Unity-loader noise**: o MoonSharp auto-detecta Unity e constrói um
  `UnityAssetsScriptLoader` cujo reflection sobre Resources lança (capturado) sob
  IL2CPP. A gente fixa um `FileSystemScriptLoader` e **suprime o ruído**
  redirecionando o `Console` na 1ª criação do `Script`.

## 11. Eventos (futuro)

Os hooks de evento (`onSummon`/`onAttack`/…) usarão o **barramento de intenção**:
campos de comando do `duelState` (`0x3cf8` cmd…) lidos no `DLL_DuelSysAct` —
**pegam o CPU** (a IA escreve o estado direto). Catálogo de `cmd` empírico em
`duel-action-primitives.md` (`0`=attack, `3`=activate, `4`=summon, `6`=set,
`13`=draw, `17`=battle). O dispatch chama `Fire(name, ctx)` na borda do `cmd`;
o ctx de evento será magro (`cid, player, zone`), props via `card_props`.

## 12. Server → client (futuro)

Server coleta os `scripts` das relíquias ativas (player+enemy), marca o `owner`
(id 0/1) e manda no payload do duelo; client `LoadScript` no início e `Clear` no
fim. Mesmo canal que os buffs antigos usariam.

## 13. Determinismo / RNG

RNG no script é **livre** (`math.random`): o duelo é jogado ao vivo, não
reproduzido pela run. O estado da run (inventário) continua determinístico.

## 14. Dev commands

- `rghook load <file.lua> [params-json]` / `rghook clear` / `rghook list`.
- `rglua <code>` (API disponível) / `rglua file <name.lua>`.
- `rgcardprops <cid>` (dump das props estáticas).

## 15. Arquivos

**Client (novo):** `Roguelike/RoguelikeLua.cs`, `Roguelike/RoguelikeDuelHooks.cs`,
`Roguelike/RoguelikeCardProps.cs`; `lib/MoonSharp/MoonSharp.Interpreter.dll` +
ref/cópia no `YgoMasterClient.csproj`; `DataLE/Roguelike/Scripts/*.lua`.

**Client (modificado):** `DuelDll.cs` (`DuelGetFieldCardVal` → `EvalFieldBuff`
com a regra do mirror; expõe `DuelLibBase`); `ConsoleHelper.cs` (`rghook`/`rglua`/
`rgcardprops`). `RoguelikeStatBuff.cs` (buff C# antigo) **removido**.

**Futuro:** `RoguelikeRelics.cs` (parse de `scripts`), baking server→client,
`Docs/config/roguelike/duelhooks.md` (config).
