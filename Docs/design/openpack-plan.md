# OpenPack Action — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adicionar a action `openpack` ao motor M5: server gera cartas (multi-pack, multi-pull, pity), cliente abre o `CardPackOpen` nativo, hook customiza o `Result` (modos `keep all` e `pick X de N`), `pack_finalize` commita ao pool/deck da run.

**Architecture:** Server-authoritative. ActionEngine entra em `openpack` → gera cards (via `RoguelikeCardPool.DrawN`), stage em `run.PendingPack`, projeta `$.Gacha` (shape vanilla) + `$.Roguelike.pendingPack`. Cliente vê via Pump no MapScreen, push do `CardPack/CardPackOpen`, hook em `CardPackOpenResultViewController.OnCreatedView` customiza UI e dispara `Roguelike.pack_finalize` no Confirmar. Pity vive em `run.Pity` (persistido), com bônus aditivo no `rarityRates` efetivo. Spec completa: `Docs/roguelike/openpack-design.md`.

**Tech Stack:** C# (.NET Framework do mod), MiniJSON do projeto, MinHook (Hook<T>) pra hooks IL2CPP, MSBuild VS 2022, ClientWork piggyback (`$.Roguelike`) pro contrato server↔cliente.

---

## File Map

**Server — modifica:**
- `YgoMasterServer/Roguelike/RoguelikeRun.cs` — campos `Pity`, `PendingPack` + serialização.
- `YgoMasterServer/Roguelike/RoguelikeCardPool.cs` — filtro `rarity`/`rarities` em `Matches`; `DrawN` helper; carregamento de `pity` do `CardPool.json`.
- `YgoMasterServer/Roguelike/RoguelikeModifiers.cs` — `Resolve` passa a chamar `DrawN` (refactor sem mudança de comportamento).
- `YgoMasterServer/Roguelike/RoguelikeEncounters.cs` — parsing/validação de action `kind: "openpack"`.
- `YgoMasterServer/Roguelike/Actions/RoguelikeActionEngine.cs` — Step do `openpack`.
- `YgoMasterServer/Roguelike/GameServer.Roguelike.cs` — `WriteRun` projeta `$.Gacha` + `pendingPack`; `Act_RoguelikePackFinalize`.
- `YgoMasterServer/GameServer.cs` — dispatch `Roguelike.pack_finalize`.

**Client — cria:**
- `YgoMasterClient/Roguelike/Actions/RoguelikePackDriver.cs`.
- `YgoMasterClient/Roguelike/Actions/RoguelikePackResultHook.cs`.

**Client — modifica:**
- `YgoMasterClient/Roguelike/RoguelikeApi.cs` — `PendingPack`/`GetPendingPack`/`PackFinalize`.
- `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs` — `Update()` chama `RoguelikePackDriver.Pump`.

**Config / dados — modifica** (install-only, fora do repo; cf. MEMORY: data files de `DataLE/Roguelike/` NÃO são versionados):
- `<install>/DataLE/Roguelike/CardPool.json` — bloco `pity` opcional.
- `<install>/DataLE/Roguelike/Encounters.json` — exemplos de action openpack pra smoke test.

Onde `<install>` = `D:\SteamLibrary\steamapps\common\Yu-Gi-Oh!  Master Duel\YgoMasterLE - Goat\` (a pasta do mod). Server runtime lê desses caminhos via `dataDirectory`. Edits no repo's `DataLE/` não têm efeito.

---

## Operational notes

**Build do client (PowerShell):**
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterClient.csproj" -nologo -v:minimal
```
Espera no fim: `[Goat] Copied YgoMasterClient.exe -> D:\SteamLibrary\steamapps\common\Yu-Gi-Oh!  Master Duel\YgoMasterLE - Goat`

**Build do server (PowerShell):**
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```

**Comando dev (já existe via ConsoleHelper):** `rgencounter <encounterId>` dispara a action de um encontro específico — usar pra smoke tests sem precisar andar pelo mapa.

**Commits**: estilo conventional (`feat(roguelike): ...`, `refactor(roguelike): ...`); sem co-author / atribuição AI. Cada task fecha em 1 commit.

**Correção descoberta na T5**: o discriminador de action node é o campo `type` (não `kind`). O design doc e exemplos JSON deste plano dizem `kind` em vários lugares — substituir mentalmente por `type` ao implementar. M5 já usava `type` para `options`/`message`; openpack segue o mesmo.

---

## Task 1: `RoguelikeRun` — campos `Pity` + `PendingPack`

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeRun.cs`

- [ ] **Step 1: Adicionar os 2 campos novos**

No bloco de campos da classe (após `public int ActionToken;`), adicionar:
```csharp
public Dictionary<string, int> Pity;            // {"UR":7, "SR":2} (por raridade); cresce sem cap no contador
public Dictionary<string, object> PendingPack;  // staging do pacote; null quando não há
```

- [ ] **Step 2: Serializar em `ToDictionary`**

Adicionar as duas entradas na dict (após `"actionToken"`):
```csharp
{ "pity",         Pity ?? new Dictionary<string, int>() },
{ "pendingPack",  PendingPack },
```

- [ ] **Step 3: Desserializar em `FromDictionary`**

Dentro do `return new RoguelikeRun { ... }`, adicionar após `ActionToken`:
```csharp
Pity         = ParsePity(Utils.GetValue<Dictionary<string, object>>(d, "pity")),
PendingPack  = Utils.GetValue<Dictionary<string, object>>(d, "pendingPack"),
```

E adicionar este helper estático na classe (perto do final, antes de `Save`):
```csharp
// "pity" vem como {string: object} no JSON; converte pra {string: int}.
static Dictionary<string, int> ParsePity(Dictionary<string, object> raw)
{
    Dictionary<string, int> r = new Dictionary<string, int>();
    if (raw == null) return r;
    foreach (KeyValuePair<string, object> kv in raw)
    {
        int v; try { v = Convert.ToInt32(kv.Value); } catch { continue; }
        r[kv.Key] = v;
    }
    return r;
}
```

- [ ] **Step 4: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add YgoMasterServer/Roguelike/RoguelikeRun.cs
git commit -m "feat(roguelike): run state for pity + pending pack"
```

---

## Task 2: `RoguelikeCardPool.Matches` — filtro hard de raridade

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeCardPool.cs`

- [ ] **Step 1: Estender `Matches` com checagem de raridade**

Localiza o `Matches` (atual fim em `return true;` após `PassesNumeric`). Adiciona o teste de raridade ANTES do `return true;` final:
```csharp
if (!PassesRarity(spec, cid, dataDirectory)) return false;
return true;
```

- [ ] **Step 2: Adicionar `PassesRarity` helper**

Adiciona um método estático perto dos outros `Passes*` helpers:
```csharp
// "rarity": "UR" / "rarities": ["SR","UR"]. None = no filter.
static bool PassesRarity(Dictionary<string, object> spec, int cid, string dataDirectory)
{
    if (spec == null) return true;
    HashSet<int> allowed = null;
    object single, multi;
    if (spec.TryGetValue("rarity", out single) && single != null)
    {
        int r = RarityKey(Convert.ToString(single));
        if (r > 0) { allowed = new HashSet<int>(); allowed.Add(r); }
    }
    if (spec.TryGetValue("rarities", out multi) && multi is List<object>)
    {
        if (allowed == null) allowed = new HashSet<int>();
        foreach (object o in (List<object>)multi)
        {
            int r = RarityKey(Convert.ToString(o));
            if (r > 0) allowed.Add(r);
        }
    }
    if (allowed == null || allowed.Count == 0) return true;
    int rarity;
    if (!CardListRarity(dataDirectory).TryGetValue(cid, out rarity)) return false;
    return allowed.Contains(rarity);
}
```

- [ ] **Step 3: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add YgoMasterServer/Roguelike/RoguelikeCardPool.cs
git commit -m "feat(roguelike): hard rarity filter in card pool Matches"
```

---

## Task 3: `RoguelikeCardPool.DrawN` — picker helper reutilizável

Refactor: extrai o miolo do `RoguelikeModifiers.Resolve` (BuildPool → Matches → Weight/uniform pick) pra um helper em `RoguelikeCardPool`, sem mudar o comportamento do `Resolve`. O `Resolve` passa a chamar `DrawN(spec, n=1)`.

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeCardPool.cs` (adiciona `DrawN` + `DrawResult`)
- Modify: `YgoMasterServer/Roguelike/RoguelikeModifiers.cs` (Resolve usa DrawN)

- [ ] **Step 1: Adicionar `DrawResult` + `DrawN` em `RoguelikeCardPool`**

Próximo aos outros helpers públicos (ex.: antes de `static void ParseRarityRates`):
```csharp
public class DrawResult
{
    public int Cid;
    public int Rarity;       // 1..4 do CardListRarity
    public bool IsNew;       // sempre true em roguelike (cliente decide visual)
    public int PremiumType;  // 0 em v1
}

// Sorteia `n` cids únicos (dentro do mesmo lote) que casem com `spec`.
// `pool` = universo (chamador define via AnyPool/Deck/Link; em modifiers/openpack).
// `used` = anti-duplicata (escope do chamador; DrawN adiciona o que pegou).
// `rarityRatesOverride` (opcional) = sobrescreve rarityRate por chave (merge no chamador).
// `weighted` = true pra usar Weight ponderado (sources any/link); false pra uniforme (deck).
// Retorna lista vazia se não der pra completar; cabe ao chamador validar.
public static List<DrawResult> DrawN(
    string dataDirectory,
    HashSet<int> pool,
    Dictionary<string, object> spec,
    int n,
    Random rng,
    int ascension,
    HashSet<int> used,
    Dictionary<int, double> rarityRatesOverride,   // null = usa global+asc
    bool weighted)
{
    List<DrawResult> result = new List<DrawResult>();
    if (pool == null || n <= 0 || rng == null) return result;

    List<int> cands = new List<int>();
    foreach (int cid in pool)
        if (!used.Contains(cid) && Matches(dataDirectory, cid, spec)) cands.Add(cid);
    if (cands.Count == 0) return result;

    for (int draw = 0; draw < n && cands.Count > 0; draw++)
    {
        int pickIdx;
        if (weighted)
        {
            double[] w = new double[cands.Count];
            double total = 0;
            for (int i = 0; i < cands.Count; i++)
            {
                w[i] = Math.Max(0, WeightWith(dataDirectory, cands[i], ascension, rarityRatesOverride));
                total += w[i];
            }
            if (total <= 0) break;
            double roll = rng.NextDouble() * total;
            pickIdx = cands.Count - 1;
            for (int i = 0; i < cands.Count; i++) { roll -= w[i]; if (roll <= 0) { pickIdx = i; break; } }
        }
        else
        {
            pickIdx = rng.Next(cands.Count);
        }
        int cid = cands[pickIdx];
        cands.RemoveAt(pickIdx);
        used.Add(cid);
        int rarity; CardListRarity(dataDirectory).TryGetValue(cid, out rarity);
        result.Add(new DrawResult { Cid = cid, Rarity = rarity, IsNew = true, PremiumType = 0 });
    }
    return result;
}

// Weight com possibilidade de override LOCAL do rarityRate (merge por chave com global+asc).
static double WeightWith(string dataDirectory, int cid, int ascension, Dictionary<int, double> rarityRatesOverride)
{
    WeightCtx ctx = Weights(dataDirectory, ascension);
    double rr = 1.0;
    int rarity;
    if (CardListRarity(dataDirectory).TryGetValue(cid, out rarity))
    {
        double v;
        if (rarityRatesOverride != null && rarityRatesOverride.TryGetValue(rarity, out v)) rr = v;
        else if (ctx.RarityRate.TryGetValue(rarity, out v)) rr = v;
    }
    double gm; if (ctx.GroupMult.TryGetValue(cid, out gm)) rr *= gm;
    return rr;
}
```

- [ ] **Step 2: Refatorar `RoguelikeModifiers.Resolve` pra usar `DrawN`**

Em `Resolve` (linhas onde monta `cands`, calcula peso, sorteia), substituir o bloco `List<int> cands = ...` até `used.Add(pick); return pick;` por:
```csharp
string source = Utils.GetValue<string>(spec, "source");
bool weighted = source == "any" || source == "link";
List<RoguelikeCardPool.DrawResult> drawn = RoguelikeCardPool.DrawN(
    DataDir, pool, spec, 1, Rng, Ascension,
    _used[playerIdx == 0 ? 0 : 1],
    /*rarityRatesOverride*/ null,
    weighted);
if (drawn.Count == 0) return null;
return drawn[0].Cid;
```

Importante: o `_used[playerIdx]` é o set que o `DrawN` deve **mutar diretamente** (não criar um set local). Validar manualmente lendo o método novo.

- [ ] **Step 3: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 4: Smoke (in-game)** — modifiers ainda funcionando

Restart server. Inicia uma run, entra num nó com modifier random (qualquer encounter já configurado com `extraLp source:"any"`). Verifica: o modifier sorteia uma carta válida e o duelo abre. Sem regressão.

- [ ] **Step 5: Commit**

```bash
git add YgoMasterServer/Roguelike/RoguelikeCardPool.cs YgoMasterServer/Roguelike/RoguelikeModifiers.cs
git commit -m "refactor(roguelike): extract DrawN from modifier Resolve"
```

---

## Task 4: `CardPool.json` — bloco `pity` (parsing)

Carrega config de pity (global + per-asc) do `CardPool.json`. Sem uso ainda — só leitura.

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeCardPool.cs`

- [ ] **Step 1: Definir `PityConfig`**

Próximo a `WeightCtx`:
```csharp
public class PityConfig
{
    public int Increment;             // soma no rarityRate efetivo por miss
    public int Max;                   // cap do bônus
    public HashSet<int> ResetOn;      // raridades (1..4) que zeram este contador ao serem puxadas
}

class PityCtx { public Dictionary<int, PityConfig> ByRarity = new Dictionary<int, PityConfig>(); }
static readonly Dictionary<int, PityCtx> _pityByAsc = new Dictionary<int, PityCtx>();
```

- [ ] **Step 2: Função `Pity` (cache por ascensão, merge global+asc com per-field merge)**

```csharp
public static Dictionary<int, PityConfig> Pity(string dataDirectory, int ascension)
{
    PityCtx cached;
    if (_pityByAsc.TryGetValue(ascension, out cached)) return cached.ByRarity;

    PityCtx ctx = new PityCtx();
    Dictionary<string, object> cfg = PoolConfig(dataDirectory);
    if (cfg != null)
    {
        ParsePity(ctx.ByRarity, Utils.GetValue<Dictionary<string, object>>(cfg, "pity"));
        Dictionary<string, object> asc = ItemAt(Utils.GetValue<List<object>>(cfg, "byAscension"), ascension);
        if (asc != null) ParsePity(ctx.ByRarity, Utils.GetValue<Dictionary<string, object>>(asc, "pity"));
    }
    _pityByAsc[ascension] = ctx;
    return ctx.ByRarity;
}

static void ParsePity(Dictionary<int, PityConfig> into, Dictionary<string, object> raw)
{
    if (raw == null) return;
    foreach (KeyValuePair<string, object> kv in raw)
    {
        int r = RarityKey(kv.Key);
        if (r <= 0) continue;
        Dictionary<string, object> entry = kv.Value as Dictionary<string, object>;
        if (entry == null) continue;
        PityConfig pc;
        if (!into.TryGetValue(r, out pc)) pc = new PityConfig { Increment = 0, Max = 0, ResetOn = new HashSet<int> { r } };
        // per-field merge:
        object v;
        if (entry.TryGetValue("increment", out v)) { try { pc.Increment = Convert.ToInt32(v); } catch { } }
        if (entry.TryGetValue("max", out v))       { try { pc.Max = Convert.ToInt32(v); } catch { } }
        List<object> rs = Utils.GetValue<List<object>>(entry, "reset_on");
        if (rs != null)
        {
            HashSet<int> set = new HashSet<int>();
            foreach (object o in rs)
            {
                int rr = RarityKey(Convert.ToString(o));
                if (rr > 0) set.Add(rr);
            }
            if (set.Count > 0) pc.ResetOn = set;
        }
        into[r] = pc;
    }
}
```

- [ ] **Step 3: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 4: Sanity (logs)** — config carrega

Adicionar TEMPORARIAMENTE no fim de `Pity()` antes do `return`:
```csharp
Console.WriteLine("[Roguelike] pity asc " + ascension + ": " + ctx.ByRarity.Count + " rarities configured");
```
Edita `DataLE/Roguelike/CardPool.json` adicionando:
```jsonc
"pity": {
  "UR": { "increment": 10, "max": 50, "reset_on": ["UR"] },
  "SR": { "increment": 5,  "max": 30, "reset_on": ["SR", "UR"] }
}
```
Restart server. Esperado no log ao 1º acesso (vai sair quando o `RoguelikeCardPool.Pity` for chamado na Task 6+; pode ainda não logar nesta task). **Importante:** após confirmar, **remover** a linha de log antes do commit (a config do CardPool.json fica, ela é parte da feature).

- [ ] **Step 5: Commit**

```bash
git add YgoMasterServer/Roguelike/RoguelikeCardPool.cs DataLE/Roguelike/CardPool.json
git commit -m "feat(roguelike): pity config parsing in card pool"
```

---

## Task 5: `RoguelikeEncounters` — parse + validate `openpack` action

Atualiza o loader de `Encounters.json` pra aceitar `kind: "openpack"` em actions. Validação cedo (sai do load com erro se inválido).

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeEncounters.cs`

- [ ] **Step 1: Adicionar caso `openpack` no validador de action**

Localiza onde os outros `kind`s (`options`, `message`) são validados. Adiciona o ramo `openpack`:
```csharp
case "openpack":
{
    int packs = Utils.GetValue<int>(node, "packs", 1);
    if (packs < 1) throw new Exception("openpack: packs must be >= 1");
    int pick = Utils.GetValue<int>(node, "pick", 0);
    if (pick < 0) throw new Exception("openpack: pick must be >= 0");

    List<object> pulls = Utils.GetValue<List<object>>(node, "pulls");
    if (pulls == null || pulls.Count == 0) throw new Exception("openpack: pulls required");
    int sizePerPack = 0;
    foreach (object pullObj in pulls)
    {
        Dictionary<string, object> pull = pullObj as Dictionary<string, object>;
        if (pull == null) throw new Exception("openpack: pull entry must be object");
        int count = Utils.GetValue<int>(pull, "count", 0);
        if (count < 1) throw new Exception("openpack: pull.count must be >= 1");
        double chance = Utils.GetValue<double>(pull, "chance", 1.0);
        if (chance < 0 || chance > 1) throw new Exception("openpack: pull.chance must be in [0,1]");
        Dictionary<string, object> pool = Utils.GetValue<Dictionary<string, object>>(pull, "pool");
        if (pool == null) throw new Exception("openpack: pull.pool required");
        ValidatePackPool(pool);
        sizePerPack += count;
    }
    int sizeTotal = sizePerPack * packs;
    if (pick > sizeTotal) throw new Exception("openpack: pick (" + pick + ") > total size (" + sizeTotal + ")");

    // pity: false | { rarityKey: {...} }
    object pityRaw;
    if (node.TryGetValue("pity", out pityRaw) && pityRaw != null && !(pityRaw is bool))
    {
        Dictionary<string, object> pity = pityRaw as Dictionary<string, object>;
        if (pity == null) throw new Exception("openpack: pity must be object or false");
        foreach (KeyValuePair<string, object> kv in pity)
        {
            if (RarityKeyStr(kv.Key) <= 0) throw new Exception("openpack: pity rarity key invalid: " + kv.Key);
        }
    }

    // next: validate recursivamente se presente
    Dictionary<string, object> nxt = Utils.GetValue<Dictionary<string, object>>(node, "next");
    if (nxt != null) ValidateActionNode(nxt);
    return;
}
```

Adiciona helpers no mesmo arquivo:
```csharp
static int RarityKeyStr(string k)
{
    switch ((k ?? "").ToUpperInvariant())
    {
        case "N": return 1; case "R": return 2; case "SR": return 3; case "UR": return 4;
    }
    return 0;
}

// Valida o pool spec do openpack pull (campos opcionais; só tipos batendo).
static void ValidatePackPool(Dictionary<string, object> pool)
{
    object v;
    if (pool.TryGetValue("rarityRates", out v))
    {
        Dictionary<string, object> rr = v as Dictionary<string, object>;
        if (rr == null) throw new Exception("openpack pool.rarityRates must be object");
        foreach (KeyValuePair<string, object> kv in rr)
            if (RarityKeyStr(kv.Key) <= 0) throw new Exception("openpack pool.rarityRates key invalid: " + kv.Key);
    }
    if (pool.TryGetValue("rarity", out v) && RarityKeyStr(Convert.ToString(v)) <= 0)
        throw new Exception("openpack pool.rarity invalid: " + v);
    if (pool.TryGetValue("rarities", out v))
    {
        List<object> rs = v as List<object>;
        if (rs == null) throw new Exception("openpack pool.rarities must be array");
        foreach (object o in rs)
            if (RarityKeyStr(Convert.ToString(o)) <= 0) throw new Exception("openpack pool.rarities entry invalid: " + o);
    }
}
```

- [ ] **Step 2: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 3: Sanity** — Encounters.json com openpack válido carrega

Adicionar TEMPORARIAMENTE em `DataLE/Roguelike/Encounters.json` um encontro com action openpack mínima:
```jsonc
"smoke_openpack": {
  "name": "Smoke OpenPack",
  "action": {
    "kind": "openpack",
    "packs": 1,
    "pick": 0,
    "pulls": [ { "count": 3, "pool": { "source": "any", "random": "monster" } } ]
  }
}
```
Restart server. Esperado: sem `Encounters.json parse EX` no log. Reverter o encounter (vai voltar na task 13).

- [ ] **Step 4: Commit**

```bash
git add YgoMasterServer/Roguelike/RoguelikeEncounters.cs
git commit -m "feat(roguelike): parse + validate openpack action"
```

---

## Task 6: `RoguelikeActionEngine` — Step do `openpack`

Gera as cartas (1 batch = N packs × M pulls), aplica `chance` por pull, atualiza pity por pack, stage em `run.PendingPack`. Não toca em `run.Cards`/`Deck` (isso é Task 8).

**Files:**
- Modify: `YgoMasterServer/Roguelike/Actions/RoguelikeActionEngine.cs`

- [ ] **Step 1: Adicionar caso `openpack` no Step**

Localiza o dispatcher do `Step` (lê `node["type"]` — o M5 já tem ramos para `"options"` e `"message"`). Adiciona o ramo:
```csharp
else if (type == "openpack")
{
    run.ActionToken++;
    int packs = Utils.GetValue<int>(node, "packs", 1);
    int pick = Utils.GetValue<int>(node, "pick", 0);
    List<object> pulls = Utils.GetValue<List<object>>(node, "pulls");
    Dictionary<string, object> next = Utils.GetValue<Dictionary<string, object>>(node, "next");

    // pity: action.pity = false desliga; merge global+asc+action
    object pityRaw;
    bool pityEnabled = true;
    Dictionary<string, object> actionPity = null;
    if (node.TryGetValue("pity", out pityRaw))
    {
        if (pityRaw is bool && !(bool)pityRaw) pityEnabled = false;
        else actionPity = pityRaw as Dictionary<string, object>;
    }
    Dictionary<int, RoguelikeCardPool.PityConfig> pityCfg =
        pityEnabled ? MergePity(RoguelikeCardPool.Pity(DataDir, run.Ascension), actionPity) : null;

    if (run.Pity == null) run.Pity = new Dictionary<string, int>();

    Random rng = new Random(unchecked((int)(run.Seed ^ ((long)run.ActionToken * 2654435761L))));
    HashSet<int> anyPool = RoguelikeCardPool.AnyPool(DataDir, Regulation, run.Ascension);

    List<Dictionary<string, object>> allCards = new List<Dictionary<string, object>>();
    for (int packIdx = 0; packIdx < packs; packIdx++)
    {
        HashSet<int> usedPack = new HashSet<int>();
        List<RoguelikeCardPool.DrawResult> packDraws = new List<RoguelikeCardPool.DrawResult>();
        foreach (object pullObj in pulls)
        {
            Dictionary<string, object> pull = pullObj as Dictionary<string, object>;
            double chance = Utils.GetValue<double>(pull, "chance", 1.0);
            if (chance < 1.0 && rng.NextDouble() >= chance) continue;
            int count = Utils.GetValue<int>(pull, "count", 0);
            Dictionary<string, object> pool = Utils.GetValue<Dictionary<string, object>>(pull, "pool");
            string source = Utils.GetValue<string>(pool, "source", "any");
            // v1: openpack só suporta source=any. Outros sources logam e caem pra any.
            if (source != "any")
            {
                Console.WriteLine("[Roguelike] openpack pool.source '" + source + "' not supported in v1; falling back to 'any'");
            }
            bool weighted = true;
            HashSet<int> universe = anyPool;

            // rarityRates: action override (já mergeado) + pity bonus
            Dictionary<int, double> rrEffective = MergeRarityRatesWithPity(
                RoguelikeCardPool.LayeredRarityRates(DataDir, run.Ascension),
                Utils.GetValue<Dictionary<string, object>>(pool, "rarityRates"),
                pityCfg, run.Pity);

            List<RoguelikeCardPool.DrawResult> drawn = RoguelikeCardPool.DrawN(
                DataDir, universe, pool, count, rng, run.Ascension,
                usedPack, rrEffective, weighted);
            packDraws.AddRange(drawn);
        }
        foreach (RoguelikeCardPool.DrawResult d in packDraws)
        {
            allCards.Add(new Dictionary<string, object>
            {
                { "cid", d.Cid }, { "rarity", d.Rarity },
                { "new", d.IsNew }, { "premium", d.PremiumType },
                { "packIdx", packIdx }
            });
        }
        // pity tick depois do pack
        if (pityCfg != null) UpdatePity(run.Pity, packDraws, pityCfg);
    }

    int size = allCards.Count;
    string mode = pick > 0 ? "pick" : "keep";
    Dictionary<string, object> labels = BuildOpenPackLabels(node, pick, size);

    run.PendingPack = new Dictionary<string, object>
    {
        { "token", run.ActionToken },
        { "mode", mode }, { "pick", pick }, { "size", size },
        { "cards", allCards.ConvertAll(c => (object)c) },
        { "labels", labels },
        { "next", next }
    };
    run.PendingAction = null;  // openpack toma o lugar
    return;
}
```

- [ ] **Step 2: Adicionar os helpers no mesmo arquivo**

```csharp
// Merge: global+asc -> action (per-field dentro da raridade). null `action` = só global+asc.
static Dictionary<int, RoguelikeCardPool.PityConfig> MergePity(
    Dictionary<int, RoguelikeCardPool.PityConfig> globalCfg,
    Dictionary<string, object> action)
{
    Dictionary<int, RoguelikeCardPool.PityConfig> merged =
        new Dictionary<int, RoguelikeCardPool.PityConfig>(globalCfg);
    if (action == null) return merged;
    foreach (KeyValuePair<string, object> kv in action)
    {
        int r = RarityFromKey(kv.Key);
        if (r <= 0) continue;
        Dictionary<string, object> entry = kv.Value as Dictionary<string, object>;
        if (entry == null) continue;
        RoguelikeCardPool.PityConfig pc;
        if (!merged.TryGetValue(r, out pc)) pc = new RoguelikeCardPool.PityConfig
            { Increment = 0, Max = 0, ResetOn = new HashSet<int> { r } };
        object v;
        if (entry.TryGetValue("increment", out v)) { try { pc.Increment = Convert.ToInt32(v); } catch { } }
        if (entry.TryGetValue("max", out v))       { try { pc.Max = Convert.ToInt32(v); } catch { } }
        List<object> rs = Utils.GetValue<List<object>>(entry, "reset_on");
        if (rs != null)
        {
            HashSet<int> set = new HashSet<int>();
            foreach (object o in rs) { int rr = RarityFromKey(Convert.ToString(o)); if (rr > 0) set.Add(rr); }
            if (set.Count > 0) pc.ResetOn = set;
        }
        merged[r] = pc;
    }
    return merged;
}

// rarityRates efetivo: layered (já vem do CardPool.LayeredRarityRates) → action override (por chave)
// → + pity bonus (min(misses*inc, max)).
static Dictionary<int, double> MergeRarityRatesWithPity(
    Dictionary<int, double> layered,
    Dictionary<string, object> actionOverride,
    Dictionary<int, RoguelikeCardPool.PityConfig> pityCfg,
    Dictionary<string, int> pity)
{
    Dictionary<int, double> r = layered != null
        ? new Dictionary<int, double>(layered)
        : new Dictionary<int, double>();
    if (actionOverride != null)
        foreach (KeyValuePair<string, object> kv in actionOverride)
        {
            int rk = RarityFromKey(kv.Key);
            if (rk <= 0) continue;
            double w; try { w = Convert.ToDouble(kv.Value); } catch { continue; }
            r[rk] = w;
        }
    if (pityCfg != null && pity != null)
        foreach (KeyValuePair<int, RoguelikeCardPool.PityConfig> kv in pityCfg)
        {
            int rk = kv.Key;
            int counter; pity.TryGetValue(RarityToKey(rk), out counter);
            double bonus = Math.Min((double)counter * kv.Value.Increment, kv.Value.Max);
            double cur; r.TryGetValue(rk, out cur);
            r[rk] = cur + bonus;
        }
    return r;
}

// Atualiza contadores após um pack: incrementa se nenhuma carta tem raridade ∈ reset_on; senão zera.
static void UpdatePity(Dictionary<string, int> pity, List<RoguelikeCardPool.DrawResult> packCards,
                      Dictionary<int, RoguelikeCardPool.PityConfig> pityCfg)
{
    HashSet<int> raritiesInPack = new HashSet<int>();
    foreach (RoguelikeCardPool.DrawResult d in packCards) raritiesInPack.Add(d.Rarity);
    foreach (KeyValuePair<int, RoguelikeCardPool.PityConfig> kv in pityCfg)
    {
        string key = RarityToKey(kv.Key);
        bool hit = false;
        foreach (int rr in kv.Value.ResetOn) if (raritiesInPack.Contains(rr)) { hit = true; break; }
        int cur; pity.TryGetValue(key, out cur);
        pity[key] = hit ? 0 : cur + 1;
    }
}

static int RarityFromKey(string k)
{
    switch ((k ?? "").ToUpperInvariant())
    {
        case "N": return 1; case "R": return 2; case "SR": return 3; case "UR": return 4;
    }
    return 0;
}
static string RarityToKey(int r)
{
    switch (r) { case 1: return "N"; case 2: return "R"; case 3: return "SR"; case 4: return "UR"; }
    return "?";
}

// Passa-through das labels da action (sem defaults — defaults ficam no client com RoguelikeLabels).
// {0}=pick, {1}=size: interpola SÓ o title_pick se presente (server-side hard format pra evitar trip
// no client por culture etc.).
static Dictionary<string, object> BuildOpenPackLabels(Dictionary<string, object> node, int pick, int size)
{
    string titleKeep = Utils.GetValue<string>(node, "title_keep", null);
    string titlePick = Utils.GetValue<string>(node, "title_pick", null);
    string confirm   = Utils.GetValue<string>(node, "confirm_label", null);
    Dictionary<string, object> r = new Dictionary<string, object>();
    if (titleKeep != null) r["title_keep"] = titleKeep;
    if (titlePick != null) r["title_pick"] = string.Format(titlePick, pick, size);
    if (confirm   != null) r["confirm"]    = confirm;
    return r;
}
```

- [ ] **Step 3: Adicionar `LayeredRarityRates` em `RoguelikeCardPool`**

(Exposição pública do `Weights().RarityRate` já mergeado global+asc.) Em `RoguelikeCardPool.cs`:
```csharp
// Snapshot do rarityRates global+asc (sem override de action).
public static Dictionary<int, double> LayeredRarityRates(string dataDirectory, int ascension)
{
    return new Dictionary<int, double>(Weights(dataDirectory, ascension).RarityRate);
}
```

- [ ] **Step 4: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add YgoMasterServer/Roguelike/Actions/RoguelikeActionEngine.cs YgoMasterServer/Roguelike/RoguelikeCardPool.cs
git commit -m "feat(roguelike): openpack Step in action engine + pity merge"
```

---

## Task 7: `WriteRun` — projeta `$.Gacha` + `$.Roguelike.pendingPack`

**Files:**
- Modify: `YgoMasterServer/Roguelike/GameServer.Roguelike.cs`

- [ ] **Step 1: Adicionar projeção e cleanup em `WriteRun`**

Localiza `WriteRun(RoguelikeRun run, GameServerWebRequest request)`. Depois do bloco que já projeta `$.Roguelike.action` (M5), adicionar:
```csharp
// openpack: projeta $.Gacha (shape vanilla) + $.Roguelike.pendingPack se há PendingPack.
if (run.PendingPack != null)
{
    Dictionary<string, object> rgDto = (Dictionary<string, object>)request.Response["Roguelike"];
    rgDto["pendingPack"] = BuildPendingPackProjection(run.PendingPack);
    Dictionary<string, object> gacha = BuildGachaProjection(run.PendingPack);
    request.Response["Gacha"] = gacha;
}
else
{
    // Cleanup explícito: garante que nada do pacote anterior vaze pra outras telas.
    request.Remove("Gacha");
    Dictionary<string, object> rg = request.Response.ContainsKey("Roguelike")
        ? (Dictionary<string, object>)request.Response["Roguelike"] : null;
    if (rg != null) rg.Remove("pendingPack");
}
```

- [ ] **Step 2: Adicionar os builders no mesmo arquivo**

```csharp
// Versão compacta pro driver do roguelike (não dá pra confiar só em $.Gacha — shop vanilla também enche).
static Dictionary<string, object> BuildPendingPackProjection(Dictionary<string, object> pack)
{
    return new Dictionary<string, object>
    {
        { "token",  Utils.GetValue<int>(pack, "token") },
        { "mode",   Utils.GetValue<string>(pack, "mode") },
        { "pick",   Utils.GetValue<int>(pack, "pick") },
        { "size",   Utils.GetValue<int>(pack, "size") },
        { "labels", Utils.GetValue<Dictionary<string, object>>(pack, "labels") ?? new Dictionary<string, object>() },
    };
}

// Reidrata o $.Gacha vanilla: 1 entry por pack, com cardInfo[].mrk = cid.
static Dictionary<string, object> BuildGachaProjection(Dictionary<string, object> pack)
{
    List<object> cards = Utils.GetValue<List<object>>(pack, "cards") ?? new List<object>();
    // agrupar por packIdx
    Dictionary<int, List<Dictionary<string, object>>> byPack = new Dictionary<int, List<Dictionary<string, object>>>();
    foreach (object o in cards)
    {
        Dictionary<string, object> c = o as Dictionary<string, object>;
        if (c == null) continue;
        int pi = Utils.GetValue<int>(c, "packIdx");
        List<Dictionary<string, object>> list;
        if (!byPack.TryGetValue(pi, out list)) { list = new List<Dictionary<string, object>>(); byPack[pi] = list; }
        list.Add(c);
    }
    List<object> packs = new List<object>();
    foreach (KeyValuePair<int, List<Dictionary<string, object>>> kv in byPack)
    {
        List<object> cardInfo = new List<object>();
        foreach (Dictionary<string, object> c in kv.Value)
        {
            int rarity = Utils.GetValue<int>(c, "rarity", 1);
            cardInfo.Add(new Dictionary<string, object>
            {
                { "mrk", Utils.GetValue<int>(c, "cid") },
                { "rarity", rarity },
                { "backSideRarity", 1 },
                { "foundSecrets", new int[0] },
                { "extendSecrets", new int[0] },
                { "new", Utils.GetValue<bool>(c, "new", true) },
                { "premiumType", Utils.GetValue<int>(c, "premium", 0) }
            });
        }
        Dictionary<string, object> effects = new Dictionary<string, object>
        {
            { "thunder", 1 }, { "rarityup", 1 }, { "cut", 1 }, { "rarityupBg", 1 }, { "rarity", 1 }
        };
        packs.Add(new Dictionary<string, object>
        {
            { "packInfo", new List<object> { new Dictionary<string, object> { { "effects", effects }, { "cardInfo", cardInfo } } } },
            { "effects", new Dictionary<string, object> { { "isPickup", false }, { "imageName", "" }, { "smokeType", 1 } } }
        });
    }
    return new Dictionary<string, object>
    {
        { "drawInfo", new Dictionary<string, object>
            {
                { "packs", packs },
                { "options", new Dictionary<string, object> { { "skippable", true } } }
            } },
        { "resultInfo", new Dictionary<string, object>
            {
                { "isSendGift", false }, { "showSecretFoundResult", false },
                { "isNextFinalizedUR", false }, { "setItems", new List<object>() },
                { "buyCardFile", 0 }
            } }
    };
}
```

- [ ] **Step 3: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add YgoMasterServer/Roguelike/GameServer.Roguelike.cs
git commit -m "feat(roguelike): project Gacha + pendingPack from run.PendingPack"
```

---

## Task 8: `Act_RoguelikePackFinalize` + dispatch

Valida o token/picks, commita ao pool/deck, limpa `PendingPack`, avança o engine (`next`).

**Files:**
- Modify: `YgoMasterServer/Roguelike/GameServer.Roguelike.cs`
- Modify: `YgoMasterServer/GameServer.cs`

- [ ] **Step 1: Implementar `Act_RoguelikePackFinalize`**

Adicionar em `GameServer.Roguelike.cs`:
```csharp
void Act_RoguelikePackFinalize(GameServerWebRequest req)
{
    RoguelikeRun run = LoadRun(req.Player);
    if (run == null || !run.Active || run.PendingPack == null)
    {
        WriteRun(run, req);
        return; // stale; cliente abriu por reentrada já resolvida
    }
    int token = Utils.GetValue<int>(req.ActParams, "token");
    int expected = Utils.GetValue<int>(run.PendingPack, "token");
    if (token != expected)
    {
        WriteRun(run, req);
        return; // stale token
    }

    List<object> picksRaw = Utils.GetValue<List<object>>(req.ActParams, "picks") ?? new List<object>();
    List<object> cards = Utils.GetValue<List<object>>(run.PendingPack, "cards");
    int size = Utils.GetValue<int>(run.PendingPack, "size");
    string mode = Utils.GetValue<string>(run.PendingPack, "mode");
    int pickRequired = Utils.GetValue<int>(run.PendingPack, "pick");

    HashSet<int> picks = new HashSet<int>();
    foreach (object o in picksRaw) { int i; try { i = Convert.ToInt32(o); } catch { continue; } picks.Add(i); }
    bool valid = true;
    foreach (int i in picks) if (i < 0 || i >= size) { valid = false; break; }
    if (mode == "keep" && picks.Count != size) valid = false;
    if (mode == "pick" && picks.Count != pickRequired) valid = false;
    if (!valid)
    {
        req.ResultCode = -1; // invalid
        WriteRun(run, req);
        return;
    }

    foreach (int idx in picks)
    {
        Dictionary<string, object> c = cards[idx] as Dictionary<string, object>;
        if (c == null) continue;
        int cid = Utils.GetValue<int>(c, "cid");
        run.AddCard(cid, 1);
        AddCidToRunDeck(run, cid);
    }

    Dictionary<string, object> next = Utils.GetValue<Dictionary<string, object>>(run.PendingPack, "next");
    run.PendingPack = null;

    // Avança a árvore de ações reaproveitando o mesmo helper que Act_RoguelikeActionRespond
    // já chama depois de processar uma resposta. Olhar Act_RoguelikeActionRespond no mesmo
    // arquivo (`GameServer.Roguelike.cs`) — copiar a chamada final que projeta/encerra
    // (provavelmente `RoguelikeActionEngine.Step(run)` ou equivalente).
    run.PendingAction = next;
    RoguelikeActionEngine.Step(run);  // ajuste pra a assinatura exata usada pelo M5

    SaveRun(req.Player, run);
    WriteRun(run, req);
}

// Põe o cid na seção certa do run.Deck (Main ou Extra). Cria estrutura mínima se faltar.
static void AddCidToRunDeck(RoguelikeRun run, int cid)
{
    if (run.Deck == null) run.Deck = new Dictionary<string, object>();
    Dictionary<string, object> deck = Utils.GetOrCreateDictionary(run.Deck, "deck");
    string section = YdkHelper.IsExtraDeck(cid) ? "Extra" : "Main";
    Dictionary<string, object> sec = Utils.GetOrCreateDictionary(deck, section);
    List<object> ids = Utils.GetValue<List<object>>(sec, "ids") ?? new List<object>();
    List<object> rs  = Utils.GetValue<List<object>>(sec, "r")   ?? new List<object>();
    ids.Add(cid); rs.Add(1);
    sec["ids"] = ids; sec["r"] = rs;
}
```

> Se `YdkHelper.IsExtraDeck(cid)` não existir com essa assinatura, usar `YdkHelper.LoadCardDataFromGame(dataDirectory)[cid].IsExtraDeck` (o método `GameCardInfo` já tem `IsExtraDeck` per a leitura do `RoguelikeCardPool`). Inline a checagem se necessário.

- [ ] **Step 2: Adicionar dispatch em `GameServer.cs`**

No switch/case onde `Roguelike.move`/`Roguelike.action_respond` são tratados, adicionar:
```csharp
case "Roguelike.pack_finalize": Act_RoguelikePackFinalize(req); break;
```

- [ ] **Step 3: Build server**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add YgoMasterServer/Roguelike/GameServer.Roguelike.cs YgoMasterServer/GameServer.cs
git commit -m "feat(roguelike): pack_finalize act + dispatch"
```

---

## Task 9: Client API — `PendingPack` + `GetPendingPack` + `PackFinalize`

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeApi.cs`

- [ ] **Step 1: Adicionar a model `PendingPack`**

No topo do `RoguelikeApi` (junto com `ActionPrompt`):
```csharp
public class PendingPack
{
    public int Token;
    public string Mode;     // "keep" | "pick"
    public int Pick;
    public int Size;
    public Labels TextLabels;
    public class Labels { public string TitleKeep; public string TitlePick; public string Confirm; }
}
```

- [ ] **Step 2: Adicionar `GetPendingPack`**

Junto com `GetActionPrompt`:
```csharp
public static PendingPack GetPendingPack()
{
    string json = ClientWork.SerializePath("Roguelike.pendingPack");
    if (string.IsNullOrEmpty(json) || json == "null") return null;
    Dictionary<string, object> d = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
    if (d == null) return null;
    Dictionary<string, object> lab = d.ContainsKey("labels") ? d["labels"] as Dictionary<string, object> : null;
    return new PendingPack
    {
        Token = Convert.ToInt32(d.ContainsKey("token") ? d["token"] : 0),
        Mode  = d.ContainsKey("mode")  ? Convert.ToString(d["mode"]) : "keep",
        Pick  = Convert.ToInt32(d.ContainsKey("pick") ? d["pick"] : 0),
        Size  = Convert.ToInt32(d.ContainsKey("size") ? d["size"] : 0),
        TextLabels = new PendingPack.Labels
        {
            // Defaults via RoguelikeLabels client-side (server só repassa o que a action especificou).
            TitleKeep = lab != null && lab.ContainsKey("title_keep")
                ? Convert.ToString(lab["title_keep"])
                : RoguelikeLabels.Get("pack.title.keep", "Cartas obtidas"),
            TitlePick = lab != null && lab.ContainsKey("title_pick")
                ? Convert.ToString(lab["title_pick"])
                : RoguelikeLabels.Get("pack.title.pick", "Selecione cartas"),
            Confirm   = lab != null && lab.ContainsKey("confirm")
                ? Convert.ToString(lab["confirm"])
                : RoguelikeLabels.Get("pack.confirm", "Confirmar"),
        }
    };
}
```

- [ ] **Step 3: Adicionar `PackFinalize`**

Junto com `ActionRespond`:
```csharp
public static void PackFinalize(int token, int[] picks)
{
    Dictionary<string, object> p = new Dictionary<string, object>
    {
        { "token", token },
        { "picks", picks }
    };
    Request.Entry("Roguelike.pack_finalize", p, 30f);
}
```

- [ ] **Step 4: Build client**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterClient.csproj" -nologo -v:minimal
```
Esperado: 0 errors + `[Goat] Copied YgoMasterClient.exe -> ...`.

- [ ] **Step 5: Commit**

```bash
git add YgoMasterClient/Roguelike/RoguelikeApi.cs
git commit -m "feat(roguelike): client api for pendingPack + pack_finalize"
```

---

## Task 10: `RoguelikePackDriver` + wire em `MapScreen.Update`

**Files:**
- Create: `YgoMasterClient/Roguelike/Actions/RoguelikePackDriver.cs`
- Modify: `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`

- [ ] **Step 1: Criar `RoguelikePackDriver.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace YgoMasterClient
{
    // Renderiza pacotes pendentes do server (run.PendingPack). Pumped from RoguelikeMapScreen.Update
    // — só dispara quando o mapa está visível, padrão do M5 action prompt.
    static class RoguelikePackDriver
    {
        static int _shownToken = -1;

        public static void Pump()
        {
            try
            {
                RoguelikeApi.PendingPack p = RoguelikeApi.GetPendingPack();
                if (p == null) { _shownToken = -1; return; }
                if (p.Token == _shownToken) return; // já abriu
                _shownToken = p.Token;
                IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
                if (manager == IntPtr.Zero) return;
                IntPtr args = NewArgs();
                YgomSystem.UI.ViewControllerManager.PushChildViewControllerArgs(manager, "CardPack/CardPackOpen", args);
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] pack pump EX: " + ex); }
        }

        static IntPtr NewArgs()
        {
            // { "ForwardResultArgs": null }
            return YgomMiniJSON.Json.Deserialize(MiniJSON.Json.Serialize(
                new Dictionary<string, object> { { "ForwardResultArgs", null } }));
        }
    }
}
```

- [ ] **Step 2: Wire em `RoguelikeMapScreen.Update`**

Localiza a linha (recente, do M5) `if (IsActive(_go)) RoguelikeActionDriver.Pump();`. **Abaixo** dela, adicionar:
```csharp
if (IsActive(_go)) RoguelikePackDriver.Pump();
```

- [ ] **Step 3: Build client**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterClient.csproj" -nologo -v:minimal
```
Esperado: 0 errors + copy do exe.

- [ ] **Step 4: Smoke (server+client com Encounters seedado)**

Adiciona TEMPORARIAMENTE em `DataLE/Roguelike/Encounters.json`:
```jsonc
"smoke_openpack_keep": {
  "name": "Smoke OpenPack Keep",
  "action": {
    "kind": "openpack", "packs": 1, "pick": 0,
    "pulls": [ { "count": 3, "pool": { "source": "any", "random": "monster" } } ]
  }
}
```
Restart server + client. Inicia run. Console: `rgencounter smoke_openpack_keep`.
Esperado: `CardPackOpen` abre, anima e transiciona pro Result com 3 monstros aleatórios.
(Não há OK customizado ainda — Result usa fluxo nativo. As cartas **não** entram no pool nesse ponto, porque finalize ainda não existe; é normal pra essa task.)

- [ ] **Step 5: Commit**

```bash
git add YgoMasterClient/Roguelike/Actions/RoguelikePackDriver.cs YgoMasterClient/Roguelike/RoguelikeMapScreen.cs
git commit -m "feat(roguelike): pack driver pumping CardPackOpen on PendingPack"
```

---

## Task 11: `RoguelikePackResultHook` — modo `keep all`

Hook no `CardPackOpenResultViewController.OnCreatedView`: relabel título + OK → "Confirmar" + dispara `pack_finalize` no clique.

**Files:**
- Create: `YgoMasterClient/Roguelike/Actions/RoguelikePackResultHook.cs`

- [ ] **Step 1: Criar `RoguelikePackResultHook.cs`**

```csharp
using IL2CPP;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YgoMasterClient
{
    // Customiza CardPackOpenResultViewController quando o pacote é nosso (PendingPack ativo).
    // Em keep mode: só relabel + Confirmar dispara pack_finalize com [0..size-1] e popa o VC.
    // (Pick mode entra na Task 12.)
    static unsafe class RoguelikePackResultHook
    {
        static IL2Class _vcClass;
        delegate void Del_OnCreatedView(IntPtr thisPtr);
        static Hook<Del_OnCreatedView> _hook;
        static IntPtr _selectionButtonType, _bindingTextType;
        static IL2Field _selBtnOnClick;
        static IL2Method _ueAddListener, _ueRemoveAll;

        static RoguelikePackResultHook()
        {
            try
            {
                IL2Assembly asm = Assembler.GetAssembly("Assembly-CSharp");
                _vcClass = asm.GetClass("CardPackOpenResultViewController", "YgomGame.CardPack");
                _hook = new Hook<Del_OnCreatedView>(OnCreatedView, _vcClass.GetMethod("OnCreatedView"));
                IL2Class selBtn = asm.GetClass("SelectionButton", "YgomSystem.UI");
                _selectionButtonType = selBtn.IL2Typeof();
                _selBtnOnClick = selBtn.GetField("onClick");
                IL2Assembly core = Assembler.GetAssembly("UnityEngine.CoreModule");
                IL2Class ue = core.GetClass("UnityEvent", "UnityEngine.Events");
                _ueAddListener = ue.GetMethod("AddListener");
                _ueRemoveAll = core.GetClass("UnityEventBase", "UnityEngine.Events").GetMethod("RemoveAllListeners");
                _bindingTextType = CastUtils.IL2Typeof("BindingTextMeshProUGUI", "YgomSystem.UI", "Assembly-CSharp");
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] pack result hook init EX: " + ex); }
        }

        public static void EnsureRegistered() { /* trigger cctor */ }

        static void OnCreatedView(IntPtr thisPtr)
        {
            _hook.Original(thisPtr);
            try
            {
                RoguelikeApi.PendingPack p = RoguelikeApi.GetPendingPack();
                if (p == null) return; // vanilla shop: passa intacto

                IntPtr go = Component.GetGameObject(thisPtr);

                // (1) Renomeia o título
                string title = p.Mode == "pick" ? p.TextLabels.TitlePick : p.TextLabels.TitleKeep;
                SetBindingText(go, "TitleSafeArea.TitleGroup.NameText", title);

                // (2) OK -> Confirmar + reage no Confirmar
                IntPtr okBtn = GameObject.FindGameObjectByPath(go, "FooterArea.BaseAll.BaseRight.Button/OKButton");
                if (okBtn != IntPtr.Zero)
                {
                    SetBindingText(okBtn, "TextTMP", p.TextLabels.Confirm);
                    WireButton(okBtn, OnConfirmKeep);
                }
                // Pick mode é Task 12 — por enquanto, em pick mode o botão dispara mesmo (com seleção vazia)
                // o server vai rejeitar pickCount != pick e re-projetar; sem mau efeito de estado.
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] pack result hook EX: " + ex); }
        }

        static void OnConfirmKeep()
        {
            RoguelikeApi.PendingPack p = RoguelikeApi.GetPendingPack();
            if (p == null) return;
            int[] picks = Enumerable.Range(0, p.Size).ToArray();
            RoguelikeApi.PackFinalize(p.Token, picks);
            IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
            if (manager != IntPtr.Zero) YgomSystem.UI.ViewControllerManager.PopChildViewController(manager);
        }

        // Replace SelectionButton.onClick with a captureless Action.
        static void WireButton(IntPtr buttonGo, Action action)
        {
            IntPtr sel = GameObject.GetComponent(buttonGo, _selectionButtonType);
            if (sel == IntPtr.Zero) return;
            IL2Object onClick = _selBtnOnClick.GetValue(sel);
            if (onClick == null) return;
            if (_ueRemoveAll != null) _ueRemoveAll.Invoke(onClick.ptr);
            IntPtr cb = UnityEngine.Events._UnityAction.CreateUnityAction(action);
            _ueAddListener.Invoke(onClick.ptr, new IntPtr[] { cb });
        }

        static void SetBindingText(IntPtr root, string path, string text)
        {
            IntPtr o = string.IsNullOrEmpty(path) ? root : GameObject.FindGameObjectByPath(root, path);
            if (o == IntPtr.Zero) return;
            IntPtr binding = GameObject.GetComponent(o, _bindingTextType);
            if (binding != IntPtr.Zero) YgomSystem.UI.BindingTextMeshProUGUI.SetTextId(binding, text);
        }
    }
}
```

- [ ] **Step 2: Trigger do cctor**

Adicionar em `Program.cs` (ou onde os outros hooks são inicializados — buscar `RoguelikeRunScreen` referenciado pra encontrar o lugar) a linha:
```csharp
RoguelikePackResultHook.EnsureRegistered();
```

Se não houver lugar central, basta tocar a classe em qualquer Pump (driver, etc.) — mas chamada explícita evita preguiça do JIT. Padrão do M5: inicialização eager.

- [ ] **Step 3: Build client**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterClient.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 4: Smoke (keep mode end-to-end)**

Restart server + client. `rgencounter smoke_openpack_keep`. Esperado:
- `CardPackOpen` abre, anima, transiciona pro Result.
- Título: "Cartas obtidas".
- Botão: "Confirmar".
- Clicar: VC fecha; abrir o editor de deck da run → 3 monstros aleatórios estão no pool + somados no Main do deck.

Caso o título não muda ou o botão não responde: log `[Roguelike] pack result hook EX:` no console. Diagnóstico: verificar que o nome de classe `CardPackOpenResultViewController` no namespace `YgomGame.CardPack` confere (usar `nsdump`).

- [ ] **Step 5: Commit**

```bash
git add YgoMasterClient/Roguelike/Actions/RoguelikePackResultHook.cs YgoMasterClient/Program.cs
git commit -m "feat(roguelike): pack result hook (keep mode) + finalize"
```

---

## Task 12: Modo `pick X` — seleção + gate + finalize

Estende o `OnCreatedView` do hook: em `pick` mode, ativa seleção em cada `CardPict`, gate do OK em `|sel| == pick`, e finalize manda só as escolhidas.

**Files:**
- Modify: `YgoMasterClient/Roguelike/Actions/RoguelikePackResultHook.cs`

- [ ] **Step 1: Estado por instância**

No topo da classe, junto com os fields:
```csharp
static HashSet<int> _selected;       // índices em PendingPack.cards
static int _pickRequired;
static IntPtr _okBtn;
static List<IntPtr> _pictGos;        // GameObjects de cada CardPict, na ordem dos índices
static IL2Property _selBtnInteractable;
```

No cctor, adicionar resolução do `interactable`:
```csharp
_selBtnInteractable = selBtn.GetProperty("interactable");
```

- [ ] **Step 2: Estender `OnCreatedView` pra ativar pick mode**

Logo após o bloco que renomeia OK pra Confirmar:
```csharp
if (p.Mode != "pick") return;

_selected = new HashSet<int>();
_pickRequired = p.Pick;
_okBtn = okBtn;
SetButtonInteractable(okBtn, false);

// Find every PackCardTemplate(Clone) in order (= índice no PendingPack.cards).
IntPtr content = GameObject.FindGameObjectByPath(go,
    "Root.ResultRoot.ObtainedCardsRoot.CardAreaGroup.CardsScrollView.Viewport.Content");
_pictGos = new List<IntPtr>();
EnumerateCardPicts(content, _pictGos);

for (int i = 0; i < _pictGos.Count; i++)
{
    int idx = i;
    IntPtr pict = _pictGos[i];
    WireButton(pict, () => ToggleCard(idx));
    SetSelectVisual(pict, false);
}

// Re-wire o OK pra ToggleSubmit que valida pick mode.
WireButton(okBtn, OnConfirmPick);
```

- [ ] **Step 3: Enumeração + visual + toggle**

```csharp
static void EnumerateCardPicts(IntPtr content, List<IntPtr> outList)
{
    // Content -> PackCardGroupTemplate(Clone) (N) -> PackCardTemplate(Clone) (M) -> CardPict
    IntPtr ct = GameObject.GetTransform(content);
    int n = Transform.GetChildCount(ct);
    for (int i = 0; i < n; i++)
    {
        IntPtr group = GameObject.GetTransform(Transform.GetChild(ct, i));
        if (group == IntPtr.Zero) continue;
        int m = Transform.GetChildCount(group);
        for (int j = 0; j < m; j++)
        {
            IntPtr card = GameObject.GetTransform(Transform.GetChild(group, j));
            if (card == IntPtr.Zero) continue;
            IntPtr cardGo = Component.GetGameObject(card);
            string name = UnityObject.GetName(cardGo);
            if (!name.StartsWith("PackCardTemplate")) continue;
            IntPtr pict = GameObject.FindGameObjectByPath(cardGo, "CardPict");
            if (pict != IntPtr.Zero) outList.Add(pict);
        }
    }
}

static void ToggleCard(int idx)
{
    if (_selected.Contains(idx))
    {
        _selected.Remove(idx);
        SetSelectVisual(_pictGos[idx], false);
    }
    else
    {
        if (_selected.Count >= _pickRequired) return; // ignora clicks acima do limite (defesa)
        _selected.Add(idx);
        SetSelectVisual(_pictGos[idx], true);
    }
    SetButtonInteractable(_okBtn, _selected.Count == _pickRequired);
}

static void SetSelectVisual(IntPtr pict, bool on)
{
    // Reaproveita o SelectCursorCardForPC/Console que já está no template.
    IntPtr pcCursor = GameObject.FindGameObjectByPath(pict, "SelectCursorCardForPC");
    if (pcCursor != IntPtr.Zero) GameObject.SetActive(pcCursor, on);
    IntPtr consoleCursor = GameObject.FindGameObjectByPath(pict, "SelectCursorForConsole");
    if (consoleCursor != IntPtr.Zero) GameObject.SetActive(consoleCursor, on);
}

static void SetButtonInteractable(IntPtr btn, bool on)
{
    IntPtr sel = GameObject.GetComponent(btn, _selectionButtonType);
    if (sel == IntPtr.Zero || _selBtnInteractable == null) return;
    csbool v = on;
    _selBtnInteractable.GetSetMethod().Invoke(sel, new IntPtr[] { new IntPtr(&v) });
}

static void OnConfirmPick()
{
    RoguelikeApi.PendingPack p = RoguelikeApi.GetPendingPack();
    if (p == null || _selected == null || _selected.Count != p.Pick) return;
    int[] picks = _selected.ToArray();
    RoguelikeApi.PackFinalize(p.Token, picks);
    IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
    if (manager != IntPtr.Zero) YgomSystem.UI.ViewControllerManager.PopChildViewController(manager);
}
```

- [ ] **Step 4: Build client**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterClient.csproj" -nologo -v:minimal
```
Esperado: 0 errors.

- [ ] **Step 5: Smoke (pick mode end-to-end)**

Adiciona TEMPORARIAMENTE em `Encounters.json`:
```jsonc
"smoke_openpack_pick": {
  "name": "Smoke OpenPack Pick 2/5",
  "action": {
    "kind": "openpack", "packs": 1, "pick": 2,
    "pulls": [ { "count": 5, "pool": { "source": "any", "random": "monster" } } ]
  }
}
```
Restart server + client. `rgencounter smoke_openpack_pick`. Esperado:
- Result com 5 cartas; título "Selecione 2 de 5 cartas"; botão "Confirmar" **desabilitado**.
- Clicar 1 carta → visual de seleção; OK ainda desabilitado.
- Clicar 2ª carta → OK habilita.
- Clicar 3ª → ignorada (defensiva).
- Confirmar → VC fecha; só 2 cartas escolhidas estão no pool + deck.

- [ ] **Step 6: Commit**

```bash
git add YgoMasterClient/Roguelike/Actions/RoguelikePackResultHook.cs
git commit -m "feat(roguelike): pick mode selection + gate + finalize"
```

---

## Task 13: Pity smoke + cleanup + commit final

Verifica que pity tickka + persiste entre packs, e remove os encounters temporários de smoke.

**Files:**
- Modify: `DataLE/Roguelike/Encounters.json`
- Modify: `DataLE/Roguelike/CardPool.json` (já modificado na Task 4)

- [ ] **Step 1: Encounter seed pra pity test**

Adiciona em `Encounters.json` (temporário pro teste):
```jsonc
"smoke_openpack_pity": {
  "name": "Smoke OpenPack Pity 3 packs",
  "action": {
    "kind": "openpack", "packs": 3, "pick": 0,
    "pulls": [ { "count": 5, "pool": { "source": "any", "random": "monster" } } ]
  }
}
```

- [ ] **Step 2: Smoke**

Restart server + client. `rgencounter smoke_openpack_pity`. Esperado:
- 3 animações de pacote, 1 Result com 15 cartas.
- Confirma → 15 cartas no pool/deck.
- No `Saves/<player>/roguelike.json`: campo `pity` reflete contadores (ex.: se não veio UR em nenhum: `"pity": {"UR": 3, "SR": 0}` ou similar).
- Disparar 2ª vez (`rgencounter smoke_openpack_pity`): pity persiste do batch anterior (não zera entre actions).
- Eventualmente, com `UR.increment: 10, max: 50`, a 5ª-6ª execução sem UR deve ter chance UR ~efetiva 60%+ — testar manualmente algumas vezes.

- [ ] **Step 3: Reentrada in-game**

`rgencounter smoke_openpack_keep` → na tela de Result, antes de Confirmar: sai pro home (Abandonar Run? NÃO — fechar o cliente). Reabrir: ir pro mapa → `CardPackOpen` reabre automaticamente com as mesmas 3 cartas. Confirmar → finaliza normal.

- [ ] **Step 4: Remover encounters de smoke**

Tirar `smoke_openpack_keep`, `smoke_openpack_pick`, `smoke_openpack_pity` de `Encounters.json`. (Os de seed real ficam pra runs normais; esses eram só dev.)

- [ ] **Step 5: Commit**

```bash
git add DataLE/Roguelike/Encounters.json
git commit -m "test(roguelike): in-game smoke for openpack (keep/pick/pity/reentry)"
```

---

## Verificação final

Após Task 13, conferir manualmente:
- [ ] `git status` limpo (sem mudanças pendentes).
- [ ] `git log --oneline -14` mostra 13 commits da feature + commits pré-existentes.
- [ ] Build limpo do client e do server.
- [ ] Modifiers (`source:"any"` / `source:"link"`) continuam funcionando em duelos (não regressão da Task 3).
- [ ] Vanilla shop pack open (fora da run, no menu normal) continua funcionando intacto (o hook só age se `GetPendingPack() != null`).

## Out of scope (decidido no design + ajustes do plano)

- Garantias entre pacotes vanilla-style ("1 UR em 10").
- `pulls[].pool.rateGroups` (override per-action de grupos).
- "Derruba o mais antigo" ao selecionar acima do limite no pick mode.
- **Pool `source: "link"` / `"deck"` em openpack** (v1 só `any`; mantém compat de schema, mas server faz fallback pra `any` com log). v2: BuildPool completo (depende de owner context — `deck_owner` num roguelike action não é tão trivial).
- Indicador visual "X/Y selecionadas" custom.
- Joystick "B" → re-push (caso vire problema observado).

Tudo isso está documentado em `Docs/roguelike/openpack-design.md` §14 e pode ser adicionado em uma feature posterior sem quebrar o schema.
