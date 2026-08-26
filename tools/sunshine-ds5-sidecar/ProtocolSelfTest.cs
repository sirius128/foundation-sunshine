using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Sunshine.Ds5Sidecar;

internal static class ProtocolSelfTest
{
    internal static async Task<int> RunAsync(bool composite, string? resultPath, string? audioWriterPath)
    {
        RunDeterministicChecks();
        var pipeName = $"sunshine-ds5-self-test-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = new SidecarServer(pipeName);
        var serverTask = server.RunAsync(stopping.Token);

        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        await client.ConnectAsync(10_000, stopping.Token);

        await SendAsync(client, new Protocol.Message(Protocol.MessageType.Hello, 1, new byte[4]), stopping.Token);
        var hello = await ReceiveAsync(client, stopping.Token);
        Require(hello.Type == Protocol.MessageType.HelloReply && hello.RequestId == 1 && hello.Payload.Length == 4,
            "hello reply");
        var helloCapabilities = (Protocol.Capability)BinaryPrimitives.ReadUInt32LittleEndian(hello.Payload);
        Require(helloCapabilities.HasFlag(Protocol.Capability.Hid), "hello HID capability");
        Require(helloCapabilities.HasFlag(Protocol.Capability.AdaptiveTriggers), "hello adaptive trigger capability");
        Require(!composite || helloCapabilities.HasFlag(Protocol.Capability.GenshinCompatibilityIdentity),
            "hello Genshin compatibility identity capability");
        Require(helloCapabilities.HasFlag(Protocol.Capability.AudioPolicyViolation),
            "hello audio endpoint policy capability");

        await SendAsync(client, new Protocol.Message(
            Protocol.MessageType.Attach, 2, new byte[] { 0, 0, composite ? (byte)1 : (byte)0, 0 }), stopping.Token);
        var attach = await ReceiveAsync(client, stopping.Token);
        if (attach.Type == Protocol.MessageType.Error)
            throw new InvalidOperationException(DecodeError(attach.Payload));
        Require(attach.Type == Protocol.MessageType.AttachReply && attach.RequestId == 2 && attach.Payload.Length == 8,
            "attach reply");
        var capabilities = (Protocol.Capability)BinaryPrimitives.ReadUInt32LittleEndian(attach.Payload.AsSpan(4));
        Require(capabilities.HasFlag(Protocol.Capability.Hid), "HID capability");
        Require(capabilities.HasFlag(Protocol.Capability.AdaptiveTriggers), "adaptive trigger capability");
        Require(!composite || capabilities.HasFlag(Protocol.Capability.AudioFourChannel),
            "composite four-channel audio capability");

        var input = new byte[20];
        input[0] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 0x1000 | 0x0010); // Cross + Start
        input[8] = 64;
        input[9] = 128;
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(12), -1234);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(14), 2345);
        await SendAsync(client, new Protocol.Message(Protocol.MessageType.InputState, 0, input), stopping.Token);

        var touch = new byte[20];
        touch[0] = 0;
        touch[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(touch.AsSpan(4), 42);
        WriteFloat(touch.AsSpan(8), 0.25f);
        WriteFloat(touch.AsSpan(12), 0.75f);
        WriteFloat(touch.AsSpan(16), 1.0f);
        await SendAsync(client, new Protocol.Message(Protocol.MessageType.Touch, 0, touch), stopping.Token);
        touch[1] = 2;
        await SendAsync(client, new Protocol.Message(Protocol.MessageType.Touch, 0, touch), stopping.Token);
        touch[1] = 5; // LI_TOUCH_EVENT_BUTTON_ONLY must not mutate contact state or fail.
        await SendAsync(client, new Protocol.Message(Protocol.MessageType.Touch, 0, touch), stopping.Token);

        var motion = new byte[16];
        motion[0] = 0;
        motion[1] = 1;
        WriteFloat(motion.AsSpan(4), 0.0f);
        WriteFloat(motion.AsSpan(8), 9.80665f);
        WriteFloat(motion.AsSpan(12), 0.0f);
        await SendAsync(client, new Protocol.Message(Protocol.MessageType.Motion, 0, motion), stopping.Token);

        await SendAsync(client, new Protocol.Message(
            Protocol.MessageType.Battery, 0, new byte[] { 0, 3, 80, 0 }), stopping.Token);

        var capturedHapticsBytes = 0;
        if (!string.IsNullOrWhiteSpace(audioWriterPath))
        {
            Require(composite, "audio writer requires composite profile");
            Require(File.Exists(audioWriterPath), "audio writer executable");
            await Task.Delay(500, stopping.Token);
            using var writer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = audioWriterPath,
                WorkingDirectory = Path.GetDirectoryName(audioWriterPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Unable to launch the haptics audio writer");
            while (capturedHapticsBytes == 0)
            {
                var haptics = await ReceiveUntilTypeAsync(client, Protocol.MessageType.HapticsPcm, stopping.Token);
                Require(haptics.Payload.Length >= 24 && haptics.Payload[3] == 2 && haptics.Payload[6] == 16,
                    "haptics PCM format");
                var frameCount = BinaryPrimitives.ReadUInt16LittleEndian(haptics.Payload.AsSpan(4));
                Require(BinaryPrimitives.ReadUInt32LittleEndian(haptics.Payload.AsSpan(20)) == 48000 &&
                        haptics.Payload.Length == 24 + frameCount * 4,
                    "haptics PCM size");
                var energy = 0L;
                for (var i = 24; i + 1 < haptics.Payload.Length; i += 2)
                    energy += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(haptics.Payload.AsSpan(i, 2)));
                if (energy != 0)
                    capturedHapticsBytes = haptics.Payload.Length - 24;
            }
            await writer.WaitForExitAsync(stopping.Token);
            Require(writer.ExitCode == 0, "audio writer exit code");
        }

        await SendAsync(client, new Protocol.Message(
            Protocol.MessageType.Detach, 3, new byte[] { 0 }), stopping.Token);
        var detach = await ReceiveUntilAsync(client, Protocol.MessageType.DetachReply, 3, stopping.Token);
        Require(detach.Payload.Length == 1 && detach.Payload[0] == 0, "detach reply");

        // Reattach and intentionally drop the owner pipe without DETACH. Completion
        // of serverTask proves the EOF path disposed the still-attached controller.
        await SendAsync(client, new Protocol.Message(
            Protocol.MessageType.Attach, 4, new byte[] { 0, 0, composite ? (byte)1 : (byte)0, 0 }), stopping.Token);
        var ownerCleanupAttach = await ReceiveUntilAsync(
            client, Protocol.MessageType.AttachReply, 4, stopping.Token);
        Require(ownerCleanupAttach.Payload.Length == 8, "owner cleanup attach reply");

        client.Dispose();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        var result = JsonSerializer.Serialize(new
        {
            protocol = Protocol.Version,
            profile = composite ? "dualsense-composite" : "dualsense",
            hello = true,
            attached = true,
            four_channel_audio = capabilities.HasFlag(Protocol.Capability.AudioFourChannel),
            input = true,
            touch = true,
            motion = true,
            battery = true,
            adaptive_triggers = true,
            haptics_pcm = capturedHapticsBytes != 0,
            haptics_bytes = capturedHapticsBytes,
            detached = true,
            owner_disconnect_cleanup = true,
            cleanup = true,
        });
        Console.WriteLine(result);
        if (!string.IsNullOrWhiteSpace(resultPath))
            await File.WriteAllTextAsync(resultPath, result, stopping.Token);
        return 0;
    }

    internal static void RunDeterministicChecks()
    {
        VerifyBundledCompositeProfile();
        VerifyProfileSelection();
        VerifyHapticsChannelIsolation();
        VerifyDefaultAudioEndpointClassification();
        VerifyDefaultAudioEndpointPolicy();
        VerifyControllerStateSubmissionPolicy();
        VerifySensorTimestampEncoding();
        VerifyAdaptiveTriggerEncoding();
        VerifyOutputValidityFlags();
        VerifyOutputValidityGating();
        VerifyLightbarForwarding();
        VerifyRumbleForwarding();
        VerifyTriggerReleaseRepeat();
    }

    private static void VerifyTriggerReleaseRepeat()
    {
        var frame = TimeSpan.FromMilliseconds(8);
        var effect = Enumerable.Range(0x21, AdaptiveTriggerState.EffectSize).Select(value => (byte)value).ToArray();
        var arm = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = effect,
            ["validFlag0"] = (byte)AdaptiveTriggerState.LeftFlag,
        };
        var off = new byte[AdaptiveTriggerState.EffectSize];
        off[0] = AdaptiveTriggerState.OffEffect;
        var release = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = off,
            ["validFlag0"] = (byte)AdaptiveTriggerState.LeftFlag,
        };
        var valid = OutputValidFlags.From(arm);

        var state = new AdaptiveTriggerState();
        Require(state.TryUpdate(arm, valid, TimeSpan.Zero, 1, 0, out _), "adaptive trigger arm");
        // An effect the client is holding is never programmed again: the player
        // would feel the stop of a stateful effect re-engage.
        Require(!state.TryUpdate(arm, valid, TimeSpan.FromSeconds(10), 1, 0, out _),
            "an armed adaptive trigger is never repeated");

        var releasedAt = TimeSpan.FromSeconds(10) + frame;
        Require(state.TryUpdate(release, valid, releasedAt, 1, 0, out var released) &&
                released.Payload[2] == AdaptiveTriggerState.LeftFlag &&
                released.Payload[3] == AdaptiveTriggerState.OffEffect &&
                released.Payload.AsSpan(6, 10).IndexOfAnyExcept((byte)0) == -1,
            "the Off type reaches the client as written");
        Require(!state.TryUpdate(release, valid,
                    releasedAt + AdaptiveTriggerState.ReleaseRepeatDelay - frame, 1, 0, out _),
            "a release repeat waits out its delay");
        Require(state.TryUpdate(release, valid,
                    releasedAt + AdaptiveTriggerState.ReleaseRepeatDelay, 1, 0, out var repeat) &&
                repeat.Payload[2] == AdaptiveTriggerState.LeftFlag &&
                repeat.Payload[3] == AdaptiveTriggerState.OffEffect,
            "a release is repeated once");
        Require(!state.TryUpdate(release, valid,
                    releasedAt + AdaptiveTriggerState.ReleaseRepeatDelay * 8, 1, 0, out _),
            "a release is repeated only once");

        // A re-arm before the repeat is due cancels it: the repeat would zero
        // the effect the client has just armed.
        var rearmed = new AdaptiveTriggerState();
        Require(rearmed.TryUpdate(arm, valid, TimeSpan.Zero, 1, 0, out _), "arm before a cancelled repeat");
        Require(rearmed.TryUpdate(release, valid, frame, 1, 0, out _), "release before a cancelled repeat");
        Require(rearmed.TryUpdate(arm, valid, frame * 2, 1, 0, out _), "re-arm before the repeat is due");
        Require(!rearmed.TryUpdate(arm, valid, frame * 2 + AdaptiveTriggerState.ReleaseRepeatDelay, 1, 0, out _),
            "a re-arm cancels the release repeat");

        // An all-zero trigger section is the other way to say "no effect".
        var zeroed = new AdaptiveTriggerState();
        var cleared = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = new byte[AdaptiveTriggerState.EffectSize],
            ["validFlag0"] = (byte)AdaptiveTriggerState.LeftFlag,
        };
        Require(zeroed.TryUpdate(arm, valid, TimeSpan.Zero, 1, 0, out _), "arm before a zeroed release");
        Require(zeroed.TryUpdate(cleared, valid, frame, 1, 0, out _), "zeroed release");
        Require(zeroed.TryUpdate(cleared, valid, frame + AdaptiveTriggerState.ReleaseRepeatDelay, 1, 0, out _),
            "a zeroed release is repeated once");

        // A release that lands as the last report has nothing following it to
        // carry the repeat, so it has to come from the clock instead.
        var quiet = new AdaptiveTriggerState();
        var quietAt = frame;
        Require(quiet.TryUpdate(arm, valid, TimeSpan.Zero, 1, 0, out _), "arm before a silent release");
        Require(quiet.TryUpdate(release, valid, quietAt, 1, 0, out _), "silent release");
        Require(!quiet.TryRefresh(quietAt + AdaptiveTriggerState.ReleaseRepeatDelay - frame, 1, 0, out _),
            "a silent release repeat waits");
        Require(quiet.TryRefresh(quietAt + AdaptiveTriggerState.ReleaseRepeatDelay, 1, 0, out var silent) &&
                silent.Payload[2] == AdaptiveTriggerState.LeftFlag &&
                silent.Payload[3] == AdaptiveTriggerState.OffEffect,
            "a release repeat reaches a quiet game");
        Require(!quiet.TryRefresh(quietAt + AdaptiveTriggerState.ReleaseRepeatDelay * 8, 1, 0, out _),
            "a delivered release repeat is not resent");

        // Each side carries its own deadline: a shared one lets the later
        // release push the earlier side's repeat out.
        var both = new AdaptiveTriggerState();
        var armBoth = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = ArmedEffect(),
            ["rightTriggerEffect"] = ArmedEffect(),
            ["validFlag0"] = (byte)(AdaptiveTriggerState.LeftFlag | AdaptiveTriggerState.RightFlag),
        };
        var releaseLeft = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = OffEffectBytes(),
            ["rightTriggerEffect"] = ArmedEffect(),
            ["validFlag0"] = (byte)(AdaptiveTriggerState.LeftFlag | AdaptiveTriggerState.RightFlag),
        };
        var releaseBoth = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = OffEffectBytes(),
            ["rightTriggerEffect"] = OffEffectBytes(),
            ["validFlag0"] = (byte)(AdaptiveTriggerState.LeftFlag | AdaptiveTriggerState.RightFlag),
        };
        var bothValid = OutputValidFlags.From(armBoth);
        Require(both.TryUpdate(armBoth, bothValid, TimeSpan.Zero, 1, 0, out _), "arm both triggers");

        var leftAt = frame;
        Require(both.TryUpdate(releaseLeft, bothValid, leftAt, 1, 0, out _), "release the left trigger");
        // The right release lands just short of the left repeat's deadline.
        var rightAt = leftAt + AdaptiveTriggerState.ReleaseRepeatDelay - frame;
        Require(both.TryUpdate(releaseBoth, bothValid, rightAt, 1, 0, out _), "release the right trigger");
        Require(both.TryRefresh(leftAt + AdaptiveTriggerState.ReleaseRepeatDelay, 1, 0, out var left) &&
                left.Payload[2] == AdaptiveTriggerState.LeftFlag,
            "a later release does not defer the left repeat");
        Require(both.TryRefresh(rightAt + AdaptiveTriggerState.ReleaseRepeatDelay, 1, 0, out var right) &&
                right.Payload[2] == AdaptiveTriggerState.RightFlag,
            "the right repeat keeps its own deadline");
    }

    private static byte[] ArmedEffect()
    {
        var effect = new byte[AdaptiveTriggerState.EffectSize];
        effect[0] = 0x02;
        effect[1] = 0x90;
        return effect;
    }

    private static byte[] OffEffectBytes()
    {
        var effect = new byte[AdaptiveTriggerState.EffectSize];
        effect[0] = AdaptiveTriggerState.OffEffect;
        return effect;
    }

    private static void VerifyLightbarForwarding()
    {
        var lightbar = new LightbarState();
        var frame = TimeSpan.FromMilliseconds(8);
        var red = Lightbar(0xFF, 0x00, 0x00);
        var green = Lightbar(0x00, 0xFF, 0x00);

        Require(lightbar.TryUpdate(red, Unflagged, TimeSpan.Zero, 1, 0, out var first) &&
                first.Type == Protocol.MessageType.Led &&
                first.Payload.Length == 5 &&
                first.Payload[0] == 1 && first.Payload[1] == 0 &&
                first.Payload[2] == 0xFF && first.Payload[3] == 0 && first.Payload[4] == 0,
            "first lightbar color");
        for (var i = 1; i <= 8; i++)
        {
            Require(!lightbar.TryUpdate(red, Unflagged, frame * i, 1, 0, out _),
                "republished lightbar color suppression");
        }

        // The client writes the color straight to the controller and nothing
        // expires it, so a color lost on the way is lost for the session.
        Require(lightbar.TryUpdate(red, Unflagged, LightbarState.RepeatDelay, 1, 0, out var repeat) &&
                repeat.Payload[2] == 0xFF && repeat.Payload[3] == 0 && repeat.Payload[4] == 0,
            "a lightbar color is repeated once");
        Require(!lightbar.TryUpdate(red, Unflagged, LightbarState.RepeatDelay * 8, 1, 0, out _),
            "a lightbar color is repeated only once");

        var changedAt = LightbarState.RepeatDelay * 8 + frame;
        Require(lightbar.TryUpdate(green, Unflagged, changedAt, 1, 0, out var changed) &&
                changed.Payload[3] == 0xFF,
            "changed lightbar color");

        // A report that is not programming the lightbar leaves those bytes
        // zero, and reading them as black is what makes a held color strobe.
        var unlit = Lightbar(0x00, 0x00, 0x00);
        unlit["validFlag1"] = (byte)0x40;
        for (var i = 1; i <= 8; i++)
        {
            Require(!lightbar.TryUpdate(unlit, OutputValidFlags.From(unlit), changedAt + frame * i, 1, 0, out _),
                "an unprogrammed lightbar is ignored");
        }
        // The repeat is owed even while the game is programming nothing.
        Require(lightbar.TryUpdate(unlit, OutputValidFlags.From(unlit),
                    changedAt + LightbarState.RepeatDelay, 1, 0, out var carried) &&
                carried.Payload[3] == 0xFF,
            "an unprogrammed report still carries a due repeat");

        var lit = Lightbar(0x00, 0xFF, 0x00);
        lit["validFlag1"] = (byte)0x44;
        Require(!lightbar.TryUpdate(lit, OutputValidFlags.From(lit),
                    changedAt + LightbarState.RepeatDelay * 8, 1, 0, out _),
            "a programming report still suppresses the color already published");

        // A game that sets a color and goes quiet writes no further report, so
        // the repeat has to reach the wire without one.
        var quiet = new LightbarState();
        Require(quiet.TryUpdate(red, Unflagged, TimeSpan.Zero, 1, 0, out _), "color before a silent repeat");
        Require(!quiet.TryRefresh(LightbarState.RepeatDelay - frame, 1, 0, out _), "a lightbar repeat waits");
        Require(quiet.TryRefresh(LightbarState.RepeatDelay, 1, 0, out var silent) &&
                silent.Payload[2] == 0xFF,
            "a lightbar repeat reaches a quiet game");
        Require(!quiet.TryRefresh(LightbarState.RepeatDelay * 8, 1, 0, out _),
            "a delivered lightbar repeat is not resent");
    }

    private static void VerifyRumbleForwarding()
    {
        var rumble = new RumbleState();
        var frame = TimeSpan.FromMilliseconds(8);

        Require(rumble.TryUpdate(0x40, 0x20, TimeSpan.Zero, 1, 0, out var message) &&
                message.Type == Protocol.MessageType.Rumble &&
                message.Payload.Length == 6 &&
                message.Payload[0] == 1 && message.Payload[1] == 0 &&
                message.Payload[2] == 0x40 && message.Payload[3] == 0x40 &&
                message.Payload[4] == 0x20 && message.Payload[5] == 0x20,
            "rumble update");
        Require(!rumble.TryUpdate(0x40, 0x20, frame, 1, 0, out _), "rumble duplicate suppression");

        // The client arms each packet for a bounded SDL window, so a level that
        // never changes is refreshed on the first report past the interval.
        Require(!rumble.TryUpdate(0x40, 0x20, RumbleState.RefreshInterval - frame, 1, 0, out _),
            "rumble refresh waits");
        Require(rumble.TryUpdate(0x40, 0x20, RumbleState.RefreshInterval, 1, 0, out _),
            "standing rumble refresh");

        // A stop that is lost leaves the motors running for the rest of the
        // client's window, so it is repeated a bounded number of times.
        var stopAt = RumbleState.RefreshInterval + frame;
        Require(rumble.TryUpdate(0, 0, stopAt, 1, 0, out var stop) &&
                stop.Payload[2] == 0 && stop.Payload[3] == 0 &&
                stop.Payload[4] == 0 && stop.Payload[5] == 0,
            "rumble stop");
        var at = stopAt;
        for (var i = 1; i <= RumbleState.StopRepeats; i++)
        {
            Require(!rumble.TryUpdate(0, 0, at + RumbleState.StopRepeatInterval - frame, 1, 0, out _),
                "a rumble stop repeat waits");
            at += RumbleState.StopRepeatInterval;
            Require(rumble.TryUpdate(0, 0, at, 1, 0, out _), "a rumble stop is repeated");
        }
        Require(!rumble.TryUpdate(0, 0, at + RumbleState.StopRepeatInterval * 8, 1, 0, out _),
            "a rumble stop is repeated a bounded number of times");

        // A game that ends a rumble with its last report leaves nothing to
        // carry the repeats, which is the case the stop exists for.
        var quiet = new RumbleState();
        var quietAt = frame;
        Require(quiet.TryUpdate(0x40, 0x20, TimeSpan.Zero, 1, 0, out _), "level before a silent stop");
        Require(quiet.TryUpdate(0, 0, quietAt, 1, 0, out _), "silent stop");
        Require(!quiet.TryRefresh(quietAt + RumbleState.StopRepeatInterval - frame, 1, 0, out _),
            "a silent stop repeat waits");
        Require(quiet.TryRefresh(quietAt + RumbleState.StopRepeatInterval, 1, 0, out var carried) &&
                carried.Payload[2] == 0 && carried.Payload[4] == 0,
            "a rumble stop repeat reaches a quiet game");

        // The idle zero a session opens on has never moved a motor, so it is
        // published once and never repeated.
        var idle = new RumbleState();
        Require(idle.TryUpdate(0, 0, TimeSpan.Zero, 1, 0, out _), "idle rumble level");
        Require(!idle.TryRefresh(RumbleState.StopRepeatInterval * 8, 1, 0, out _),
            "an idle rumble level is never repeated");
    }

    private static void VerifyOutputValidityFlags()
    {
        var absent = OutputValidFlags.From(new Dictionary<string, object>());
        Require(!absent.Known && absent.LeftTrigger && absent.RightTrigger && absent.Lightbar,
            "output validity fallback");

        var silent = Flags(0x00, 0x00);
        Require(silent.Known && !silent.LeftTrigger && !silent.RightTrigger && !silent.Lightbar,
            "output validity gates every field it governs");

        Require(Flags(0x0C, 0x00) is { LeftTrigger: true, RightTrigger: true },
            "output validity both triggers");
        Require(Flags(0x08, 0x00) is { LeftTrigger: true, RightTrigger: false },
            "output validity left trigger only");
        Require(Flags(0x04, 0x00) is { LeftTrigger: false, RightTrigger: true },
            "output validity right trigger only");
        Require(Flags(0x00, 0x44).Lightbar && !Flags(0x00, 0x40).Lightbar,
            "output validity lightbar control");

        // A byte the decoder hides governs nothing: exposing only the lightbar
        // byte must not start gating the triggers.
        var lightbarOnly = OutputValidFlags.From(new Dictionary<string, object>
        {
            ["validFlag1"] = (byte)0x40,
        });
        Require(lightbarOnly.Known && !lightbarOnly.Lightbar &&
                lightbarOnly.LeftTrigger && lightbarOnly.RightTrigger,
            "output validity gates per byte");
    }

    private static void VerifyOutputValidityGating()
    {
        var effect = Enumerable.Range(0x22, AdaptiveTriggerState.EffectSize).Select(value => (byte)value).ToArray();
        var idle = new byte[AdaptiveTriggerState.EffectSize];
        var state = new AdaptiveTriggerState();

        // A report that programs one trigger leaves the other's bytes zero. The
        // trigger it is not programming keeps the effect the client holds.
        var rightOnly = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = idle,
            ["rightTriggerEffect"] = effect,
            ["validFlag0"] = (byte)AdaptiveTriggerState.RightFlag,
        };
        var leftOnly = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = effect,
            ["rightTriggerEffect"] = idle,
            ["validFlag0"] = (byte)AdaptiveTriggerState.LeftFlag,
        };

        Require(state.TryUpdate(rightOnly, OutputValidFlags.From(rightOnly), TimeSpan.Zero, 1, 0, out var right) &&
                right.Payload[2] == AdaptiveTriggerState.RightFlag &&
                right.Payload[4] == effect[0],
            "right trigger armed by its own report");
        Require(state.TryUpdate(leftOnly, OutputValidFlags.From(leftOnly), TimeSpan.Zero, 1, 0, out var left) &&
                left.Payload[2] == AdaptiveTriggerState.LeftFlag &&
                left.Payload[3] == effect[0] && left.Payload[4] == effect[0],
            "left trigger armed without disturbing the right");
        Require(!state.TryUpdate(leftOnly, OutputValidFlags.From(leftOnly), TimeSpan.Zero, 1, 0, out _),
            "an unprogrammed trigger is never released");

        // A report that says it is programming both triggers with no effect is
        // the game letting go, and it reaches the client as written.
        var releaseBoth = new Dictionary<string, object>
        {
            ["leftTriggerEffect"] = idle,
            ["rightTriggerEffect"] = idle,
            ["validFlag0"] = (byte)(AdaptiveTriggerState.LeftFlag | AdaptiveTriggerState.RightFlag),
        };
        Require(state.TryUpdate(releaseBoth, OutputValidFlags.From(releaseBoth), TimeSpan.Zero, 1, 0, out var release) &&
                release.Payload[2] == (AdaptiveTriggerState.LeftFlag | AdaptiveTriggerState.RightFlag) &&
                release.Payload.AsSpan(3).IndexOfAnyExcept((byte)0) == -1,
            "a programmed release reaches the client");
    }

    private static Dictionary<string, object> Lightbar(byte r, byte g, byte b) =>
        new() { ["lightbar"] = new[] { r, g, b } };

    // A report whose validity bytes the decoder does not expose, which is how
    // every field was read before.
    private static readonly OutputValidFlags Unflagged =
        OutputValidFlags.From(new Dictionary<string, object>());

    private static OutputValidFlags Flags(byte flag0, byte flag1) =>
        OutputValidFlags.From(new Dictionary<string, object>
        {
            ["validFlag0"] = flag0,
            ["validFlag1"] = flag1,
        });

    private static void VerifySensorTimestampEncoding()
    {
        Require(ControllerSession.EncodeSensorTimestamp(0) == 0,
            "DualSense sensor timestamp origin");
        Require(ControllerSession.EncodeSensorTimestamp(1_000_000) == 3_000_000,
            "DualSense sensor timestamp tick rate");
        Require(ControllerSession.EncodeSensorTimestamp(0x55555556) == 2,
            "DualSense sensor timestamp rollover");
    }

    private static void VerifyProfileSelection()
    {
        Require(SidecarServer.SelectProfileId(
                1, Protocol.AttachFlags.GenshinCompatibilityIdentity, true, true) ==
                DualSenseHapticsAudio.GenshinCompatibilityProfileId,
            "Genshin compatibility attach profile selection");
        Require(SidecarServer.SelectProfileId(
                1, Protocol.AttachFlags.None, true, true) ==
                DualSenseHapticsAudio.CompositeProfileId,
            "standard composite attach profile selection");
        try
        {
            SidecarServer.SelectProfileId(
                0, Protocol.AttachFlags.GenshinCompatibilityIdentity, true, true);
            Require(false, "Genshin compatibility HID attach rejection");
        }
        catch (InvalidDataException)
        {
            // Expected.
        }
    }

    private static void VerifyControllerStateSubmissionPolicy()
    {
        var policy = new ControllerStateSubmissionPolicy();
        Require(!policy.ObserveInput(0, true), "idle controller state coalescing");
        Require(policy.ObserveInput(0, false), "analog activation boundary");
        Require(!policy.ObserveInput(0, false), "continuous analog state coalescing");
        Require(policy.ObserveInput(0, true), "analog neutral boundary");
        Require(policy.ObserveInput(0x1000, true), "button press boundary");
        Require(policy.ObserveInput(0, true), "button release boundary");
    }

    private static void VerifyDefaultAudioEndpointClassification()
    {
        Require(Enum.GetUnderlyingType(typeof(DefaultAudioEndpointGuard.AudioRole)) == typeof(int),
            "default audio role COM width");
        var virtualDualSense = new[]
        {
            new DefaultAudioEndpointGuard.DeviceNodeIdentity(
                @"SWD\MMDEVAPI\{0.0.0.00000000}.fixture", Array.Empty<string>()),
            new DefaultAudioEndpointGuard.DeviceNodeIdentity(
                @"USB\VID_054C&PID_0CE6&MI_00\fixture",
                new[] { @"USB\VID_054C&PID_0CE6&MI_00" }),
            new DefaultAudioEndpointGuard.DeviceNodeIdentity(
                @"ROOT\USB\0000", new[] { @"ROOT\HIDMAESTRO_UDE" }),
        };
        Require(DefaultAudioEndpointGuard.IsVirtualDualSenseChain(virtualDualSense),
            "virtual HIDMaestro DualSense endpoint classification");

        Require(!DefaultAudioEndpointGuard.IsVirtualDualSenseChain(virtualDualSense[..2]),
            "physical DualSense endpoint exclusion");
        Require(!DefaultAudioEndpointGuard.IsVirtualDualSenseChain(new[]
        {
            new DefaultAudioEndpointGuard.DeviceNodeIdentity(
                @"ROOT\USB\0000", new[] { @"ROOT\HIDMAESTRO_UDE" }),
        }), "unrelated HIDMaestro endpoint exclusion");
    }

    private static void VerifyDefaultAudioEndpointPolicy()
    {
        Require(DefaultAudioEndpointPolicy.NeedsUpdate(null, null),
            "missing default audio endpoint policy");
        Require(DefaultAudioEndpointPolicy.NeedsUpdate(
                "{00000000-0000-0000-0000-000000000000}", 0x00000101),
            "partial default audio endpoint policy");
        Require(!DefaultAudioEndpointPolicy.NeedsUpdate(
                "{00000000-0000-0000-0000-000000000000}", 0x00000307),
            "complete default audio endpoint policy");
        var quadraphonic = DualSenseSpeakerConfiguration.CreateQuadraphonicFormat();
        Require(DualSenseSpeakerConfiguration.HasValidInteropLayout(),
            "Core Audio interop ABI");
        Require(DualSenseSpeakerConfiguration.IsQuadraphonic(quadraphonic),
            "quadraphonic Core Audio speaker configuration");
        quadraphonic.ChannelMask = 0x00000003;
        Require(!DualSenseSpeakerConfiguration.IsQuadraphonic(quadraphonic),
            "stereo Core Audio speaker configuration rejection");
    }

    private static void VerifyBundledCompositeProfile()
    {
        using var stream = typeof(ProtocolSelfTest).Assembly.GetManifestResourceStream(
            "Sunshine.Ds5Sidecar.profiles.dualsense-composite.json")
            ?? throw new InvalidOperationException("Bundled composite profile is missing");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var profileJson = DualSenseHapticsAudio.CreateRuntimeCompositeProfile(memory.ToArray());
        DualSenseHapticsAudio.ValidateCompositeProfile(profileJson);
        var compatibilityProfile = DualSenseHapticsAudio.CreateGenshinCompatibilityProfile(profileJson);
        using (var compatibilityDocument = JsonDocument.Parse(compatibilityProfile))
        {
            var root = compatibilityDocument.RootElement;
            Require(root.GetProperty("id").GetString() ==
                    DualSenseHapticsAudio.GenshinCompatibilityProfileId,
                "Genshin compatibility profile id");
            Require(root.GetProperty("productString").GetString() ==
                    DualSenseHapticsAudio.GenshinCompatibilityProductString,
                "Genshin compatibility product string");
            Require(root.GetProperty("vid").GetString() == "0x054C" &&
                    root.GetProperty("pid").GetString() == "0x0CE6",
                "Genshin compatibility profile preserves Sony identity");
        }

        var profileText = System.Text.Encoding.UTF8.GetString(profileJson);
        RequireProfileRejected(profileText.Replace("\"channels\":4", "\"channels\":2", StringComparison.Ordinal),
            "stereo composite profile rejection");
        RequireProfileRejected(profileText.Replace(
                "\"hapticLeft\",\"hapticRight\"",
                "\"hapticRight\",\"hapticLeft\"",
                StringComparison.Ordinal),
            "swapped haptics role rejection");
        RequireProfileRejected(profileText.Replace(
                "\"volumeCurRaw\":0",
                "\"volumeCurRaw\":-25600",
                StringComparison.Ordinal),
            "muted speaker control rejection");
    }

    private static void RequireProfileRejected(string profileJson, string operation)
    {
        try
        {
            DualSenseHapticsAudio.ValidateCompositeProfile(System.Text.Encoding.UTF8.GetBytes(profileJson));
            Require(false, operation);
        }
        catch (InvalidDataException)
        {
            // Expected.
        }
    }

    private static void VerifyHapticsChannelIsolation()
    {
        var frames = new byte[DualSenseHapticsAudio.InputFrameBytes * 2];
        WriteSample(frames, 0, 1234);
        WriteSample(frames, 1, -2345);
        WriteSample(frames, 4, short.MaxValue);
        WriteSample(frames, 5, short.MinValue);
        var speakerOnly = DualSenseHapticsAudio.Extract(frames);
        Require(speakerOnly.AsSpan().IndexOfAnyExcept((byte)0) == -1,
            "speaker channels cannot leak into haptics");

        WriteSample(frames, 2, 3456);
        WriteSample(frames, 3, -4567);
        WriteSample(frames, 6, 5678);
        WriteSample(frames, 7, -6789);
        var haptics = DualSenseHapticsAudio.Extract(frames);
        Require(BinaryPrimitives.ReadInt16LittleEndian(haptics.AsSpan(0, 2)) == 3456 &&
                BinaryPrimitives.ReadInt16LittleEndian(haptics.AsSpan(2, 2)) == -4567 &&
                BinaryPrimitives.ReadInt16LittleEndian(haptics.AsSpan(4, 2)) == 5678 &&
                BinaryPrimitives.ReadInt16LittleEndian(haptics.AsSpan(6, 2)) == -6789,
            "haptics channels preserve exact samples");

        try
        {
            DualSenseHapticsAudio.Extract(frames.AsSpan(0, frames.Length - 1));
            Require(false, "incomplete four-channel frame rejection");
        }
        catch (InvalidDataException)
        {
            // Expected: arbitrary callback fragmentation is reassembled by
            // ControllerSession before complete frames reach the extractor.
        }
    }

    private static void WriteSample(Span<byte> frames, int sample, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(frames.Slice(sample * 2, 2), value);

    private static async Task SendAsync(Stream stream, Protocol.Message message, CancellationToken cancellationToken)
    {
        var frame = Protocol.Encode(message);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<Protocol.Message> ReceiveAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new byte[Protocol.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken);
        var header = Protocol.DecodeHeader(headerBytes);
        var payload = new byte[header.PayloadLength];
        if (payload.Length != 0)
            await stream.ReadExactlyAsync(payload, cancellationToken);
        return new Protocol.Message(header.Type, header.RequestId, payload);
    }

    private static async Task<Protocol.Message> ReceiveUntilAsync(
        Stream stream, Protocol.MessageType type, uint requestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveAsync(stream, cancellationToken);
            if (message.Type == Protocol.MessageType.Error && message.RequestId == requestId)
                throw new InvalidOperationException(DecodeError(message.Payload));
            if (message.Type == type && message.RequestId == requestId)
                return message;
        }
    }

    private static async Task<Protocol.Message> ReceiveUntilTypeAsync(
        Stream stream, Protocol.MessageType type, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveAsync(stream, cancellationToken);
            if (message.Type == Protocol.MessageType.Error)
                throw new InvalidOperationException(DecodeError(message.Payload));
            if (message.Type == type)
                return message;
        }
    }

    private static string DecodeError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return "Malformed sidecar error";
        var length = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)), (uint)(payload.Length - 8));
        return System.Text.Encoding.UTF8.GetString(payload.Slice(8, (int)length));
    }

    private static void WriteFloat(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static void VerifyAdaptiveTriggerEncoding()
    {
        var state = new AdaptiveTriggerState();
        var left = Enumerable.Range(0x20, AdaptiveTriggerState.EffectSize).Select(value => (byte)value).ToArray();
        var right = Enumerable.Range(0x40, AdaptiveTriggerState.EffectSize).Select(value => (byte)value).ToArray();

        Require(state.TryUpdate(new Dictionary<string, object> { ["leftTriggerEffect"] = left },
                                Unflagged, TimeSpan.Zero, 3, 2, out var leftMessage),
            "left adaptive trigger update");
        Require(leftMessage.Type == Protocol.MessageType.AdaptiveTriggers &&
                leftMessage.Payload.Length == 26 &&
                leftMessage.Payload[0] == 3 && leftMessage.Payload[1] == 2 &&
                leftMessage.Payload[2] == AdaptiveTriggerState.LeftFlag &&
                leftMessage.Payload[3] == left[0] && leftMessage.Payload[4] == 0 &&
                leftMessage.Payload.AsSpan(6, 10).SequenceEqual(left.AsSpan(1, 10)),
            "left adaptive trigger encoding");

        Require(!state.TryUpdate(new Dictionary<string, object> { ["leftTriggerEffect"] = left },
                                 Unflagged, TimeSpan.Zero, 3, 2, out _),
            "adaptive trigger duplicate suppression");

        Require(state.TryUpdate(new Dictionary<string, object> { ["rightTriggerEffect"] = right },
                                Unflagged, TimeSpan.Zero, 3, 2, out var rightMessage) &&
                rightMessage.Payload[2] == AdaptiveTriggerState.RightFlag &&
                rightMessage.Payload[3] == left[0] && rightMessage.Payload[4] == right[0] &&
                rightMessage.Payload.AsSpan(16, 10).SequenceEqual(right.AsSpan(1, 10)),
            "right adaptive trigger encoding");

        Require(state.TryReset(3, 2, out var resetMessage) &&
                resetMessage.Payload[2] == (AdaptiveTriggerState.LeftFlag | AdaptiveTriggerState.RightFlag) &&
                resetMessage.Payload.AsSpan(3).IndexOfAnyExcept((byte)0) == -1,
            "adaptive trigger reset");
        Require(!state.TryReset(3, 2, out _), "adaptive trigger reset duplicate suppression");
    }

    private static void Require(bool condition, string operation)
    {
        if (!condition) throw new InvalidOperationException($"Protocol self-test failed at {operation}");
    }
}
