namespace ProductOrderApi.Errors;

public sealed class AppException : Exception
{
    public AppException(
        int statusCode,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Errors = errors;
    }

    public int StatusCode { get; }
    public string Title { get; }
    public string Detail { get; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; }

    public static AppException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(StatusCodes.Status400BadRequest, "Validation failed", "One or more validation errors occurred.", errors);

    public static AppException NotFound(string detail) =>
        new(StatusCodes.Status404NotFound, "Resource not found", detail);

    public static AppException Conflict(string detail, Exception? innerException = null) =>
        new(StatusCodes.Status409Conflict, "Conflict", detail, innerException: innerException);

    public static AppException IdempotencyConflict() =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            "Idempotency key reuse",
            "The Idempotency-Key has already been used with a different request body.");

    public static AppException PaymentDeclined(string? diagnosticReason = null) =>
        new(
            StatusCodes.Status402PaymentRequired,
            "Payment declined",
            "The payment was declined.",
            innerException: CreateDiagnosticException(diagnosticReason));

    public static AppException PaymentProviderError(
        Exception? innerException = null,
        string? diagnosticReason = null) =>
        new(
            StatusCodes.Status502BadGateway,
            "Payment provider error",
            "The payment provider could not process the request.",
            innerException: innerException ?? CreateDiagnosticException(diagnosticReason));

    public static AppException PaymentTimeout(Exception innerException) =>
        new(
            StatusCodes.Status504GatewayTimeout,
            "Payment provider timeout",
            "The payment provider did not respond before the timeout.",
            innerException: innerException);

    public static AppException ServiceUnavailable(string detail, Exception? innerException = null) =>
        new(StatusCodes.Status503ServiceUnavailable, "Service unavailable", detail, innerException: innerException);

    private static Exception? CreateDiagnosticException(string? diagnosticReason) =>
        string.IsNullOrWhiteSpace(diagnosticReason)
            ? null
            : new InvalidOperationException(diagnosticReason);
}
