using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Client;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEKEYBOARD = 1;
    private const ushort RI_KEY_BREAK = 0x0001;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    private readonly ObservableCollection<string> _events = new();
    private HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();
        EventsList.ItemsSource = _events;

        SourceInitialized += (_, __) => InitRawInput();
        Closed += (_, __) => _source?.RemoveHook(WndProc);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _events.Clear();
        LastEventText.Text = "—";
    }

    private void InitRawInput()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);

        RAWINPUTDEVICE[] rid =
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            var error = Marshal.GetLastWin32Error();
            StatusText.Text = $"Статус: ошибка RegisterRawInputDevices (0x{error:X})";
            StatusText.Foreground = Brushes.DarkRed;
            MessageBox.Show(this,
                $"RegisterRawInputDevices завершилась с ошибкой: 0x{error:X}",
                "Raw Input",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        StatusText.Text = "Статус: Raw Input активен (INPUTSINK)";
        StatusText.Foreground = Brushes.DarkGreen;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            ReadRawInput(lParam);
            handled = false;
        }

        return IntPtr.Zero;
    }

    private void ReadRawInput(IntPtr lParam)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize) == 0 && size == 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (raw.header.dwType != RIM_TYPEKEYBOARD)
            {
                return;
            }

            var k = raw.keyboard;
            bool isUp = (k.Flags & RI_KEY_BREAK) != 0;
            int vkey = k.VKey;
            Key key = KeyInterop.KeyFromVirtualKey(vkey);
            string device = raw.header.hDevice == IntPtr.Zero
                ? "N/A"
                : $"0x{raw.header.hDevice.ToInt64():X}";
            string message = $"{DateTime.Now:HH:mm:ss.fff} {device} VK={vkey} ({key}) {(isUp ? "UP" : "DOWN")}";

            LastEventText.Text = message;
            AppendEvent(message);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void AppendEvent(string message)
    {
        _events.Add(message);
        EventsList.ScrollIntoView(message);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
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
}
