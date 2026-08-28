using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FileProcessing.Api.Validation;

/// <summary>
/// Runs any registered FluentValidation validator against each bound action argument and turns
/// failures into the same RFC 7807 <see cref="ValidationProblemDetails"/> shape the framework
/// produces, so a client sees one error format regardless of which layer rejected the request.
/// </summary>
/// <remarks>
/// Written as a filter rather than using <c>FluentValidation.AspNetCore</c>, whose automatic
/// MVC integration the library has deprecated. Doing it explicitly is a few lines, keeps
/// validation on a code path that is easy to follow, and avoids a package the maintainers have
/// stopped recommending.
/// <para>
/// Arguments with no registered validator pass straight through, so adding a validator is the
/// only step needed to start validating a new request type.
/// </para>
/// </remarks>
public sealed class FluentValidationFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var errors = new ModelStateDictionary();

        foreach (var (name, argument) in context.ActionArguments)
        {
            if (argument is null)
            {
                continue;
            }

            if (services.GetService(typeof(IValidator<>).MakeGenericType(argument.GetType()))
                is not IValidator validator)
            {
                continue;
            }

            var result = await validator.ValidateAsync(
                new ValidationContext<object>(argument),
                context.HttpContext.RequestAborted);

            foreach (var failure in result.Errors)
            {
                // Property names are camel-cased to match the JSON the client sent; an error keyed
                // "PageSize" against a body that said "pageSize" is needlessly confusing.
                errors.AddModelError(ToCamelCase(failure.PropertyName, name), failure.ErrorMessage);
            }
        }

        if (!errors.IsValid)
        {
            var problem = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
                Instance = context.HttpContext.Request.Path,
            };
            problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;

            context.Result = new BadRequestObjectResult(problem)
            {
                ContentTypes = { "application/problem+json" },
            };

            return;
        }

        await next();
    }

    private static string ToCamelCase(string propertyName, string fallback)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return fallback;
        }

        // Only the leading segment needs lowering: "File.Length" becomes "file.Length", and a
        // name the validator already set explicitly (such as "file") is left alone.
        var separator = propertyName.IndexOf('.');
        var head = separator < 0 ? propertyName : propertyName[..separator];
        var tail = separator < 0 ? string.Empty : propertyName[separator..];

        return char.ToLowerInvariant(head[0]) + head[1..] + tail;
    }
}
