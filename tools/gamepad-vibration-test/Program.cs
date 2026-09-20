// gamepad-vibration-test: a throwaway diagnostic tool, not part of the
// Apollo build. Drives Windows.Gaming.Input.Gamepad directly (the same
// API real games use for Xbox Series/Elite impulse trigger haptics) to
// cycle each of the four vibration motors one at a time, so you can
// confirm rumble works and which physical motor is which - without
// needing a specific game or the Xbox Accessories app.
//
// Run with `dotnet run` from this directory on the Windows machine where
// the controller is connected (through Apollo/HIDMaestro, or a real
// controller - works the same either way, since it's talking to whatever
// Windows.Gaming.Input enumerates).

using Windows.Gaming.Input;

Console.WriteLine("Looking for a connected gamepad...");

Gamepad? gamepad = null;
for (var attempt = 0; attempt < 50 && gamepad is null; attempt++)
{
    gamepad = Gamepad.Gamepads.FirstOrDefault();
    if (gamepad is null)
    {
        Thread.Sleep(100);
    }
}

if (gamepad is null)
{
    Console.WriteLine("No gamepad found after 5 seconds. Is it connected and recognized by Windows?");
    return 1;
}

Console.WriteLine("Gamepad found. Cycling each vibration motor for 2 seconds, with 1 second off between - watch/feel which one reacts.");
Console.WriteLine("Press Ctrl+C to stop at any time.\n");

var steps = new (string Label, GamepadVibration Vibration)[]
{
    ("Left motor (main, low-frequency)", new GamepadVibration { LeftMotor = 1.0 }),
    ("Right motor (main, high-frequency)", new GamepadVibration { RightMotor = 1.0 }),
    ("Left trigger (impulse)", new GamepadVibration { LeftTrigger = 1.0 }),
    ("Right trigger (impulse)", new GamepadVibration { RightTrigger = 1.0 }),
};

while (true)
{
    foreach (var (label, vibration) in steps)
    {
        Console.WriteLine($"-> {label}");
        gamepad.Vibration = vibration;
        Thread.Sleep(2000);

        gamepad.Vibration = new GamepadVibration();
        Thread.Sleep(1000);
    }

    Console.WriteLine("\nFull cycle done. Repeating - Ctrl+C to stop.\n");
}
