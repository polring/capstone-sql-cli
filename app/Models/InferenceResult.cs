namespace SqlApp.Models;

public sealed record InferenceResult(string Text, string StopReason, double ComputeTimeMs);
