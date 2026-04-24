using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ✅ Add DbContext
builder.Services.AddDbContext<HulaminDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ✅ THIS IS WHAT YOU WERE MISSING
builder.Services.AddControllers();

var app = builder.Build();

// ✅ THIS MAPS YOUR ProductionController
app.MapControllers();

app.Run();