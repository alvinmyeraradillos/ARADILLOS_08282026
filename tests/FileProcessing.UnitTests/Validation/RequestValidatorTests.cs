using FileProcessing.Api.Contracts;
using FileProcessing.Api.Validation;
using FluentValidation.TestHelper;

namespace FileProcessing.UnitTests.Validation;

public sealed class ListFilesRequestValidatorTests
{
    private readonly ListFilesRequestValidator _validator = new();

    [Fact]
    public void Accepts_the_defaults()
    {
        _validator.TestValidate(new ListFilesRequest()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_page_below_one(int page)
    {
        _validator.TestValidate(new ListFilesRequest { Page = page })
            .ShouldHaveValidationErrorFor(request => request.Page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ListFilesRequestValidator.MaxPageSize + 1)]
    public void Rejects_a_page_size_outside_the_permitted_range(int pageSize)
    {
        _validator.TestValidate(new ListFilesRequest { PageSize = pageSize })
            .ShouldHaveValidationErrorFor(request => request.PageSize);
    }

    [Fact]
    public void Accepts_the_largest_permitted_page_size()
    {
        _validator.TestValidate(new ListFilesRequest { PageSize = ListFilesRequestValidator.MaxPageSize })
            .ShouldNotHaveValidationErrorFor(request => request.PageSize);
    }

    [Fact]
    public void Rejects_an_inverted_date_range()
    {
        var request = new ListFilesRequest
        {
            ReceivedFrom = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            ReceivedTo = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(r => r.ReceivedFrom);
    }

    [Fact]
    public void Accepts_a_range_with_only_one_end_supplied()
    {
        // An open-ended window is legitimate, so the cross-field rule must not fire when only one
        // bound is present.
        var request = new ListFilesRequest { ReceivedFrom = DateTimeOffset.UnixEpoch };

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Rejects_an_over_long_file_name_filter()
    {
        _validator.TestValidate(new ListFilesRequest { FileName = new string('x', 256) })
            .ShouldHaveValidationErrorFor(request => request.FileName);
    }
}

public sealed class SummaryReportRequestValidatorTests
{
    private readonly SummaryReportRequestValidator _validator = new();

    [Fact]
    public void Accepts_an_empty_request()
    {
        _validator.TestValidate(new SummaryReportRequest()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Rejects_an_inverted_date_range()
    {
        var request = new SummaryReportRequest
        {
            From = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(r => r.From);
    }

    [Fact]
    public void Accepts_a_range_that_starts_and_ends_at_the_same_instant()
    {
        var instant = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        _validator.TestValidate(new SummaryReportRequest { From = instant, To = instant })
            .ShouldNotHaveAnyValidationErrors();
    }
}

public sealed class UploadFileRequestValidatorTests
{
    private readonly UploadFileRequestValidator _validator = new();

    [Fact]
    public void Rejects_a_missing_file_part()
    {
        _validator.TestValidate(new UploadFileRequest()).ShouldHaveValidationErrorFor(request => request.File);
    }

    [Fact]
    public void Does_not_police_size()
    {
        // Size is deliberately not a validation rule: an oversized upload must surface as 413, not
        // as a 400 validation problem. This test exists so that split cannot be undone silently.
        var rules = _validator
            .CreateDescriptor()
            .GetMembersWithValidators()
            .SelectMany(group => group)
            .Select(rule => rule.Validator.Name);

        Assert.DoesNotContain("LessThanOrEqualValidator", rules);
    }
}
