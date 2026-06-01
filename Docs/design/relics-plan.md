# Relíquias — Implementation Plan (Fase 1: motor de buff)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) ou superpowers:executing-plans pra implementar task-a-task. Steps usam checkbox (`- [ ]`).

**Goal:** Transformar `RoguelikeStatBuff` (hoje um buff dev flat) num **motor de regras** que avalia buffs por-card (match type/subType/attr/level/cid → atk/def/level, com scope field/permanent e timing always/damage), ligado aos dois hooks já mapeados, testável in-game pelo comando `rgbuff` — **sem** depender ainda do server/JSON.

**Architecture:** `RoguelikeStatBuff` guarda uma `List<Rule>` (setada via `SetRules`/comando dev). Os hooks `FUN_1800b0000` (campo, locate 0-6) e `DLL_DuelGetCardBasicVal` (indexado, pos≥7) chamam `Evaluate(...)` passando o contexto (cid/type/attr/level/side/location/timing) e somam os deltas retornados. `subType` resolve por cid via CARD_Prop (cache local). Spec: `Docs/design/relics-design.md` §5, §10.

**Tech Stack:** C# (.NET Framework do mod), MinHook (`Hook<T>`), `Marshal` pra ler a struct de stats, MSBuild VS 2022. Sem server nesta fase.

---

## File Map

**Client — modifica:**
- `YgoMasterClient/Roguelike/RoguelikeStatBuff.cs` — vira o motor de regras (`Rule`, `SetRules`, `Clear`, `Evaluate`, resolução de `subType`).
- `YgoMasterClient/DuelDll.cs` — hook `FUN_1800b0000`: troca `TryGet` por `Evaluate` (location=field, timing por flag), aplica delta de `level`.
- `YgoMasterClient/DuelDll.ProxyFunctions.cs` — hook `DLL_DuelGetCardBasicVal`: troca `TryGet` por `Evaluate` (location=permanent), aplica delta de `level`.
- `YgoMasterClient/ConsoleHelper.cs` — comando `rgbuff` vira/ganha `rgbuff` (injeta regras de teste no `SetRules`).

Nenhum arquivo novo. Sem mudanças de server/dados nesta fase.

---

## Operational notes

**Build do client (Git Bash, já usado nesta sessão):**
```bash
cd "/d/www/ygomaster-fork/YgoMasterRogueLike" && "/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" YgoMasterClient.csproj -t:Build -p:Configuration=Release -v:minimal -nologo 2>&1 | grep -iE "error|Copied" | tail -15
```
Espera no fim: `[Goat] Copied YgoMasterClient.exe -> ...YgoMasterLE - Goat`. Reiniciar o client carrega o exe novo.

**Struct de stats** (compartilhada pelos dois hooks; `short*`/`PvpBasicVal`):
`[0]=cid`, `+4=eff ATK`, `+8=eff DEF`, `+12=org ATK`, `+16=org DEF`, `+20=type(raça)`, `+22=attr`, `+26=level`. `DLL_DuelGetCardBasicVal` usa `PvpBasicVal` (campos `Atk/Def/Type/Attr/Level/EffectID`).

**Discriminador de action = `type`** (não `kind`) — só relevante nas fases 2+.

**Commits**: `feat(roguelike): ...` / `refactor(roguelike): ...`; 1 commit por task; sem co-author.

---

## Task 1: `RoguelikeStatBuff` vira motor de regras

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeStatBuff.cs`

- [ ] **Step 1: Reescrever a classe pro modelo de regras**

Substituir o conteúdo (mantendo namespace `YgoMasterClient`) por:
```csharp
using System.Collections.Generic;

namespace YgoMasterClient
{
    // Runtime ATK/DEF/level buffs avaliados por-card via os hooks de stat. Cada Rule casa um card
    // por type(raça)/subType/attr/level/cid e soma deltas. scope decide quais hooks aplicam; timing
    // restringe a "só no cálculo de dano". side decide se buffa cards meus ou do oponente.
    static class RoguelikeStatBuff
    {
        public class Rule
        {
            public bool Mine = true;       // side: true = meus cards, false = do oponente
            // match (null = ignora o eixo)
            public int? Type, Attr, Level, Cid;
            public string SubType;         // "normal"/"effect"/"fusion"/... ; null = ignora
            // escopo / timing
            public bool Permanent;         // false = só campo; true = campo + mão/deck
            public bool DamageOnly;        // true = só no contexto de dano (flag bit3)
            // efeito
            public int Atk, Def, LevelDelta;
        }

        static readonly List<Rule> Rules = new List<Rule>();

        public static bool HasAny { get { return Rules.Count > 0; } }

        public static void SetRules(IEnumerable<Rule> rules)
        {
            Rules.Clear();
            if (rules != null) Rules.AddRange(rules);
        }

        public static void Clear() { Rules.Clear(); }

        // Avaliado pelos hooks. permanentLocation: card está em mão/deck (não campo). damageContext:
        // a chamada é do passo de dano (flag bit3). Soma atk/def/level das regras que casam.
        public static bool Evaluate(int cid, int type, int attr, int level, bool mine,
                                    bool permanentLocation, bool damageContext,
                                    out int atk, out int def, out int levelDelta)
        {
            atk = 0; def = 0; levelDelta = 0;
            if (Rules.Count == 0) return false;
            for (int i = 0; i < Rules.Count; i++)
            {
                Rule r = Rules[i];
                if (r.Mine != mine) continue;
                if (permanentLocation && !r.Permanent) continue;   // mão/deck: só regras permanentes
                if (r.DamageOnly && !damageContext) continue;
                if (r.Cid.HasValue && r.Cid.Value != cid) continue;
                if (r.Type.HasValue && r.Type.Value != type) continue;
                if (r.Attr.HasValue && r.Attr.Value != attr) continue;
                if (r.Level.HasValue && r.Level.Value != level) continue;
                if (r.SubType != null && !SubTypeMatches(cid, r.SubType)) continue;
                atk += r.Atk; def += r.Def; levelDelta += r.LevelDelta;
            }
            return atk != 0 || def != 0 || levelDelta != 0;
        }

        // subType (categoria do card) não vem na struct de stats — resolve por cid via CARD_Prop.
        // STUB (Task 4): por enquanto retorna true (ignora o filtro). Implementado quando o accessor
        // de CardProp estiver confirmado.
        static bool SubTypeMatches(int cid, string subType) { return true; }
    }
}
```

- [ ] **Step 2: Build (vai quebrar — esperado)**

Build do client. **Esperado: FALHA** em `DuelDll.cs` / `DuelDll.ProxyFunctions.cs` / `ConsoleHelper.cs` porque `TryGet`/`TestAtk`/`SetCard`/`SetTest` sumiram. Isso confirma os call-sites a migrar (Tasks 2-5). Não commitar ainda.

---

## Task 2: Hook de campo (`FUN_1800b0000`) chama `Evaluate`

**Files:**
- Modify: `YgoMasterClient/DuelDll.cs`

- [ ] **Step 1: Trocar o bloco de buff do detour `DuelGetFieldCardVal`**

O bloco que hoje faz `TryGet(cid, mine, out atk, out def)` + escreve em offsets 4/8 passa a chamar `Evaluate` e aplicar atk/def **e level**. Substituir a partir do `int atk, def;` até o fim da aplicação por:
```csharp
            int type = Marshal.ReadInt16(outVal, 20);
            int attr = Marshal.ReadInt16(outVal, 22);
            int level = Marshal.ReadInt16(outVal, 26);
            bool mine = (player & 1) == (uint)MyID;
            bool damageContext = (flags & 8) != 0;     // bit3 = contexto raw/dano
            int atk, def, levelDelta;
            if (!RoguelikeStatBuff.Evaluate(cid, type, attr, level, mine,
                                            /*permanentLocation*/ false, damageContext,
                                            out atk, out def, out levelDelta))
            {
                return;
            }
            // mirror de batalha: o valor já vem buffado da foto -> não re-somar (ver IsBattleMirrorCombatant)
            bool mirrorRead = (flags & 8) == 0 && IsBattleMirrorCombatant(player, zone);
            if (!mirrorRead)
            {
                if (atk != 0) Marshal.WriteInt32(outVal, 4, Marshal.ReadInt32(outVal, 4) + atk);
                if (def != 0) Marshal.WriteInt32(outVal, 8, Marshal.ReadInt32(outVal, 8) + def);
                if (levelDelta != 0) Marshal.WriteInt16(outVal, 26, (short)(level + levelDelta));
            }
```
(O `RoguelikeStatBuff.HasAny` no early-return de cima continua válido. O log dev `rgbuff log` pode ser mantido ou simplificado — opcional.)

- [ ] **Step 2: Build** — `DuelDll.cs` deve compilar; ainda falha em ProxyFunctions/ConsoleHelper. OK seguir.

---

## Task 3: Hook indexado (`DLL_DuelGetCardBasicVal`) chama `Evaluate`

**Files:**
- Modify: `YgoMasterClient/DuelDll.ProxyFunctions.cs`

- [ ] **Step 1: Trocar o bloco de buff da mão**

No `else` do `DLL_DuelGetCardBasicVal`, substituir o `TryGet` por `Evaluate` com `permanentLocation:true`:
```csharp
                if (RoguelikeStatBuff.HasAny && (pos >= 7 || index != 0))
                {
                    bool mine = (player & 1) == MyID;
                    int hAtk, hDef, hLvl;
                    if (RoguelikeStatBuff.Evaluate((ushort)pVal.EffectID, pVal.Type, pVal.Attr, pVal.Level,
                                                   mine, /*permanentLocation*/ true, /*damageContext*/ false,
                                                   out hAtk, out hDef, out hLvl))
                    {
                        pVal.Atk += hAtk;
                        pVal.Def += hDef;
                        if (hLvl != 0) pVal.Level = (short)(pVal.Level + hLvl);
                    }
                }
```

- [ ] **Step 2: Build** — server-side hooks compilam; ainda falha em ConsoleHelper. OK.

---

## Task 4: Resolver `subType` por CARD_Prop

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeStatBuff.cs`

- [ ] **Step 1: Implementar `SubTypeMatches` (read-first)**

Ler como o client acessa CARD_Prop (ex.: `DuelDll.CardPropMem`, `DuelDll.CardIsLegend` que lê bits do PropB). Implementar `SubTypeMatches(cid, subType)`: lê os bits de cardtype do CARD_Prop do `cid`, mapeia pra `"normal"/"effect"/"ritual"/"fusion"/"synchro"/"xyz"/"link"/"pendulum"`, compara (case-insensitive), com **cache** `Dictionary<int,string>` por cid. Se o accessor não estiver disponível em duelo, manter o stub `return true` e anotar como limitação (subType só funciona quando CardProp está carregado).

- [ ] **Step 2: Build** — compila (ConsoleHelper ainda pendente).

---

## Task 5: Comando dev `rgbuff`

**Files:**
- Modify: `YgoMasterClient/ConsoleHelper.cs`

- [ ] **Step 1: Estender o handler `rgbuff` pro motor de regras**

Migrar o bloco de comando atual pra `rgbuff`, injetando `Rule`s no `SetRules`. Suporte mínimo:
```
rgbuff clear                                  -> SetRules([])
rgbuff add mine|foe atk <n> [type <t>]        -> add Rule {Mine, Atk=n, Type?, scope=field, timing=always}
rgbuff add mine|foe def <n> [type <t>]
rgbuff add mine|foe lvl <n> [type <t>]
rgbuff add ... permanent                       -> Permanent=true
rgbuff add ... damage                          -> DamageOnly=true
rgbuff list                                    -> imprime as regras ativas
```
Acumula numa `List<Rule>` estática no ConsoleHelper e chama `RoguelikeStatBuff.SetRules(list)` a cada `add`/`clear`. Parsing terso (split por espaço), igual ao `rgbuff` de hoje.

- [ ] **Step 2: Build — deve PASSAR e copiar**

Build do client. Espera `[Goat] Copied ...`. Todos os call-sites migrados.

- [ ] **Step 3: Commit**
```
feat(roguelike): RoguelikeStatBuff vira motor de regras de buff (Fase 1 relíquias)
```

---

## Verificação (manual, in-game) — fecha a Fase 1

Reiniciar o client e, num duelo:

1. `rgbuff add mine atk 300 type <raça-do-seu-monstro>` → só os monstros daquela raça ficam +300 azul no campo; outros intactos. (descobre a raça pelo `rgbuff log` antigo ou por tentativa)
2. `rgbuff clear` → some o buff.
3. `rgbuff add mine atk 500` (sem type) → todos os seus monstros +500. Confirma: sem double no ataque, sem flicker nos não-combatentes, dano correto (regressão da base).
4. `rgbuff add mine atk 300 permanent` → o +300 aparece também na **mão**. Sem `permanent` → mão intacta.
5. `rgbuff add mine atk 300 damage` → +300 só durante o cálculo de dano (declara ataque).
6. `rgbuff add mine lvl 1 type <raça>` → estrelas daquela raça sobem 1 (confere no display do card).
7. `rgbuff add foe atk -500` → monstros do **oponente** -500.

Passou tudo → Fase 1 fechada. Próximo: escrever `relics-plan-2.md` (dados server + bake server→client), lendo `RoguelikeActionEngine`, o ponto de bake do duelo, e `RoguelikeApi`.

---

## Fases seguintes (planos próprios, após a Fase 1)

- **Fase 2 — Dados + estado**: `RoguelikeRelics.cs` (modelo+loader do `Relics.json`), `RoguelikeRun.Relics`, `Encounter.relics`/`relicDrop`.
- **Fase 3 — Bake server→client**: modifiers das relíquias no `Merge`; compilar `buffs`→`Rule`s (com side) e anexar no payload de início de duelo; `SetRules` no client ao entrar, `Clear` ao sair.
- **Fase 4 — Aquisição**: action `type:"addRelic"` (specific/random/chance), sorteio via CardPool com gating act/floor/ascension, `relicDrop` na vitória, `dropConfig` global.
- **Fase 5 — UI**: barra de ícones do player + relíquias do inimigo no node detail drawer.

Cada uma vira um `Docs/design/relics-plan-N.md` quando chegarmos nela (precisa ler os arquivos-alvo pra dar código exato).
