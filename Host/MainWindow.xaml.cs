using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

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

    public MainWindow()
    {
        InitializeComponent();
        PlayersGrid.ItemsSource = _players;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, __) => RefreshPlayers();
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

        _server = new TcpHostServer(bindIp, port);
        _server.Start();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => _server.AcceptLoopAsync(_cts.Token));

        StatusText.Text = $"Status: running on {bindIp}:{port}";
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _timer.Start();
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

        StatusText.Text = "Status: stopped";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
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
