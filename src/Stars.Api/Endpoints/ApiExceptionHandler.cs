using Microsoft.AspNetCore.Diagnostics;
using Stars.Infrastructure.Registry;

namespace Stars.Api.Endpoints;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            KeyNotFoundException => StatusCodes.Status404NotFound,
            LinkConflictException => StatusCodes.Status409Conflict,
            ReviewValidationException => StatusCodes.Status400BadRequest,
            _ => 0,
        };
        if (status == 0)
        {
            return false;
        }

        await Results.Problem(statusCode: status, detail: exception.Message).ExecuteAsync(context);
        return true;
    }
}
