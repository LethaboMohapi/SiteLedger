using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SiteLedger.Api.Projects;
using SiteLedger.Core;

namespace SiteLedger.Api.Controllers;

public sealed record CreateProjectRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, StringLength(200)] string ClientName,
    [Required, StringLength(300)] string SiteAddress,
    decimal Budget,
    DateOnly StartDate, DateOnly? EndDate, ProjectStatus Status);

[ApiController]
[Route("api/v1/projects")]
public sealed class ProjectsController(IProjectService projects) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.ReadProjects)]
    public async Task<IActionResult> List([FromQuery, Range(1, 100000)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20, CancellationToken cancellationToken = default)
        => Ok(await projects.ListAsync(User.IsInRole(Roles.TenantAdmin), page, pageSize, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Permissions.ManageProjects)]
    public async Task<IActionResult> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (request.StartDate == default || request.EndDate < request.StartDate || !Enum.IsDefined(request.Status) ||
            request.Budget < 0 || request.Budget > 9999999999999999.99m || decimal.Round(request.Budget, 2) != request.Budget ||
            string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ClientName) || string.IsNullOrWhiteSpace(request.SiteAddress))
            return ValidationProblem("Project details are invalid.");
        try
        {
            var result = await projects.CreateAsync(new NewProject(request.Name, request.ClientName, request.SiteAddress,
                request.Budget, request.StartDate, request.EndDate, request.Status), cancellationToken);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (PlanLimitException ex) { return Problem(statusCode: StatusCodes.Status409Conflict, title: "Plan limit reached", detail: ex.Message); }
    }
}
