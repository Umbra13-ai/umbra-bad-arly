using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Boardgame;
using Boardgame.Board;
using Boardgame.BoardEntities;
using Boardgame.BoardEntities.Abilities;
using Boardgame.Data;
using Boardgame.SerializableEvents;
using DataKeys;
using HarmonyLib;
using UnityEngine;
using Utils;

namespace BadArly;

/// <summary>
/// Corrupted Arly: Arly the crossbow sellsword fighting for the monsters, wearing the corrupted look. He joins each
/// floor's monster deck from the second floor on. Host only; guests need nothing.
/// </summary>
/// <remarks>
/// His piece (SellswordArbalestierActive) counts as a monster with an AI in every player's game, so the game's full
/// syncs read the same on guests' machines (unlike hero pieces, which guests treat as players). His setup is applied
/// only when the game creates that piece on the monsters' team, so a hired Arly is untouched. Guests see Arly's model
/// and name; his health and stats reach them through the game's syncs, and the Corruption look is a status effect,
/// which travels in the full sync.
/// </remarks>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class BadArlyPlugin : BaseUnityPlugin
{
    internal const string PluginId = "umbra.badarly";
    internal const string PluginName = "Bad Arly";
    internal const string PluginVersion = "0.2.0";

    private const BoardPieceId ArlyPiece = BoardPieceId.SellswordArbalestierActive;
    private const int LookTurns = 99;

    private static readonly FieldInfo? GameContextField =
        typeof(GameHub).GetField("gameContext", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly AccessTools.FieldRef<EffectSink, int> EffectSinkOwnerRef =
        AccessTools.FieldRefAccess<EffectSink, int>("ownerPieceId");

    // Taken off his copy: party-side types (a hired Arly's), and the panic Revolutions puts on whoever hits Arly as an ally.
    private static readonly PieceType[] PartyTypes =
    {
        PieceType.Player, PieceType.Bot, PieceType.Downable, PieceType.NonAttackable, PieceType.NonGrabbable,
        PieceType.PreserveBetweenLevels, PieceType.UpdateFogOfWar,
        PieceType.RefillPotionsOnLevelTransition, PieceType.CanReceiveStatBoostPotions,
    };

    private static BadArlyPlugin? instance;
    private static AbilityFactory? abilityFactory;

    private readonly Dictionary<GameConfigType, PieceConfigData> copies = new();
    private readonly HashSet<int> seen = new();
    private ConfigEntry<bool> modEnabled = null!;
    private ConfigEntry<int> deckCount = null!;
    private ConfigEntry<int> fromFloor = null!;
    private ConfigEntry<int> health = null!;
    private ConfigEntry<int> armor = null!;
    private ConfigEntry<int> attack = null!;
    private ConfigEntry<int> crit = null!;
    private ConfigEntry<int> move = null!;
    private ConfigEntry<string> abilities = null!;
    private ConfigEntry<string> behaviours = null!;
    private ConfigEntry<string> cooldownsSetting = null!;
    private ConfigEntry<bool> corruptedLook = null!;
    private ConfigEntry<string> extraLook = null!;
    private ConfigEntry<string> castLook = null!;
    private string? cooldownsParsedFrom;
    private Dictionary<AbilityKey, int> cooldowns = new();
    private Harmony? harmony;
    private bool patchFailed;
    private bool inLevel;
    private float nextCheck;

    private void Awake()
    {
        instance = this;
        modEnabled = Config.Bind("General", "Enabled", true, "Corrupted Arly joins the monsters.");
        deckCount = Config.Bind("Corrupted Arly", "DeckCount", 1, new ConfigDescription(
            "How many go into each floor's monster deck. The game draws from the deck as it spawns, so a floor gets at most this many, and sometimes none.",
            new AcceptableValueRange<int>(0, 5)));
        fromFloor = Config.Bind("Corrupted Arly", "FromFloor", 2, new ConfigDescription(
            "The first floor whose deck gets him (1 = the entrance floor too).", new AcceptableValueRange<int>(1, 10)));
        health = Config.Bind("Corrupted Arly", "Health", 14, "Health at full strength (3 heroes); the game scales monsters by party size.");
        armor = Config.Bind("Corrupted Arly", "Armor", 0, "Armor.");
        attack = Config.Bind("Corrupted Arly", "Attack", 3, "Damage of his basic attack.");
        crit = Config.Bind("Corrupted Arly", "CritDamage", 6, "Damage of a critical hit.");
        move = Config.Bind("Corrupted Arly", "Move", 5, "Tiles he can move in a turn.");
        abilities = Config.Bind("Corrupted Arly", "Abilities", "ArbalestierArrow, ArbalestierBarrage, ArbalestierPoisonedTip, PanicPowderArrow, EnemyArrowSnipe, EnemyHumanHeal, EnemyStealGold, EnemyMelee",
            "Comma-separated ability names (the game's). EnemyArrowSnipe is the Elven Archer's aimed shot, EnemyHumanHeal his own bandaging, " +
            "EnemyStealGold the Bandit's theft (he flees into the fog with it; killing him gets it back). EnemyMelee is his swing when every shot is waiting.");
        behaviours = Config.Bind("Corrupted Arly", "Behaviours", "Patrol, RangedAttackHighPrio, RangedSpellCaster, FollowPlayerRangedAttacker, SellswordHeal, AttackAndRetreat, FleeToFOW, AttackPlayer",
            "Comma-separated AI behaviour names (the game's).");
        cooldownsSetting = Config.Bind("Corrupted Arly", "Cooldowns", "ArbalestierArrow: 0, ArbalestierBarrage: 2, ArbalestierPoisonedTip: 2, PanicPowderArrow: 3, EnemyArrowSnipe: 2, EnemyHumanHeal: 3, EnemyStealGold: 3",
            "His own cooldowns, in his turns: 'Ability: turns' (0 = none). Any ability not listed gets 1, so nothing is used twice in one turn; " +
            "his basic attack is never limited. Only he is affected: abilities are shared with other pieces.");
        corruptedLook = Config.Bind("Looks", "Corrupted", true,
            "He wears the Corruption look (the purple corruption glow), kept on all the time. It changes nothing in play, and guests see it.");
        castLook = Config.Bind("Looks", "CastLook", "BossHandHealState",
            "A look shown each time he shoots or casts: the game's add-effect event just before the shot and remove-effect just after, so every " +
            "player sees it. BossHandHealState is the Mad King's hands' healing aura; it does nothing " +
            "on him (only the Mad King reacts to it, on his own floor, where he then shows no cast look). Empty = none.");
        extraLook = Config.Bind("Looks", "ExtraLook", string.Empty,
            "A second look worn all the time with Corruption, a status effect name (the game's). ForestEndLevelChestHalfOpened is the swirl chosen in the beholder test. Empty = none. Use only look-only effects " +
            "such as AbilityBuildUp or ChargeUp (others change play).");

        harmony = new Harmony(PluginId);
        try
        {
            harmony.Patch(AccessTools.Method(typeof(GameStartup), "InitializeGame"),
                postfix: new HarmonyMethod(typeof(BadArlyPlugin), nameof(InitializeGamePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Piece), nameof(Piece.CreatePiece)),
                prefix: new HarmonyMethod(typeof(BadArlyPlugin), nameof(CreatePiecePrefix)));
            harmony.Patch(AccessTools.Method(typeof(AIDirectorDeckConstructor), nameof(AIDirectorDeckConstructor.ConstructMonsterDeck)),
                postfix: new HarmonyMethod(typeof(BadArlyPlugin), nameof(ConstructMonsterDeckPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Boardgame.BoardEntities.AI.PieceAI), nameof(Boardgame.BoardEntities.AI.PieceAI.CreatePlan)),
                postfix: new HarmonyMethod(typeof(BadArlyPlugin), nameof(CastLookPostfix)));
            harmony.Patch(AccessTools.Method(typeof(EffectSink), nameof(EffectSink.SetAbilityCountdown)),
                prefix: new HarmonyMethod(typeof(BadArlyPlugin), nameof(SetAbilityCountdownPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Boardgame.BoardEntities.AI.Behaviour), "CanUseAbility",
                    new[] { typeof(Piece), typeof(Ability), typeof(Boardgame.BoardEntities.AI.AILogger) }),
                prefix: new HarmonyMethod(typeof(BadArlyPlugin), nameof(CanUseAbilityPrefix)));
        }
        catch (Exception e)
        {
            patchFailed = true;
            Logger.LogError($"The game could not be patched, so the mod stays off. Look for a newer version. {e}");
            return;
        }

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded: {(modEnabled.Value ? $"{deckCount.Value} per floor deck from floor {fromFloor.Value}" : "off")}.");
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
    }

    private static bool Active => instance != null && !instance.patchFailed && instance.modEnabled.Value;

    private static GameContext? Context => GameContextField?.GetValue(null) as GameContext;

    private static bool IsHost(GameContext? context) => context?.networkController != null && context.networkController.IsMasterClient;

    private bool IsOurs(Piece? piece) => piece != null && copies.Values.Any(copy => ReferenceEquals(copy, piece.GetPieceConfig()));

    private void Update()
    {
        try
        {
            var context = Context;
            var playing = context != null && !GameHub.NoActiveLevel && context.pieceAndTurnController != null && context.gameStateMachine != null
                && context.gameStateMachine.GetCurrentGameState() == GameStates.PlayingState;
            if (!playing)
            {
                if (inLevel)
                {
                    inLevel = false;
                    seen.Clear();
                }

                return;
            }

            inLevel = true;
            if (!Active || !IsHost(context) || Time.unscaledTime < nextCheck)
            {
                return;
            }

            nextCheck = Time.unscaledTime + 0.5f;
            KeepLooks(context!);
        }
        catch (Exception e)
        {
            Logger.LogError($"Update failed: {e}");
        }
    }

    /// <summary>
    /// Keeps his looks on: added when he first appears, topped up before they run out, and put back if something
    /// cleared them (Invulnerability clears Corruption). A full sync carries them to guests.
    /// </summary>
    private void KeepLooks(GameContext context)
    {
        var looks = new List<EffectStateType>();
        if (corruptedLook.Value)
        {
            looks.Add(EffectStateType.Corruption);
        }

        var extra = extraLook.Value?.Trim() ?? string.Empty;
        if (extra.Length > 0 && !char.IsDigit(extra[0]) && Enum.TryParse<EffectStateType>(extra, true, out var extraEffect) && Enum.IsDefined(typeof(EffectStateType), extraEffect))
        {
            looks.Add(extraEffect);
        }

        var changed = false;
        foreach (var piece in context.pieceAndTurnController.Pieces(PieceAndTurnController.SearchPiece.TurnQueue).ToList())
        {
            if (piece == null || piece.IsDead() || !IsOurs(piece))
            {
                continue;
            }

            if (seen.Add(piece.networkID))
            {
                Logger.LogInfo($"Corrupted Arly #{piece.networkID} is on the board at {piece.gridPos.min}: HP {piece.GetHealth()}/{piece.GetMaxHealth()}, " +
                    $"AI={(piece.pieceAI != null)}, player={piece.IsPlayer()}.");
            }

            foreach (var look in looks)
            {
                if (!piece.HasEffectState(look))
                {
                    piece.effectSink.AddStatusEffect(look, LookTurns);
                    changed = true;
                }
                else if (piece.effectSink.GetEffectStateDurationTurnsLeft(look) < 10)
                {
                    piece.effectSink.SetStatusEffectDuration(look, LookTurns);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            context.serializableEventQueue.SendResponseEvent(SerializableEvent.CreateRecovery());
            Logger.LogInfo($"Corrupted Arly: looks [{string.Join(", ", looks)}] put on; full sync sent.");
        }
    }

    private static void InitializeGamePostfix(GameStartup __instance)
    {
        try
        {
            abilityFactory = Traverse.Create(__instance).Field<AbilityFactory>("abilityFactory").Value;
        }
        catch (Exception e)
        {
            instance?.Logger.LogWarning($"The game's ability loader could not be read: {e.Message}");
        }
    }

    /// <summary>Gives Arly his monster setup as the game creates him on the monsters' team. A hired Arly is untouched.</summary>
    private static void CreatePiecePrefix(ref PieceConfigData config, BoardPieceId pieceType, Team team)
    {
        if (!Active || pieceType != ArlyPiece || team != Team.Two || !IsHost(Context))
        {
            return;
        }

        try
        {
            var copy = instance!.SetupFor(Context!);
            if (copy != null)
            {
                config = copy;
            }
        }
        catch (Exception e)
        {
            instance!.Logger.LogError($"Setting up Corrupted Arly failed; he keeps the game's own setup. {e}");
        }
    }

    private PieceConfigData? SetupFor(GameContext context)
    {
        var configType = MotherbrainGlobalVars.CurrentConfig;
        if (copies.TryGetValue(configType, out var existing) && existing != null)
        {
            return existing;
        }

        var baseConfig = context.gameDataAPI.GetPieceConfig(ArlyPiece, configType);
        if (baseConfig == null)
        {
            Logger.LogWarning($"No setup found for Arly in {configType}.");
            return null;
        }

        var copy = UnityEngine.Object.Instantiate(baseConfig);
        copy.name = "Bad Arly";
        var types = (baseConfig.PieceType ?? Array.Empty<PieceType>()).Where(type => !PartyTypes.Contains(type)).ToList();
        foreach (var added in new[] { PieceType.Creature, PieceType.ShowHealthbar })
        {
            if (!types.Contains(added))
            {
                types.Add(added);
            }
        }

        copy.PieceType = types.ToArray();
        copy.ApplyEffectOnHit = EffectStateType.Undefined;
        copy.InnateCounterDamage = 0;
        copy.InnateCounterDamageExtraDamage = 0;
        copy.UseWhenKilled = Array.Empty<AbilityKey>();
        copy.ActionPoint = 2;
        copy.StartHealth = Math.Max(1, health.Value);
        copy.StartArmor = Math.Max(0, armor.Value);
        copy.AttackDamage = Math.Max(0, attack.Value);
        copy.CriticalHitDamage = Math.Max(0, crit.Value);
        copy.MoveRange = Math.Max(0, move.Value);
        var abilityKeys = ParseNames<AbilityKey>(abilities.Value, "abilities");
        var behaviourKeys = ParseNames<DataKeys.Behaviour>(behaviours.Value, "behaviours");
        if (abilityKeys.Length > 0)
        {
            copy.Abilities = abilityKeys;
        }

        if (behaviourKeys.Length > 0)
        {
            copy.Behaviours = behaviourKeys;
        }

        if (abilityFactory != null)
        {
            copy.AbilityPromises = abilityFactory.LoadAbilities(copy.Abilities);
        }

        copies[configType] = copy;
        Logger.LogInfo($"Corrupted Arly set up for {configType}: abilities [{string.Join(", ", copy.Abilities)}], behaviours [{string.Join(", ", copy.Behaviours)}], " +
            $"HP {copy.StartHealth}, armor {copy.StartArmor}, attack {copy.AttackDamage}, crit {copy.CriticalHitDamage}, move {copy.MoveRange}, types [{string.Join(", ", copy.PieceType)}].");
        return copy;
    }

    /// <summary>Adds him to the floor's monster deck, after the game (or Revolutions) has built it.</summary>
    private static void ConstructMonsterDeckPostfix(MonsterDeck __result, int floorIndex, IRnd rng)
    {
        if (!Active || __result?.monsterDeckStandard == null || !IsHost(Context))
        {
            return;
        }

        try
        {
            var self = instance!;
            if (self.deckCount.Value <= 0 || floorIndex < self.fromFloor.Value)
            {
                return;
            }

            for (var i = 0; i < self.deckCount.Value; i++)
            {
                var entry = new MonsterDeck.MonsterDeckEntry { BoardPieceId = ArlyPiece, enemyWeight = EnemyWeight.Light, isRedrawEnabled = false };
                var at = __result.monsterDeckStandard.Count == 0 ? 0 : rng.Range(0, __result.monsterDeckStandard.Count + 1);
                __result.monsterDeckStandard.Insert(at, entry);
            }

            self.Logger.LogInfo($"Floor deck {floorIndex}: Corrupted Arly x{self.deckCount.Value} added among {__result.monsterDeckStandard.Count} entries.");
        }
        catch (Exception e)
        {
            instance!.Logger.LogError($"Adding Corrupted Arly to the monster deck failed: {e}");
        }
    }

    /// <summary>
    /// His cast look: around each ability in his plan (shots and his melee swing), the game's own add-effect and
    /// remove-effect events, which every client plays in order with the shot. The AI plans only on the host.
    /// </summary>
    private static void CastLookPostfix(Piece piece, ref SerializableEvent[] __result)
    {
        if (!Active || __result == null || __result.Length == 0 || piece == null || !instance!.IsOurs(piece) || !IsHost(Context))
        {
            return;
        }

        try
        {
            var name = instance.castLook.Value?.Trim() ?? string.Empty;
            if (name.Length == 0 || char.IsDigit(name[0]) || !Enum.TryParse<EffectStateType>(name, true, out var look) || !Enum.IsDefined(typeof(EffectStateType), look))
            {
                return;
            }

            // The Mad King reacts to his hands' healing aura on his own floor; no cast look there.
            if (look == EffectStateType.BossHandHealState && Context!.pieceAndTurnController.Pieces().Any(p => p != null && p.boardPieceId == BoardPieceId.BossTown))
            {
                return;
            }

            var events = new List<SerializableEvent>(__result);
            var casts = 0;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i] is not SerializableEventUseAbility use || use.pieceId != piece.networkID)
                {
                    continue;
                }

                events.Insert(i, SerializableEvent.CreateAddEffectState(piece.networkID, look, 1));
                events.Insert(i + 2, SerializableEvent.CreateRemoveEffectState(piece.networkID, look));
                i += 2;
                casts++;
            }

            if (casts > 0)
            {
                __result = events.ToArray();
            }
        }
        catch (Exception e)
        {
            instance!.Logger.LogError($"Corrupted Arly's cast look failed; the turn plays without it. {e}");
        }
    }

    /// <summary>His own cooldown, in his turns: the configured value, or 1 for any other ability; none for his basic attack.</summary>
    private bool TryGetCooldown(AbilityKey ability, out int turns)
    {
        turns = 0;
        if (ability == AbilityKey.EnemyMelee || ability == AbilityKey.PlayerMelee)
        {
            return false;
        }

        if (cooldownsParsedFrom != cooldownsSetting.Value)
        {
            cooldownsParsedFrom = cooldownsSetting.Value;
            cooldowns = new Dictionary<AbilityKey, int>();
            foreach (var part in (cooldownsSetting.Value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split(':');
                if (pieces.Length == 2 && int.TryParse(pieces[1].Trim(), out var value)
                    && Enum.TryParse<AbilityKey>(pieces[0].Trim(), true, out var key) && Enum.IsDefined(typeof(AbilityKey), key))
                {
                    cooldowns[key] = value;
                }
                else
                {
                    Logger.LogWarning($"Cooldowns: '{part.Trim()}' ignored; expected 'Ability: turns'.");
                }
            }
        }

        turns = cooldowns.TryGetValue(ability, out var configured) ? configured : 1;
        return true;
    }

    /// <summary>When he uses an ability, the countdown stored on him comes from his Cooldowns setting (N is stored as N - 1).</summary>
    private static void SetAbilityCountdownPrefix(EffectSink __instance, AbilityKey ability, ref int countdown)
    {
        if (!Active)
        {
            return;
        }

        try
        {
            var context = Context;
            if (context?.pieceAndTurnController == null || !IsHost(context)
                || !context.pieceAndTurnController.TryGetPiece(EffectSinkOwnerRef(__instance), out var piece) || !instance!.IsOurs(piece)
                || !instance.TryGetCooldown(ability, out var turns))
            {
                return;
            }

            countdown = turns <= 0 ? -1 : Math.Max(countdown, turns - 1);
        }
        catch (Exception e)
        {
            instance!.Logger.LogWarning($"Cooldown for {ability} not applied: {e.Message}");
        }
    }

    /// <summary>The AI treats one of his abilities as not ready while its countdown on him is running.</summary>
    private static bool CanUseAbilityPrefix(Piece p, Ability ability, ref bool __result)
    {
        if (!Active || p == null || ability == null || !instance!.IsOurs(p)
            || !instance.TryGetCooldown(ability.abilityKey, out var turns) || turns <= 0 || p.GetCooldownForAbility(ability.abilityKey) < 0)
        {
            return true;
        }

        __result = false;
        return false;
    }

    private T[] ParseNames<T>(string list, string what)
        where T : struct
    {
        var result = new List<T>();
        foreach (var raw in (list ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (char.IsDigit(name[0]) || !Enum.TryParse<T>(name, true, out var value) || !Enum.IsDefined(typeof(T), value))
            {
                Logger.LogWarning($"{what}: '{name}' is not a name the game knows; left out.");
                continue;
            }

            if (!result.Contains(value))
            {
                result.Add(value);
            }
        }

        return result.ToArray();
    }
}
