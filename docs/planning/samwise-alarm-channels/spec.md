# Samwise — alarm channels, per-stage loop, collision behavior

**Tracked in:** _no issue yet_

## Context

Today's Samwise alarms ([AlarmService.cs](../../../src/Samwise.Module/Alarms/AlarmService.cs), [AlarmSettings.cs](../../../src/Samwise.Module/Alarms/AlarmSettings.cs)) are one-shot per `(char|plot|stage)` and rely on a single global toggle for cross-sound behavior: [`AudioPlayer.ConcurrentPlayback`](../../../src/Mithril.Shared/Audio/AudioPlayer.cs#L22), surfaced as the "Allow concurrent alarm sounds" checkbox in both [SamwiseSettingsView.xaml:35-37](../../../src/Samwise.Module/Views/SamwiseSettingsView.xaml#L35-L37) and [GandalfSettingsView.xaml:18-20](../../../src/Gandalf.Module/Views/GandalfSettingsView.xaml#L18-L20).

The user wants three behaviors:

1. **Loop a stage's sound until it's resolved or dismissed** — currently sounds play once and stop.
2. **Suppress a new alarm if one is already playing** — concrete motivating case: 8 plots ripen within seconds and the user gets one sound, not eight.
3. **Group stages so the user can pick which alarms share collision behavior** — e.g., "Ripe and Thirsty share a stream; NeedsFertilizer is independent."

The natural shape that came out of brainstorming: per-stage `Loop` flag + per-stage `ChannelId`, with `AlarmChannel` carrying a `CollisionBehavior` of `Mix | Replace | Suppress`. Channels are user-named groupings, auto-created from stage assignments. Inside a channel, the behavior decides what happens when a new alarm arrives while one is already playing; across channels, sounds always mix.

The cross-module audio question ("does Samwise interrupt Gandalf?") is shell-owned. The user accepted bundling the global flip into this spec to avoid issue fragmentation: `AudioPlayer.ConcurrentPlayback` becomes `true` permanently and the user-facing checkbox is removed from both module settings views. Net effect: modules' audio is independent.

## Approach

**Channels carry the policy; rules carry the routing.** An `AlarmChannel` is a small POCO with `Id` (stable), `Name` (user-editable), and `Collision` (`Mix | Replace | Suppress`). Each `StageAlarmRule` adds `Loop` and `ChannelId`. The default channel `{ Id = "default", Name = "Default", Collision = Replace }` is created at first run, and all three existing stage rules route to it — identical observable behavior to today.

**Migration is deserialize-time normalization, not versioned schema.** `SamwiseSettings` already uses `JsonSettingsStore<T>` (a flat round-trip; no migration hook beyond `IPostLoadInit`). Old user JSON lacking `Channels`/`Loop`/`ChannelId` deserializes with the defaults baked into the new properties; `IPostLoadInit` injects the "Default" channel if `Channels` is empty and reassigns any orphaned `ChannelId` to the first channel. No version bump needed; no silent data loss possible (new fields have safe defaults). Follow-up work to introduce proper `SchemaVersion` stamping on module-wide settings is tracked in #208 — this spec ships against the pre-versioning state and `SamwiseSettings` will adopt the versioning when #208 lands.

**Service rekeys playback by channel, not by alarm.** Today's `Dictionary<string, IPlaybackHandle> _playback` keyed by `char|plot|stage` becomes a per-channel structure that tracks one or more `(alarmKey, handle)` pairs. For `Replace`/`Suppress` channels there's at most one entry (newer replaces or is suppressed); for `Mix` channels there can be several (sounds layer, each remembered so `StopOnInteraction` can find its own). The structure: `Dictionary<string channelId, List<ChannelOwner>>`. Lookups for "is *my* alarm still the owner?" iterate the small list.

**Loop is wired through NAudio's `LoopStream` wrapper.** When `rule.Loop` is true, the `WaveStream` opened in `AudioPlayer` is wrapped so the engine restarts at EOF instead of stopping. This is a small additive change to `AudioPlayer.Play`.

## Files to modify

### 1. `AudioPlayer` — concurrent default + loop support

[AudioPlayer.cs](../../../src/Mithril.Shared/Audio/AudioPlayer.cs)

- Default `ConcurrentPlayback = true`. The static initializer or backing field flips to `true`. Existing wiring in [Program.cs:254-261](../../../src/Mithril.Shell/Program.cs#L254-L261) that writes the flag from `audioSettings.ConcurrentAlarms` becomes a no-op write — see file (2) for the cleanup.
- Add `loop: bool = false` parameter to `Play(path, volume, callerId, loop)`. When true, the `WaveStream` returned by `OpenReader` is wrapped in `LoopStream` before `ToSampleProvider()`. `LoopStream` is a small internal class implementing `WaveStream` that overrides `Read` to seek to 0 when the underlying reader hits EOF. NAudio doesn't ship a built-in for this — keep the helper file-local.

### 2. Remove the user-facing "Allow concurrent alarm sounds" wiring

The flag is no longer user-controllable. Audit and remove:

- [AudioSettings.cs](../../../src/Mithril.Shared/Settings/AudioSettings.cs) — delete the `ConcurrentAlarms` property (the whole class is just that one field; either delete the file or leave it as a marker if other audio settings are imminent — current call sites suggest delete).
- [ShellSettings.cs:23-24](../../../src/Mithril.Shell/ShellSettings.cs#L23-L24) — delete `ConcurrentAlarms` field/property. Persisted JSON in users' `%LocalAppData%/Mithril/shell.json` will silently drop the field on next save.
- [Program.cs:131-135, 254-262](../../../src/Mithril.Shell/Program.cs#L131-L135) — drop `AudioSettings` construction and the `PropertyChanged` round-trip; just rely on the `true` default.
- [SamwiseSettingsView.xaml:35-37](../../../src/Samwise.Module/Views/SamwiseSettingsView.xaml#L35-L37) — delete the checkbox.
- [SamwiseSettingsView.xaml.cs:15](../../../src/Samwise.Module/Views/SamwiseSettingsView.xaml.cs#L15) — delete the `AudioSettings? Audio { get; set; }` property; remove the corresponding initializer in the module's view bootstrap.
- [GandalfSettingsView.xaml:18-20](../../../src/Gandalf.Module/Views/GandalfSettingsView.xaml#L18-L20) — delete the checkbox.
- Gandalf's settings-view code-behind — same `Audio` property cleanup (mirror of Samwise).

### 3. `AlarmChannel` — new type

New file `src/Samwise.Module/Alarms/AlarmChannel.cs`:

```csharp
public enum AlarmCollisionBehavior { Mix, Replace, Suppress }

public sealed class AlarmChannel : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    private string _name = "";
    private AlarmCollisionBehavior _collision = AlarmCollisionBehavior.Mix;

    public string Name { get => _name; set => Set(ref _name, value); }
    public AlarmCollisionBehavior Collision { get => _collision; set => Set(ref _collision, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null) { /* standard */ }
}
```

`Id` is stable across renames (it's what `StageAlarmRule.ChannelId` references). `Name` is user-facing. `Collision` defaults to `Mix` for new channels created by the user; the migration code stamps `Replace` on the auto-created default channel (see file 4).

### 4. `AlarmSettings` / `StageAlarmRule` — add fields + migration normalization

[AlarmSettings.cs](../../../src/Samwise.Module/Alarms/AlarmSettings.cs)

**`StageAlarmRule` additions:**

```csharp
private bool _loop;
private string _channelId = "default";  // stable id of "Default" channel

public bool Loop { get => _loop; set => Set(ref _loop, value); }
public string ChannelId { get => _channelId; set => Set(ref _channelId, value); }
```

**`AlarmSettings` additions:**

```csharp
private List<AlarmChannel> _channels = DefaultChannels();
public List<AlarmChannel> Channels
{
    get => _channels;
    set { /* detach old + attach new event handlers, mirror Rules pattern */ }
}

private static List<AlarmChannel> DefaultChannels() => new()
{
    new AlarmChannel { Id = "default", Name = "Default", Collision = AlarmCollisionBehavior.Replace },
};

private static Dictionary<PlotStage, StageAlarmRule> DefaultRules() => new()
{
    [PlotStage.Ripe]            = new() { Enabled = true,  ChannelId = "default" },
    [PlotStage.Thirsty]         = new() { Enabled = false, ChannelId = "default" },
    [PlotStage.NeedsFertilizer] = new() { Enabled = false, ChannelId = "default" },
};
```

**Migration via `IPostLoadInit`:** `AlarmSettings` implements `IPostLoadInit` (today it doesn't — adding the interface and wiring it through the parent `SamwiseSettings.PostLoadInit()`). The body:

1. If `Channels` is empty (old JSON lacked the property entirely → STJ source-gen leaves the field initializer's `DefaultChannels()` in place — verify this for the source-gen path; if it _doesn't_, the empty-list fallback here catches it).
2. For each rule in `Rules`: if its `ChannelId` doesn't match any channel in `Channels`, reassign to `Channels[0].Id`. Old JSON without a `channelId` property keeps the field initializer's `"default"` value (the parameterless ctor runs before STJ populates properties), which lines up with the auto-created default channel — but the dangling-id fixup still matters for a user who manually edited the file or whose channel was deleted in a future revision.
3. Re-attach `PropertyChanged` handlers on Channels members so renames/collision changes bubble up to the autosaver.
4. Compute and set the initial `MembershipSummary` on each channel (see UI section).

Make sure `SamwiseSettings.PostLoadInit` (which may not exist yet — add it if absent) calls `_alarms.PostLoadInit()` after the property handlers are re-attached.

Add `[JsonSerializable(typeof(AlarmChannel))]` and `[JsonSerializable(typeof(List<AlarmChannel>))]` to [SamwiseSettingsJsonContext](../../../src/Samwise.Module/Alarms/AlarmSettings.cs#L161-L164).

### 5. `AlarmService` — channel-scoped playback

[AlarmService.cs](../../../src/Samwise.Module/Alarms/AlarmService.cs)

Replace per-alarm playback dictionary with per-channel:

```csharp
private sealed record ChannelOwner(string AlarmKey, IPlaybackHandle Handle);
private readonly Dictionary<string, List<ChannelOwner>> _channelPlayback = new(StringComparer.Ordinal);
```

The list-per-channel shape supports `Mix` (multiple concurrent owners) without special-casing.

**`OnPlotChanged` flow (the firing path):**

After all existing gates (Enabled, MutedCrops, dedup against `_firedAt`/`_snoozedUntil`), resolve the channel:

```csharp
var channel = ResolveChannel(rule.ChannelId);  // falls back to first channel if dangling
var owners = _channelPlayback.TryGetValue(channel.Id, out var list) ? list : (_channelPlayback[channel.Id] = new());
PruneStopped(owners);   // drop entries whose handle.IsPlaying is false
```

Then dispatch on `channel.Collision`:

- **Mix** — call `AudioPlayer.Play(rule.SoundFilePath, volume, "samwise", loop: rule.Loop)`. Append the new `ChannelOwner` to `owners`.
- **Replace** — for each existing owner in `owners`, call `Stop()`. Clear the list. Then `Play` and add the new owner.
- **Suppress** — if `owners` is non-empty (after `PruneStopped`), skip `Play` entirely. **Still** record `_firedAt[key]`, flash, balloon, fire `AlarmTriggered`. If `owners` is empty, proceed as Replace would.

The `_firedAt[key]` dedup happens regardless — Suppress means "don't make sound", not "didn't fire".

**Stop-on-interaction flow (the resolution path):**

When a plot transitions out of an alarmed stage and `oldRule.StopOnInteraction` is true:

```csharp
if (_channelPlayback.TryGetValue(channel.Id, out var owners))
{
    var mine = owners.FirstOrDefault(o => o.AlarmKey == resolvedKey);
    if (mine is not null)
    {
        mine.Handle.Stop();
        owners.Remove(mine);
    }
}
```

The per-owner match (`AlarmKey == resolvedKey`) matters because in Replace mode a *different* plot may have taken over the channel between fire and resolve, and in Mix mode the channel holds several concurrent alarms — we want to stop *our* sound, not any of the others.

`HandleHarvested(plot)` follows the same pattern: for each rule whose `StopOnInteraction` is true and whose `(char, plot, stage)` key matches an owner, stop and remove.

**`DismissAll` / `SnoozeAll`:** iterate every channel's owner list, stop each handle; clear the dictionary.

**`ResolveChannel`:**

```csharp
private AlarmChannel ResolveChannel(string id)
    => _settings.Alarms.Channels.FirstOrDefault(c => c.Id == id)
       ?? _settings.Alarms.Channels.First();   // guaranteed non-empty by IPostLoadInit
```

### 6. Settings UI — channels card section + per-stage Channel/Loop rows

[SamwiseSettingsView.xaml](../../../src/Samwise.Module/Views/SamwiseSettingsView.xaml)

**Remove:** the "Allow concurrent alarm sounds" `CheckBox` at lines 35-37.

**Add a Channels section** above the existing "Per-stage rules" section. An `ItemsControl` bound to `Alarms.Channels`. Each item template is a card with:

- `TextBox` bound to `Name` (editable label, save-on-LostFocus).
- `ComboBox` bound to `Collision` over the three enum values. Reuse the `EnumToBoolConverter` pattern already in the file (lines 100-107) or add an `EnumToValuesConverter` — whichever is lighter.
- A read-only `TextBlock` showing the comma-joined list of stage names whose `ChannelId` equals this channel's `Id`. Implementation: add `string MembershipSummary { get; private set; }` to `AlarmChannel` as a notify-on-change property. `AlarmSettings` owns recomputation — when any rule's `ChannelId` changes (caught by `OnRuleChanged` watching for the `ChannelId` property), `AlarmSettings` walks `Channels` and sets each one's `MembershipSummary` from its current member rules. `AlarmChannel` itself doesn't compute; it just exposes the property and fires `PropertyChanged` when `AlarmSettings` updates it. Same fan-out runs in `IPostLoadInit`.
- A delete `Button` (`X` icon) bound to a `RelayCommand`. Disable when `Channels.Count == 1`. On click: reassign all rules whose `ChannelId == this.Id` to `Channels[0].Id` (after removal), then remove this channel.

Below the items control, an "Add channel" `Button` that creates a new `AlarmChannel { Name = "Channel N", Collision = Mix }` and appends to `Channels`. `N` = `Channels.Count + 1`; ID is the auto-generated GUID.

**Modify the per-stage rules cards:** inside each existing `Border` (lines 63-90), after the "Stop sound when resolved" `CheckBox`, add two rows:

- `CheckBox` bound to `Value.Loop`, content "Loop until dismissed", `IsEnabled` bound to `Value.Enabled`.
- A `DockPanel` with a `TextBlock` "Channel:" and a `ComboBox` whose `ItemsSource` is the parent's `Alarms.Channels` (use `RelativeSource AncestorType=UserControl`), `DisplayMemberPath = "Name"`, `SelectedValuePath = "Id"`, `SelectedValue = "{Binding Value.ChannelId}"`.

The "New channel…" affordance in the stage dropdown is optional polish — defer it for now. Users can scroll up to "Add channel" and then come back.

### 7. Test coverage

New tests under [tests/Samwise.Tests](../../../tests/Samwise.Tests) — find the existing `AlarmServiceTests` (or add the file if absent) and cover:

- **Mix channel:** two stages on the same channel both fire → both handles tracked, no audio is stopped (verify via fake `IAudioPlayer` recording stops/plays).
- **Mix channel + StopOnInteraction:** two stages fire and both layer; the older plot's stage transitions out → only the older plot's handle is stopped, the newer keeps playing.
- **Replace channel:** second stage fires → first handle's `Stop` is invoked, second handle becomes the channel owner.
- **Suppress channel:** second stage fires while first handle `IsPlaying == true` → no second `Play` call, but `AlarmTriggered` event still raised and `_firedAt` records the second key.
- **Suppress with finished handle:** first handle finished playback (IsPlaying false), second alarm arrives → proceeds like Replace (treat as idle).
- **StopOnInteraction with Replace:** plot A fires, plot B takes the channel (Replace), plot A transitions out → channel owner is B, A's resolve doesn't silence B.
- **Loop flag:** with `rule.Loop = true`, `AudioPlayer.Play` is invoked with `loop: true`. (Concrete loop-restart behavior is NAudio's responsibility; we just assert the parameter is propagated.)
- **Migration:** load a JSON blob lacking `Channels` and `ChannelId` → after `PostLoadInit`, `Channels` has the default entry and every rule's `ChannelId` matches it.
- **Channel delete cascade:** delete a channel with members → those rules get reassigned to the first remaining channel.

This will require a thin `IAudioPlayer` abstraction (or refactoring `AudioPlayer.Play` to be injectable behind an interface) — today it's a `static`. Wrap the static behind an `IAudioPlaybackSink` interface registered in DI; the real impl forwards to `AudioPlayer.Play/Stop`, tests use a recording fake. Keep the static `AudioPlayer` as-is for non-Samwise callers; just don't have `AlarmService` reach for it directly.

## Open at implementation time

- **Loop + StopOnInteraction interaction:** when `Loop = true` and `StopOnInteraction = false`, what stops a looping alarm whose plot keeps re-emitting the same stage forever? The current dedup via `_firedAt` already prevents re-fires, so the loop just keeps running until `DismissAll`/`SnoozeAll`/`StopAllSoundsCommand`. That seems correct (matches "until dismissed"), but verify in QA.
- **Audio test seam:** the `IAudioPlaybackSink` abstraction is a non-trivial side change. If it bloats this PR, the alternative is a `static AudioPlayer.TestSink` override property exposed only to tests — uglier but smaller. Prefer the interface; reach for the property only if the interface refactor cascades.
- **`LoopStream` helper — per-format seek verification:** the internal wrapper relies on `Position = 0` working on the underlying reader. MP3 / WMA / AAC use `MediaFoundationReader` which supports it; WAV (`AudioFileReader`) supports it; OGG / FLAC need a quick check that `MediaFoundationReader.Position = 0` actually rewinds rather than throwing. Edge case: a very short sound looping rapidly could starve the audio thread — clamp by requiring sounds ≥ 200ms or just accept it.
