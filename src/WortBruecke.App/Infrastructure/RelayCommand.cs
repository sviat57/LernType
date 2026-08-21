using System.IO;
using System.Net.Http;
using System.Windows.Input;

namespace WortBruecke.App.Infrastructure;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ParameterizedRelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public enum AsyncCommandStatus
{
    Idle,
    Running,
    Succeeded,
    Canceled,
    Failed
}

public enum OperationErrorKind
{
    Unexpected,
    Validation,
    StorageBusy,
    StorageUnavailable,
    NetworkUnavailable,
    Timeout,
    Protocol
}

/// <summary>A UI-safe descriptor which deliberately retains neither input nor exception messages.</summary>
public sealed record OperationError(OperationErrorKind Kind, string UserMessage, string TechnicalCode)
{
    public static OperationError FromException(Exception exception, string fallbackMessage = "Операция не завершена. Повторите попытку.")
    {
        ArgumentNullException.ThrowIfNull(exception);
        var name = exception.GetType().Name;
        if (exception is TimeoutException || exception is TaskCanceledException { InnerException: TimeoutException })
        {
            return new(OperationErrorKind.Timeout, "Время ожидания истекло. Повторите попытку.", name);
        }
        if (exception is HttpRequestException)
        {
            return new(OperationErrorKind.NetworkUnavailable, "Сетевой сервис временно недоступен.", name);
        }
        if (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return new(OperationErrorKind.Protocol, "Получены данные неподдерживаемого формата.", name);
        }
        if (exception is UnauthorizedAccessException)
        {
            return new(OperationErrorKind.StorageUnavailable, "Нет доступа к выбранному файлу или локальному хранилищу.", name);
        }
        if (exception is IOException)
        {
            return new(OperationErrorKind.StorageUnavailable, "Локальное хранилище временно недоступно.", name);
        }
        if (name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return new(OperationErrorKind.StorageBusy, "Локальная база занята. Закройте вторую копию LernType и повторите попытку.", name);
        }
        if (exception is ArgumentException)
        {
            return new(OperationErrorKind.Validation, "Проверьте введённые данные.", name);
        }
        return new(OperationErrorKind.Unexpected, fallbackMessage, name);
    }
}

public interface IAsyncCommand : ICommand
{
    bool IsRunning { get; }
    AsyncCommandStatus Status { get; }
    OperationError? LastError { get; }
    Task? ExecutionTask { get; }
    Task ExecuteAsync(object? parameter = null);
    void Cancel();
    void RaiseCanExecuteChanged();
}

/// <summary>
/// Awaitable and cancellable command. Exceptions never escape the ICommand dispatcher boundary.
/// </summary>
public sealed class AsyncRelayCommand : IAsyncCommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<OperationError>? _onError;
    private CancellationTokenSource? _executionCancellation;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<OperationError>? onError = null)
        : this(_ => execute(), canExecute, onError)
    {
    }

    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        Action<OperationError>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => _isRunning;
    public AsyncCommandStatus Status { get; private set; } = AsyncCommandStatus.Idle;
    public OperationError? LastError { get; private set; }
    public Task? ExecutionTask { get; private set; }
    public bool CanExecute(object? parameter)
    {
        try { return !_isRunning && (_canExecute?.Invoke() ?? true); }
        catch { return false; }
    }

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return Task.CompletedTask;
        }
        ExecutionTask = ExecuteCoreAsync();
        return ExecutionTask;
    }

    public void Cancel()
    {
        try
        {
            _executionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won a race with cancellation.
        }
        catch (AggregateException)
        {
            // A third-party cancellation callback must not escape a synchronous ICommand call.
        }
    }

    public void RaiseCanExecuteChanged()
    {
        try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* A view subscriber must not break command execution. */ }
    }

    private async Task ExecuteCoreAsync()
    {
        var cancellation = new CancellationTokenSource();
        _executionCancellation = cancellation;
        _isRunning = true;
        Status = AsyncCommandStatus.Running;
        LastError = null;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(cancellation.Token);
            Status = cancellation.IsCancellationRequested ? AsyncCommandStatus.Canceled : AsyncCommandStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = AsyncCommandStatus.Canceled;
        }
        catch (Exception exception)
        {
            LastError = OperationError.FromException(exception);
            Status = AsyncCommandStatus.Failed;
            NotifyError(LastError);
        }
        finally
        {
            if (ReferenceEquals(_executionCancellation, cancellation)) _executionCancellation = null;
            cancellation.Dispose();
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    private void NotifyError(OperationError error)
    {
        try
        {
            _onError?.Invoke(error);
        }
        catch
        {
            // An optional UI error presenter must never break the dispatcher exception shield.
        }
    }
}

public sealed class AsyncParameterizedRelayCommand : IAsyncCommand
{
    private readonly Func<object?, CancellationToken, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<OperationError>? _onError;
    private CancellationTokenSource? _executionCancellation;
    private bool _isRunning;

    public AsyncParameterizedRelayCommand(
        Func<object?, CancellationToken, Task> execute,
        Predicate<object?>? canExecute = null,
        Action<OperationError>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => _isRunning;
    public AsyncCommandStatus Status { get; private set; } = AsyncCommandStatus.Idle;
    public OperationError? LastError { get; private set; }
    public Task? ExecutionTask { get; private set; }
    public bool CanExecute(object? parameter)
    {
        try { return !_isRunning && (_canExecute?.Invoke(parameter) ?? true); }
        catch { return false; }
    }
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return Task.CompletedTask;
        }
        ExecutionTask = ExecuteCoreAsync(parameter);
        return ExecutionTask;
    }

    public void Cancel()
    {
        try
        {
            _executionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won a race with cancellation.
        }
        catch (AggregateException)
        {
            // A third-party cancellation callback must not escape a synchronous ICommand call.
        }
    }

    public void RaiseCanExecuteChanged()
    {
        try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* A view subscriber must not break command execution. */ }
    }

    private async Task ExecuteCoreAsync(object? parameter)
    {
        var cancellation = new CancellationTokenSource();
        _executionCancellation = cancellation;
        _isRunning = true;
        Status = AsyncCommandStatus.Running;
        LastError = null;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter, cancellation.Token);
            Status = cancellation.IsCancellationRequested ? AsyncCommandStatus.Canceled : AsyncCommandStatus.Succeeded;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = AsyncCommandStatus.Canceled;
        }
        catch (Exception exception)
        {
            LastError = OperationError.FromException(exception);
            Status = AsyncCommandStatus.Failed;
            NotifyError(LastError);
        }
        finally
        {
            if (ReferenceEquals(_executionCancellation, cancellation)) _executionCancellation = null;
            cancellation.Dispose();
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    private void NotifyError(OperationError error)
    {
        try
        {
            _onError?.Invoke(error);
        }
        catch
        {
            // An optional UI error presenter must never break the dispatcher exception shield.
        }
    }
}
