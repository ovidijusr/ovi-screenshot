namespace Ovi.Screenshot.Native;

// One explicit chord only. This reducer stores no text, history, or device identifiers.
internal sealed class ShortcutCaptureState
{
    private readonly HashSet<uint> _modifiers = [];
    private readonly HashSet<uint> _alreadyHeld = [];
    private uint _pendingKey;
    private bool _keyReleased;

    internal CaptureShortcut? Pending { get; private set; }
    internal bool HasPendingShortcut => Pending is not null;

    internal ShortcutCaptureState(IEnumerable<uint>? alreadyHeld = null)
    {
        if (alreadyHeld is null) return;
        foreach (uint key in alreadyHeld)
        {
            if (Modifier(key) == 0) continue;
            uint normalized = NormalizeModifier(key, 0, 0);
            _modifiers.Add(normalized); _alreadyHeld.Add(normalized);
        }
    }

    internal ShortcutLearningStep Process(uint key, uint scanCode, uint flags, bool down, bool up, bool ownerForeground)
    {
        if (!down && !up) return default;
        if (!ownerForeground && Pending is null)
            return new(false, true, null, "Keep Settings open, then try Set key again.");
        if (ownerForeground && down && key == 0x1b) return new(true, true, null, null);

        if (Modifier(key) != 0)
        {
            uint normalized = NormalizeModifier(key, scanCode, flags);
            bool wasHeld = _modifiers.Contains(normalized);
            // After deactivation, only drain releases belonging to the one chord already selected.
            if (!ownerForeground && (down || !wasHeld)) return default;
            if (up && !wasHeld) return default;
            bool preExisting = _alreadyHeld.Contains(normalized);
            if (down) _modifiers.Add(normalized);
            else { _modifiers.Remove(normalized); _alreadyHeld.Remove(normalized); }
            // A modifier held before learning already reached Windows; its release must reach it too.
            return CompleteIfReleased(!preExisting);
        }

        if (Pending is not null)
        {
            if (key != _pendingKey) return default;
            if (down) _keyReleased = false;
            else if (up) _keyReleased = true;
            return CompleteIfReleased(true);
        }

        // Some snapshot/remapping paths deliver only a release. Never learn an arbitrary text keyup.
        bool standalone = key == 0x2c || key is >= 0x70 and <= 0x87 || key is >= 0xa6 and <= 0xb7;
        if (!down && !(up && standalone)) return default;
        uint modifiers = 0;
        foreach (uint modifier in _modifiers) modifiers |= Modifier(modifier);
        try { Pending = CaptureShortcut.FromKeyCode(modifiers, key); }
        catch (ArgumentException error) { return new(true, true, null, error.Message); }
        _pendingKey = key; _keyReleased = up;
        return CompleteIfReleased(true);
    }

    private ShortcutLearningStep CompleteIfReleased(bool consume) =>
        Pending is not null && _keyReleased && _modifiers.Count == 0
            ? new(consume, true, Pending, null)
            : new(consume, false, null, null);

    internal static uint Modifier(uint key) => key switch
    {
        0x10 or 0xa0 or 0xa1 => 4,
        0x11 or 0xa2 or 0xa3 => 2,
        0x12 or 0xa4 or 0xa5 => 1,
        0x5b or 0x5c => 8,
        _ => 0
    };

    private static uint NormalizeModifier(uint key, uint scanCode, uint flags) => key switch
    {
        0x10 => scanCode == 0x36 ? 0xa1u : 0xa0u,
        0x11 => (flags & 1) != 0 ? 0xa3u : 0xa2u,
        0x12 => (flags & 1) != 0 ? 0xa5u : 0xa4u,
        _ => key
    };
}

internal readonly record struct ShortcutLearningStep(bool Consume, bool Finished, CaptureShortcut? Shortcut, string? Message);
