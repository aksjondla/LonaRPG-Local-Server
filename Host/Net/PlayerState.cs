using System;

namespace Host;

public sealed class PlayerState
{
    public ushort Pid { get; init; }
    public ushort Npc { get; set; }
    public uint Seq { get; set; }
    public ulong KeysMask { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string? Name { get; set; }
}
