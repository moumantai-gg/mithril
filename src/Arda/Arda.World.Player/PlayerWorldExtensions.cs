using System.Collections.Frozen;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.Hosting;
using Arda.World.Player.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Arda.World.Player;

/// <summary>
/// Registers the Player-world L3 handlers with the Arda dispatch pipeline.
/// </summary>
public static class PlayerWorldExtensions
{
    /// <summary>
    /// Add Player-world state handlers (Map, Inventory, Player, Npc, Calendar).
    /// </summary>
    /// <param name="builder">The Arda builder from <c>AddArda()</c>.</param>
    /// <param name="itemPoolFactory">
    /// Optional factory for the item <see cref="InternPool"/>. When provided, the pool
    /// is seeded from reference data (e.g. <c>IReferenceDataService.ItemsByInternalName.Keys</c>)
    /// for true zero-alloc interning. When null, an empty pool is used (allocates on first
    /// encounter, caches for subsequent hits).
    /// </param>
    /// <param name="skillPoolFactory">
    /// Optional factory for the skill <see cref="InternPool"/>. When provided, the pool
    /// is seeded from reference data skill keys. When null, an empty pool is used
    /// (miss-cache handles the ~100 skill keys after first encounter).
    /// </param>
    /// <param name="projectToGameHourFactory">
    /// Optional factory for the game-hour projection function. In production, wired to
    /// <c>at => GameClock.Project(at).Hour</c>. When null, defaults to a constant 0.
    /// </param>
    /// <param name="shiftsFactory">
    /// Optional factory for the shift definition list (slug, startHour tuples in
    /// StartHour order). In production, sourced from <c>IShiftCatalog.Shifts</c>.
    /// When null, defaults to an empty list (no shift events emitted).
    /// </param>
    public static ArdaBuilder AddPlayerWorld(
        this ArdaBuilder builder,
        Func<IServiceProvider, InternPool>? itemPoolFactory = null,
        Func<IServiceProvider, InternPool>? skillPoolFactory = null,
        Func<IServiceProvider, Func<DateTimeOffset, int>>? projectToGameHourFactory = null,
        Func<IServiceProvider, IReadOnlyList<(string Slug, int StartHour)>>? shiftsFactory = null)
    {
        // --- Map handler ---
        // Area keys are few (~50) and stable; miss-cache-only interning is acceptable.
        // Each key allocates once on first encounter and is reused thereafter.
        var areaPool = new InternPool(FrozenDictionary<string, string>.Empty);

        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Map(bus, areaPool);
        });
        builder.Services.AddSingleton<IAreaState>(sp => sp.GetRequiredService<Map>());

        // --- Inventory handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            var itemPool = itemPoolFactory?.Invoke(sp)
                ?? new InternPool(FrozenDictionary<string, string>.Empty);
            return new Inventory(bus, itemPool);
        });
        builder.Services.AddSingleton<IInventoryState>(sp => sp.GetRequiredService<Inventory>());

        // --- Player handler (skills + recipes) ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            var skillPool = skillPoolFactory?.Invoke(sp)
                ?? new InternPool(FrozenDictionary<string, string>.Empty);
            return new Internal.Player(bus, skillPool);
        });
        builder.Services.AddSingleton<IPlayerState>(sp => sp.GetRequiredService<Internal.Player>());
        builder.Services.AddSingleton<ISkillState>(sp => sp.GetRequiredService<Internal.Player>());

        // --- Vault handler (vault deposit/withdrawal correlation + stack correction) ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            var inventory = sp.GetRequiredService<Inventory>();
            return new Vault(bus, inventory);
        });

        // --- Npc handler (interaction context + gift correlation) ---
        // NPC keys (~200) use miss-cache-only interning like area keys.
        var npcPool = new InternPool(FrozenDictionary<string, string>.Empty);

        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Npc(bus, npcPool);
        });
        builder.Services.AddSingleton<INpcState>(sp => sp.GetRequiredService<Npc>());

        // --- Weather handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Weather(bus);
        });
        builder.Services.AddSingleton<IWeatherState>(sp => sp.GetRequiredService<Weather>());

        // --- Session handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Session(bus);
        });
        builder.Services.AddSingleton<ISessionState>(sp => sp.GetRequiredService<Session>());

        // --- Celestial handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Celestial(bus);
        });
        builder.Services.AddSingleton<ICelestialState>(sp => sp.GetRequiredService<Celestial>());

        // --- Map pins handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new MapPins(bus);
        });
        builder.Services.AddSingleton<IMapPinState>(sp => sp.GetRequiredService<MapPins>());

        // --- Map asset loader handler (Downloading Map line) ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new MapAssetLoader(bus);
        });

        // --- Map scope composite (flat IMapState over all map-scoped handlers) ---
        builder.Services.AddSingleton<IMapState>(sp => new MapScope(
            sp.GetRequiredService<Map>(),
            sp.GetRequiredService<Position>(),
            sp.GetRequiredService<Weather>(),
            sp.GetRequiredService<MapPins>(),
            sp.GetRequiredService<MapAssetLoader>()));

        // --- Effects handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Effects(bus);
        });
        builder.Services.AddSingleton<IEffectsState>(sp => sp.GetRequiredService<Effects>());

        // --- Quest handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Quest(bus);
        });
        builder.Services.AddSingleton<IQuestState>(sp => sp.GetRequiredService<Quest>());

        // --- Position handler (Tier 1 state, ProcessNewPosition + ProcessAddPlayer spawn) ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new Position(bus);
        });
        builder.Services.AddSingleton<IPositionState>(sp => sp.GetRequiredService<Position>());

        // --- Calendar handler (line observer — sees every timestamp) ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            var projectToGameHour = projectToGameHourFactory?.Invoke(sp)
                ?? (Func<DateTimeOffset, int>)(_ => 0);
            var shifts = shiftsFactory?.Invoke(sp) ?? [];
            return new Calendar(bus, projectToGameHour, shifts);
        });
        builder.Services.AddSingleton<ICalendarState>(sp => sp.GetRequiredService<Calendar>());
        builder.AddLineObserver<Calendar>();

        // --- Appearance observer (line observer — Download appearance loop pattern) ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new AppearanceObserver(bus);
        });
        builder.AddLineObserver<AppearanceObserver>();

        // --- Dispatch table wiring ---
        builder.ConfigureHandlers((sp, registry) =>
        {
            var map = sp.GetRequiredService<Map>();
            RegisterHandler(registry, Verbs.LoadingLevel, map);
            RegisterHandler(registry, Verbs.InitializingArea, map);

            var mapAsset = sp.GetRequiredService<MapAssetLoader>();
            RegisterHandler(registry, Verbs.DownloadingMap, mapAsset);

            var inventory = sp.GetRequiredService<Inventory>();
            var player = sp.GetRequiredService<Internal.Player>();
            var npc = sp.GetRequiredService<Npc>();
            var vault = sp.GetRequiredService<Vault>();
            var weather = sp.GetRequiredService<Weather>();
            var session = sp.GetRequiredService<Session>();
            var celestial = sp.GetRequiredService<Celestial>();
            var mapPins = sp.GetRequiredService<MapPins>();
            var effects = sp.GetRequiredService<Effects>();
            var quest = sp.GetRequiredService<Quest>();
            var position = sp.GetRequiredService<Position>();

            // Order matters: Map must run before StateResetHandler for LOADING_LEVEL.
            // Map clears CurrentArea; StateResetHandler then resets downstream state.
            // DispatchTable preserves insertion order within a verb's handler list.
            RegisterHandler(registry, Verbs.LoadingLevel,
                new StateResetHandler(inventory, player, npc, vault, weather, session, celestial, mapPins, position, effects, quest));

            RegisterHandler(registry, Verbs.ProcessAddItem, new AddItemHandler(inventory));
            RegisterHandler(registry, Verbs.ProcessAddItem, new VaultAddItemHandler(vault));
            RegisterHandler(registry, Verbs.ProcessDeleteItem, new DeleteItemHandler(inventory));
            RegisterHandler(registry, Verbs.ProcessUpdateItemCode, new UpdateItemCodeHandler(inventory));

            RegisterHandler(registry, Verbs.ProcessLoadSkills, player.LoadSkillsHandler);
            RegisterHandler(registry, Verbs.ProcessUpdateSkill, player.UpdateSkillHandler);
            RegisterHandler(registry, Verbs.ProcessLoadRecipes, player.LoadRecipesHandler);
            RegisterHandler(registry, Verbs.ProcessUpdateRecipe, player.UpdateRecipeHandler);

            RegisterHandler(registry, Verbs.ProcessStartInteraction, new StartInteractionHandler(npc));
            RegisterHandler(registry, Verbs.ProcessDeleteItem, new NpcDeleteItemHandler(npc));
            RegisterHandler(registry, Verbs.ProcessDeleteItem, new VaultDeleteItemHandler(vault));

            RegisterHandler(registry, Verbs.ProcessDeltaFavor, new DeltaFavorHandler(npc));

            var pub = sp.GetRequiredService<IDomainEventPublisher>();

            // --- Tier 1 state handlers (multi-consumer) ---
            RegisterHandler(registry, Verbs.ProcessSetWeather, weather);
            RegisterHandler(registry, Verbs.ProcessAddPlayer, session);
            RegisterHandler(registry, Verbs.ProcessNewPosition, position);
            RegisterHandler(registry, Verbs.ProcessAddPlayer, position);
            RegisterHandler(registry, Verbs.ProcessSetCelestialInfo, celestial);

            RegisterHandler(registry, Verbs.ProcessMapPinAdd, mapPins.PinAddHandler);
            RegisterHandler(registry, Verbs.ProcessMapPinRemove, mapPins.PinRemoveHandler);

            RegisterHandler(registry, Verbs.ProcessAddEffects, effects.AddHandler);
            RegisterHandler(registry, Verbs.ProcessRemoveEffects, effects.RemoveHandler);
            RegisterHandler(registry, Verbs.ProcessUpdateEffectName, effects.UpdateNameHandler);

            RegisterHandler(registry, Verbs.ProcessBook, quest);
            RegisterHandler(registry, Verbs.ProcessLoadQuests, quest.LoadQuestsHandler);
            RegisterHandler(registry, Verbs.ProcessCompleteQuest, quest.CompleteQuestHandler);

            // --- Garden passthrough handlers (Tier 2, single consumer: Samwise) ---
            RegisterHandler(registry, Verbs.ProcessUpdateDescription, new UpdateDescriptionHandler(pub));
            RegisterHandler(registry, Verbs.ProcessSetPetOwner, new SetPetOwnerHandler(pub));
            RegisterHandler(registry, Verbs.ProcessScreenText, new ScreenTextHandler(pub));
            RegisterHandler(registry, Verbs.ProcessErrorMessage, new ErrorMessageHandler(pub));

            // --- Vendor handlers (routed through Npc for NPC-key enrichment; primary consumer: Smaug) ---
            RegisterHandler(registry, Verbs.ProcessVendorScreen, new VendorScreenHandler(npc));
            RegisterHandler(registry, Verbs.ProcessVendorAddItem, new VendorAddItemHandler(npc));
            RegisterHandler(registry, Verbs.ProcessVendorUpdateAvailableGold, new VendorGoldHandler(pub));

            // --- Vault handlers (Tier 1, multi-consumer: Bilbo, Arwen, accumulator) ---
            // NpcVaultOpenedHandler runs first so Npc knows to suppress
            // gift-pending stashes for deposit deletes that follow.
            RegisterHandler(registry, Verbs.ProcessShowStorageVault, new NpcVaultOpenedHandler(npc));
            RegisterHandler(registry, Verbs.ProcessShowStorageVault, new VaultShowHandler(vault));
            RegisterHandler(registry, Verbs.ProcessRemoveFromStorageVault, new VaultWithdrawHandler(vault));
            RegisterHandler(registry, Verbs.ProcessAddToStorageVault, new VaultDepositHandler(vault));

            // --- Interaction/loot passthrough handlers (Tier 2, primary consumer: Gandalf) ---
            // Order matters: NpcEndInteractionHandler must clear Npc state
            // BEFORE the passthrough EndInteractionHandler publishes
            // InteractionEnded — subscribers must see a clean Npc state.
            RegisterHandler(registry, Verbs.ProcessEndInteraction, new NpcEndInteractionHandler(npc));
            RegisterHandler(registry, Verbs.ProcessEndInteraction, new EndInteractionHandler(pub));
            RegisterHandler(registry, Verbs.ProcessDoDelayLoop, new DelayLoopHandler(pub));
            RegisterHandler(registry, Verbs.ProcessWaitInteraction, new WaitInteractionHandler(pub));
            RegisterHandler(registry, Verbs.ProcessEnableInteractors, new EnableInteractorsHandler(pub));
            RegisterHandler(registry, Verbs.ProcessTalkScreen, new TalkScreenHandler(pub));

            // --- Map effects passthrough handler (Tier 2, primary consumer: Legolas) ---
            RegisterHandler(registry, Verbs.ProcessMapFx, new MapFxHandler(pub));

            // --- Book handler (multi-consumer: Pippin, Saruman/GameState, generic) ---
            RegisterHandler(registry, Verbs.ProcessBook, new ProcessBookHandler(pub));
        });

        return builder;
    }

    private static void RegisterHandler(
        Dictionary<string, List<IFrameHandler>> registry,
        string verb,
        IFrameHandler handler)
    {
        if (!registry.TryGetValue(verb, out var list))
        {
            list = [];
            registry[verb] = list;
        }
        list.Add(handler);
    }
}
