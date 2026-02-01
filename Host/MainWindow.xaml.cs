using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _cts;
    private TcpHostServer? _server;
    private RubyBridgePipe? _rubyBridge;
    private Task? _rubyLoopTask;

    private static readonly TimeSpan RubyInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RubyStaleTimeout = TimeSpan.FromMilliseconds(500);

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

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => _server.AcceptLoopAsync(_cts.Token));
        _rubyLoopTask = Task.Run(() => RubyLoopAsync(_cts.Token));

        StatusText.Text = $"Status: running on {bindIp}:{port} -> Pipe \\\\.\\pipe\\{pipeName}";
        PipeStatusText.Text = "Pipe: waiting for client...";
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

        StatusText.Text = "Status: stopped";
        PipeStatusText.Text = "Pipe: n/a";
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

            row.UpdateFrom(st);
        }

        for (int i = _players.Count - 1; i >= 0; i--)
        {
            var row = _players[i];
            if (!seen.Contains(row.Pid))
            {
                _players.RemoveAt(i);
                _rowsByPid.Remove(row.Pid);
            }
        }
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

        if (DebugConsole.IsOpen)
        {
            DebugConsole.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Pipe {conn}, sent={stats.Frames}, lastBytes={stats.LastBytes}, dropped={stats.Dropped}, timeouts={stats.Timeouts}");
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

        public void UpdateFrom(PlayerState st)
        {
            Name = st.Name ?? "";
            Npc = st.Npc;
            Seq = st.Seq;
            KeysMask = $"0x{st.KeysMask:X16}";
            LastSeen = st.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
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
