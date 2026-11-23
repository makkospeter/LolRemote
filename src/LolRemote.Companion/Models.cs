namespace LolRemote.Companion;

public class ReadyCheck
{
    public string State { get; set; } = "";
    public string PlayerResponse { get; set; } = "";
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
    public string Type { get; set; } = "";
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
}

public class SessionTimer
{
    public string Phase { get; set; } = "";
    public long AdjustedTimeLeftInPhase { get; set; }
    public long TotalTimeInPhase { get; set; }
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
    public string State { get; set; } = "";
}

public class ChampSelectSession
{
    public int LocalPlayerCellId { get; set; }
    public List<List<ChampSelectAction>> Actions { get; set; } = new();
    public List<TeamMember> MyTeam { get; set; } = new();
    public List<TeamMember> TheirTeam { get; set; } = new();
    public SessionTimer Timer { get; set; } = new();
    public SessionBans? Bans { get; set; }
    public List<SwapEntry>? Trades { get; set; }
    public List<SwapEntry>? PickOrderSwaps { get; set; }
}

public class RunePage
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int PrimaryStyleId { get; set; }
    public int SubStyleId { get; set; }
    public List<int> SelectedPerkIds { get; set; } = new();
    public bool Current { get; set; }
    public bool IsEditable { get; set; }
}

public class ChampionSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
}

public class SummonerSpell
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public class NamedItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public record SwapOption(string Label, bool IsTrade, int Id, string Verb);
