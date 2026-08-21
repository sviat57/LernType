using WortBruecke.App.Infrastructure;

namespace WortBruecke.Tests.Infrastructure;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteAsync_IsAwaitableAndPreventsConcurrentExecution()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var command = new AsyncRelayCommand(async cancellationToken =>
        {
            executions++;
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });

        var operation = command.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var ignoredConcurrentOperation = command.ExecuteAsync();

        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        Assert.Same(operation, command.ExecutionTask);
        Assert.True(ignoredConcurrentOperation.IsCompletedSuccessfully);
        Assert.Equal(1, executions);

        release.TrySetResult();
        await operation;
        Assert.Equal(AsyncCommandStatus.Succeeded, command.Status);
        Assert.Null(command.LastError);
    }

    [Fact]
    public async Task Cancel_PropagatesTokenAndReturnsCanceledStateWithoutThrowing()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var operation = command.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        command.Cancel();
        await operation;

        Assert.Equal(AsyncCommandStatus.Canceled, command.Status);
        Assert.Null(command.LastError);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task Cancel_ContainsThirdPartyCancellationCallbackFailure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() =>
                throw new InvalidOperationException("broken cancellation callback"));
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var operation = command.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        command.Cancel();
        await operation;

        Assert.Equal(AsyncCommandStatus.Canceled, command.Status);
    }

    [Fact]
    public async Task Failure_IsConvertedToTypedPrivacySafeState()
    {
        OperationError? reported = null;
        var command = new AsyncRelayCommand(
            _ => throw new IOException("raw-sensitive-path-and-content"),
            onError: error => reported = error);

        await command.ExecuteAsync();

        Assert.Equal(AsyncCommandStatus.Failed, command.Status);
        Assert.Equal(OperationErrorKind.StorageUnavailable, command.LastError?.Kind);
        Assert.Equal(command.LastError, reported);
        Assert.DoesNotContain("raw-sensitive", command.LastError?.UserMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailurePresenterFailure_DoesNotEscapeCommandBoundary()
    {
        var command = new AsyncRelayCommand(
            _ => throw new InvalidOperationException("operation failure"),
            onError: _ => throw new InvalidOperationException("presenter failure"));

        await command.ExecuteAsync();

        Assert.Equal(AsyncCommandStatus.Failed, command.Status);
        Assert.Equal(OperationErrorKind.Unexpected, command.LastError?.Kind);
    }

    [Fact]
    public async Task CanExecuteChangedSubscriberFailure_DoesNotEscapeCommandBoundary()
    {
        var executed = false;
        var command = new AsyncRelayCommand(_ =>
        {
            executed = true;
            return Task.CompletedTask;
        });
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("broken view subscriber");

        await command.ExecuteAsync();

        Assert.True(executed);
        Assert.Equal(AsyncCommandStatus.Succeeded, command.Status);
    }
}
