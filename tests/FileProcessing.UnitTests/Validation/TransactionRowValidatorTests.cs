using FileProcessing.Core.Processing;
using FileProcessing.Core.Validation;

namespace FileProcessing.UnitTests.Validation;

public sealed class TransactionRowValidatorTests
{
    private static readonly RawTransactionRow Valid = new(
        "TXN-1",
        "2026-07-01",
        "Linehaul Melbourne to Geelong",
        "1450.00",
        "AUD",
        "Linehaul");

    [Fact]
    public void Accepts_a_well_formed_row()
    {
        var (accepted, errors, record) = Validate(Valid);

        Assert.True(accepted);
        Assert.Empty(errors.Errors);
        Assert.NotNull(record);
        Assert.Equal("TXN-1", record.TransactionId);
        Assert.Equal(new DateOnly(2026, 7, 1), record.TransactionDate);
        Assert.Equal(1450.00m, record.Amount);
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        var (accepted, _, record) = Validate(Valid with
        {
            TransactionId = "  TXN-1  ",
            Category = " Linehaul ",
        });

        Assert.True(accepted);
        Assert.Equal("TXN-1", record!.TransactionId);
        Assert.Equal("Linehaul", record.Category);
    }

    [Fact]
    public void Normalises_currency_to_upper_case()
    {
        var (accepted, _, record) = Validate(Valid with { Currency = "aud" });

        Assert.True(accepted);
        Assert.Equal("AUD", record!.Currency);
    }

    [Theory]
    [InlineData("", "transactionId.missing")]
    [InlineData("   ", "transactionId.missing")]
    public void Requires_a_transaction_id(string id, string expectedCode) =>
        AssertRejected(Valid with { TransactionId = id }, expectedCode);

    [Fact]
    public void Rejects_an_over_long_transaction_id() =>
        AssertRejected(Valid with { TransactionId = new string('x', 65) }, "transactionId.too_long");

    [Theory]
    [InlineData("01/07/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-07-32")]
    [InlineData("20260701")]
    [InlineData("2026-7-1")]
    public void Requires_an_iso_date(string date) =>
        AssertRejected(Valid with { TransactionDate = date }, "transactionDate.invalid_format");

    [Fact]
    public void Rejects_a_date_in_the_future() =>
        AssertRejected(Valid with { TransactionDate = "2099-01-01" }, "transactionDate.in_future");

    [Fact]
    public void Allows_a_date_one_day_ahead_of_utc()
    {
        // A client in a time zone ahead of UTC can legitimately book a transaction that is
        // "tomorrow" from the server's point of view.
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 1, 23, 0, 0, TimeSpan.Zero));
        var (accepted, _, _) = Validate(Valid with { TransactionDate = "2026-07-02" }, clock: clock);

        Assert.True(accepted);
    }

    [Theory]
    [InlineData("twelve", "amount.not_a_number")]
    [InlineData("", "amount.missing")]
    [InlineData("10.005", "amount.too_many_decimals")]
    [InlineData("2000000000", "amount.out_of_range")]
    public void Enforces_the_amount_rules(string amount, string expectedCode) =>
        AssertRejected(Valid with { Amount = amount }, expectedCode);

    [Theory]
    [InlineData("-45.10")]
    [InlineData("1,250.00")]
    [InlineData("0")]
    public void Accepts_negative_thousand_separated_and_zero_amounts(string amount)
    {
        var (accepted, errors, _) = Validate(Valid with { Amount = amount });

        Assert.True(accepted, string.Join(", ", errors.Errors.Select(e => e.Code)));
    }

    [Theory]
    [InlineData("AU", "currency.invalid_format")]
    [InlineData("AUDD", "currency.invalid_format")]
    [InlineData("A1D", "currency.invalid_format")]
    [InlineData("", "currency.missing")]
    public void Enforces_the_currency_rules(string currency, string expectedCode) =>
        AssertRejected(Valid with { Currency = currency }, expectedCode);

    [Theory]
    [InlineData("JPY")]
    [InlineData("ZAR")]
    [InlineData("XYZ")]
    public void Accepts_any_well_formed_currency_code(string currency)
    {
        // The rule is the shape of the code, not membership of a list. Which currencies a business
        // accepts is not this service's decision, and an allow-list here would reject a legitimate
        // file the first time someone started trading in a new one.
        var (accepted, errors, record) = Validate(Valid with { Currency = currency });

        Assert.True(accepted, string.Join(", ", errors.Errors.Select(e => e.Code)));
        Assert.Equal(currency, record!.Currency);
    }

    [Fact]
    public void Requires_a_category() =>
        AssertRejected(Valid with { Category = "" }, "category.missing");

    [Fact]
    public void Rejects_control_characters_in_the_description() =>
        AssertRejected(Valid with { Description = "bell\u0007char" }, "description.invalid_characters");

    [Fact]
    public void Allows_an_empty_description()
    {
        var (accepted, _, _) = Validate(Valid with { Description = "" });

        Assert.True(accepted);
    }

    [Fact]
    public void Reports_the_line_number_it_was_given()
    {
        var (_, errors, _) = Validate(Valid with { Amount = "nope" }, lineNumber: 42);

        Assert.Equal(42, Assert.Single(errors.Errors).LineNumber);
    }

    private static void AssertRejected(RawTransactionRow row, string expectedCode)
    {
        var (accepted, errors, record) = Validate(row);

        Assert.False(accepted);
        Assert.Null(record);
        Assert.Contains(expectedCode, errors.Errors.Select(e => e.Code));
    }

    private static (bool Accepted, ErrorSink Errors, TransactionRecord? Record) Validate(
        RawTransactionRow row,
        long lineNumber = 2,
        FixedClock? clock = null)
    {
        var validator = new TransactionRowValidator(clock ?? new FixedClock());
        var errors = new ErrorSink(100);
        var accepted = validator.TryValidate(lineNumber, in row, errors, out var record);

        return (accepted, errors, record);
    }
}
