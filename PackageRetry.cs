using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace SurfArchive;

internal sealed class PackageRetry(TimeProvider clock)
{
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)];
    private static readonly TimeSpan RecoveryLimit = TimeSpan.FromHours(12);
    private TimeSpan recoveryUsed;

    public async Task<T> Run<T>(string operation, Func<int, CancellationToken, Task<T>> perform,
        Action resetConnection, Action<string> onRetry, CancellationToken token)
    {
        CancellationTokenSource? deadline = null, linked = null;
        long started = 0;
        Exception? lastFailure = null;
        bool finalAttempt = false;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (deadline?.IsCancellationRequested == true)
                    throw LimitReached();
                try
                {
                    return await perform(attempt, linked?.Token ?? token);
                }
                catch (Exception error) when (!token.IsCancellationRequested && IsConnectionFailure(error))
                {
                    lastFailure = error;
                    if (deadline is null)
                    {
                        var remaining = RecoveryLimit - recoveryUsed;
                        if (remaining <= TimeSpan.Zero)
                            throw LimitReached();
                        started = clock.GetTimestamp();
                        deadline = new CancellationTokenSource(remaining, clock);
                        linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                    }
                    resetConnection();
                    if (finalAttempt)
                        throw LimitReached();
                    var delay = Delays[Math.Min(attempt - 1, Delays.Length - 1)];
                    var budget = RecoveryLimit - recoveryUsed - clock.GetElapsedTime(started);
                    if (delay >= budget)
                    {
                        // Leave time for a final connection attempt in the recovery window.
                        delay = budget - TimeSpan.FromMinutes(1);
                        if (delay <= TimeSpan.Zero)
                            throw LimitReached();
                        finalAttempt = true;
                    }

                    long scheduled = clock.GetTimestamp();
                    string next = clock.GetLocalNow().Add(delay).ToString("dd MMM HH:mm:ss");
                    onRetry($"{operation}: {error.Message} Next attempt {next} (in {FormatDelay(delay)}).");
                    TimeSpan untilNext = delay - clock.GetElapsedTime(scheduled);
                    if (untilNext > TimeSpan.Zero)
                        await Task.Delay(untilNext, clock, linked!.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw LimitReached();
        }
        finally
        {
            if (deadline is not null)
            {
                recoveryUsed += clock.GetElapsedTime(started);
                linked!.Dispose();
                deadline.Dispose();
            }
        }

        IOException LimitReached() =>
            new($"{operation} stopped at the 12-hour retry limit. {lastFailure?.Message}", lastFailure);
    }

    private static bool IsConnectionFailure(Exception error) =>
        error is SshOperationTimeoutException or SocketException or TimeoutException
            or SshConnectionException { DisconnectReason: not DisconnectReason.HostKeyNotVerifiable };

    private static string FormatDelay(TimeSpan delay) => delay.TotalHours >= 1 ? "1 hour"
        : delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} min" : $"{delay.TotalSeconds:0} sec";
}
