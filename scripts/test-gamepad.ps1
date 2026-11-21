param(
    [ValidateRange(1, 600)]
    [int] $Seconds = 120
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$reader = Get-Content -LiteralPath (Join-Path $root 'src\Gamepad.cs') -Raw
$diagnostic = @'

public static class GamepadDiagnostic
{
    public static void Run(int seconds)
    {
        var connected = new bool[Gamepad.SlotCount];
        var previous = new GamepadButtons[Gamepad.SlotCount];
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("Listening for XInput button presses for {0} seconds.", seconds);
        while (elapsed.Elapsed.TotalSeconds < seconds)
        {
            for (int slot = 0; slot < Gamepad.SlotCount; slot++)
            {
                GamepadButtons current;
                bool found = Gamepad.TryRead(slot, out current);
                if (found != connected[slot])
                {
                    Report(slot, found ? "connected" : "disconnected", current);
                    connected[slot] = found;
                    previous[slot] = current;
                    continue;
                }
                if (!found || current == previous[slot])
                    continue;

                var pressed = current & ~previous[slot];
                var released = previous[slot] & ~current;
                if (pressed != GamepadButtons.None)
                    Report(slot, "pressed", pressed);
                if (released != GamepadButtons.None)
                    Report(slot, "released", released);
                previous[slot] = current;
            }
            System.Threading.Thread.Sleep(10);
        }
        Console.WriteLine("Gamepad capture finished.");
    }

    private static void Report(int slot, string action, GamepadButtons buttons)
    {
        Console.WriteLine("{0:HH:mm:ss.fff}  Pad {1}  {2,-12} {3}", DateTime.Now, slot + 1, action, buttons);
    }
}
'@

Add-Type -TypeDefinition ($reader + $diagnostic)
[GamepadDiagnostic]::Run($Seconds)
