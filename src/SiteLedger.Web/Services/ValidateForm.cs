using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SiteLedger.Web.Services;

public sealed class ValidateForm : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method) && !context.ModelState.IsValid)
            context.Result = new ViewResult { ViewName = "~/Views/Shared/InvalidRequest.cshtml", StatusCode = 400 };
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
