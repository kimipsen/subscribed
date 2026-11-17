using System.Globalization;

namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>Normalization and limits for payment option names and labels, plus the card-number rule (D7, D8).</summary>
public static class PaymentOptionText
{
    public const int MaxName = 40;
    public const int MaxLabel = 40;

    /// <summary>A run of this many digits or more is rejected (NFR-1). Stricter than the 13-19 digits of the requirement on purpose.</summary>
    public const int CardNumberDigits = 13;

    public const string ReasonCardNumberLike = "card_number_like";
    public const string ReasonInvalidLength = "invalid_length";
    public const string ReasonControlCharacters = "control_characters";
    public const string ReasonUnknownBrand = "unknown_brand";

    /// <summary>The fixed brand rejection message. It never contains the submitted value (NFR-2).</summary>
    public const string BrandMessage = "The brand is not a known payment option brand.";

    /// <summary>Null means no brand. Otherwise the value must match a known id exactly (ordinal, no trimming, no case folding).</summary>
    public static bool TryValidateBrand(string? raw, out string reason)
    {
        reason = raw is null || PaymentOptionBrands.IsKnown(raw) ? "" : ReasonUnknownBrand;
        return reason.Length == 0;
    }

    /// <summary>
    /// Scans left to right with a running digit count. Any Unicode decimal digit (category Nd) counts; a letter resets the run;
    /// every other character is a separator that does not reset it. True when a run reaches 13 digits. Never throws.
    /// </summary>
    public static bool LooksLikeCardNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var run = 0;
        foreach (var c in text.Trim())
        {
            if (char.IsDigit(c))
            {
                if (++run >= CardNumberDigits)
                {
                    return true;
                }
            }
            else if (char.IsLetter(c))
            {
                run = 0;
            }
        }

        return false;
    }

    /// <summary>Trimmed, 1-40 UTF-16 code units, no control characters, not card-number-like. The reason is a fixed code, never the input.</summary>
    public static bool TryNormalizeName(string? raw, out string name, out string reason)
    {
        name = (raw ?? "").Trim();
        reason = "";
        if (name.Length is < 1 or > MaxName)
        {
            reason = ReasonInvalidLength;
        }
        else if (name.Any(char.IsControl))
        {
            reason = ReasonControlCharacters;
        }
        else if (LooksLikeCardNumber(name))
        {
            reason = ReasonCardNumberLike;
        }

        return reason.Length == 0;
    }

    /// <summary>Empty or whitespace-only means absent (null). Otherwise at most 40 characters, no control characters, not card-number-like.</summary>
    public static bool TryNormalizeLabel(string? raw, out string? label, out string reason)
    {
        label = null;
        reason = "";
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > MaxLabel)
        {
            reason = ReasonInvalidLength;
        }
        else if (trimmed.Any(char.IsControl))
        {
            reason = ReasonControlCharacters;
        }
        else if (LooksLikeCardNumber(trimmed))
        {
            reason = ReasonCardNumberLike;
        }
        else
        {
            label = trimmed;
        }

        return reason.Length == 0;
    }

    /// <summary>The fixed, input-free message for a rejection reason (NFR-2).</summary>
    public static string MessageFor(string field, string reason) => reason switch
    {
        ReasonUnknownBrand => BrandMessage,
        _ when field == "brand" => BrandMessage,
        ReasonCardNumberLike => $"The {field} must not look like a card number (no run of {CardNumberDigits} or more digits).",
        ReasonControlCharacters => $"The {field} must not contain control characters.",
        _ => field == "name"
            ? string.Create(CultureInfo.InvariantCulture, $"The name must be 1-{MaxName} characters.")
            : string.Create(CultureInfo.InvariantCulture, $"The label must be at most {MaxLabel} characters."),
    };
}
