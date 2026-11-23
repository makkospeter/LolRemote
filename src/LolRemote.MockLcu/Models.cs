namespace LolRemote.MockLcu;

public record ActionPatch(int? ChampionId, bool? Completed);
public record MySelectionPatch(long? Spell1Id, long? Spell2Id);

public class ReadyCheck
{
    public string State { get; set; } = "InProgress";
    public string PlayerResponse { get; set; } = "None";
    public double Timer { get; set; }
}

public class ChampSelectAction
{
    public int Id { get; set; }
    public int ActorCellId { get; set; }
    public int ChampionId { get; set; }
    public bool Completed { get; set; }
    public bool IsInProgress { get; set; }
    public bool IsAllyAction { get; set; }
    public string Type { get; set; } = "pick";
}

public class TeamMember
{
    public int CellId { get; set; }
    public int Team { get; set; }
    public int ChampionId { get; set; }
    public int ChampionPickIntent { get; set; }
    public string AssignedPosition { get; set; } = "";
    public long Spell1Id { get; set; }
    public long Spell2Id { get; set; }
    public long SummonerId { get; set; }
}

public class SessionTimer
{
    public string Phase { get; set; } = "BAN_PICK";
    public long AdjustedTimeLeftInPhase { get; set; }
    public long TotalTimeInPhase { get; set; }
    public long InternalNowInEpochMs { get; set; }
}

public class SessionBans
{
    public List<int> MyTeamBans { get; set; } = new();
    public List<int> TheirTeamBans { get; set; } = new();
}

public class SwapEntry
{
    public int Id { get; set; }
    public int CellId { get; set; }
    public string State { get; set; } = "INVALID";
}

public class ChampSelectSession
{
    public long GameId { get; set; }
    public int LocalPlayerCellId { get; set; }
    public List<List<ChampSelectAction>> Actions { get; set; } = new();
    public List<TeamMember> MyTeam { get; set; } = new();
    public List<TeamMember> TheirTeam { get; set; } = new();
    public SessionTimer Timer { get; set; } = new();
    public SessionBans Bans { get; set; } = new();
    public List<SwapEntry> Trades { get; set; } = new();
    public List<SwapEntry> PickOrderSwaps { get; set; } = new();
}
