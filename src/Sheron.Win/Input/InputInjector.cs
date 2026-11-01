using System.Diagnostics;
using System.Runtime.InteropServices;
using Sheron.Core.Session;

namespace Sheron.Win.Input;

/// <summary>
/// The remote PC's side of remote control (TRD › Input path): turns the viewer's input messages into Windows input
/// with <c>SendInput</c>, and lets go of every held key and button when asked or when the session ends. Mouse
/// positions are absolute on the main monitor, the one Sheron captures. Thread-safe.
/// </summary>
internal sealed class InputInjector
{
    private const uint InputMouse = 0;            // INPUT_MOUSE
    private const uint InputKeyboard = 1;         // INPUT_KEYBOARD

    // MOUSEINPUT.dwFlags (winuser.h)
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHorizontalWheel = 0x1000;
    private const uint MouseAbsolute = 0x8000;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    // KEYBDINPUT.dwFlags (winuser.h)
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyScanCode = 0x0008;

    private readonly HeldInput _held = new();
    private readonly object _lock = new();
    private long _lastInputAt;

    /// <summary>When the viewer last used the mouse or keyboard (<c>Stopwatch</c> timestamp), or 0: for the window.</summary>
    public long LastInputAt => Interlocked.Read(ref _lastInputAt);

    /// <summary>Inputs Windows refused (SendInput returned fewer than sent), such as into a program run as administrator.</summary>
    public int Blocked { get; private set; }

    /// <summary>Carries out one control message from the viewer; other messages are ignored.</summary>
    public void Apply(ReadOnlySpan<byte> message)
    {
        lock (_lock)
        {
            switch (ControlMessage.TypeOf(message))
            {
                case ControlType.MouseMove when InputMessage.TryReadMove(message, out var at):
                    Send(Mouse(MouseMove | MouseAbsolute, at));
                    break;
                case ControlType.MouseButton when InputMessage.TryReadButton(message, out var button):
                    _held.Track(button);
                    Send(Button(button.Button, button.Down, button.At));
                    break;
                case ControlType.MouseWheel when InputMessage.TryReadWheel(message, out var wheel):
                    var turn = Mouse(MouseMove | MouseAbsolute | (wheel.Horizontal ? MouseHorizontalWheel : MouseWheel), wheel.At);
                    turn.Union.Mouse.MouseData = unchecked((uint)wheel.Delta);
                    Send(turn);
                    break;
                case ControlType.Key when InputMessage.TryReadKey(message, out var key):
                    _held.Track(key);
                    Send(Key(key));
                    break;
                case ControlType.ReleaseAll:
                    ReleaseHeld();
                    break;
                default:
                    return;
            }
            Interlocked.Exchange(ref _lastInputAt, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Lets go of every key and button the viewer holds: when it asks, and when the session ends.</summary>
    public void ReleaseAll()
    {
        lock (_lock) ReleaseHeld();
    }

    private void ReleaseHeld()
    {
        var (keys, buttons) = _held.ReleaseAll();
        foreach (var key in keys) Send(Key(key));
        foreach (var button in buttons)
        {
            var up = Button(button, false, default);
            up.Union.Mouse.Flags &= ~(MouseMove | MouseAbsolute);   // let go where the pointer is now
            Send(up);
        }
    }

    private static Native Mouse(uint flags, AbsolutePoint at) => new()
    {
        Type = InputMouse,
        Union = new NativeUnion { Mouse = new NativeMouse { X = at.X, Y = at.Y, Flags = flags } },
    };

    private static Native Button(MouseButton button, bool down, AbsolutePoint at)
    {
        (uint flags, uint data) = (button, down) switch
        {
            (MouseButton.Left, true) => (MouseLeftDown, 0u),
            (MouseButton.Left, false) => (MouseLeftUp, 0u),
            (MouseButton.Right, true) => (MouseRightDown, 0u),
            (MouseButton.Right, false) => (MouseRightUp, 0u),
            (MouseButton.Middle, true) => (MouseMiddleDown, 0u),
            (MouseButton.Middle, false) => (MouseMiddleUp, 0u),
            (MouseButton.Back, _) => (down ? MouseXDown : MouseXUp, XButton1),
            _ => (down ? MouseXDown : MouseXUp, XButton2),
        };
        // Move there first, in the same input, so the click lands where the viewer clicked.
        var input = Mouse(MouseMove | MouseAbsolute | flags, at);
        input.Union.Mouse.MouseData = data;
        return input;
    }

    private static Native Key(KeyInput key) => new()
    {
        Type = InputKeyboard,
        Union = new NativeUnion
        {
            Keyboard = new NativeKeyboard
            {
                ScanCode = key.ScanCode,
                Flags = KeyScanCode | (key.Extended ? KeyExtended : 0) | (key.Down ? 0 : KeyUp),
            },
        },
    };

    private void Send(Native input)
    {
        var inputs = new[] { input };
        if (SendInput(1, inputs, Marshal.SizeOf<Native>()) != 1) Blocked++;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Native[] inputs, int size);

    /// <summary>INPUT (winuser.h): 40 bytes on 64-bit Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public uint Type;
        public NativeUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct NativeUnion
    {
        [FieldOffset(0)] public NativeMouse Mouse;
        [FieldOffset(0)] public NativeKeyboard Keyboard;
    }

    /// <summary>MOUSEINPUT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMouse
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>KEYBDINPUT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeKeyboard
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
