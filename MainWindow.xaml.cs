using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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

    private const ushort DefaultDesiredPid = 0;
    private const ushort DefaultNpc = 0;

    private readonly ObservableCollection<string> _events = new();
    private readonly DispatcherTimer _sendTimer;
    private readonly Dictionary<int, int> _keyBitByVKey = new()
    {
        { 0x57, 0 }, // W
        { 0x41, 1 }, // A
        { 0x53, 2 }, // S
        { 0x44, 3 }, // D
        { 0x10, 4 }, // SHIFT
        { 0xA0, 4 }, // LSHIFT
        { 0xA1, 4 }, // RSHIFT
        { 0x11, 5 }, // CTRL
        { 0xA2, 5 }, // LCTRL
        { 0xA3, 5 }, // RCTRL
        { 0x12, 6 }, // ALT
        { 0xA4, 6 }, // LALT
        { 0xA5, 6 }, // RALT
        { 0x20, 7 }, // SPACE
        { 0x51, 8 }, // Q
        { 0x45, 9 }, // E
        { 0x52, 10 }, // R
        { 0x46, 11 }, // F
        { 0x31, 12 }, // 1
        { 0x32, 13 }, // 2
        { 0x33, 14 }, // 3
    };

    private HwndSource? _source;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _netCts;
    private bool _handshakeDone;
    private bool _sending;
    private ushort _assignedPid;
    private uint _seq;
    private ulong _keysMask;

    public MainWindow()
    {
        InitializeComponent();
        EventsList.ItemsSource = _events;

        _sendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _sendTimer.Tick += SendTimer_Tick;

        SourceInitialized += (_, __) => InitRawInput();
        Closed += (_, __) =>
        {
            _source?.RemoveHook(WndProc);
            DisconnectInternal("disconnected");
        };
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _events.Clear();
        LastEventText.Text = "-";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tcp != null)
        {
            return;
        }

        string host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Host is required.", "Client", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show(this, "Invalid port number.", "Client", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConnectButton.IsEnabled = false;
        NetStatusText.Text = "connecting...";

        try
        {
            _netCts = new CancellationTokenSource();
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(host, port);
            _tcp.NoDelay = true;
            _stream = _tcp.GetStream();

            await SendHelloAsync(_netCts.Token);
            var packet = await ReadPacketAsync(_stream, _netCts.Token);
            if (packet.Type != PacketType.Welcome)
            {
                throw new InvalidOperationException($"Unexpected packet: {packet.Type}");
            }

            if (!TryParseWelcome(packet.Payload, out ushort ver, out ushort pid, out bool ok, out string msg))
            {
                throw new InvalidOperationException("Invalid Welcome payload.");
            }

            if (!ok)
            {
                throw new InvalidOperationException(msg.Length == 0 ? "Handshake failed." : msg);
            }

            _assignedPid = pid;
            _handshakeDone = true;
            PidText.Text = pid.ToString();
            NetStatusText.Text = $"connected (v{ver})";
            DisconnectButton.IsEnabled = true;

            _sendTimer.Start();
            await SendStateAsync(_netCts.Token);
        }
        catch (Exception ex)
        {
            DisconnectInternal($"connect failed: {ex.Message}");
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectInternal("disconnected");
    }

    private async void SendTimer_Tick(object? sender, EventArgs e)
    {
        if (_stream == null || _netCts == null || !_handshakeDone || _sending)
        {
            return;
        }

        _sending = true;
        try
        {
            await SendStateAsync(_netCts.Token);
        }
        catch (Exception ex)
        {
            DisconnectInternal($"send failed: {ex.Message}");
        }
        finally
        {
            _sending = false;
        }
    }

    private void DisconnectInternal(string status)
    {
        _sendTimer.Stop();
        _handshakeDone = false;
        _assignedPid = 0;
        _seq = 0;

        _netCts?.Cancel();
        _netCts?.Dispose();
        _netCts = null;

        _stream?.Close();
        _tcp?.Close();
        _stream = null;
        _tcp = null;

        NetStatusText.Text = status;
        PidText.Text = "-";
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
    }

    private async Task SendHelloAsync(CancellationToken ct)
    {
        string name = NameBox.Text.Trim();
        byte flags = 0;
        byte[] namePacked = Array.Empty<byte>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            flags |= 1;
            namePacked = Proto.PackString(name);
        }

        byte[] payload = new byte[2 + 2 + 1 + namePacked.Length];
        int i = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i, 2), Proto.Version);
        i += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i, 2), DefaultDesiredPid);
        i += 2;
        payload[i++] = flags;
        if (namePacked.Length > 0)
        {
            Buffer.BlockCopy(namePacked, 0, payload, i, namePacked.Length);
        }

        if (_stream == null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        await Proto.SendPacketAsync(_stream, PacketType.Hello, payload, ct);
    }

    private async Task SendStateAsync(CancellationToken ct)
    {
        if (_stream == null || !_handshakeDone)
        {
            return;
        }

        ulong keysMask = _keysMask;
        uint seq = ++_seq;

        byte[] payload = new byte[2 + 2 + 4 + 8];
        int i = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i, 2), _assignedPid);
        i += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i, 2), DefaultNpc);
        i += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i, 4), seq);
        i += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(i, 8), keysMask);

        await Proto.SendPacketAsync(_stream, PacketType.State, payload, ct);
    }

    private static async Task<(PacketType Type, byte[] Payload)> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = new byte[2];
        await Proto.ReadExactAsync(stream, lenBuf, 0, 2, ct);
        ushort bodyLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
        if (bodyLen < 1)
        {
            throw new InvalidOperationException("Invalid packet length.");
        }

        byte[] body = new byte[bodyLen];
        await Proto.ReadExactAsync(stream, body, 0, bodyLen, ct);

        var type = (PacketType)body[0];
        int payloadLen = bodyLen - 1;
        byte[] payload = Array.Empty<byte>();
        if (payloadLen > 0)
        {
            payload = new byte[payloadLen];
            Buffer.BlockCopy(body, 1, payload, 0, payloadLen);
        }

        return (type, payload);
    }

    private static bool TryParseWelcome(byte[] payload, out ushort ver, out ushort pid, out bool ok, out string msg)
    {
        ver = 0;
        pid = 0;
        ok = false;
        msg = "";

        if (payload.Length < 2 + 2 + 1)
        {
            return false;
        }

        int idx = 0;
        ver = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(idx, 2));
        idx += 2;
        pid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(idx, 2));
        idx += 2;
        ok = payload[idx++] != 0;

        if (idx < payload.Length)
        {
            try
            {
                msg = Proto.UnpackString(payload, ref idx);
            }
            catch (Exception)
            {
                msg = "";
            }
        }

        return true;
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
            StatusText.Text = $"Status: RegisterRawInputDevices failed (0x{error:X})";
            StatusText.Foreground = Brushes.DarkRed;
            MessageBox.Show(this,
                $"RegisterRawInputDevices failed: 0x{error:X}",
                "Raw Input",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        StatusText.Text = "Status: Raw Input active (INPUTSINK)";
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

            UpdateKeyMask(vkey, isUp);

            LastEventText.Text = message;
            AppendEvent(message);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void UpdateKeyMask(int vkey, bool isUp)
    {
        if (!_keyBitByVKey.TryGetValue(vkey, out int bit))
        {
            return;
        }

        ulong mask = 1UL << bit;
        if (isUp)
        {
            _keysMask &= ~mask;
        }
        else
        {
            _keysMask |= mask;
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
