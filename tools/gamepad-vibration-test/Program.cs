// gamepad-vibration-test: a throwaway diagnostic tool, not part of the
// Apollo build. Drives classic XInputSetState directly (P/Invoke into
// xinput1_4.dll) - the same API most PC games use for controller rumble
// - to test main-motor vibration on a connected gamepad.
//
// NOTE (2026-09-20): this used to drive Windows.Gaming.Input.Gamepad
// instead. Switched after finding HIDMaestro's own investigation
// (docs/investigations/wgi-silent-sink-2026-04/ in their repo) which
// confirms Windows.Gaming.Input.Gamepad.Vibration (and GameInput, and
// anything built on top of them - Chromium/browser haptics, and very
// likely Steam's own controller vibration test) never delivers motor
// bytes to a ROOT-enumerated virtual controller like HIDMaestro's,
// regardless of the app. It's a Windows architectural gate (WGI's XUSB
// vibration dispatch requires a real USB bus parent), not something
// fixable from a companion process. Classic XInputSetState is the one
// path HIDMaestro's own team confirmed DOES reach the virtual device
// correctly - hence this tool now uses it instead, as an actual
// positive-control test. It only covers the two main motors: classic
// XInput has no concept of trigger-impulse motors at all (those are
// WGI/GameInput-only), so this tool can't test trigger haptics - per the
// same investigation, those very likely don't work on this setup at all.
//
// Run with `dotnet run` from this directory on the Windows machine where
// the controller is connected (through Apollo/HIDMaestro, or a real
// controller - works the same either way).

using System.Runtime.InteropServices;

Console.WriteLine("Looking for a connected XInput gamepad (slots 0-3)...");

int foundSlot = -1;
for (var attempt = 0; attempt < 50 && foundSlot < 0; attempt++)
{
    for (var slot = 0; slot < 4; slot++)
    {
        if (XInputGetState(slot, out _) == 0 /* ERROR_SUCCESS */)
        {
            foundSlot = slot;
            break;
        }
    }
    if (foundSlot < 0)
    {
        Thread.Sleep(100);
    }
}

if (foundSlot < 0)
{
    Console.WriteLine("No XInput gamepad found after 5 seconds. Is it connected and recognized by Windows?");
    return 1;
}

Console.WriteLine($"Gamepad found at XInput slot {foundSlot}. Cycling left and right motors, 2 seconds each with 1 second off between.");
Console.WriteLine("(Trigger-impulse motors aren't testable this way - see the note at the top of this file.)");
Console.WriteLine("Press Ctrl+C to stop at any time.\n");

var steps = new (string Label, ushort Left, ushort Right)[]
{
    ("Left motor (main, low-frequency)", ushort.MaxValue, 0),
    ("Right motor (main, high-frequency)", 0, ushort.MaxValue),
    ("Both motors", ushort.MaxValue, ushort.MaxValue),
};

while (true)
{
    foreach (var (label, left, right) in steps)
    {
        Console.WriteLine($"-> {label}");
        var vibration = new XINPUT_VIBRATION { wLeftMotorSpeed = left, wRightMotorSpeed = right };
        XInputSetState(foundSlot, ref vibration);
        Thread.Sleep(2000);

        var off = new XINPUT_VIBRATION();
        XInputSetState(foundSlot, ref off);
        Thread.Sleep(1000);
    }

    Console.WriteLine("\nFull cycle done. Repeating - Ctrl+C to stop.\n");
}

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_VIBRATION
{
    public ushort wLeftMotorSpeed;
    public ushort wRightMotorSpeed;
}

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_GAMEPAD
{
    public ushort wButtons;
    public byte bLeftTrigger;
    public byte bRightTrigger;
    public short sThumbLX;
    public short sThumbLY;
    public short sThumbRX;
    public short sThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
struct XINPUT_STATE
{
    public uint dwPacketNumber;
    public XINPUT_GAMEPAD Gamepad;
}

partial class Program
{
    [LibraryImport("xinput1_4.dll")]
    internal static partial int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [LibraryImport("xinput1_4.dll")]
    internal static partial int XInputSetState(int dwUserIndex, ref XINPUT_VIBRATION pVibration);
}
