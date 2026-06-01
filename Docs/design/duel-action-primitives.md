# duel.dll — Catálogo de primitivas de ação

Referência das funções/estado da `duel.dll` usadas para executar ações de
duelo a partir do client (relíquias / duelHooks). Image base `0x180000000`
(RVA = VA − 0x180000000). Tudo roda na **thread do duelo** — chamar via
`ActionsToRunInNextSysAct` (drena dentro de `DLL_DuelSysAct`).

## Estado do duelo (offsets)

- **Duel state**: `duelState = *(IntPtr*)(libBase + 0x11adc50)` (`DAT_1811adc50`).
- **Place descriptor**: `desc = *(IntPtr*)(libBase + 0x11adc38)` (`DAT_1811adc38`), buffer único (`ushort[]`).
- Stride por player no duelState: **`0x0ddc`**.

Contadores (em `duelState + player*0xddc + off`):

| off | zona |
|---|---|
| `0x0c` | mão |
| `0x10` | **main deck** |
| `0x14` | cemitério |
| `0x18` | extra deck |
| `0x1c` | banidos |

Arrays indexados das zonas (entrada = 4 bytes `[cid(2), state(2)]`), base em
`duelState + base + (player*0x377 + index)*4`:

| location | code | base |
|---|---|---|
| mão | `0xd`/13 | `0x1e4` |
| extra | `0xe`/14 | `0x5a4` |
| main deck | `0xf`/15 | `0x3c4` |
| cemitério | `0x10`/16 | `0x7fc` |
| banidos | `0x11`/17 | `0xa54` |

`uniqueId` da entrada = `(state & 1) + (state >> 8) * 2`.

Campos de comando (em `duelState + off`):

| off | campo |
|---|---|
| `0x3cf8` | cmd (legenda completa em "Detecção de eventos": 0=attack, 3=activate, 4=summon, 6=set, 13=draw, 17=battle…) |
| `0x3ce4` | pending stage |
| `0x3cd0` | command-mode (7 = seleção pendente) |
| `0x3ce0` | command-mode salvo (1 slot, não é pilha) |
| `0x3cdc` | command-available flag |
| `0x3d04` / `0x3d08` / `0x3d0c` | player / position / index do comando |
| `0x3d00` | player que decide a seleção |
| `0x3d10` | zona selecionada / default |
| `0x3d18` | callback da seleção |

Campos do descritor `desc` (índices `ushort`):

| idx | byte | campo |
|---|---|---|
| `[0]` | 0x00 | owner player |
| `[1]` | 0x02 | dest player |
| `[2]` | 0x04 | zona (`&0x1f`) |
| `[3]` | 0x06 | face flag |
| `[4]` | 0x08 | modo (atk/def) |
| `[5]` | 0x0a | location code |
| `[6]` | 0x0c | card uniqueId |
| `[7]` | 0x0e | card text id |
| `[8]` | 0x10 | flags |
| `[9]` | 0x12 | card id |
| `[0x13]` | 0x26 | op type (**4 = special summon**) |
| `[0x14]` | 0x28 | sub-estado (driver do state machine) |

## Detecção de eventos — os dois barramentos (base dos duelHooks)

Há **dois** caminhos pra observar o que acontece no duelo, com finalidades
distintas. A escolha entre eles é o que decide se um evento **pega a CPU**.

### 1. Barramento de intenção (campos de comando, **polling**)

Os campos `0x3cf8`(cmd) / `0x3d04`(player) / `0x3d08`(pos) / `0x3ce4`(pend) são
escritos pelo engine **pros dois lados**: o humano via `DLL_DuelComDoCommand`
(UI) e a **CPU escrevendo o estado direto** (ela **não** chama o DoCommand).
Por isso a detecção tem que ser **polling desses campos dentro de
`DLL_DuelSysAct`**, não hook da função — o hook (`rgcmdlog`) só pega o humano.
Foi assim que o ataque foi achado. Sonda: **`rgsys`** (discovery logger).

- `pend != 0` = intenção real em andamento (`cmd=0`/`pend=0` é **idle**, não ataque).
- `player`/`pos` só são confiáveis com `pend != 0` (fora disso ficam *stale*).
- `pos` = slot relevante do comando: zona do campo (`2`=monstro, `9`=S/T) ou
  location de origem (`13`=mão, `15`=deck).
- `state` (`0x3c50`) avança pelos stages na batalha (no ataque da CPU: 1→2→3).

Legenda de `cmd` (empírica, confirmada jogando — **inclusive com a CPU**):

| cmd | evento | nota |
|---|---|---|
| `0` | **ATTACK** | real só com `pend!=0`; `pos`=zona do atacante |
| `3` | **ACTIVATE** | efeito/magia/armadilha; `pos`=zona da carta (ex.: Lily quick-effect no damage calc) |
| `4` | **SUMMON** | invocação normal face-up; `pos=13`=da mão |
| `6` | **SET** | setar monstro/carta face-down; `pos=13`=da mão |
| `13` | **DRAW** | saca no início do turno; `pos=15`=deck |
| `17` | **→BATTLE** | entra na Battle Phase |
| `18` | →MAIN2 | Main Phase 2 (provável) |
| `19` | END-TURN | fim de turno (provável) |
| `0xc`/12 | SELECT/CONFIRM | confirma slot; pós-ataque vem `player=1 pos=2` (provável **alvo**); auto-SS usa pra zona livre |
| `14` | RECALC | recorre após cada ação (refresh da máscara de comandos) — **ruído** |

**Provado pros dois lados:** a CPU dispara `DRAW`/`ACTIVATE`/`→BATTLE`/`ATTACK`
com `player=1` e `pend!=0` sem nenhum input humano.

### 2. Barramento de resultado (records do emitter)

`FUN_1801085b0` emite um **record de 8 bytes** (opcode + 3 shorts) por evento de
board, num ring buffer (`DAT_1811adc20+0x10`, contador `+0x810`), repassado ao
delegate `DAT_1811adb58` (setado por `DLL_SetAddRecordDelegate`) — é o que o
replay/UI consome. Cobre o **resultado** (destroy, dano, draw efetivo, mover pra
grave/banir). Sonda: **`rgop`** (discovery logger). Opcodes (low byte = tipo):
`0x2c`=move, `0x43`/`0x44`=place, `0x6f`=level — mapeamento empírico em andamento.

**Resumo:** *intenção* (pré-ação, pega CPU) = barramento 1 (`rgsys`);
*resultado* (pós-ação) = barramento 2 (`rgop`).

## DLL_DuelComDoDebugCommand — o "canivete"

`DLL_DuelComDoDebugCommand(player, location, index, cmd)` — dispatcher de
comandos de debug do engine (já bindado no client). Faz move/modify com a
contabilidade do engine. **Não tem summon pro campo.** `location` usa os codes
da tabela acima (13=mão, 15=deck, 16=cemitério…). Alguns `cmd` repurposam os
params (LP/fase).

| cmd | ação | params |
|---|---|---|
| 0 | troca de fase | `index` = fase (0=draw,1=standby,2=main…); `location=0x12` troca turn player |
| 3 | +LP | `location` = valor (default 1000) |
| 4 | −LP | `location` = valor (default 1500) |
| 5 | reseta flags/buffs do campo | — |
| 6 | → mão (de qualquer location; **draw** = deck[0]→mão) | location/index |
| 7 | → topo do deck | |
| 8 | → cemitério | |
| 9 / 10 | banir face-up / face-down | |
| 11 | destruir → cemitério | |
| 12 | destruir (só no campo, `location<7`) | |
| 13 | revelar topo do deck | |
| 15 / 16 | level +1 / −1 (opcode `0x6f`) | location=13 (mão) |
| 17 | dar a carta pro oponente | |
| 18/19 | counters | |
| 20 | embaralhar deck | |
| 21 | toggle de LP (stele) | |
| 22 | (= cmd 8 / variante grave) | |
| 27 | perde o duelo (LP→0) | |
| 1, 2 | emite `0x35` com id `0x178f`/`0x26d1` (não testado) | |

## Primitivas diretas

| Ação | função | RVA | assinatura |
|---|---|---|---|
| **Special summon** | `FUN_180625c00` | `0x625c00` | `(player, cardRefPtr, face, mode, (cid<<16)\|flags, reason)` |
| → cemitério / banir | `FUN_180153dd0` | `0x153dd0` | `(player, location, mode 0=grave 1=banish, uniqueId, flags, reason)` |
| dano / −LP | `FUN_180142750` | `0x142750` | `(player, amount, 1, 0)` |
| ganhar LP | `FUN_180148660` | `0x148660` | `(player, amount, 0, 0)` |
| destruir (campo) | `FUN_1801548f0` | `0x1548f0` | `(player, location, slotPtr, 0x12cd)` |
| controle | `FUN_1801158f0` | `0x1158f0` | `(0, player, alvoPlayer, location)` |
| → mão (struct) | `FUN_180148f50` / `FUN_1801180a0` | — | `(struct*, player, …)` |
| token / criar do nada | `DLL_DuelComCheatCard` | export | `(player, pos, index, cid, faceUp, def)` |
| emit record (low-level) | `FUN_1801085b0` | `0x1085b0` | `(opcode, a, b, c)` |

Infra da colocação (não chamar direto — é state machine):
- `FUN_180588df0` — pump da colocação (SysAct dispatch state 1, `PTR_FUN_18105c688[1]`).
- `FUN_18061ba30` — state machine de colocação (`switch(desc[0x14])`; emite `0x2c`/`0x44`).
- `FUN_180625b50` — "kicker": seta `desc[0x14]=0` (inicia a máquina).
- `FUN_1805928d0` — monta o prompt de seleção de zona (`0x3cd0=7`, default em `0x3d10` via `FUN_180592810`).
- `DLL_DuelComGetCommandMask` (`FUN_180592fe0`) — máscara de comandos válidos (validação).

## Special summon — mecanismo

`FUN_180625c00` **preenche o `desc`** (`[0x13]=4`, card/location/zona/modo a
partir do `cardRefPtr`) e chama `FUN_180625b50` (que seta `desc[0x14]=0` →
inicia o state machine). O `cardRefPtr` é a **entrada de 4 bytes da zona**
(`[cid, state]`) — ex.: `duelState + 0x3c4 + (player*0x377 + deckIndex)*4`.
`param5 = (cardTextId << 16) | flagsLow`.

A colocação roda nos ticks seguintes (pump state 1 → state machine). Ela
**sempre sobe o prompt da zona** (`FUN_1805928d0` → `0x3cd0=7`), respondido por:
- **humano**: UI;
- **CPU**: a IA;
- **auto (nosso)**: `DLL_DuelComDoCommand(player, zonaLivre, 0, 12)` por tick até a carta sair do deck (count cai).

Achado via **breakpoint de hardware** na escrita de `desc+0x26 == 4` (DR0,
write 2 bytes) — o RIP que escreve `op=4` cai dentro da begin-SS.

## Receita C# — special summon (a reimplementar)

> Removido do `DuelDll.cs` (estava como dev `rgss`) até a gente saber fazer
> mid-attack sem cancelar (ver Notas). Esta é a receita pra reconstruir.

Estado base: `dsP = *(IntPtr*)(libBase + 0x11adc50)`; stride por player `0xddc`.

**Special summon do deck** (a carta sai do deck, dispara triggers):

```csharp
// begin-SS: FUN_180625c00(player, cardRef, face, mode, flags, reason), RVA 0x625c00
//   cardRef = ponteiro pra entrada de 4 bytes [cid(2), state(2)] da zona
//   flags   = (cid << 16) | flagsLow   (high word = card id; low word vai pro desc[8])
long pbase     = dsP + player*0xddc;
int  countBefore = ReadInt32(pbase + 0x10);                       // main deck count
long entryAddr = dsP + 0x3c4 + (player*0x377 + deckIndex)*4;       // deck[deckIndex]
int  cid       = ReadInt16(entryAddr);
Func625c00(player, entryAddr, face, mode, (uint)cid << 16, 0);
// depois: auto-zone (o prompt de zona só sobe quando o SysAct processa).
```

**Auto-zone** (responde o prompt de zona automaticamente; roda por tick no
`DLL_DuelSysAct` até a carta sair do deck):

```csharp
// cada tick enquanto pendente:
int cnt = ReadInt32(dsP + player*0xddc + 0x10);
if (cnt < countBefore) { /* colocado, fim */ }
else {
    int zone = FindFreeMonsterZone(player);            // slot 0-6 com cid==0, do centro
    if (zone >= 0) hookDLL_DuelComDoCommand.Original(player, zone, 0, 12);  // cmd 12 = confirma zona
}
```

**Revive do cemitério (estilo Call of the Haunted):** idêntico, trocando a base
da zona de `0x3c4` (deck) por **`0x7fc`** (cemitério) e o índice pelo slot do
monstro no grave. O engine usa a mesma begin-SS; o alvo vem do targeting
(uniqueId→ptr), não de índice fixo. (Detalhe em `duelhooks-design.md` §… e na
análise do cid `0x137d`.)

**Mandar carta → cemitério / banir** (`FUN_180153dd0`, RVA `0x153dd0`):

```csharp
// FUN_180153dd0(player, location, mode 0=grave/1=banish, uniqueId, flags, reason)
// uniqueId resolvido da entrada da zona: (entry & 1) + (entry >> 8) * 2
// baseOff por location (note: +2 vs a tabela de zonas, p/ ler o state):
//   hand 0x1e6 | extra 0x5a6 | deck 0x3c6 | grave 0x7fe | banish 0xa56
long entryAddr = dsP + baseOff + (player*0x377 + index)*4;
int  entry     = ReadInt16(entryAddr);
uint uniqueId  = (uint)((entry & 1) + (entry >> 8) * 2);
Func153dd0(player, location, mode, uniqueId, flags, reason);
```

**Pendências** (por isso foi removido): o auto-zone via `cmd 12` **cancela um
ataque em andamento** (o prompt sobrescreve o command-mode), e mid-attack
dispara a regra de Replay. Reimplementar só depois de resolver isso (ver Notas
+ TODO). A begin-SS (`FUN_180625c00`) e o emit/record continuam no decomp.

## Stat de campo (`FUN_1800b0000`) — buff e a regra do mirror (anti-double)

Hook do **valor de carta no campo**: `FUN_1800b0000` (RVA `0xb0000`), bindado como
`DuelGetFieldCardVal(player, zone, outVal, flags, param5)`. É onde o stat-buff
(atk/def/level) é aplicado — escreve de volta em `outVal`. Hoje o `DuelDll.cs`
tem só o **esqueleto** (depth-guard + zona), com um `TODO(stat-buff)`; a lógica de
buff foi removida pra recriar. Este é o conhecimento pra religar sem bugar.

**Struct `outVal`** (offsets em bytes):

| off | campo |
|---|---|
| `+0` | cid (ushort) |
| `+4` | atk (int) — **escrever o delta aqui** |
| `+8` | def (int) — delta |
| `+0xc` | (int, lido p/ Peek) |
| `+0x14` | type / raça (short) |
| `+0x16` | attr (short) |
| `+0x1a` | level (short) — delta de level |

**Cuidados ao aplicar:**

- **Depth-guard:** `FUN_1800b0000` chama a si mesma (sub-avaliações internas).
  Aplicar o buff **só na chamada mais externa** (`_fieldCardValDepth == 0` após
  decrementar), senão a carta leva o delta múltiplas vezes. Usar um contador
  `[ThreadStatic]` em torno do `.Original`.
- **Só zonas de monstro:** `zone` 0-6. Outras locations (mão/S&T/GY) retornam
  ATK 0 — buffar pintaria valor-fantasma em slot vazio.
- **`flags` bit3 (`& 8`):** contexto **raw/damage** (cálculo de dano). `set` =
  caminho de cómputo; `clear` = leitura "de display".

### A regra do mirror (por que o buff DOBRA no ataque)

Durante a batalha, as leituras **bit3-clear** de `FUN_1800b0000` para os
**combatentes** são servidas de um **snapshot mirror** (`DAT_1811adc60`, RVA
`0x11adc60`) — que **já foi construído** a partir de uma chamada do caminho de
cómputo, **com o buff já somado**. Se você **re-somar** o delta nessa leitura, o
valor **dobra** (é o "ATK dobrado" que aparece na animação de ataque).

Solução: **`IsBattleMirrorCombatant(player, zone)`** detecta se aquela carta é um
combatente cujo valor bit3-clear vem do mirror; nesse caso **não re-aplica** o
delta (os monstros que não estão batalhando continuam recebendo o buff normal).
A detecção espelha o match do próprio engine:

```
duelState +0x1bb8  &0x10  == 0  -> batalha NÃO ativa (sem mirror; aplica normal)
slot pos  = duelState + player*0xddc + zone*0x1c + 0x5e   (ushort)
posId     = (pos & 1) + (pos >> 8) * 2
para idx em {0,1}:                       // 2 combatentes
    entryId   = mirror + idx*0x28 + 0x18   (ushort)
    if posId != entryId: continua
    stateByte = (duelState + 0x1bf1 + (entryId & 0x1ff)*8) & 3
    entry17   = mirror + idx*0x28 + 0x17
    if stateByte == entry17:  -> É combatente do mirror (PULA o buff)
```

Resumo: aplica o delta sempre, **exceto** quando `flags` bit3 está clear **e**
`IsBattleMirrorCombatant` retorna true (leitura do mirror já-buffada). O
`IsBattleMirrorCombatant` continua no `DuelDll.cs` pra reuso.

## Notas importantes

- **Regra de Replay (mid-attack):** invocar **durante** a declaração de ataque
  muda a contagem de monstros do oponente → o jogo dispara o **replay**
  ("continuar o ataque?" + re-selecionar alvo). O "cancelamento" observado é o
  replay, não bug. Invocar mid-attack via `FUN_180625c00` também **preempta** o
  command-mode (o prompt da zona seta `0x3cd0=7`, sobrescrevendo o do ataque).
  Os efeitos de carta conseguem porque rodam **com a batalha suspensa pela
  chain**. Para fazer mid-attack "de verdade" precisaria entrar nesse contexto
  (suspender/retomar a batalha) — **TODO**.
- **Pré-setar a zona não pula o prompt:** `FUN_1805928d0` recalcula e
  sobrescreve `0x3d10`. Forçar `desc[2]`/`0x3d10` antes é inócuo.
- **Timing pós-ataque:** invocar depois que o sistema de comando fica ocioso
  (cmd/pending/mode = 0) é estável.

## Bindings & dev-commands no client (`DuelDll.cs` / `ConsoleHelper.cs`)

> **Removidos do código** (a investigação de RE cumpriu seu papel; este doc é a
> referência). O `DuelDll.cs` ficou só com o stat-buff. Pra reconstruir o
> special-summon / send-to-grave, ver "Receita C#" acima. Removidos: `rgss`
> (`SpecialSummonFromDeck`+`Func625c00`), `rg153` (`FUN_180153dd0`), os discovery
> loggers `rgsys`/`rgop` (+ emit hook), e os probes `rgdesc`/`rgcaller`/`rghwbp`/
> `rgcmd`/`rgcmdlog`/`rgsummon`/`rgdeck`/`rgtokens`.

Continuam no client (pré-existentes ou em uso): `DLL_DuelComDoDebugCommand` /
`DLL_DuelComCheatCard` / `DLL_SetAddRecordDelegate` (bindings nativos, sem
dev-command agora), e os dev-commands atuais `rgbuff` (stat-buff), `rgcardprops`
(props por cid), `rglua` (smoke test MoonSharp).

## TODO / a confirmar

- Legenda de `cmd` 🔶: confirmar `12` (alvo do ataque?), `18` (main2), `19` (end), e capturar standby/main1/change-position.
- Mapear o **barramento de resultado** (`rgop`): opcodes de destroy / LP / draw / →grave / →banish (significado dos args `a/b/c`). Agente de decoder estático rodando em paralelo.
- Assinaturas exatas de **destroy** e **draw** via efeito real (não só debug).
- **Mid-attack via chain**: achar suspender/retomar batalha (`0x3ce0` save é 1 slot).
- Mover entre zonas arbitrárias; mudar posição (atk/def) no campo.
- `subType` (categoria de carta) pro filtro de stat-buff (hoje STUB).
