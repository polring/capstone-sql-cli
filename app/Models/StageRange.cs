namespace SqlApp.Models;

public enum PipelineStage { Front, Mapper, Back, Postprocessor }

public readonly record struct StageRange(PipelineStage Start, PipelineStage End)
{
    public bool IsValid => Enum.IsDefined(Start) && Enum.IsDefined(End) && Start <= End;
    public bool Contains(PipelineStage stage) => Start <= stage && stage <= End;

    public StageRange SnapForSelection() => IsValid
        ? WithStart(Start)
        : new(PipelineStage.Front, PipelineStage.Postprocessor);

    public StageRange WithStart(PipelineStage start) => start >= PipelineStage.Back
        ? new(PipelineStage.Back, PipelineStage.Back)
        : new(PipelineStage.Front, End);

    public StageRange WithEnd(PipelineStage end) => Start == PipelineStage.Back
        ? new(PipelineStage.Back, PipelineStage.Back)
        : new(PipelineStage.Front, end);
}
