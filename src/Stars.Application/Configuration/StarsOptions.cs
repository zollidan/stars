namespace Stars.Application.Configuration;

public sealed class StarsOptions
{
    public const string SectionName = "Stars";

    public bool AutoMigrate { get; set; } = true;

    public int MaxRecordsPerSnapshot { get; set; } = 5000;

    public int WorkerPollMilliseconds { get; set; } = 750;

    public int JobBatchSize { get; set; } = 4;
}
