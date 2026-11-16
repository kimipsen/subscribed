using Microsoft.Extensions.Logging;

namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>
/// Content-free payment option signals (NFR-2). Only the opaque option id and a fixed reason code are ever logged: never a name or a
/// label, and never an exception that could echo them. Source-generated (CA1848).
/// </summary>
public static partial class PaymentOptionLog
{
    public const string Category = "Subscribed.PaymentOptions";

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "payment_option.created {OptionId}")]
    private static partial void CreatedCore(ILogger logger, string optionId);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "payment_option.removed {OptionId}")]
    private static partial void RemovedCore(ILogger logger, string optionId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "payment_option.rejected {Reason}")]
    private static partial void RejectedCore(ILogger logger, string reason);

    public static void Created(ILogger logger, string optionId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        CreatedCore(logger, optionId);
    }

    public static void Removed(ILogger logger, string optionId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        RemovedCore(logger, optionId);
    }

    /// <summary>One entry per reason code of the rejected request.</summary>
    public static void Rejected(ILogger logger, PaymentOptionRejectedException rejection)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(rejection);
        foreach (var reason in rejection.Reasons.Distinct(StringComparer.Ordinal))
        {
            RejectedCore(logger, reason);
        }
    }
}
