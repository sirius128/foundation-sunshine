namespace Sunshine.Ds5Sidecar;

/// <summary>
/// A DS5 output report carries every field the format defines, and its validity
/// bytes say which of them the device is meant to read. Reading a field whose
/// bit is clear takes the zeros of a field the game never programmed for an
/// instruction.
/// </summary>
internal readonly struct OutputValidFlags
{
    private const byte RightTriggerEffect = 0x04;
    private const byte LeftTriggerEffect = 0x08;
    private const byte LightbarControl = 0x04;

    private readonly byte _flag0;
    private readonly byte _flag1;
    private readonly bool _has0;
    private readonly bool _has1;

    private OutputValidFlags(byte flag0, bool has0, byte flag1, bool has1)
    {
        _flag0 = flag0;
        _flag1 = flag1;
        _has0 = has0;
        _has1 = has1;
    }

    /// <summary>
    /// Whether the decoder exposed either validity byte. A byte that is not
    /// exposed governs nothing, and the fields it would gate are read at face
    /// value.
    /// </summary>
    internal bool Known => _has0 || _has1;

    internal bool LeftTrigger => !_has0 || (_flag0 & LeftTriggerEffect) != 0;

    internal bool RightTrigger => !_has0 || (_flag0 & RightTriggerEffect) != 0;

    internal bool Lightbar => !_has1 || (_flag1 & LightbarControl) != 0;

    // The motor bytes are deliberately not gated: a stop cleared in a report
    // that does not claim them would never arrive, while misreading them only
    // asks for a level the client plays for a bounded time.
    internal static OutputValidFlags From(IReadOnlyDictionary<string, object> fields)
    {
        var has0 = TryFlag(fields, "validFlag0", out var flag0);
        var has1 = TryFlag(fields, "validFlag1", out var flag1);
        return new OutputValidFlags(flag0, has0, flag1, has1);
    }

    private static bool TryFlag(IReadOnlyDictionary<string, object> fields, string name, out byte value)
    {
        if (fields.TryGetValue(name, out var item))
        {
            switch (item)
            {
                case byte single:
                    value = single;
                    return true;
                case byte[] { Length: > 0 } bytes:
                    value = bytes[0];
                    return true;
            }
        }

        value = 0;
        return false;
    }
}
