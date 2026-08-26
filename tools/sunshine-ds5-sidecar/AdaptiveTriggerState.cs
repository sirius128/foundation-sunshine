namespace Sunshine.Ds5Sidecar;

internal sealed class AdaptiveTriggerState
{
    internal const byte RightFlag = 0x04;
    internal const byte LeftFlag = 0x08;
    internal const int EffectSize = 11;

    // "No effect" is spelled two ways: an all-zero trigger section, and the
    // documented Off type.
    internal const byte OffEffect = 0x05;

    // An arm and a release are not equally recoverable: a lost arm costs one
    // action and the next arm fixes it, while a lost release leaves the effect
    // standing on the trigger. Only a release is repeated; repeating an arm
    // would restart a stateful effect under the player's finger.
    internal static readonly TimeSpan ReleaseRepeatDelay = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private readonly byte[] _left = new byte[EffectSize];
    private readonly byte[] _right = new byte[EffectSize];
    private byte _releaseRepeatFlags;
    // One deadline per side: a shared one lets a later release defer a repeat
    // that is already pending.
    private TimeSpan _leftRepeatAt;
    private TimeSpan _rightRepeatAt;

    internal bool TryUpdate(IReadOnlyDictionary<string, object> fields,
                            OutputValidFlags valid,
                            TimeSpan now,
                            byte deviceId,
                            byte controllerNumber,
                            out Protocol.Message message)
    {
        lock (_lock)
        {
            byte flags = 0;
            // A trigger this report is not programming keeps the effect the
            // client holds; its zero bytes are not a release.
            if (valid.LeftTrigger &&
                TryGetEffect(fields, "leftTriggerEffect", out var left) &&
                !_left.AsSpan().SequenceEqual(left))
            {
                left.CopyTo(_left);
                flags |= LeftFlag;
            }

            if (valid.RightTrigger &&
                TryGetEffect(fields, "rightTriggerEffect", out var right) &&
                !_right.AsSpan().SequenceEqual(right))
            {
                right.CopyTo(_right);
                flags |= RightFlag;
            }

            if (flags != 0)
            {
                ScheduleReleaseRepeatLocked(flags, now);
                message = BuildMessage(deviceId, controllerNumber, flags, _left, _right);
                return true;
            }

            if (TryTakeDueRepeatLocked(now, out flags))
            {
                message = BuildMessage(deviceId, controllerNumber, flags, _left, _right);
                return true;
            }

            message = default;
            return false;
        }
    }

    /// <summary>
    /// Delivers a repeat that came due without a further report to carry it.
    /// The effect has no timeout of its own, so a release that never lands
    /// stands until that trigger is programmed again.
    /// </summary>
    internal bool TryRefresh(TimeSpan now, byte deviceId, byte controllerNumber, out Protocol.Message message)
    {
        lock (_lock)
        {
            if (TryTakeDueRepeatLocked(now, out var flags))
            {
                message = BuildMessage(deviceId, controllerNumber, flags, _left, _right);
                return true;
            }

            message = default;
            return false;
        }
    }

    private bool TryTakeDueRepeatLocked(TimeSpan now, out byte flags)
    {
        byte due = 0;
        if ((_releaseRepeatFlags & LeftFlag) != 0 && now >= _leftRepeatAt)
            due |= LeftFlag;
        if ((_releaseRepeatFlags & RightFlag) != 0 && now >= _rightRepeatAt)
            due |= RightFlag;

        flags = due;
        if (due == 0)
            return false;

        _releaseRepeatFlags &= (byte)~due;
        return true;
    }

    /// <summary>
    /// Queues the one repeat a release gets. Sides this message armed drop out:
    /// a re-arm cancels a repeat that would zero the effect just programmed.
    /// </summary>
    private void ScheduleReleaseRepeatLocked(byte flags, TimeSpan now)
    {
        var released = 0;
        if ((flags & LeftFlag) != 0 && !IsArmed(_left))
            released |= LeftFlag;
        if ((flags & RightFlag) != 0 && !IsArmed(_right))
            released |= RightFlag;

        _releaseRepeatFlags = (byte)((_releaseRepeatFlags & ~flags) | released);
        if ((released & LeftFlag) != 0)
            _leftRepeatAt = now + ReleaseRepeatDelay;
        if ((released & RightFlag) != 0)
            _rightRepeatAt = now + ReleaseRepeatDelay;
    }

    private static bool IsArmed(ReadOnlySpan<byte> effect) =>
        effect[0] != 0 && effect[0] != OffEffect;

    internal bool TryReset(byte deviceId, byte controllerNumber, out Protocol.Message message)
    {
        lock (_lock)
        {
            _releaseRepeatFlags = 0;
            if (!_left.AsSpan().ContainsAnyExcept((byte)0) &&
                !_right.AsSpan().ContainsAnyExcept((byte)0))
            {
                message = default;
                return false;
            }

            Array.Clear(_left);
            Array.Clear(_right);
            message = BuildMessage(deviceId, controllerNumber, LeftFlag | RightFlag, _left, _right);
            return true;
        }
    }

    private static bool TryGetEffect(IReadOnlyDictionary<string, object> fields,
                                     string name,
                                     out ReadOnlySpan<byte> effect)
    {
        if (fields.TryGetValue(name, out var item) &&
            item is byte[] bytes &&
            bytes.Length >= EffectSize)
        {
            effect = bytes.AsSpan(0, EffectSize);
            return true;
        }

        effect = default;
        return false;
    }

    private static Protocol.Message BuildMessage(byte deviceId,
                                                 byte controllerNumber,
                                                 byte flags,
                                                 ReadOnlySpan<byte> left,
                                                 ReadOnlySpan<byte> right)
    {
        // id:u8, controller:u8, flags:u8, left/right type:u8, reserved:u8,
        // left/right effect payload:10 bytes = 26 bytes.
        var payload = new byte[26];
        payload[0] = deviceId;
        payload[1] = controllerNumber;
        payload[2] = flags;
        payload[3] = left[0];
        payload[4] = right[0];
        left.Slice(1, 10).CopyTo(payload.AsSpan(6, 10));
        right.Slice(1, 10).CopyTo(payload.AsSpan(16, 10));
        return new Protocol.Message(Protocol.MessageType.AdaptiveTriggers, 0, payload);
    }
}
