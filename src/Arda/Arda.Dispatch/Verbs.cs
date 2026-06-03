namespace Arda.Dispatch;

/// <summary>
/// Canonical verb keys used by <see cref="VerbExtractor"/> and handler registrations.
/// Synthetic keys (for non-Process* system lines) are prefixed with their structural origin.
/// </summary>
public static class Verbs
{
    /// <summary>Synthetic verb for "LOADING LEVEL AreaKey" system lines.</summary>
    public const string LoadingLevel = "LOADING_LEVEL";

    /// <summary>Synthetic verb for "!!! Initializing area! (id): AreaKey" system lines.</summary>
    public const string InitializingArea = "InitializingArea";

    /// <summary>Synthetic verb for the unbracketed "Downloading Map [GUID] ... runtime key GUID[Map_&lt;X&gt;]" asset-loader line.</summary>
    public const string DownloadingMap = "DownloadingMap";

    // Standard LocalPlayer: Process* verbs (literal text from log)
    public const string ProcessAddItem = "ProcessAddItem";
    public const string ProcessDeleteItem = "ProcessDeleteItem";
    public const string ProcessUpdateItemCode = "ProcessUpdateItemCode";
    public const string ProcessLoadSkills = "ProcessLoadSkills";
    public const string ProcessUpdateSkill = "ProcessUpdateSkill";
    public const string ProcessLoadRecipes = "ProcessLoadRecipes";
    public const string ProcessUpdateRecipe = "ProcessUpdateRecipe";
    public const string ProcessStartInteraction = "ProcessStartInteraction";

    // Session / identity
    public const string ProcessAddPlayer = "ProcessAddPlayer";

    // Weather
    public const string ProcessSetWeather = "ProcessSetWeather";

    // Celestial
    public const string ProcessSetCelestialInfo = "ProcessSetCelestialInfo";

    // Map pins
    public const string ProcessMapPinAdd = "ProcessMapPinAdd";
    public const string ProcessMapPinRemove = "ProcessMapPinRemove";

    // Effects
    public const string ProcessAddEffects = "ProcessAddEffects";
    public const string ProcessRemoveEffects = "ProcessRemoveEffects";
    public const string ProcessUpdateEffectName = "ProcessUpdateEffectName";

    // Quests
    public const string ProcessLoadQuests = "ProcessLoadQuests";
    public const string ProcessCompleteQuest = "ProcessCompleteQuest";

    // Garden verbs (Tier 2 passthrough, primary consumer: Samwise)
    public const string ProcessUpdateDescription = "ProcessUpdateDescription";
    public const string ProcessSetPetOwner = "ProcessSetPetOwner";
    public const string ProcessScreenText = "ProcessScreenText";
    public const string ProcessErrorMessage = "ProcessErrorMessage";

    // Vendor verbs (Tier 2 passthrough, primary consumer: Smaug)
    public const string ProcessVendorScreen = "ProcessVendorScreen";
    public const string ProcessVendorAddItem = "ProcessVendorAddItem";
    public const string ProcessVendorUpdateAvailableGold = "ProcessVendorUpdateAvailableGold";

    // Favor verb (dispatched to Npc handler for gift correlation)
    public const string ProcessDeltaFavor = "ProcessDeltaFavor";

    // Position
    public const string ProcessNewPosition = "ProcessNewPosition";

    // Map effects (Tier 2 passthrough, primary consumer: Legolas)
    public const string ProcessMapFx = "ProcessMapFx";

    // Interaction/loot verbs (Tier 2 passthrough, primary consumer: Gandalf)
    public const string ProcessEndInteraction = "ProcessEndInteraction";
    public const string ProcessDoDelayLoop = "ProcessDoDelayLoop";
    public const string ProcessWaitInteraction = "ProcessWaitInteraction";
    public const string ProcessEnableInteractors = "ProcessEnableInteractors";
    public const string ProcessTalkScreen = "ProcessTalkScreen";

    // Vault verbs (Tier 1, multi-consumer: Bilbo, Arwen, accumulator)
    public const string ProcessShowStorageVault = "ProcessShowStorageVault";
    public const string ProcessAddToStorageVault = "ProcessAddToStorageVault";
    public const string ProcessRemoveFromStorageVault = "ProcessRemoveFromStorageVault";

    // Book verb (multi-consumer: Pippin, Saruman/GameState, generic)
    public const string ProcessBook = "ProcessBook";

    // Chat log synthetic verbs
    /// <summary>Synthetic verb for <c>**** Logged In As &lt;char&gt;. Server &lt;server&gt;. ...</c></summary>
    public const string ChatLoginBanner = "CHAT_LOGIN_BANNER";

    /// <summary>Synthetic verb for <c>[Status] X [xN] added to inventory.</c></summary>
    public const string StatusInventory = "STATUS_INVENTORY";

    /// <summary>Synthetic verb for <c>[Channel] Speaker: text</c> player chat lines.</summary>
    public const string ChatPlayerLine = "CHAT_PLAYER_LINE";
}
