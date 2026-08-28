using FileProcessing.Api.Authentication;
using FileProcessing.Api.Contracts;
using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileProcessing.Api.Controllers;

/// <summary>Aggregate reporting over everything the service has processed.</summary>
[ApiController]
[Route("api/v1/reports")]
[Produces("application/json", "application/problem+json")]
public sealed class ReportsController(IProcessedFileRepository repository) : ControllerBase
{
    /// <summary>Summarises processing activity over an optional date window.</summary>
    /// <remarks>
    /// Scoped to the caller's own uploads unless the key carries the cross-client read scope, in
    /// which case the per-client breakdown is populated for every client.
    /// </remarks>
    [HttpGet("summary")]
    [Authorize(Policy = AuthorizationPolicies.ReadReports)]
    [ProducesResponseType<SummaryReportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SummaryReportResponse>> GetSummaryAsync(
        [FromQuery] SummaryReportRequest request,
        CancellationToken cancellationToken)
    {
        var report = await repository.SummariseAsync(
            new ReportQuery
            {
                RestrictToClientId = User.GetReadRestriction(),
                FromUtc = request.From,
                ToUtc = request.To,
            },
            cancellationToken);

        return Ok(ResponseMapper.ToReportResponse(report));
    }
}
