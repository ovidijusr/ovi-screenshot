namespace Ovi.Screenshot.Native;

// Bounded state for one configured chord, not a history of keyboard input.
internal sealed class GlobalShortcutState(CaptureShortcut shortcut)
{
    private readonly HashSet<uint> _modifiers = [];
    private bool _enabled, _latched, _mainReleased, _ignoreHeldMain, _unmatchedMainDown;

    internal bool IsPending => _latched;

    internal void Reset(bool enabled, IEnumerable<uint>? heldModifiers = null, bool mainHeld = false)
    {
        _enabled = enabled;
        _latched = _mainReleased = _unmatchedMainDown = false;
        _ignoreHeldMain = enabled && mainHeld;
        _modifiers.Clear();
        if (enabled && heldModifiers is not null)
            foreach (uint key in heldModifiers) _modifiers.Add(NormalizeModifier(key, 0, 0));
    }

    internal GlobalShortcutStep Process(uint key, uint scanCode, uint flags, bool down, bool up)
    {
        if (!_enabled || (!down && !up)) return default;
        if (Modifier(key) != 0)
        {
            uint normalized = NormalizeModifier(key, scanCode, flags);
            if (down) _modifiers.Add(normalized); else _modifiers.Remove(normalized);
            // Modifier events always pass through, including all releases Windows needs for balance.
            return CompleteIfReleased(false);
        }
        if (key != shortcut.Key) return default;
        if (_ignoreHeldMain)
        {
            if (up) _ignoreHeldMain = false;
            return default;
        }
        if (_latched)
        {
            if (down) _mainReleased = false;
            else if (up) _mainReleased = true;
            return CompleteIfReleased(true); // Includes auto-repeat from the consumed downstroke.
        }
        if (down)
        {
            // Do not steal a repeat/up from a downstroke already delivered to Windows.
            if (_unmatchedMainDown) return default;
            if (CurrentModifiers() != shortcut.Modifiers) { _unmatchedMainDown = true; return default; }
            _unmatchedMainDown = false;
            _latched = true;
            _mainReleased = false;
            return new(true, false);
        }
        if (_unmatchedMainDown) { _unmatchedMainDown = false; return default; }
        if (up && key == 0x2c && CurrentModifiers() == shortcut.Modifiers)
        {
            _latched = _mainReleased = true;
            return CompleteIfReleased(true); // PrintScreen can arrive through a release-only path.
        }
        return default;
    }

    private GlobalShortcutStep CompleteIfReleased(bool consume)
    {
        if (!_latched || !_mainReleased || (CurrentModifiers() & shortcut.Modifiers) != 0) return new(consume, false);
        _latched = _mainReleased = false;
        return new(consume, true);
    }

    private uint CurrentModifiers()
    {
        uint result = 0;
        foreach (uint key in _modifiers) result |= Modifier(key);
        return result;
    }

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

internal readonly record struct GlobalShortcutStep(bool Consume, bool Capture);
