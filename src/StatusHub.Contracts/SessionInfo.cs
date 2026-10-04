namespace StatusHub.Contracts;

/// <summary>One active Claude Code session (a main agent) and its running subagents, both in start order.</summary>
/// <param name="Failed">The session's last turn ended with StopFailure and nothing has happened in it since.</param>
public sealed record SessionInfo(string SessionId, bool Failed, IReadOnlyList<SubagentInfo> Subagents);

/// <summary>A running subagent. Claude Code's internal agents (no agent type) are not listed.</summary>
public sealed record SubagentInfo(string AgentId, string AgentType);
