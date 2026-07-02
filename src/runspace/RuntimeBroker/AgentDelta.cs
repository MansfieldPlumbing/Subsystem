namespace Subsystem
{
    // SS's turn vocabulary. A citizen (DPX) or a mounted guest yields these; ToolCatalog is the tool surface
    // (projected from Cm, never engine-typed).
    public enum AgentDeltaKind { Token, Think, ToolCall, ToolResult, Error }

    // One event of a turn. Token/Think carry visible/thinking text; ToolCall/ToolResult carry a tool name +
    // a JSON payload; an Error delta carries the structured fault record.
    public readonly record struct AgentDelta(AgentDeltaKind Kind, string Text, string? Name = null, RbFault? Fault = null);
}
