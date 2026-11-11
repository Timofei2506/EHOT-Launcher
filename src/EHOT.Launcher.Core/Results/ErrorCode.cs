namespace EHOT.Launcher.Core.Results;

/// <summary>
/// Машиночитаемые коды ошибок. UI показывает локализованный текст по коду,
/// а не «сырой» exception message.
/// </summary>
public enum ErrorCode
{
    Unknown = 0,
    Network,
    Timeout,
    NotFound,
    RateLimited,
    Unauthorized,
    Forbidden,
    Validation,
    Io,
    Cancelled,
    HashMismatch,
    Unsupported,
    DependencyMissing,
    Incompatible,
    Conflict,
    Unavailable,
    QuotaExceeded,
    SecurityViolation,
    JavaNotFound,
    JavaVersionMismatch,
    GameCrash,
    InstallIncomplete,
    AuthRequired,
    AuthDenied,
    AuthPending,
    Configuration
}
