using System.Buffers.Binary;

namespace Sunshine.Ds5Sidecar;

/// <summary>
/// Forwards the motor levels a game writes without republishing the one the
/// client already applied.
/// </summary>
internal sealed class RumbleState
{
    // The client arms each rumble packet for 30 seconds of SDL playback, so a
    // standing level only has to be republished often enough to stay inside
    // that window. Six attempts per window is redundancy enough.
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    // A stop has no window to stay inside: it either lands or the motors run
    // out the client's 30 seconds. It is repeated a bounded number of times,
    // spaced so one queue-clearing burst cannot take every copy, and then falls
    // silent.
    internal static readonly TimeSpan StopRepeatInterval = TimeSpan.FromMilliseconds(250);
    internal const int StopRepeats = 4;

    private readonly object _lock = new();
    private bool _published;
    private byte _left;
    private byte _right;
    private TimeSpan _sentAt;
    private int _stopRepeatsLeft;

    internal bool TryUpdate(byte left,
                            byte right,
                            TimeSpan now,
                            byte deviceId,
                            byte controllerNumber,
                            out Protocol.Message message)
    {
        lock (_lock)
        {
            var changed = !_published || left != _left || right != _right;
            if (!changed && !IsDueLocked(now))
            {
                message = default;
                return false;
            }

            message = PublishLocked(left, right, now, changed, deviceId, controllerNumber);
            return true;
        }
    }

    /// <summary>
    /// Republishes the standing level, or the next copy of a stop, when it
    /// comes due without a further report to carry it.
    /// </summary>
    internal bool TryRefresh(TimeSpan now, byte deviceId, byte controllerNumber, out Protocol.Message message)
    {
        lock (_lock)
        {
            if (!_published || !IsDueLocked(now))
            {
                message = default;
                return false;
            }

            message = PublishLocked(_left, _right, now, changed: false, deviceId, controllerNumber);
            return true;
        }
    }

    private bool IsDueLocked(TimeSpan now)
    {
        if (_left != 0 || _right != 0)
            return now - _sentAt >= RefreshInterval;
        return _stopRepeatsLeft > 0 && now - _sentAt >= StopRepeatInterval;
    }

    private Protocol.Message PublishLocked(byte left,
                                           byte right,
                                           TimeSpan now,
                                           bool changed,
                                           byte deviceId,
                                           byte controllerNumber)
    {
        var stopping = left == 0 && right == 0;
        if (changed)
        {
            // Only a level that was actually running needs its stop repeated.
            // The idle zero a session opens on has never moved a motor.
            _stopRepeatsLeft = stopping && _published && (_left != 0 || _right != 0) ? StopRepeats : 0;
        }
        else if (stopping && _stopRepeatsLeft > 0)
        {
            _stopRepeatsLeft--;
        }

        _published = true;
        _left = left;
        _right = right;
        _sentAt = now;
        return Build(deviceId, controllerNumber, left, right);
    }

    private static Protocol.Message Build(byte deviceId, byte controllerNumber, byte left, byte right)
    {
        var payload = new byte[6];
        payload[0] = deviceId;
        payload[1] = controllerNumber;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)(left * 257));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), (ushort)(right * 257));
        return new Protocol.Message(Protocol.MessageType.Rumble, 0, payload);
    }
}
