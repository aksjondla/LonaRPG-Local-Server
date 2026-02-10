using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Navigation;

namespace Host;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<PlayerRow> _players = new();
    private readonly Dictionary<ushort, PlayerRow> _rowsByPid = new();
    private readonly Dictionary<ushort, bool> _forceSyncPressedByPid = new();
    private readonly Dictionary<ushort, DateTime> _lastSaveRequestUtcByPid = new();
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _cts;
    private TcpHostServer? _server;
    private RubyBridgePipe? _rubyBridge;
    private Task? _rubyLoopTask;
    private CamRelayPipe? _camRelay;
    private SaveRelayPipe? _saveRelay;
    private Task? _camTxLoopTask;

    private static readonly TimeSpan RubyInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RubyStaleTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CamInterval = TimeSpan.FromMilliseconds(16);

    private const int ForceSyncBit = 63;
    private const string CamPipeName = "LCOCamHost";
    private const string SavePipeName = "LCOSaveHost";

    public MainWindow()
    {
        InitializeComponent();
        PlayersGrid.ItemsSource = _players;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, __) =>
        {
            RefreshPlayers();
            UpdatePipeStatus();
        };
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_server != null)
        {
            return;
        }

        if (!IPAddress.TryParse(BindIpBox.Text.Trim(), out var bindIp))
        {
            MessageBox.Show(this, "Invalid bind IP address.", "Host", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show(this, "Invalid port number.", "Host", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string pipeName = PipeNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            MessageBox.Show(this, "Invalid pipe name.", "Host", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _server = new TcpHostServer(bindIp, port);
        _server.Start();

        _rubyBridge = new RubyBridgePipe(pipeName);
        _camRelay = new CamRelayPipe(CamPipeName);
        _saveRelay = new SaveRelayPipe(SavePipeName, _server);

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => _server.AcceptLoopAsync(_cts.Token));
        _rubyLoopTask = Task.Run(() => RubyLoopAsync(_cts.Token));
        _ = _camRelay.RunAsync(_cts.Token);
        _ = _saveRelay.RunAsync(_cts.Token);
        _camTxLoopTask = Task.Run(() => CamTxLoopAsync(_cts.Token));

        StatusText.Text = $"Status: running on {bindIp}:{port} -> Pipe \\\\.\\pipe\\{pipeName} (cam=\\\\.\\pipe\\{CamPipeName}, save=\\\\.\\pipe\\{SavePipeName})";
        PipeStatusText.Text = "Pipe: waiting for client...";
        CamPipeStatusText.Text = "CamPipe: waiting for client...";
        SavePipeStatusText.Text = "SavePipe: waiting for client...";
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _timer.Start();

        DebugConsole.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Host started on {bindIp}:{port}, pipe=\\\\.\\pipe\\{pipeName}");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopServer();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _players.Clear();
        _rowsByPid.Clear();
        _forceSyncPressedByPid.Clear();
        _lastSaveRequestUtcByPid.Clear();
    }

    private void DisablePlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_server == null)
        {
            return;
        }

        if (sender is Button btn && btn.DataContext is PlayerRow row)
        {
            try
            {
                _server.DisconnectPlayer(row.Pid, removePlayer: true);
            }
            catch
            {
            }
        }
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

    protected override void OnClosed(EventArgs e)
    {
        StopServer();
        base.OnClosed(e);
    }

    private void StopServer()
    {
        _timer.Stop();
        _cts?.Cancel();
        _cts = null;

        _server?.Stop();
        _server = null;

        _rubyBridge?.Dispose();
        _rubyBridge = null;
        _rubyLoopTask = null;

        _camRelay?.Dispose();
        _camRelay = null;
        _saveRelay?.Dispose();
        _saveRelay = null;
        _camTxLoopTask = null;

        StatusText.Text = "Status: stopped";
        PipeStatusText.Text = "Pipe: n/a";
        CamPipeStatusText.Text = "CamPipe: n/a";
        SavePipeStatusText.Text = "SavePipe: n/a";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;

        DebugConsole.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Host stopped");
    }

    private void RefreshPlayers()
    {
        if (_server == null)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        string saveRxAgo = FormatAgo(nowUtc, _saveRelay?.GetLastRxUtc());

        var snapshot = _server.SnapshotPlayers();
        var seen = new HashSet<ushort>();

        foreach (var st in snapshot)
        {
            seen.Add(st.Pid);
            if (!_rowsByPid.TryGetValue(st.Pid, out var row))
            {
                row = new PlayerRow(st.Pid);
                _rowsByPid[st.Pid] = row;
                _players.Add(row);
            }

            bool pressed = (st.KeysMask & (1UL << ForceSyncBit)) != 0;
            bool prevPressed = _forceSyncPressedByPid.TryGetValue(st.Pid, out var prev) && prev;
            if (pressed && !prevPressed)
            {
                _lastSaveRequestUtcByPid[st.Pid] = nowUtc;
            }
            _forceSyncPressedByPid[st.Pid] = pressed;

            DateTime? lastReqUtc = _lastSaveRequestUtcByPid.TryGetValue(st.Pid, out var last) ? last : null;
            string saveReqAgo = FormatAgo(nowUtc, lastReqUtc);

            row.UpdateFrom(st, saveReqAgo, saveRxAgo);
        }

        for (int i = _players.Count - 1; i >= 0; i--)
        {
            var row = _players[i];
            if (!seen.Contains(row.Pid))
            {
                _players.RemoveAt(i);
                _rowsByPid.Remove(row.Pid);
                _forceSyncPressedByPid.Remove(row.Pid);
                _lastSaveRequestUtcByPid.Remove(row.Pid);
            }
        }
    }

    private static string FormatAgo(DateTime nowUtc, DateTime? utc)
    {
        if (utc == null)
        {
            return "--:--";
        }

        TimeSpan span = nowUtc - utc.Value;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        int totalSeconds = (int)span.TotalSeconds;
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return $"{minutes:00}:{seconds:00}";
    }

    private void SendRubySnapshot()
    {
        var server = _server;
        var bridge = _rubyBridge;
        if (server == null || bridge == null)
        {
            return;
        }

        try
        {
            var snapshot = server.SnapshotPlayers();
            var sw = Stopwatch.StartNew();
            bridge.SendSnapshot(snapshot, RubyStaleTimeout);
            sw.Stop();
            if (DebugConsole.IsOpen && sw.ElapsedMilliseconds >= 5)
            {
                DebugConsole.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] SendSnapshot took {sw.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            if (DebugConsole.IsOpen)
            {
                DebugConsole.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] SendSnapshot error: {ex.Message}");
            }
        }
    }

    private async Task RubyLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            SendRubySnapshot();
            sw.Stop();

            var delay = RubyInterval - sw.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void UpdatePipeStatus()
    {
        var bridge = _rubyBridge;
        if (bridge == null)
        {
            return;
        }

        var stats = bridge.GetStats();
        string conn = stats.Connected ? "connected" : "waiting";
        PipeStatusText.Text = $"Pipe: {conn}, sent={stats.Frames}, lastBytes={stats.LastBytes}, dropped={stats.Dropped}, timeouts={stats.Timeouts}";

        var cam = _camRelay;
        if (cam != null)
        {
            var cs = cam.GetStats();
            string cconn = cs.Connected ? "connected" : "waiting";
            CamPipeStatusText.Text = $"CamPipe: {cconn}, rx={cs.Frames}, lastBytes={cs.LastBytes}";
        }

        var save = _saveRelay;
        if (save != null)
        {
            var ss = save.GetStats();
            string sconn = ss.Connected ? "connected" : "waiting";
            SavePipeStatusText.Text = $"SavePipe: {sconn}, rxChunks={ss.Chunks}, lastBytes={ss.LastBytes}";
        }

        if (DebugConsole.IsOpen)
        {
            DebugConsole.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Pipe {conn}, sent={stats.Frames}, lastBytes={stats.LastBytes}, dropped={stats.Dropped}, timeouts={stats.Timeouts}");
        }
    }

    private async Task CamTxLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = _server;
            var relay = _camRelay;
            if (server != null && relay != null && relay.TryConsumeLatest(out var frame) && frame != null)
            {
                try
                {
                    await server.BroadcastCamFrameAsync(frame, ct);
                }
                catch
                {
                }
            }

            try
            {
                await Task.Delay(CamInterval, ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private void ConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        DebugConsole.Open();
        DebugConsole.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Console attached");
    }

    private sealed class PlayerRow : INotifyPropertyChanged
    {
        private string _name = "";
        private ushort _npc;
        private uint _seq;
        private string _keysMask = "0x0000000000000000";
        private string _lastSeen = "";
        private string _saveReqAgo = "--:--";
        private string _saveRxAgo = "--:--";

        public event PropertyChangedEventHandler? PropertyChanged;

        public PlayerRow(ushort pid)
        {
            Pid = pid;
        }

        public ushort Pid { get; }

        public string Name
        {
            get => _name;
            private set => SetField(ref _name, value, nameof(Name));
        }

        public ushort Npc
        {
            get => _npc;
            private set => SetField(ref _npc, value, nameof(Npc));
        }

        public uint Seq
        {
            get => _seq;
            private set => SetField(ref _seq, value, nameof(Seq));
        }

        public string KeysMask
        {
            get => _keysMask;
            private set => SetField(ref _keysMask, value, nameof(KeysMask));
        }

        public string LastSeen
        {
            get => _lastSeen;
            private set => SetField(ref _lastSeen, value, nameof(LastSeen));
        }

        public string SaveReqAgo
        {
            get => _saveReqAgo;
            private set => SetField(ref _saveReqAgo, value, nameof(SaveReqAgo));
        }

        public string SaveRxAgo
        {
            get => _saveRxAgo;
            private set => SetField(ref _saveRxAgo, value, nameof(SaveRxAgo));
        }

        public void UpdateFrom(PlayerState st, string saveReqAgo, string saveRxAgo)
        {
            Name = st.Name ?? "";
            Npc = st.Npc;
            Seq = st.Seq;
            KeysMask = $"0x{st.KeysMask:X16}";
            LastSeen = st.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            SaveReqAgo = saveReqAgo;
            SaveRxAgo = saveRxAgo;
        }

        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
