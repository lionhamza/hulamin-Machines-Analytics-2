using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Client;
using Microsoft.PowerBI.Api;
using Microsoft.PowerBI.Api.Models;
using Microsoft.Rest;
using System;

[ApiController]
[Route("api/powerbi")]
public class PowerBiController : ControllerBase
{
    private readonly IConfiguration _config;

    public PowerBiController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet("embed-token")]
    public async Task<IActionResult> GetEmbedToken()
    {
        var tenantId = _config["PowerBI:TenantId"];
        var clientId = _config["PowerBI:ClientId"];
        var clientSecret = _config["PowerBI:ClientSecret"];
        var workspaceId = _config["PowerBI:WorkspaceId"];
        var reportId = _config["PowerBI:ReportId"];

        var authorityUrl = $"https://login.microsoftonline.com/{tenantId}";
        var scopes = new[] { "https://analysis.windows.net/powerbi/api/.default" };

        var app = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(authorityUrl)
            .Build();

        var result = await app.AcquireTokenForClient(scopes).ExecuteAsync();

        var tokenCredentials = new TokenCredentials(result.AccessToken, "Bearer");

        using (var pbiClient = new PowerBIClient(
            new Uri("https://api.powerbi.com/"),
            tokenCredentials))
        {
            var report = await pbiClient.Reports.GetReportInGroupAsync(
                Guid.Parse(workspaceId),
                Guid.Parse(reportId));

            var tokenRequest = new GenerateTokenRequest(accessLevel: "view");

            var embedToken = await pbiClient.Reports.GenerateTokenAsync(
                Guid.Parse(workspaceId),
                Guid.Parse(reportId),
                tokenRequest);

            return Ok(new
            {
                embedUrl = report.EmbedUrl,
                accessToken = embedToken.Token,
                reportId = reportId
            });
        }
    }
}