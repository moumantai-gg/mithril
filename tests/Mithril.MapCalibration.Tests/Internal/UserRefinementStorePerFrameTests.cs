using System;
using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

/// <summary>
/// Phase A storage round-trips for mithril#1082 — frame-aware
/// <see cref="UserRefinementStore"/>. Asserts the typed-slot
/// <see cref="SceneRefinements"/> invariants: per-scene texture + overlay
/// records coexist; per-frame <see cref="UserRefinementStore.Save"/> /
/// <see cref="UserRefinementStore.Remove(string, CalibrationFrame)"/>
/// preserve the sibling slot; scene entries compact when the last slot empties;
/// transactional Persist rolls back the in-memory state on IO failure.
/// </summary>
public sealed class UserRefinementStorePerFrameTests : IDisposable
{
    private readonly string _dir;

    public UserRefinementStorePerFrameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mithril-refstore-perframe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* CI temp dir gets reaped */ }
    }

    private const string Key = "Map_AreaTest";

    private static AreaCalibration Cal(
        CalibrationFrame frame,
        CalibrationSource source = CalibrationSource.UserRefinement,
        double scale = 1.0,
        double residual = 0.5,
        int refs = 6) =>
        new(Scale: scale, RotationRadians: 0, OriginX: 100, OriginY: 200,
            ReferenceCount: refs, ResidualPixels: residual)
        {
            Frame = frame,
            Source = source,
        };

    [Fact]
    public void Save_TextureThenOverlay_SameScene_BothCoexist()
    {
        var store = new UserRefinementStore(_dir);
        var tex = Cal(CalibrationFrame.Texture, CalibrationSource.AutoCapture, scale: 1.0);
        var ovl = Cal(CalibrationFrame.Overlay, CalibrationSource.UserRefinement, scale: 2.0);

        store.Save(Key, tex);
        store.Save(Key, ovl);

        store.TryGet(Key, CalibrationFrame.Texture, out var texOut).Should().BeTrue();
        texOut.Scale.Should().Be(1.0);
        texOut.Frame.Should().Be(CalibrationFrame.Texture);

        store.TryGet(Key, CalibrationFrame.Overlay, out var ovlOut).Should().BeTrue();
        ovlOut.Scale.Should().Be(2.0);
        ovlOut.Frame.Should().Be(CalibrationFrame.Overlay);

        store.All.Should().ContainSingle().Which.Key.Should().Be(Key);
    }

    [Fact]
    public void Save_TextureTwice_SameScene_SecondReplacesInTextureSlot()
    {
        var store = new UserRefinementStore(_dir);
        var first = Cal(CalibrationFrame.Texture, scale: 1.0);
        var second = Cal(CalibrationFrame.Texture, scale: 99.0);

        store.Save(Key, first);
        store.Save(Key, second);

        store.TryGet(Key, CalibrationFrame.Texture, out var current).Should().BeTrue();
        current.Scale.Should().Be(99.0);

        // Overlay slot stays null — frame-scoped writes don't leak across slots.
        store.TryGet(Key, CalibrationFrame.Overlay, out _).Should().BeFalse();
    }

    [Fact]
    public void Save_PreservesSourceStamp_RewriteOnlyForeignSources()
    {
        var store = new UserRefinementStore(_dir);

        // BundledBaseline is a foreign source for the user store; the defensive
        // stamp rewrites it to UserRefinement on save. Frame is untouched.
        var foreign = Cal(CalibrationFrame.Texture, CalibrationSource.BundledBaseline);
        store.Save(Key, foreign);

        store.TryGet(Key, CalibrationFrame.Texture, out var stamped).Should().BeTrue();
        stamped.Source.Should().Be(CalibrationSource.UserRefinement);
        stamped.Frame.Should().Be(CalibrationFrame.Texture);

        // AutoCapture and UserRefinement survive verbatim.
        var autocap = Cal(CalibrationFrame.Texture, CalibrationSource.AutoCapture);
        store.Save(Key, autocap);
        store.TryGet(Key, CalibrationFrame.Texture, out var verbatim).Should().BeTrue();
        verbatim.Source.Should().Be(CalibrationSource.AutoCapture);
    }

    [Fact]
    public void Remove_FrameAgnostic_RemovesEntireSceneEntry()
    {
        var store = new UserRefinementStore(_dir);
        store.Save(Key, Cal(CalibrationFrame.Texture));
        store.Save(Key, Cal(CalibrationFrame.Overlay));

        store.Remove(Key).Should().BeTrue();

        store.TryGetAny(Key, out _).Should().BeFalse();
        store.All.Should().BeEmpty();
    }

    [Fact]
    public void Remove_FrameScoped_LeavesOtherSlotIntact()
    {
        var store = new UserRefinementStore(_dir);
        store.Save(Key, Cal(CalibrationFrame.Texture, scale: 1.0));
        store.Save(Key, Cal(CalibrationFrame.Overlay, scale: 2.0));

        store.Remove(Key, CalibrationFrame.Texture).Should().BeTrue();

        store.TryGet(Key, CalibrationFrame.Texture, out _).Should().BeFalse();
        store.TryGet(Key, CalibrationFrame.Overlay, out var ovl).Should().BeTrue();
        ovl.Scale.Should().Be(2.0);
        // Scene entry survives — Overlay slot is still populated.
        store.TryGetAny(Key, out var slots).Should().BeTrue();
        slots.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Remove_FrameScoped_CompactsWhenLastSlotEmptied()
    {
        var store = new UserRefinementStore(_dir);
        store.Save(Key, Cal(CalibrationFrame.Texture));
        store.Save(Key, Cal(CalibrationFrame.Overlay));

        store.Remove(Key, CalibrationFrame.Texture).Should().BeTrue();
        store.Remove(Key, CalibrationFrame.Overlay).Should().BeTrue();

        // No empty SceneRefinements left in the dict.
        store.TryGetAny(Key, out _).Should().BeFalse();
        store.All.Should().BeEmpty();
    }

    [Fact]
    public void Remove_FrameScoped_Idempotent_ReturnsFalseWhenSlotEmpty()
    {
        var store = new UserRefinementStore(_dir);
        store.Save(Key, Cal(CalibrationFrame.Overlay));

        // Texture slot was never populated — removing it is a no-op.
        store.Remove(Key, CalibrationFrame.Texture).Should().BeFalse();
        // Overlay slot still present after the no-op.
        store.TryGet(Key, CalibrationFrame.Overlay, out _).Should().BeTrue();

        // Frame-scoped remove on a missing scene is also a false-return no-op.
        store.Remove("Map_NeverSaved", CalibrationFrame.Texture).Should().BeFalse();
    }

    [Fact]
    public void Save_PersistFailureRollsBackInMemory()
    {
        // Match the existing MapCalibrationServiceTests rollback test idiom:
        // hold the .tmp path exclusively so File.WriteAllText inside Persist
        // throws, then assert the in-memory state reflects the pre-write value.
        var store = new UserRefinementStore(_dir);
        var initial = Cal(CalibrationFrame.Texture, scale: 1.0);
        store.Save(Key, initial);

        var tmpPath = Path.Combine(_dir, "refinements.json.tmp");
        using (var _ = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            FluentActions.Invoking(() => store.Save(Key, Cal(CalibrationFrame.Texture, scale: 99.0)))
                .Should().Throw<IOException>();
        }

        store.TryGet(Key, CalibrationFrame.Texture, out var current).Should().BeTrue();
        current.Scale.Should().Be(1.0, "rollback restored the pre-Persist in-memory value");
    }

    [Fact]
    public void Save_PersistFailureOnFirstWrite_RollsBackToAbsent()
    {
        // Frame-scoped variant of the rollback discipline: when the scene had
        // no prior entry, a Persist failure must REMOVE the in-memory entry
        // (not leave a half-mutated slot behind).
        var store = new UserRefinementStore(_dir);

        var tmpPath = Path.Combine(_dir, "refinements.json.tmp");
        using (var _ = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            FluentActions.Invoking(() => store.Save(Key, Cal(CalibrationFrame.Overlay)))
                .Should().Throw<IOException>();
        }

        store.TryGetAny(Key, out _).Should().BeFalse("rollback removed the never-persisted entry");
    }

    [Fact]
    public void Roundtrip_v3_WriteThenRead_Idempotent()
    {
        // Save both slots, dispose the store, load a fresh one against the
        // same directory, assert the records survive deep-equal.
        var store1 = new UserRefinementStore(_dir);
        var tex = Cal(CalibrationFrame.Texture, CalibrationSource.AutoCapture, scale: 1.0);
        var ovl = Cal(CalibrationFrame.Overlay, CalibrationSource.UserRefinement, scale: 2.0);
        store1.Save(Key, tex);
        store1.Save(Key, ovl);

        var store2 = new UserRefinementStore(_dir);

        store2.TryGet(Key, CalibrationFrame.Texture, out var texOut).Should().BeTrue();
        store2.TryGet(Key, CalibrationFrame.Overlay, out var ovlOut).Should().BeTrue();

        texOut.Should().BeEquivalentTo(tex);
        ovlOut.Should().BeEquivalentTo(ovl);
    }
}
