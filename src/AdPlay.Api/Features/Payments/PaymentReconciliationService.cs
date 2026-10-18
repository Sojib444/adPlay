using Microsoft.Extensions.Options;

namespace AdPlay.Api.Features.Payments;

/// <summary>
/// Periodically settles Unknown and stuck payments. Safe on several instances at once: every update is
/// conditional on the current status, so concurrent sweeps can only duplicate a lookup, never a state change.
/// </summary>
public sealed partial class PaymentReconciliationService(
    IServiceScopeFactory scopes,
    IOptions<PaymentOptions> options,
    ILogger<PaymentReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<PaymentService>();
                var settled = await service.ReconcileAsync(batchSize: 50, stoppingToken);
                if (settled > 0) LogSettled(settled);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogSweepFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation settled {Count} payments")]
    private partial void LogSettled(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment reconciliation sweep failed")]
    private partial void LogSweepFailed(Exception ex);
}
