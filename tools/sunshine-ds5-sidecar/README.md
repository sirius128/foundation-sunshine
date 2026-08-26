# Sunshine DualSense sidecar

This optional Windows helper isolates Sunshine from the third-party
HIDMaestro runtime. It owns virtual DS5 devices and exposes the versioned
`SDS5` named-pipe protocol. The helper does not contain HIDMaestro binaries.

Build against the pinned upstream v1.6.2 runtime:

```powershell
dotnet build -c Release `
  -p:HIDMaestroCorePath=C:\path\to\HIDMaestro.Core.dll
```

Read-only capability probe:

```powershell
dotnet Sunshine.Ds5Sidecar.dll --probe
```

Deterministic four-channel layout and channel-isolation check (no elevation
or virtual device required):

```powershell
dotnet Sunshine.Ds5Sidecar.dll --self-check
```

The production process must be launched elevated and placed in the Sunshine
Job Object. The pipe accepts a single elevated client of the creating user;
non-elevated callers are rejected at connect time and dropped without
ending the sidecar.
Disconnecting the owning pipe disposes every device created by
that connection. Standard `dualsense` uses UMDF2; `dualsense-composite`
enables the USB composite HID/audio profile and authored haptics PCM.
The optional Genshin compatibility attach flag derives a third profile from
`dualsense-composite` at runtime. It preserves the Sony VID/PID, descriptors,
and four-channel layout while changing only the USB product string from
`DualSense Wireless Controller` to the launch-model `Wireless Controller`.
The runtime profile starts the USB speaker control unmuted at its declared
maximum and commits the active endpoint's 4-channel, available-speaker and
full-range masks as quadraphonic (`0x33`) through Core Audio. This matches the
three settings applied by completing Windows' speaker setup wizard as required
by Genshin.
The sidecar advertises this support through a protocol capability bit so an
older runtime cannot silently accept an ineffective setting.
The composite session monitors every Windows default render and capture role.
If Windows selects a HIDMaestro-backed virtual DualSense endpoint as a default,
the helper reports the policy violation and exits; Sunshine then performs its
single recovery attach in HID-only DS5 mode. This read-only fail-closed guard
avoids undocumented audio-policy writes and never changes a user's defaults.

The trigger effects and lightbar of an output report a game writes to the
virtual pad are read the way the hardware reads them: only when the report's
validity byte for that field is set. A game leaves the fields it is not
programming zero, so taking those zeros at face value cancels an effect the game
just armed and strobes a held color. A validity byte the decoder does not expose
governs nothing, and the fields it would gate are read at face value, as before.
The motor bytes are the exception and stay at face value: a writer may clear
them in a report that does not claim them, and a stop that never arrives leaves
the motors running for the rest of the client's 30-second SDL window.
What survives that reading is forwarded only when it differs from what the
client already applied, because the client replays every forwarded packet as a
write to the player's physical controller.

The feedback queue keeps 32 messages and clears every one of them when a 33rd
arrives, and haptics PCM shares it, so a burst discards whatever was waiting.
Feedback is not equally recoverable, so each kind is repeated to the degree its
loss costs, on a 250 ms timer rather than on the next report to arrive, because
a game that ends a rumble or releases a trigger with its last output report
leaves nothing behind to carry the repeat:

| Feedback | Client-side lifetime | Delivery |
| --- | --- | --- |
| Standing motor level | 30 s SDL window | refreshed every 5 s |
| Motor stop | none | repeated 4 times, 250 ms apart |
| Trigger arm | none, held on the trigger | sent once |
| Trigger release | none, held on the trigger | repeated once, 250 ms later |
| Lightbar color | none, held on the controller | repeated once, 250 ms later |

Copies are spaced because packets sent a frame apart share the queue and are
discarded together. An arm is never repeated: an effect that carries internal
state restarts when it is programmed again, which a player holding the trigger
feels as the stop re-engaging.
