namespace Sunshine.Ds5Sidecar;

/// <summary>
/// Forwards the lightbar colors a game writes without republishing the one the
/// client already applied.
/// </summary>
internal sealed class LightbarState
{
    // Nothing on the client expires a color, so one lost on the way stays lost
    // until the game writes a different one. Each color is sent twice, spaced
    // far enough apart that one queue-clearing burst cannot take both copies.
    internal static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private bool _published;
    private int _color;
    private bool _repeatPending;
    private TimeSpan _repeatAt;

    internal bool TryUpdate(IReadOnlyDictionary<string, object> fields,
                            OutputValidFlags valid,
                            TimeSpan now,
                            byte deviceId,
                            byte controllerNumber,
                            out Protocol.Message message)
    {
        if (!valid.Lightbar ||
            !fields.TryGetValue("lightbar", out var value) ||
            value is not byte[] rgb ||
            rgb.Length < 3)
        {
            // A report that is not programming the lightbar still carries the
            // clock forward, and a repeat that has come due is owed either way.
            return TryRefresh(now, deviceId, controllerNumber, out message);
        }

        var color = (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
        lock (_lock)
        {
            // A game that rewrites its report every frame republishes the color
            // it is holding, and every forwarded packet is a write to the pad.
            if (_published && color == _color)
                return TryTakeRepeatLocked(now, deviceId, controllerNumber, out message);

            _published = true;
            _color = color;
            _repeatPending = true;
            _repeatAt = now + RepeatDelay;
            message = Build(deviceId, controllerNumber, color);
            return true;
        }
    }

    /// <summary>
    /// Sends the second copy of a color once it comes due without a further
    /// report to carry it.
    /// </summary>
    internal bool TryRefresh(TimeSpan now, byte deviceId, byte controllerNumber, out Protocol.Message message)
    {
        lock (_lock)
        {
            return TryTakeRepeatLocked(now, deviceId, controllerNumber, out message);
        }
    }

    private bool TryTakeRepeatLocked(TimeSpan now, byte deviceId, byte controllerNumber, out Protocol.Message message)
    {
        if (!_repeatPending || now < _repeatAt)
        {
            message = default;
            return false;
        }

        _repeatPending = false;
        message = Build(deviceId, controllerNumber, _color);
        return true;
    }

    private static Protocol.Message Build(byte deviceId, byte controllerNumber, int color) =>
        new(Protocol.MessageType.Led,
            0,
            new[] { deviceId, controllerNumber, (byte)(color >> 16), (byte)(color >> 8), (byte)color });
}
