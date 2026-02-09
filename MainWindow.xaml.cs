using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Navigation;
using Microsoft.Win32;

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

    private readonly ObservableCollection<string> _events = new();
    private readonly ObservableCollection<KeyBindingRow> _bindings = new();
    private readonly DispatcherTimer _sendTimer;
    private readonly Dictionary<int, int> _keyBitByVKey = new();

    private KeyBindingRow? _pendingBind;

    private HwndSource? _source;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _netCts;
    private bool _handshakeDone;
    private bool _sending;
    private ushort _assignedPid;
    private uint _seq;
    private ulong _keysMask;

    private Task? _recvTask;
    private Task? _camPipeTxTask;

    private volatile string _gameRoot = "";

    private const string ViewerCamPipeName = "LCOCam";
    private NamedPipeClientStream? _camPipe;
    private byte[]? _pendingCamFrame;
    private readonly byte[] _camLenBuf = new byte[4];

    private uint _camAsmId;
    private uint _camAsmTotalLen;
    private int _camAsmReceived;
    private byte[]? _camAsmBuf;

    private uint _saveAsmSeq;
    private uint _saveAsmTotalLen;
    private uint _saveAsmMaxWritten;
    private FileStream? _saveAsmStream;
    private string? _saveAsmTmpPath;
    private string? _saveAsmFinalPath;

    private sealed class KeyBindingRow : INotifyPropertyChanged
    {
        private int _vkey;
        private string _keyName;

        public KeyBindingRow(string action, int bit, int vkey)
        {
            Action = action;
            Bit = bit;
            _keyName = "";
            SetVKey(vkey);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Action { get; }
        public int Bit { get; }

        public int VKey => _vkey;

        public string KeyName
        {
            get => _keyName;
            private set
            {
                if (_keyName == value)
                {
                    return;
                }
                _keyName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyName)));
            }
        }

        public void SetVKey(int vkey)
        {
            _vkey = vkey;
            KeyName = FormatVKey(vkey);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VKey)));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        EventsList.ItemsSource = _events;
        BindingsGrid.ItemsSource = _bindings;
        InitKeyBindings();

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

    private void OnTelegramNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
        }
        e.Handled = true;
    }

    private void OnTelegramClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://t.me/CooperativeMode/1") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is KeyBindingRow row)
        {
            _pendingBind = row;
            BindStatusText.Text = $"Нажмите клавишу для: {row.Action}";
        }
    }

    private void GameRootBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _gameRoot = GameRootBox.Text.Trim();
    }

    private void BrowseGameButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select LonaRPG game folder (pick Game.ini)",
                Filter = "Game.ini|Game.ini|Executable (*.exe)|*.exe|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            bool? ok = dlg.ShowDialog(this);
            if (ok == true)
            {
                string? dir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    GameRootBox.Text = dir;
                    _gameRoot = dir.Trim();
                }
            }
        }
        catch
        {
        }
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
            _recvTask = Task.Run(() => ReceiveLoopAsync(_netCts.Token));
            _camPipeTxTask = Task.Run(() => CamPipeTxLoopAsync(_netCts.Token));
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

        _recvTask = null;
        _camPipeTxTask = null;
        Interlocked.Exchange(ref _pendingCamFrame, null);

        _camAsmId = 0;
        _camAsmTotalLen = 0;
        _camAsmReceived = 0;
        _camAsmBuf = null;

        try
        {
            _camPipe?.Dispose();
        }
        catch
        {
        }
        _camPipe = null;

        try
        {
            _saveAsmStream?.Dispose();
        }
        catch
        {
        }
        _saveAsmStream = null;
        _saveAsmSeq = 0;
        _saveAsmTotalLen = 0;
        _saveAsmMaxWritten = 0;
        _saveAsmTmpPath = null;
        _saveAsmFinalPath = null;

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
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i, 2), GetNpcIdFromUi());
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

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null)
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await ReadPacketAsync(stream, ct);
                switch (packet.Type)
                {
                    case PacketType.CamChunk:
                        HandleCamChunk(packet.Payload);
                        break;

                    case PacketType.SaveChunk:
                        await HandleSaveChunkAsync(packet.Payload, ct);
                        break;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_tcp != null)
                {
                    DisconnectInternal($"recv failed: {ex.Message}");
                }
            });
        }
    }

    private void HandleCamChunk(byte[] payload)
    {
        if (payload.Length < 12)
        {
            return;
        }

        uint camId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
        uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
        int chunkLen = payload.Length - 12;

        if (totalLen == 0 || totalLen > 4_000_000)
        {
            return;
        }

        if (offset > totalLen || offset + (uint)chunkLen > totalLen)
        {
            return;
        }

        if (_camAsmBuf == null || camId != _camAsmId || totalLen != _camAsmTotalLen)
        {
            if (offset != 0)
            {
                return;
            }

            _camAsmId = camId;
            _camAsmTotalLen = totalLen;
            _camAsmReceived = 0;
            _camAsmBuf = new byte[(int)totalLen];
        }

        if (_camAsmBuf == null)
        {
            return;
        }

        if (offset != (uint)_camAsmReceived)
        {
            // Protocol is ordered; if we see an unexpected offset, drop the partial frame.
            _camAsmBuf = null;
            _camAsmReceived = 0;
            _camAsmTotalLen = 0;
            return;
        }

        Buffer.BlockCopy(payload, 12, _camAsmBuf, (int)offset, chunkLen);
        _camAsmReceived += chunkLen;

        if ((uint)_camAsmReceived >= _camAsmTotalLen)
        {
            var frame = _camAsmBuf;
            _camAsmBuf = null;
            _camAsmReceived = 0;
            _camAsmTotalLen = 0;
            Interlocked.Exchange(ref _pendingCamFrame, frame);
        }
    }

    private async Task CamPipeTxLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = Interlocked.Exchange(ref _pendingCamFrame, null);
            if (frame != null)
            {
                await SendCamFrameToViewerAsync(frame, ct);
                continue;
            }

            try
            {
                await Task.Delay(8, ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task SendCamFrameToViewerAsync(byte[] frame, CancellationToken ct)
    {
        if (frame.Length == 0)
        {
            return;
        }

        try
        {
            if (_camPipe == null || !_camPipe.IsConnected)
            {
                try
                {
                    _camPipe?.Dispose();
                }
                catch
                {
                }

                var pipe = new NamedPipeClientStream(".", ViewerCamPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                using var tmo = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tmo.CancelAfter(150);
                await pipe.ConnectAsync(tmo.Token);
                _camPipe = pipe;
            }

            var p = _camPipe;
            if (p == null || !p.IsConnected)
            {
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(_camLenBuf, (uint)frame.Length);
            await p.WriteAsync(_camLenBuf, ct);
            await p.WriteAsync(frame, ct);
            await p.FlushAsync(ct);
        }
        catch
        {
            try
            {
                _camPipe?.Dispose();
            }
            catch
            {
            }
            _camPipe = null;
        }
    }

    private async Task HandleSaveChunkAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length < 12)
        {
            return;
        }

        uint saveSeq = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
        uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
        int chunkLen = payload.Length - 12;

        if (totalLen == 0 || totalLen > 64_000_000)
        {
            return;
        }

        if (offset > totalLen)
        {
            return;
        }

        if (offset + (uint)chunkLen > totalLen)
        {
            chunkLen = (int)(totalLen - offset);
        }

        string gameRoot = _gameRoot;
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return;
        }

        string userDataDir = Path.Combine(gameRoot, "UserData");
        string finalPath = Path.Combine(userDataDir, "SavQuick.rvdata2");
        string tmpPath = finalPath + ".tmp";

        // New save transfer begins only at offset=0.
        if (_saveAsmStream == null || saveSeq != _saveAsmSeq || totalLen != _saveAsmTotalLen)
        {
            if (offset != 0)
            {
                return;
            }

            try
            {
                _saveAsmStream?.Dispose();
            }
            catch
            {
            }

            Directory.CreateDirectory(userDataDir);
            _saveAsmSeq = saveSeq;
            _saveAsmTotalLen = totalLen;
            _saveAsmMaxWritten = 0;
            _saveAsmTmpPath = tmpPath;
            _saveAsmFinalPath = finalPath;
            _saveAsmStream = new FileStream(
                tmpPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
            _saveAsmStream.SetLength(totalLen);
        }

        if (_saveAsmStream == null)
        {
            return;
        }

        if (chunkLen > 0)
        {
            _saveAsmStream.Position = offset;
            await _saveAsmStream.WriteAsync(payload.AsMemory(12, chunkLen), ct);
            uint end = offset + (uint)chunkLen;
            if (end > _saveAsmMaxWritten)
            {
                _saveAsmMaxWritten = end;
            }
        }

        if (_saveAsmMaxWritten >= _saveAsmTotalLen)
        {
            try
            {
                await _saveAsmStream.FlushAsync(ct);
            }
            catch
            {
            }

            try
            {
                _saveAsmStream.Dispose();
            }
            catch
            {
            }
            _saveAsmStream = null;

            if (!string.IsNullOrWhiteSpace(_saveAsmTmpPath) && !string.IsNullOrWhiteSpace(_saveAsmFinalPath))
            {
                try
                {
                    File.Move(_saveAsmTmpPath!, _saveAsmFinalPath!, overwrite: true);

                    // Marker file used by the viewer mod to know the quicksave is fully received.
                    try
                    {
                        string markerPath = Path.Combine(userDataDir, "SavQuick.seq");
                        string markerTmp = markerPath + ".tmp";
                        File.WriteAllText(markerTmp, saveSeq.ToString());
                        File.Move(markerTmp, markerPath, overwrite: true);
                    }
                    catch
                    {
                    }
                }
                catch
                {
                    // ignore; viewer will keep trying next save_seq
                }
            }

            _saveAsmTmpPath = null;
            _saveAsmFinalPath = null;
        }
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
            int vkey = NormalizeVKey(k.VKey);
            Key key = KeyInterop.KeyFromVirtualKey(vkey);
            string device = raw.header.hDevice == IntPtr.Zero
                ? "N/A"
                : $"0x{raw.header.hDevice.ToInt64():X}";
            string message = $"{DateTime.Now:HH:mm:ss.fff} {device} VK={vkey} ({key}) {(isUp ? "UP" : "DOWN")}";

            if (_pendingBind != null && !isUp)
            {
                ApplyBinding(_pendingBind, vkey);
                BindStatusText.Text = $"Назначено: {_pendingBind.Action} = {FormatVKey(vkey)}";
                _pendingBind = null;
                return;
            }

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
        ScrollEventsToEndIfPinned();
    }

    private ushort GetNpcIdFromUi()
    {
        if (NpcBackRadio != null && NpcBackRadio.IsChecked == true)
        {
            return 2;
        }
        if (NpcFrontRadio != null && NpcFrontRadio.IsChecked == true)
        {
            return 1;
        }
        return 0;
    }

    private void ScrollEventsToEndIfPinned()
    {
        if (EventsList.Items.Count == 0)
        {
            return;
        }

        var scrollViewer = FindScrollViewer(EventsList);
        if (scrollViewer == null)
        {
            return;
        }

        if (scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        const double threshold = 16.0;
        if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - threshold)
        {
            scrollViewer.ScrollToEnd();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private void InitKeyBindings()
    {
        _bindings.Clear();
        AddBinding("Up", 0, 0x57); // W
        AddBinding("Left", 1, 0x41); // A
        AddBinding("Down", 2, 0x53); // S
        AddBinding("Right", 3, 0x44); // D
        AddBinding("Dodge", 4, 0x10); // SHIFT
        AddBinding("Ctrl", 5, 0x11); // CTRL
        AddBinding("Alt", 6, 0x12); // ALT
        AddBinding("Space", 7, 0x20); // SPACE
        AddBinding("Skill 1", 8, 0x51); // Q
        AddBinding("Skill 2", 9, 0x45); // E
        AddBinding("Skill 3", 10, 0x52); // R
        AddBinding("Skill 4", 11, 0x46); // F
        AddBinding("Skill 5", 12, 0x31); // 1
        AddBinding("Skill 6", 13, 0x32); // 2
        AddBinding("Skill Grab", 14, 0x33); // 3
        AddBinding("Cycle Companion", 15, 0x34); // 4
        RebuildKeyMap();
    }

    private void AddBinding(string action, int bit, int vkey)
    {
        _bindings.Add(new KeyBindingRow(action, bit, vkey));
    }

    private void ApplyBinding(KeyBindingRow row, int vkey)
    {
        if (vkey <= 0 || vkey == 0xFF)
        {
            return;
        }

        foreach (var other in _bindings)
        {
            if (!ReferenceEquals(other, row) && other.VKey == vkey)
            {
                other.SetVKey(0);
            }
        }

        row.SetVKey(vkey);
        RebuildKeyMap();
    }

    private void RebuildKeyMap()
    {
        _keyBitByVKey.Clear();
        foreach (var row in _bindings)
        {
            if (row.VKey <= 0)
            {
                continue;
            }
            _keyBitByVKey[row.VKey] = row.Bit;
        }
    }

    private static int NormalizeVKey(int vkey)
    {
        return vkey switch
        {
            0xA0 or 0xA1 => 0x10, // LSHIFT/RSHIFT -> SHIFT
            0xA2 or 0xA3 => 0x11, // LCTRL/RCTRL -> CTRL
            0xA4 or 0xA5 => 0x12, // LALT/RALT -> ALT
            _ => vkey
        };
    }

    private static string FormatVKey(int vkey)
    {
        if (vkey <= 0)
        {
            return "—";
        }

        var key = KeyInterop.KeyFromVirtualKey(vkey);
        if (key != Key.None)
        {
            return key.ToString();
        }

        return $"VK_0x{vkey:X2}";
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
