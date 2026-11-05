using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace ClassHub_API.Controllers;

[ApiController, Authorize, Route("api/cabinet/qr")]
public class QrAccessController(QrAccessService flow) : ControllerBase
{
    public class IssueRequest { [Required, RegularExpression("^(RECEIVE|RETURN)$")] public string Purpose { get; set; } = ""; [MaxLength(32)] public string[]? SelectedUids { get; set; } }
    public class ScanRequest : IssueRequest { [Required, StringLength(64, MinimumLength = 64)] public string Token { get; set; } = ""; }
    private async Task<IActionResult> Run(Func<string, Task<object>> action, bool accepted = false) {
        var user = User.FindFirstValue(ClaimTypes.NameIdentifier); if (user == null) return Unauthorized();
        try { var data = await action(user); return accepted ? Accepted(data) : Ok(data); }
        catch (QrFlowException error) { return StatusCode(error.Status, new { message = error.Message }); }
    }
    [HttpGet("{id:int}/equipment")] public Task<IActionResult> Equipment(int id) => Run(user => flow.EquipmentOptions(id, user));
    [HttpPost("{id:int}/issue")] public Task<IActionResult> Issue(int id, IssueRequest request) => Run(user => flow.Issue(id, user, request.Purpose, request.SelectedUids));
    [HttpPost("{id:int}/preview")] public Task<IActionResult> Preview(int id, ScanRequest request) => Run(user => flow.Preview(id, user, request.Token, request.Purpose));
    [HttpPost("{id:int}/open")] public Task<IActionResult> Open(int id, ScanRequest request) => Run(user => flow.Open(id, user, request.Token, request.Purpose), true);
    [HttpPost("{id:int}/retry")] public Task<IActionResult> Retry(int id) => Run(user => flow.Retry(id, user), true);
}
