using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
public class ProductionController : ControllerBase
{
    private readonly HulaminDbContext _context;

    public ProductionController(HulaminDbContext context)
    {
        _context = context;
    }

    [HttpGet("machines")]
public async Task<IActionResult> GetMachines()
{
    var machines = await _context.Machines
        .Select(m => new
        {
            m.machine_id,
            m.machine_name
        })
        .ToListAsync();

    return Ok(machines);
}

    [HttpGet("waterfall")]
    public async Task<IActionResult> GetWaterfall(
        string machineId,
        DateTime startDate,
        DateTime endDate)
    {
        // Ensure PostgreSQL UTC compatibility
        startDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        endDate   = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        var data = await _context.Production
            .Where(p => p.machine_id == machineId
                     && p.date >= startDate
                     && p.date <= endDate)
            .ToListAsync();

        if (!data.Any())
            return Ok(new { message = "No data for selected range" });

        // Sum all components
        var baseCap = data.Sum(x => x.base_capacity);
        var fe      = data.Sum(x => x.fe);
        var fp      = data.Sum(x => x.fp);
        var pa      = data.Sum(x => x.pa);
        var se      = data.Sum(x => x.fe);
        var sp      = data.Sum(x => x.sp);

        // TRUE waterfall output (derived, not trusted from DB)
        var output =
            baseCap
            - fe
            - fp
            - pa
            - se
            - sp;

        var result = new[]
        {
            new { Step = "Base Capacity", Value = baseCap },
            new { Step = "FE Loss",        Value = -fe },
            new { Step = "FP Loss",        Value = -fp },
            new { Step = "PA Loss",        Value = -pa },
            new { Step = "SE Loss",        Value = -se },
            new { Step = "SP Loss",        Value = -sp },
            new { Step = "Output",         Value = output }
        };

        return Ok(result);
    }
}