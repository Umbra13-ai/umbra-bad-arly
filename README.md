# Bad Arly

A BepInEx mod for Demeo: Arly, the crossbow sellsword, has turned. **Corrupted Arly** joins the monsters, glowing with corruption, and fights the party with his own crossbow tricks, an elf's aimed shot and a bandit's light fingers.

Only the host needs the mod. Guests, including players on standalone Quest, join without installing anything.

> **Testing build.** This is an early version made for testing. It has had little play so far, so expect rough edges. Problems and ideas are welcome as GitHub issues.

## Features

- **A new enemy:** Corrupted Arly joins each floor's monster deck from the second floor on, so the game places him like any other monster. Some floors have none.
- **His shots:**
  - **Arbalestier Arrow:** his plain crossbow bolt, every turn.
  - **Arbalestier Barrage:** a volley of bolts (every 2 turns).
  - **Poisoned Tip:** a poisoning bolt (every 2 turns).
  - **Panic Powder Arrow:** a charged shot that panics its target (every 3 turns).
  - **Arrow Snipe:** the Elven Archer's long aimed shot (every 2 turns).
- **His tricks:**
  - **Bandage:** he patches his own wounds (every 3 turns).
  - **Steal Gold:** up close he robs a hero, like a bandit, then runs off into the fog with the gold. Kill him to get it back (every 3 turns).
  - A melee swing when every shot is waiting.
- **He never repeats himself:** no shot or trick is used twice in one turn, and the big ones wait their turns. Only his own use of them is limited; the abilities stay as they are for every other piece.
- **The look:** the purple Corruption glow on him all the time, and a brief healing-aura flash each time he shoots. Both are visual only and change nothing in play.
- **Fair to a hired Arly:** a sellsword you hire is a separate piece and fights for you as normal.

## What guests see

Arly's model and his name, his glow and the flash, his shots and tricks (the game's own abilities, played on every machine), and his health through the game's own syncs.

## Requirements

- Demeo on PC with BepInEx 5. Tested with Demeo 1.42 (PC VR) and BepInEx 5.4.22.
- Works on its own, or alongside other mods such as HouseRules rulesets (including Demeo Revolutions, whose monster deck he joins).

## Installation

1. Install BepInEx 5 in your Demeo folder, as for other Demeo mods.
2. With Demeo closed, put `BadArly.dll` in `BepInEx\plugins`.

## Settings

Everything is in `BepInEx\config\umbra.badarly.cfg`, created on first launch:

- **General:** `Enabled`, the master switch.
- **Corrupted Arly:**
  - `DeckCount`: how many go into each floor's deck (default 1)
  - `FromFloor`: the first floor he can appear on (default 2)
  - `Health` 14, `Armor` 0, `Attack` 3, `CritDamage` 6, `Move` 5 (health at full strength; the game scales monsters by party size)
  - `Abilities`, `Behaviours`: his shots and tricks, and his AI, by the game's names
  - `Cooldowns`: his own cooldowns in his turns, `Ability: turns` (0 = none); anything not listed gets 1
- **Looks:**
  - `Corrupted`: the Corruption glow (default on)
  - `CastLook`: a look shown during each shot (default `BossHandHealState`, a healing-aura flash; not shown on the Mad Elven King's floor, where he reacts to it)
  - `ExtraLook`: a second always-on look (default none)

## How it works

When the game creates Arly on the monsters' team, the host gives him the corrupted setup: his shots and tricks, his stats and his AI. His piece already counts as a monster in every player's game, so the game's syncs read the same for guests. His cooldowns are kept on him alone, and his looks are status effects the game carries to guests.

## Logs

`BepInEx\LogOutput.log` in the Demeo folder records his setup, each floor he is added to, and each time he appears.

## Building

```powershell
dotnet build .\BadArly\BadArly.csproj -c Release -p:GameDir="<your Demeo folder>"
```

## Credits

- Made by Umbra (Umbra13-ai).
- Built on BepInEx and Harmony.
- Demeo is made by Resolution Games. This is an unofficial fan mod, not made or endorsed by Resolution Games.

## License

MIT License. See [LICENSE](LICENSE).
