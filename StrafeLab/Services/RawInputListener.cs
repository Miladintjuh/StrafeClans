using System.Runtime.InteropServices;
using System.Windows.Interop;
using StrafeLab.Models;

namespace StrafeLab.Services;

public sealed class RawInputListener : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int RID_INPUT = 0x10000003;
    private const int RIM_TYPEMOUSE = 0;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int RIDEV_INPUTSINK = 0x00000100;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_A = 0x41;
    private const int VK_D = 0x44;

    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;

    private readonly HighResolutionClock _clock;
    private readonly HashSet<int> _keyDownState = new();
    private HwndSource? _source;

    public event Action<InputEventRecord>? InputEdge;
    public bool IsRegistered { get; private set; }

    public RawInputListener(HighResolutionClock clock)
    {
        _clock = clock;
    }

    public void Attach(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Could not attach to WPF window handle.");
        _source.AddHook(WndProc);
        Register(hwnd);
    }

    private void Register(IntPtr hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        IsRegistered = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            ProcessRawInput(lParam);
        }
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        _ = GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize);
            if (read != size) return;

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            double t = _clock.NowMs();

            if (raw.header.dwType == RIM_TYPEKEYBOARD)
            {
                ProcessKeyboard(raw.data.keyboard, t);
            }
            else if (raw.header.dwType == RIM_TYPEMOUSE)
            {
                ProcessMouse(raw.data.mouse, t);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessKeyboard(RAWKEYBOARD keyboard, double t)
    {
        int vKey = keyboard.VKey;
        if (vKey != VK_A && vKey != VK_D) return;

        bool isDown = keyboard.Message is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = keyboard.Message is WM_KEYUP or WM_SYSKEYUP;
        if (!isDown && !isUp) return;

        string code = vKey == VK_A ? "A" : "D";

        if (isDown)
        {
            if (!_keyDownState.Add(vKey)) return;
            Raise(code, InputKind.KeyDown, t);
        }
        else
        {
            if (!_keyDownState.Remove(vKey)) return;
            Raise(code, InputKind.KeyUp, t);
        }
    }

    private void ProcessMouse(RAWMOUSE mouse, double t)
    {
        // For normal gaming mice this is relative movement in raw counts. Absolute-mode
        // devices are uncommon for FPS practice, but the deltas are still preserved as-is.
        bool hasMovement = mouse.lLastX != 0 || mouse.lLastY != 0;
        if (hasMovement)
        {
            string code = (mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0 ? "MOVE_ABS" : "MOVE";
            Raise(code, InputKind.MouseMove, t, mouse.lLastX, mouse.lLastY);
        }

        ushort flags = mouse.usButtonFlags;
        if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) Raise("M1", InputKind.MouseDown, t);
        if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) Raise("M1", InputKind.MouseUp, t);
        if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) Raise("M2", InputKind.MouseDown, t);
        if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) Raise("M2", InputKind.MouseUp, t);
    }

    private void Raise(string code, InputKind kind, double t, int dx = 0, int dy = 0)
    {
        InputEdge?.Invoke(new InputEventRecord
        {
            WallTime = DateTimeOffset.Now,
            SessionTimeMs = t,
            Code = code,
            Kind = kind,
            DeltaX = dx,
            DeltaY = dy
        });
    }

    public void Dispose()
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    [DllImport("User32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public int dwType;
        public int dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public uint ulButtons;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUTUNION
    {
        [FieldOffset(0)] public RAWMOUSE mouse;
        [FieldOffset(0)] public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWINPUTUNION data;
    }
}
