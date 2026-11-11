namespace EHOT.Launcher.Core.Results;

/// <summary>
/// Явный результат операции: успех или ошибка с кодом.
/// Используется на границах модулей, чтобы UI не ловил исключения бизнес-логики.
/// </summary>
public readonly record struct OperationResult
{
    private OperationResult(bool ok, ErrorCode code, string? message, Exception? exception)
    {
        IsSuccess = ok;
        Code = ok ? ErrorCode.Unknown : code;
        Message = message;
        Exception = exception;
    }

    public bool IsSuccess { get; }
    public ErrorCode Code { get; }
    public string? Message { get; }
    public Exception? Exception { get; }

    public static OperationResult Ok() => new(true, ErrorCode.Unknown, null, null);

    public static OperationResult Fail(ErrorCode code, string message, Exception? ex = null)
        => new(false, code, message, ex);

    public static OperationResult FromException(Exception ex, ErrorCode fallback = ErrorCode.Unknown)
        => ex switch
        {
            OperationCanceledException => new(false, ErrorCode.Cancelled, "Операция отменена.", ex),
            TimeoutException => new(false, ErrorCode.Timeout, "Истекло время ожидания.", ex),
            UnauthorizedAccessException => new(false, ErrorCode.Io, "Нет доступа к файлу или папке.", ex),
            IOException io => new(false, ErrorCode.Io, io.Message, ex),
            HttpRequestException http => new(false, ErrorCode.Network, http.Message, ex),
            _ => new(false, fallback, ex.Message, ex)
        };

    public override string ToString() => IsSuccess ? "Ok" : $"{Code}: {Message}";
}

/// <summary>Результат операции со значением.</summary>
public readonly record struct OperationResult<T>
{
    private OperationResult(bool ok, T? value, ErrorCode code, string? message, Exception? exception)
    {
        IsSuccess = ok;
        Value = value;
        Code = ok ? ErrorCode.Unknown : code;
        Message = message;
        Exception = exception;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public ErrorCode Code { get; }
    public string? Message { get; }
    public Exception? Exception { get; }

    // Фабрики намеренно живут на generic-типе: OperationResult<T>.Ok(value) читается лучше
    // и не требует вывода типа из аргумента. CA1000 здесь осознанно подавлен.
#pragma warning disable CA1000
    public static OperationResult<T> Ok(T value) => new(true, value, ErrorCode.Unknown, null, null);

    public static OperationResult<T> Fail(ErrorCode code, string message, Exception? ex = null)
        => new(false, default, code, message, ex);

    public static OperationResult<T> FromException(Exception ex, ErrorCode fallback = ErrorCode.Unknown)
    {
        var r = OperationResult.FromException(ex, fallback);
        return new OperationResult<T>(false, default, r.Code, r.Message, r.Exception);
    }
#pragma warning restore CA1000

    /// <summary>Значение или исключение (для мест, где ошибка — действительно исключительная ситуация).</summary>
    public T ValueOrThrow() => IsSuccess
        ? Value!
        : throw new InvalidOperationException($"Operation failed with {Code}: {Message}", Exception);

    public bool TryGetValue(out T value)
    {
        value = Value!;
        return IsSuccess;
    }

    public OperationResult<TTarget> Map<TTarget>(Func<T, TTarget> map)
        => IsSuccess
            ? OperationResult<TTarget>.Ok(map(Value!))
            : OperationResult<TTarget>.Fail(Code, Message ?? string.Empty, Exception);

    public OperationResult ToOperationResult()
        => IsSuccess ? OperationResult.Ok() : OperationResult.Fail(Code, Message ?? string.Empty, Exception);

    public override string ToString() => IsSuccess ? "Ok" : $"{Code}: {Message}";
}

public static class OperationResultExtensions
{
    /// <summary>Складывает несколько результатов: первый неуспешный возвращается как есть.</summary>
    public static OperationResult Combine(this IEnumerable<OperationResult> results)
    {
        foreach (var r in results)
        {
            if (!r.IsSuccess)
            {
                return r;
            }
        }

        return OperationResult.Ok();
    }

    public static async Task<OperationResult> CombineAsync(this IEnumerable<Task<OperationResult>> results)
        => (await Task.WhenAll(results).ConfigureAwait(false)).Combine();
}
