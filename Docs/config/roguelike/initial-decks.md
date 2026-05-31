# Roguelike `InitialDecks.json` Reference

Curation layer for the **starter decks offered at the start of a run**
(`DataLE/Roguelike/InitialDecks.json`). Each entry names a deck file under
**`Roguelike/StartingDecks/`** plus optional **weight** (rate) and **ascension**
gating, and an optional display-name override.

**Fallback:** when this file is absent — or nothing is eligible for the run's
ascension — the game falls back to the legacy behavior: a uniform shuffle of
every `.json` in `StartingDecks/`, offering `offerCount` of them.

**When changes apply:** read once and cached, so **restart the server** after
editing. Affects new runs only.

---

## Structure

```jsonc
{
  "offerCount": 3,   // how many decks to offer at run start (default 3)

  "decks": [
    { "deck": "Aggro Bomb.json" },
    { "deck": "Burn.json",    "weight": 0.5, "ascension": { "min": 1 } },
    { "deck": "Control.json", "weight": 2.0, "ascension": { "max": 3 }, "name": "Controle Clássico" }
  ]
}
```

## Fields

### Top level

| Field | Type | Default | Notes |
|---|---|---|---|
| `offerCount` | int | `3` | How many decks the run offers to choose from. Clamped to ≥ 1. |
| `decks` | array | — | The curated entries (below). |

### Per deck

| Field | Required | Default | Notes |
|---|---|---|---|
| `deck` | **yes** | — | Filename under `Roguelike/StartingDecks/` (e.g. `"Burn.json"`). Missing files are warned + skipped. |
| `weight` | no | `1.0` | Rate in the weighted, distinct pick. Higher = more likely. `0` (or less) = never offered. |
| `ascension` | no | any | `{ "min": X, "max": Y }` range gating (same shape as `Encounters.json`). Absent ends = open. |
| `name` | no | deck file name | Display-name override for the offer card. |

## How offers are chosen

1. Filter `decks` by the run's **ascension** (`ascension` range must contain it)
   and drop entries whose `deck` file doesn't exist.
2. **Weighted pick without replacement** of `offerCount` entries (seeded by the
   run seed, so a given run always rolls the same offers).
3. If the filtered set is empty → **fallback** to the uniform folder shuffle.

`ascension` semantics match the rest of the roguelike config:

```jsonc
"ascension": { "min": 3, "max": 3 }   // only ascension 3
"ascension": { "min": 3 }            // ascension 3 and up
"ascension": { "max": 3 }            // up to ascension 3
// (absent)                          // any ascension
```

## Notes

- Only `ascension` gates here — starter decks are picked at run start (before any
  act/floor exists), so `act`/`floor` ranges would be inert and aren't supported.
- Decks are **player-format** deck JSONs (same as the files the folder already
  holds); this file only curates which ones are offered, with what weight/gating.
- Keep enough decks eligible for every ascension you support, or the run falls
  back to offering the whole folder.
