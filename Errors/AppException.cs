namespace ProductOrderApi.Errors;

public sealed class AppException : Exception
{
    public AppException(
        int statusCode,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null)
        : base(detail)
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

    public static AppException Conflict(string detail) =>
        new(StatusCodes.Status409Conflict, "Conflict", detail);

    public static AppException IdempotencyConflict() =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            "Idempotency key reuse",
            "The Idempotency-Key has already been used with a different request body.");

    public static AppException PaymentDeclined(string? detail = null) =>
        new(StatusCodes.Status402PaymentRequired, "Payment declined", detail ?? "The payment was declined.");

    public static AppException PaymentProviderError() =>
        new(StatusCodes.Status502BadGateway, "Payment provider error", "The payment provider could not process the request.");

    public static AppException PaymentTimeout() =>
        new(StatusCodes.Status504GatewayTimeout, "Payment provider timeout", "The payment provider did not respond before the timeout.");

    public static AppException ServiceUnavailable(string detail) =>
        new(StatusCodes.Status503ServiceUnavailable, "Service unavailable", detail);
}
