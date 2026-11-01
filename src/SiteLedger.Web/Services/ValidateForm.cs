using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SiteLedger.Web.Services;

public sealed class ValidateForm : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        // Typed account actions render their own forms and keep safe input on validation errors.
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method) && !context.ModelState.IsValid &&
            !context.ActionArguments.Values.Any(value => value is Models.AccountForm))
            context.Result = new ViewResult { ViewName = "~/Views/Shared/InvalidRequest.cshtml", StatusCode = 400 };
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
