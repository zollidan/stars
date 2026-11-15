using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Stars.Application.Configuration;

namespace Stars.Infrastructure.Jobs;

/// <summary>
/// Single in-process worker with a PostgreSQL-backed queue. Job rows are leased with
/// FOR UPDATE SKIP LOCKED, which keeps replays idempotent and allows a second worker
/// later without changing the contract.
/// </summary>
public sealed class ProcessingJobWorker(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    IOptions<StarsOptions> options,
    ILogger<ProcessingJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("STARS processing worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                for (var index = 0; index < options.Value.JobBatchSize; index++)
                {
                    var job = await LeaseJobAsync(stoppingToken);
                    if (job is null)
                    {
                        break;
                    }

                    await ProcessJobAsync(job, stoppingToken);
                    processed++;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Processing worker iteration failed");
            }

            if (processed == 0)
            {
                try
                {
                    await Task.Delay(options.Value.WorkerPollMilliseconds, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<LeasedJob?> LeaseJobAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE processing_jobs
            SET status = 'processing',
                lease_until = now() + interval '2 minutes',
                attempts = attempts + 1
            WHERE id = (
                SELECT id FROM processing_jobs
                WHERE status IN ('pending', 'processing')
                  AND run_after <= now()
                  AND (lease_until IS NULL OR lease_until < now())
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, source_entity_id, input_hash, attempts
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LeasedJob(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3));
    }

    private async Task ProcessJobAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<Matching.MatchingProcessor>();
            var result = await processor.ProcessAsync(job.SourceEntityId, job.InputHash, cancellationToken);

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                UPDATE processing_jobs
                SET status = 'done', lease_until = NULL
                WHERE id = @id
                """, connection);
            command.Parameters.AddWithValue("id", job.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);

            logger.LogInformation(
                "Job {JobId} done: {Reason} ({Candidates} candidates, linked={Linked})",
                job.Id,
                result.Reason,
                result.Candidates.Count,
                result.Linked);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Job {JobId} failed on attempt {Attempt}", job.Id, job.Attempts);
            await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand(
                """
                UPDATE processing_jobs
                SET status = CASE WHEN attempts >= 3 THEN 'failed' ELSE 'pending' END,
                    lease_until = NULL,
                    run_after = now() + make_interval(secs => @delay)
                WHERE id = @id
                """, connection);
            command.Parameters.AddWithValue("id", job.Id);
            command.Parameters.AddWithValue("delay", Math.Min(60, job.Attempts * 5));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private sealed record LeasedJob(Guid Id, Guid SourceEntityId, string InputHash, int Attempts);
}
