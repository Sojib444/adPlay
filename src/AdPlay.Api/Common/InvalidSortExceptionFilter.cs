using AdPlay.Api.Common.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AdPlay.Api.Common;

/// <summary>Turns invalid sort input into a 400 with the reason, instead of a 500.</summary>
public sealed class InvalidSortExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not InvalidSortException ex) return;

        var problem = new ValidationProblemDetails(new Dictionary<string, string[]> { ["sortBy"] = [ex.Message] })
        {
            Status = StatusCodes.Status400BadRequest
        };
        context.Result = new BadRequestObjectResult(problem);
        context.ExceptionHandled = true;
    }
}
