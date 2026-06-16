using Spectre.Investigation.Api;

namespace Spectre.Pipeline.Api;

public sealed record StartPipelineRunRequest(string? InputPath);

public sealed record PipelineRunControlResult(bool Accepted, string Message, RunStatusDto Status);
