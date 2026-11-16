namespace Beachtro.Application.Common;

/// <summary>
/// Typed command outcomes (system design §Use-case contracts). Messages are safe for display:
/// no raw exceptions, tokens or secrets.
/// </summary>
public enum ResultStatus
{
    Success,
    Validation,
    Unauthenticated,
    Forbidden,
    ReauthenticationRequired,
    PasswordChangeRequired,
    NotFound,
    Conflict,
    StaleQuote,
    InsufficientStock,
    PaymentNotVerified,
    StorageUnavailable,
    BusyRetryable,
    OutcomeUnknown,
    RecoveryRequired,
}

public readonly record struct Unit
{
    public static Unit Value => default;
}

public sealed record Result<T>(ResultStatus Status, T? Value, string? Message)
{
    public bool IsSuccess => Status == ResultStatus.Success;

    public static Result<T> Ok(T value) => new(ResultStatus.Success, value, null);

    public static Result<T> Fail(ResultStatus status, string message) =>
        status == ResultStatus.Success
            ? throw new ArgumentException("A failure cannot have Success status.", nameof(status))
            : new(status, default, message);

    /// <summary>Carries a failure across result types.</summary>
    public Result<TOther> As<TOther>() =>
        IsSuccess ? throw new InvalidOperationException("Only failures can be converted.") : new(Status, default, Message);
}

public static class Result
{
    public static Result<Unit> Ok() => Result<Unit>.Ok(Unit.Value);

    public static Result<Unit> Fail(ResultStatus status, string message) => Result<Unit>.Fail(status, message);
}
