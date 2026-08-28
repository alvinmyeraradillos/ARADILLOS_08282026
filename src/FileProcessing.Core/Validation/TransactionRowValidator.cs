using System.Globalization;
using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Processing;

namespace FileProcessing.Core.Validation;

/// <summary>
/// Field-level rules for a transactions row. Kept separate from the CSV plumbing so the rules can
/// be unit tested on their own and reused if another input format is added later.
/// </summary>
public sealed class TransactionRowValidator(IClock clock)
{
    private const int MaxIdLength = 64;
    private const int MaxDescriptionLength = 256;
    private const int MaxCategoryLength = 64;
    private const decimal MaxAbsoluteAmount = 1_000_000_000m;

    /// <summary>
    /// Validates one row. Returns <see langword="true"/> and the parsed record when every rule
    /// passes; otherwise writes one error per broken rule to <paramref name="errors"/>.
    /// </summary>
    /// <remarks>
    /// Every rule is evaluated rather than short-circuiting on the first failure, so a client
    /// fixing a bad file sees all of that row's problems in one round trip. Messages deliberately
    /// describe the rule and never echo the offending value back to the caller.
    /// </remarks>
    public bool TryValidate(
        long lineNumber,
        in RawTransactionRow raw,
        ErrorSink errors,
        out TransactionRecord? record)
    {
        record = null;
        var valid = true;

        var id = raw.TransactionId.Trim();
        if (id.Length == 0)
        {
            errors.Add(lineNumber, "transactionId.missing", "Transaction id is required.", "TransactionId");
            valid = false;
        }
        else if (id.Length > MaxIdLength)
        {
            errors.Add(
                lineNumber,
                "transactionId.too_long",
                $"Transaction id must be {MaxIdLength} characters or fewer.",
                "TransactionId");
            valid = false;
        }

        var dateText = raw.TransactionDate.Trim();
        DateOnly date = default;
        if (dateText.Length == 0)
        {
            errors.Add(lineNumber, "transactionDate.missing", "Transaction date is required.", "TransactionDate");
            valid = false;
        }
        else if (!DateOnly.TryParseExact(
                     dateText,
                     "yyyy-MM-dd",
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.None,
                     out date))
        {
            errors.Add(
                lineNumber,
                "transactionDate.invalid_format",
                "Transaction date must be an ISO 8601 date in the form yyyy-MM-dd.",
                "TransactionDate");
            valid = false;
        }
        else if (date > DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(1))
        {
            // One day of slack absorbs clients posting from a time zone ahead of UTC.
            errors.Add(
                lineNumber,
                "transactionDate.in_future",
                "Transaction date cannot be in the future.",
                "TransactionDate");
            valid = false;
        }

        var description = raw.Description.Trim();
        if (description.Length > MaxDescriptionLength)
        {
            errors.Add(
                lineNumber,
                "description.too_long",
                $"Description must be {MaxDescriptionLength} characters or fewer.",
                "Description");
            valid = false;
        }
        else if (ContainsControlCharacters(description))
        {
            errors.Add(
                lineNumber,
                "description.invalid_characters",
                "Description contains control characters.",
                "Description");
            valid = false;
        }

        var amountText = raw.Amount.Trim();
        var amount = 0m;
        if (amountText.Length == 0)
        {
            errors.Add(lineNumber, "amount.missing", "Amount is required.", "Amount");
            valid = false;
        }
        else if (!decimal.TryParse(
                     amountText,
                     NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                     CultureInfo.InvariantCulture,
                     out amount))
        {
            errors.Add(lineNumber, "amount.not_a_number", "Amount must be a decimal number.", "Amount");
            valid = false;
        }
        else if (Math.Abs(amount) > MaxAbsoluteAmount)
        {
            errors.Add(
                lineNumber,
                "amount.out_of_range",
                "Amount is outside the range this service accepts.",
                "Amount");
            valid = false;
        }
        else if (Scale(amount) > 2)
        {
            errors.Add(
                lineNumber,
                "amount.too_many_decimals",
                "Amount must have at most two decimal places.",
                "Amount");
            valid = false;
        }

        var currency = raw.Currency.Trim().ToUpperInvariant();
        if (currency.Length == 0)
        {
            errors.Add(lineNumber, "currency.missing", "Currency is required.", "Currency");
            valid = false;
        }
        else if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            errors.Add(
                lineNumber,
                "currency.invalid_format",
                "Currency must be a three letter ISO 4217 code.",
                "Currency");
            valid = false;
        }

        var category = raw.Category.Trim();
        if (category.Length == 0)
        {
            errors.Add(lineNumber, "category.missing", "Category is required.", "Category");
            valid = false;
        }
        else if (category.Length > MaxCategoryLength)
        {
            errors.Add(
                lineNumber,
                "category.too_long",
                $"Category must be {MaxCategoryLength} characters or fewer.",
                "Category");
            valid = false;
        }

        if (!valid)
        {
            return false;
        }

        record = new TransactionRecord(id, date, description, amount, currency, category);
        return true;
    }

    private static int Scale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static bool ContainsControlCharacters(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\n'))
            {
                return true;
            }
        }

        return false;
    }
}
