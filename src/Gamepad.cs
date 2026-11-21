using System;
using System.Runtime.InteropServices;

[Flags]
internal enum GamepadButtons : uint
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftStick = 0x0040,
    RightStick = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    LeftTrigger = 0x10000,
    RightTrigger = 0x20000
}

internal static class Gamepad
{
    public const int SlotCount = 4;
    private const byte TriggerThreshold = 30;

    public static bool TryRead(int slot, out GamepadButtons buttons)
    {
        XInputState state;
        buttons = GamepadButtons.None;
        if (XInputGetState((uint)slot, out state) != 0)
            return false;

        buttons = (GamepadButtons)(state.Buttons & 0xF3FF);
        if (state.LeftTrigger >= TriggerThreshold)
            buttons |= GamepadButtons.LeftTrigger;
        if (state.RightTrigger >= TriggerThreshold)
            buttons |= GamepadButtons.RightTrigger;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}
