namespace Stars.Application.Configuration;

public sealed class StarsOptions
{
    public const string SectionName = "Stars";

    public bool AutoMigrate { get; set; } = true;

    public int MaxRecordsPerSnapshot { get; set; } = 5000;

    public int WorkerPollMilliseconds { get; set; } = 750;

    public int JobBatchSize { get; set; } = 4;

    public DemoOptions Demo { get; set; } = new();
}

public sealed class DemoOptions
{
    public bool Enabled { get; set; } = true;

    public string FixtureRoot { get; set; } = "fixtures";

    public bool AutoSync { get; set; } = true;

    public string? EvaAddress { get; set; }

    public string? EvaTokenEnvironmentVariable { get; set; } = "DATAHUB_SERVICE_TOKEN";

    public int EvaMaxRecords { get; set; } = 300;
}
