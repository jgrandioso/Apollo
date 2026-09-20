// hidmaestro-bridge: a small companion process that lets Apollo (C++) drive
// a virtual Xbox Series X|S controller through HIDMaestro (C#-only SDK,
// https://github.com/hifihedgehog/HIDMaestro), and get rumble/trigger-rumble
// events back. See src/platform/windows/input.cpp (hidmaestro_t class) for
// the wire protocol this program speaks over stdin/stdout, and
// docs/dev/hidmaestro-backend-analysis.md for why this exists as a separate
// process instead of being linked into Apollo directly.
//
// CONFIDENCE NOTE: the SDK surface used below (HMContext, HMController,
// HMGamepadState, HMButton, HMAxis, HMHat, HMGamepadStateHelpers,
// HMOutputSource) is taken directly from HIDMaestro's real source
// (sdk/HIDMaestro.Core/HMGamepadState.cs) and its official SDK demo
// (example/SdkDemo/Program.cs), both fetched and read in full - not
// guessed from documentation summaries. The demo even builds an Xbox
// Series X|S controller (its "12b" section) with real HMButton usage
// matching what's used here.
//
// The one thing that is STILL a best-guess, because the demo never
// actually receives output from an Xbox-family controller (only from a
// DualSense, via the different OutputDecoded/vendor-blob path): the exact
// byte layout of the OutputReceived packet this profile emits for
// rumble/trigger-rumble, further down in HandleOutputReceived(). That
// assumption is grounded in a concrete reference (PadForge's
// XboxImpulseHidWriter.cs, same author, same controller family, for
// physical Xbox One+/Series controllers on the same GIP path) but has not
// been confirmed against this specific virtual profile. Check this first
// if rumble/trigger-rumble events don't fire, or fire with garbled values,
// on a real Windows test.
//
// UPDATE (real Windows test #1, 2026-09-20): no rumble at all was
// observed, confirming the above guess needed verification -
// HandleOutputReceived() logged every OutputReceived packet
// unconditionally (via EmitError, prefixed "[diag]") instead of only
// decoding ones matching the guessed 13-byte shape. Same test also
// confirmed both sticks' vertical axes were inverted - fixed (see
// NormalizeStickY below), unrelated to the rumble byte-layout guess.
//
// UPDATE (real Windows test #2, 2026-09-20): the [diag] logging paid off -
// captured a real packet from Steam's controller vibration test:
// source=HidOutput (NOT XInput - the original code only accepted XInput
// and silently dropped everything else, which alone explained the total
// silence), len=7, bytes 00 00 00 00 FF 00 EB. HandleOutputReceived()
// briefly handled HidOutput with a 7-byte shape inferred from that capture,
// alongside XInput using the format HMOutputPacket.cs's own doc comment
// actually documents (5 bytes: cmd + size + lo motor + hi motor +
// reserved).
//
// UPDATE (2026-09-20, same day): the HidOutput branch was reverted.
// HIDMaestro's own WGI investigation (docs/investigations/wgi-silent-sink-2026-04
// in their repo - see docs/dev/hidmaestro-backend.md for the summary)
// describes WGI sending control/probe packets (00 0D 00 00 01, 00 00 00 00 02)
// that never carry real motor data, structurally similar to the all-zero
// capture this branch was built from. Steam almost certainly talks to this
// device via WGI. Treating that capture as real (if zero) rumble data risked
// emitting a spurious rumble:0,0 event on every WGI probe, potentially
// stomping a real value that had just arrived via genuine XInputSetState.
// Only the XInput path remains - real rumble was confirmed working through
// an actual game using it. Diagnostic [diag] logging kept in place.

using System.Text.Json.Nodes;
using HIDMaestro;

namespace ApolloHidMaestroBridge;

internal static class Program
{
    // Apollo's own gamepad button bitmask (src/platform/common.h) - passed
    // through the wire protocol as a raw uint32 rather than re-decoded name
    // by name, so this table is the only place that needs to match Apollo's
    // constants if they ever change.
    private const uint DPAD_UP = 0x0001;
    private const uint DPAD_DOWN = 0x0002;
    private const uint DPAD_LEFT = 0x0004;
    private const uint DPAD_RIGHT = 0x0008;
    private const uint START = 0x0010;
    private const uint BACK = 0x0020;
    private const uint LEFT_STICK = 0x0040;
    private const uint RIGHT_STICK = 0x0080;
    private const uint LEFT_BUTTON = 0x0100;
    private const uint RIGHT_BUTTON = 0x0200;
    private const uint HOME = 0x0400;
    private const uint A = 0x1000;
    private const uint B = 0x2000;
    private const uint X = 0x4000;
    private const uint Y = 0x8000;
    private const uint MISC_BUTTON = 0x200000;

    private static readonly object s_stdoutLock = new();
    private static HMContext? s_context;
    private static readonly Dictionary<int, HMController> s_controllers = new();

    private static int Main()
    {
        try
        {
            s_context = new HMContext();
            if (!s_context.IsDriverInstalled)
            {
                s_context.InstallDriver();
            }
            s_context.LoadDefaultProfiles();
        }
        catch (Exception ex)
        {
            EmitError($"Failed to initialize HIDMaestro: {ex.Message}");
            return 1;
        }

        EmitEvent(new JsonObject { ["event"] = "ready" });

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                HandleCommand(line);
            }
            catch (Exception ex)
            {
                EmitError($"Error handling command: {ex.Message}");
            }
        }

        // stdin closed (Apollo exited or explicitly asked to shut down) -
        // clean up every virtual controller before exiting.
        foreach (var controller in s_controllers.Values)
        {
            try { controller.Dispose(); } catch { /* best effort on shutdown */ }
        }
        s_controllers.Clear();
        s_context?.Dispose();

        return 0;
    }

    private static void HandleCommand(string line)
    {
        var msg = JsonNode.Parse(line)?.AsObject();
        if (msg is null)
        {
            EmitError("Received a line that wasn't a JSON object, ignoring.");
            return;
        }

        var cmd = msg["cmd"]?.GetValue<string>();

        switch (cmd)
        {
            case "create_pad":
                CreatePad(msg);
                break;
            case "set_state":
                SetState(msg);
                break;
            case "destroy_pad":
                DestroyPad(msg);
                break;
            case "shutdown":
                // Closing stdin (which Apollo does right after sending this)
                // is what actually ends the Main() read loop above; nothing
                // else to do here.
                break;
            default:
                EmitError($"Unknown command: {cmd}");
                break;
        }
    }

    private static void CreatePad(JsonObject msg)
    {
        int id = msg["id"]!.GetValue<int>();
        // "xbox-series-xs" is the real profile id from HIDMaestro's own
        // profiles/microsoft/xbox-series-xs.json (fetched and read directly -
        // it declares "haptics":{"rumble":"impulse_triggers",
        // "triggerHaptics":true}), not a guess.
        string profileId = msg["profile"]?.GetValue<string>() ?? "xbox-series-xs";

        if (s_context is null)
        {
            EmitError("create_pad requested but HIDMaestro context failed to initialize earlier.");
            return;
        }

        var profile = s_context.GetProfile(profileId);
        if (profile is null)
        {
            EmitError($"HIDMaestro has no profile named '{profileId}' loaded.");
            return;
        }

        var controller = s_context.CreateControllerAt(id, profile);
        controller.OutputReceived += (_, packet) => HandleOutputReceived(id, packet);

        s_controllers[id] = controller;
    }

    /// <summary>
    /// Decodes rumble + trigger-rumble from a raw output report and emits the
    /// corresponding wire-protocol events. See the CONFIDENCE NOTE at the top
    /// of this file - the byte layout here is the single biggest unverified
    /// assumption in this whole program.
    /// </summary>
    private static void HandleOutputReceived(int id, HMOutputPacket packet)
    {
        // Diagnostic-only, kept from the previous round: logs every packet
        // unconditionally so a wrong assumption below is visible in Apollo's
        // log instead of silently dropping rumble with no trace at all.
        EmitError($"[diag] OutputReceived source={packet.Source} len={packet.Data.Length} bytes={Convert.ToHexString(packet.Data.Span)}");

        var data = packet.Data.Span;

        // Apollo fork addition (2026-09-20, REVERTED same day): originally
        // this also handled HMOutputSource.HidOutput, inferred from a Steam
        // capture (00 00 00 00 FF 00 EB) that looked like the tail of the
        // old 13-byte guess with a stripped header. Reverted after finding
        // HIDMaestro's own WGI investigation (see the update further down
        // in docs/dev/hidmaestro-backend.md): Steam almost certainly talks
        // to this virtual device via WGI, and WGI is documented to send
        // control/probe packets (00 0D 00 00 01, 00 00 00 00 02) that carry
        // no real motor data even when it never reaches the driver's
        // vibration dispatch at all - structurally similar in shape to what
        // was captured here. Treating that capture as real (if all-zero)
        // rumble data risked emitting a spurious rumble:0,0 event on every
        // WGI probe, potentially stomping a real value that had just arrived
        // via a genuine XInputSetState call. Real-hardware rumble was
        // confirmed working through a real game after this revert, using
        // only the path below.
        if (packet.Source == HMOutputSource.XInput)
        {
            // Per HMOutputPacket.cs's own doc comment (not a guess this
            // time): "XInputSetState. Bytes are the XUSB-wire-format
            // vibration packet, typically 5 bytes: cmd + size + lo motor +
            // hi motor + reserved." No trigger rumble in this format - XInput
            // itself has no trigger motors, only main left/right.
            if (data.Length == 5)
            {
                EmitEvent(new JsonObject
                {
                    ["event"] = "rumble",
                    ["id"] = id,
                    ["large"] = data[2],
                    ["small"] = data[3],
                });
            }
            return;
        }
    }

    private static void SetState(JsonObject msg)
    {
        int id = msg["id"]!.GetValue<int>();
        if (!s_controllers.TryGetValue(id, out var controller))
        {
            return;
        }

        uint buttons = msg["buttons"]!.GetValue<uint>();
        byte lt = msg["lt"]!.GetValue<byte>();
        byte rt = msg["rt"]!.GetValue<byte>();
        short lsX = msg["lsX"]!.GetValue<short>();
        short lsY = msg["lsY"]!.GetValue<short>();
        short rsX = msg["rsX"]!.GetValue<short>();
        short rsY = msg["rsY"]!.GetValue<short>();

        // HMButton has no D-pad members - the d-pad is a separate Hat field
        // (HMHat, 8-octant enum). Confirmed against sdk/HIDMaestro.Core/HMGamepadState.cs.
        HMButton hmButtons = HMButton.None;
        if ((buttons & START) != 0) hmButtons |= HMButton.Start;
        if ((buttons & BACK) != 0) hmButtons |= HMButton.Back;
        if ((buttons & LEFT_STICK) != 0) hmButtons |= HMButton.LeftStick;
        if ((buttons & RIGHT_STICK) != 0) hmButtons |= HMButton.RightStick;
        if ((buttons & LEFT_BUTTON) != 0) hmButtons |= HMButton.LeftBumper;
        if ((buttons & RIGHT_BUTTON) != 0) hmButtons |= HMButton.RightBumper;
        if ((buttons & (HOME | MISC_BUTTON)) != 0) hmButtons |= HMButton.Guide;
        if ((buttons & A) != 0) hmButtons |= HMButton.A;
        if ((buttons & B) != 0) hmButtons |= HMButton.B;
        if ((buttons & X) != 0) hmButtons |= HMButton.X;
        if ((buttons & Y) != 0) hmButtons |= HMButton.Y;

        HMHat hat = DecodeHat(buttons);

        // Axes are [0..1] uniformly: 0.5 = center on sticks, 0.0 = released
        // on triggers (confirmed in HMGamepadState.cs's own doc comments and
        // used throughout the official SDK demo). StandardAxes resolves the
        // correct HMAxis keys for whichever profile is active instead of
        // this file hardcoding axis names itself.
        var axes = HMGamepadStateHelpers.StandardAxes(
            controller.Profile,
            leftStickX: NormalizeStick(lsX),
            leftStickY: NormalizeStickY(lsY),
            rightStickX: NormalizeStick(rsX),
            rightStickY: NormalizeStickY(rsY),
            leftTrigger: NormalizeTrigger(lt),
            rightTrigger: NormalizeTrigger(rt)
        );

        var state = new HMGamepadState
        {
            Buttons = hmButtons,
            Hat = hat,
            Axes = axes,
        };

        controller.SubmitState(in state);
    }

    /// <summary>Maps Apollo's 4 individual d-pad bits to HIDMaestro's 8-octant Hat enum.</summary>
    private static HMHat DecodeHat(uint buttons)
    {
        bool up = (buttons & DPAD_UP) != 0;
        bool down = (buttons & DPAD_DOWN) != 0;
        bool left = (buttons & DPAD_LEFT) != 0;
        bool right = (buttons & DPAD_RIGHT) != 0;

        // Conflicting opposite directions (e.g. up+down together) fall back
        // to None rather than picking one arbitrarily.
        if (up && down) { up = false; down = false; }
        if (left && right) { left = false; right = false; }

        if (up && right) return HMHat.NorthEast;
        if (down && right) return HMHat.SouthEast;
        if (down && left) return HMHat.SouthWest;
        if (up && left) return HMHat.NorthWest;
        if (up) return HMHat.North;
        if (right) return HMHat.East;
        if (down) return HMHat.South;
        if (left) return HMHat.West;
        return HMHat.None;
    }

    /// <summary>Apollo's signed 16-bit stick range to HIDMaestro's [0..1] (0.5 = center).</summary>
    private static float NormalizeStick(short raw) => (raw / 32768f + 1f) / 2f;

    /// <summary>Same as <see cref="NormalizeStick"/> but flipped for the Y
    /// axes specifically. Apollo's raw lsY/rsY follow XInput's sThumbLY/RY
    /// convention (positive = stick pushed up). HIDMaestro's HMAxis.Y (and
    /// whatever Y usage each stick's profile declares) is a generic HID
    /// Y usage, which - unlike XInput - conventionally increases downward
    /// (same as DirectInput/most raw joystick HID reports). Confirmed as
    /// the actual root cause of both sticks appearing vertically inverted
    /// in a real HIDMaestro test; see docs/dev/hidmaestro-backend.md.</summary>
    private static float NormalizeStickY(short raw) => 1f - NormalizeStick(raw);

    /// <summary>Apollo's unsigned 8-bit trigger range to HIDMaestro's [0..1] (0 = released).</summary>
    private static float NormalizeTrigger(byte raw) => raw / 255f;

    private static void DestroyPad(JsonObject msg)
    {
        int id = msg["id"]!.GetValue<int>();
        if (s_controllers.Remove(id, out var controller))
        {
            controller.Dispose();
        }
    }

    private static void EmitEvent(JsonObject obj)
    {
        // stdout is also shared with EmitError - lock so two events from
        // different threads (OutputReceived fires on whatever thread
        // HIDMaestro's driver uses, not necessarily the stdin-reading main
        // thread) can't interleave their bytes into a line Apollo can't parse.
        lock (s_stdoutLock)
        {
            Console.Out.Write(obj.ToJsonString());
            Console.Out.Write('\n');
            Console.Out.Flush();
        }
    }

    private static void EmitError(string message)
    {
        EmitEvent(new JsonObject { ["event"] = "error", ["message"] = message });
    }
}
