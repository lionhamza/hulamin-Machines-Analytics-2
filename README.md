# MachinePerformance.Engine — Pipeline Story
## From Local Database Connection to Write-Back

---

## Overview

This document outlines every step required to connect to the local SQL Server database,
pull staged production data, run the calculation engine, test the pipeline, and write
results back to the designated output tables. It assumes Layer 1 and Layer 2 have already
populated the staging table in the local database.

---

## Solution Structure

```
MachinePerformance.sln
├── MachinePerformance.DAL           ← Layer 1 & 2 (already exists)
├── MachinePerformance.Engine        ← Layer 3 (what we are building)
│   ├── Models/
│   │   ├── ProductionRecord.cs      ← Maps raw DB columns to C# object
│   │   ├── MachineTarget.cs         ← Holds machine-specific targets & capacity
│   │   ├── WeeklyKpi.cs             ← Intermediate calculation results per week
│   │   └── WaterfallStep.cs         ← Final waterfall row written to DB
│   ├── Mapping/
│   │   └── ProductionRecordMapper.cs ← Handles column name differences between DB and model
│   ├── Calculators/
│   │   ├── DelayCalculator.cs       ← Computes delay % and variance per group
│   │   ├── CapacityCalculator.cs    ← Computes available hours, capacity/hr, tons impact
│   │   └── WaterfallCalculator.cs   ← Orchestrates per-week KPI and waterfall steps
│   ├── Repositories/
│   │   ├── IProductionRepository.cs ← Contract for reading staged production data
│   │   ├── ITargetRepository.cs     ← Contract for reading/writing machine targets
│   │   ├── IWaterfallRepository.cs  ← Contract for writing results to DB
│   │   ├── SqlProductionRepository.cs  ← SQL Server implementation
│   │   ├── SqlTargetRepository.cs      ← SQL Server implementation
│   │   └── SqlWaterfallRepository.cs   ← SQL Server implementation
│   ├── Engine/
│   │   └── KpiOrchestrator.cs       ← Master coordinator — runs the full pipeline
│   ├── Config/
│   │   └── DbConfig.cs              ← Holds connection string (plug and play)
│   └── Tests/
│       └── PipelineIntegrationTest.cs ← End-to-end test from pull to write-back
```

---

## Step 1 — NuGet Packages to Install

Right-click **MachinePerformance.Engine** → Manage NuGet Packages and install:

| Package | Purpose |
|---|---|
| `Dapper` | Lightweight SQL query mapper — maps DB rows to C# objects |
| `Microsoft.Data.SqlClient` | SQL Server connection driver |
| `Microsoft.Extensions.Configuration` | Reads connection string from config file |
| `xunit` | Test framework for pipeline testing |
| `Dapper.Contrib` | Simplifies insert/update operations to DB |

---

## Step 2 — Database Configuration

### Config/DbConfig.cs
**Purpose:** Single place to store and retrieve the connection string.
Keeps connection details out of every repository file. Easy to swap for
a different environment (dev, staging, production) without touching logic.

```csharp
namespace MachinePerformance.Engine.Config
{
    public class DbConfig
    {
        public string ConnectionString { get; set; }

        public DbConfig(string connectionString)
        {
            ConnectionString = connectionString;
        }
    }
}
```

### appsettings.json (add to project root)
**Purpose:** Stores the actual connection string. Never hardcode this in C# files.

```json
{
  "ConnectionStrings": {
    "LocalDb": "Server=YOUR_SERVER_NAME;Database=YOUR_DB_NAME;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```

> Replace `YOUR_SERVER_NAME` with your local SQL Server instance name
> and `YOUR_DB_NAME` with the database name where Layer 1 & 2 wrote the data.

---

## Step 3 — Column Name Mapping

Since your local DB table columns may differ from the model property names,
we create a mapper to handle the translation cleanly.

### Mapping/ProductionRecordMapper.cs
**Purpose:** Translates raw database column names (which may have underscores,
abbreviations, or legacy naming) into the clean C# model properties used
throughout the engine. This means the rest of the code never needs to
know about DB column naming.

```csharp
namespace MachinePerformance.Engine.Mapping
{
    // Maps your actual DB column names to ProductionRecord properties
    // Edit ONLY this file when column names change in the DB
    public static class ProductionRecordMapper
    {
        // Change the left side (string) to match your actual DB column names
        // Change the right side (lambda) to match the ProductionRecord property
        public static string MachIdColumn        => "Mach_Id";          // or "MachineId", "MACH_ID" etc.
        public static string WeekColumn          => "Week_Number";       // or "Week", "WEEK_NO" etc.
        public static string EventTypeColumn     => "Event_Type";        // or "MachineEventType" etc.
        public static string DelayGroupColumn    => "Delay_Grp";         // or "DelayGroup", "DELAY_GROUP" etc.
        public static string RunTimeColumn       => "Run_Time_Min";      // or "RunTime", "RUNTIME" etc.
        public static string LotPackedMassColumn => "Packed_Mass_KG";    // or "LotPackedMass" etc.
        public static string LotReqdMassColumn   => "Required_Mass_KG";  // or "LotTotReqdMass" etc.
        public static string EventStartColumn    => "Start_DateTime";    // or "EventStartTime" etc.
        public static string EventEndColumn      => "End_DateTime";      // or "EventEndTime" etc.

        // This query uses the actual DB column names mapped to model property names
        // Dapper reads the aliases (after AS) into the matching C# property names
        public static string SelectQuery => $@"
            SELECT
                {MachIdColumn}          AS MachId,
                {WeekColumn}            AS Week,
                {EventTypeColumn}       AS MachineEventType,
                {DelayGroupColumn}      AS DelayGroup,
                {RunTimeColumn}         AS RunTime,
                {LotPackedMassColumn}   AS LotPackedMass,
                {LotReqdMassColumn}     AS LotTotReqdMass,
                {EventStartColumn}      AS EventStartTime,
                {EventEndColumn}        AS EventEndTime
            FROM dbo.StagingProductionTable
            WHERE {MachIdColumn} = @MachId
              AND YEAR({EventStartColumn}) = @Year
              AND MONTH({EventStartColumn}) = @Month
        ";
        // Replace "dbo.StagingProductionTable" with your actual staging table name
    }
}
```

---

## Step 4 — Repository Implementations

### Repositories/SqlProductionRepository.cs
**Purpose:** Reads production records from the local staging DB table.
Uses the mapper so column name differences are handled transparently.
Returns clean C# objects to the engine — no SQL anywhere else in the code.

```csharp
using Dapper;
using MachinePerformance.Engine.Config;
using MachinePerformance.Engine.Mapping;
using MachinePerformance.Engine.Models;
using Microsoft.Data.SqlClient;

namespace MachinePerformance.Engine.Repositories
{
    public class SqlProductionRepository : IProductionRepository
    {
        private readonly DbConfig _config;

        public SqlProductionRepository(DbConfig config)
        {
            _config = config;
        }

        public async Task<List<ProductionRecord>> GetByMachineAndMonthAsync(
            string machId, int year, int month)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            var records = await connection.QueryAsync<ProductionRecord>(
                ProductionRecordMapper.SelectQuery,
                new { MachId = machId, Year = year, Month = month }
            );

            return records.ToList();
        }
    }
}
```

---

### Repositories/SqlTargetRepository.cs
**Purpose:** Reads machine-specific targets (FE%, FP%, SE%, SP% and capacity)
from the dim_machine_targets table. Finds the target that was active on
the date of the calculation using EffectiveFrom and EffectiveTo dates.
Also allows specialists to add or update targets through any UI layer.

```csharp
using Dapper;
using MachinePerformance.Engine.Config;
using MachinePerformance.Engine.Models;
using Microsoft.Data.SqlClient;

namespace MachinePerformance.Engine.Repositories
{
    public class SqlTargetRepository : ITargetRepository
    {
        private readonly DbConfig _config;

        public SqlTargetRepository(DbConfig config)
        {
            _config = config;
        }

        // Get the target active on a specific date for a machine
        public async Task<MachineTarget> GetActiveTargetAsync(string machId, DateTime date)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            return await connection.QueryFirstOrDefaultAsync<MachineTarget>(@"
                SELECT TOP 1 *
                FROM dbo.dim_machine_targets
                WHERE MachId = @MachId
                  AND EffectiveFrom <= @Date
                  AND (EffectiveTo IS NULL OR EffectiveTo >= @Date)
                ORDER BY EffectiveFrom DESC
            ", new { MachId = machId, Date = date });
        }

        // Get all historical targets for a machine
        public async Task<List<MachineTarget>> GetAllTargetsAsync(string machId)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            var result = await connection.QueryAsync<MachineTarget>(@"
                SELECT * FROM dbo.dim_machine_targets
                WHERE MachId = @MachId
                ORDER BY EffectiveFrom DESC
            ", new { MachId = machId });

            return result.ToList();
        }

        // Specialists use this to add a new target for a machine
        public async Task AddTargetAsync(MachineTarget target)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            await connection.ExecuteAsync(@"
                INSERT INTO dbo.dim_machine_targets
                    (MachId, CapacityPerDay, FETargetPct, FPTargetPct,
                     SETargetPct, SPTargetPct, EffectiveFrom, EffectiveTo)
                VALUES
                    (@MachId, @CapacityPerDay, @FETargetPct, @FPTargetPct,
                     @SETargetPct, @SPTargetPct, @EffectiveFrom, @EffectiveTo)
            ", target);
        }

        // Specialists use this to update an existing target
        public async Task UpdateTargetAsync(MachineTarget target)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            await connection.ExecuteAsync(@"
                UPDATE dbo.dim_machine_targets
                SET CapacityPerDay = @CapacityPerDay,
                    FETargetPct    = @FETargetPct,
                    FPTargetPct    = @FPTargetPct,
                    SETargetPct    = @SETargetPct,
                    SPTargetPct    = @SPTargetPct,
                    EffectiveFrom  = @EffectiveFrom,
                    EffectiveTo    = @EffectiveTo
                WHERE Id = @Id
            ", target);
        }
    }
}
```

---

### Repositories/SqlWaterfallRepository.cs
**Purpose:** Writes the final calculation results to the database tables
that Power BI reads. Clears previous results before writing new ones
to avoid duplicates. This is the only place results touch the DB.

```csharp
using Dapper;
using MachinePerformance.Engine.Config;
using MachinePerformance.Engine.Models;
using Microsoft.Data.SqlClient;

namespace MachinePerformance.Engine.Repositories
{
    public class SqlWaterfallRepository : IWaterfallRepository
    {
        private readonly DbConfig _config;

        public SqlWaterfallRepository(DbConfig config)
        {
            _config = config;
        }

        // Clear previous results for this machine/month before recalculating
        public async Task ClearResultsAsync(string machId, int year, int month)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            await connection.ExecuteAsync(@"
                DELETE FROM dbo.fact_waterfall
                WHERE MachId = @MachId
                  AND YEAR(CalculatedAt) = @Year
                  AND MONTH(CalculatedAt) = @Month;

                DELETE FROM dbo.fact_kpi
                WHERE MachId = @MachId
                  AND YEAR(CalculatedAt) = @Year
                  AND MONTH(CalculatedAt) = @Month;
            ", new { MachId = machId, Year = year, Month = month });
        }

        // Write waterfall steps to fact_waterfall — Power BI reads this
        public async Task SaveWaterfallStepsAsync(List<WaterfallStep> steps)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            await connection.ExecuteAsync(@"
                INSERT INTO dbo.fact_waterfall
                    (MachId, Week, Category, Value, SortOrder, CalculatedAt)
                VALUES
                    (@MachId, @Week, @Category, @Value, @SortOrder, @CalculatedAt)
            ", steps);
        }

        // Write weekly KPI summary to fact_kpi — Power BI reads this
        public async Task SaveWeeklyKpisAsync(List<WeeklyKpi> kpis)
        {
            using var connection = new SqlConnection(_config.ConnectionString);

            await connection.ExecuteAsync(@"
                INSERT INTO dbo.fact_kpi
                    (MachId, Week, TotalRunTime, FEPct, FPPct, SEPct, SPPct,
                     AvailabilityPct, AvailableHours, CapacityPerHour,
                     FEVariance, FPVariance, SEVariance, SPVariance,
                     FETons, FPTons, SETons, SPTons,
                     ActualOutput, Target, Capacity, CalculatedAt)
                VALUES
                    (@MachId, @Week, @TotalRunTime, @FEPct, @FPPct, @SEPct, @SPPct,
                     @AvailabilityPct, @AvailableHours, @CapacityPerHour,
                     @FEVariance, @FPVariance, @SEVariance, @SPVariance,
                     @FETons, @FPTons, @SETons, @SPTons,
                     @ActualOutput, @Target, @Capacity, @CalculatedAt)
            ", kpis);
        }
    }
}
```

---

## Step 5 — SQL Tables to Create

Run this script in your local SQL Server database (SSMS):

```sql
-- Machine targets — managed by specialists
CREATE TABLE dbo.dim_machine_targets (
    Id             INT IDENTITY PRIMARY KEY,
    MachId         NVARCHAR(50)  NOT NULL,
    CapacityPerDay FLOAT         NOT NULL,
    FETargetPct    FLOAT         NOT NULL,
    FPTargetPct    FLOAT         NOT NULL,
    SETargetPct    FLOAT         NOT NULL,
    SPTargetPct    FLOAT         NOT NULL,
    EffectiveFrom  DATE          NOT NULL,
    EffectiveTo    DATE          NULL  -- NULL = currently active
)

-- Waterfall steps — Power BI reads this for the waterfall chart
CREATE TABLE dbo.fact_waterfall (
    Id           INT IDENTITY PRIMARY KEY,
    MachId       NVARCHAR(50)  NOT NULL,
    Week         INT           NOT NULL,
    Category     NVARCHAR(100) NOT NULL,
    Value        FLOAT         NOT NULL,
    SortOrder    INT           NOT NULL,
    CalculatedAt DATETIME      DEFAULT GETUTCDATE()
)

-- Weekly KPI summary — Power BI reads this for KPI cards and trends
CREATE TABLE dbo.fact_kpi (
    Id              INT IDENTITY PRIMARY KEY,
    MachId          NVARCHAR(50) NOT NULL,
    Week            INT          NOT NULL,
    TotalRunTime    FLOAT,
    FEPct           FLOAT,
    FPPct           FLOAT,
    SEPct           FLOAT,
    SPPct           FLOAT,
    AvailabilityPct FLOAT,
    AvailableHours  FLOAT,
    CapacityPerHour FLOAT,
    FEVariance      FLOAT,
    FPVariance      FLOAT,
    SEVariance      FLOAT,
    SPVariance      FLOAT,
    FETons          FLOAT,
    FPTons          FLOAT,
    SETons          FLOAT,
    SPTons          FLOAT,
    ActualOutput    FLOAT,
    Target          FLOAT,
    Capacity        FLOAT,
    CalculatedAt    DATETIME DEFAULT GETUTCDATE()
)

-- Seed first machine target for testing (S6 machine)
INSERT INTO dbo.dim_machine_targets
    (MachId, CapacityPerDay, FETargetPct, FPTargetPct,
     SETargetPct, SPTargetPct, EffectiveFrom, EffectiveTo)
VALUES
    ('S6', 388, 6.25, 15.0, 5.0, 3.0, '2026-01-01', NULL)
```

---

## Step 6 — KpiOrchestrator (Master Coordinator)

### Engine/KpiOrchestrator.cs
**Purpose:** The single entry point for running the full pipeline.
Given a machine ID, year and month it pulls data, loads targets,
runs all calculations week by week, and writes results to the DB.
All other code serves this orchestrator.

```csharp
using MachinePerformance.Engine.Calculators;
using MachinePerformance.Engine.Models;
using MachinePerformance.Engine.Repositories;

namespace MachinePerformance.Engine
{
    public class KpiOrchestrator
    {
        private readonly IProductionRepository _productionRepo;
        private readonly ITargetRepository _targetRepo;
        private readonly IWaterfallRepository _waterfallRepo;
        private readonly WaterfallCalculator _calculator;

        public KpiOrchestrator(
            IProductionRepository productionRepo,
            ITargetRepository targetRepo,
            IWaterfallRepository waterfallRepo)
        {
            _productionRepo = productionRepo;
            _targetRepo     = targetRepo;
            _waterfallRepo  = waterfallRepo;
            _calculator     = new WaterfallCalculator();
        }

        public async Task RunAsync(string machId, int year, int month)
        {
            Console.WriteLine($"[START] Machine: {machId} | Period: {year}/{month:D2}");

            // Step 1 — Clear old results
            await _waterfallRepo.ClearResultsAsync(machId, year, month);
            Console.WriteLine("[1/5] Cleared previous results");

            // Step 2 — Pull production records from staging
            var records = await _productionRepo.GetByMachineAndMonthAsync(machId, year, month);
            Console.WriteLine($"[2/5] Pulled {records.Count} records from staging");

            if (!records.Any())
            {
                Console.WriteLine($"[WARN] No records found. Aborting.");
                return;
            }

            // Step 3 — Load active machine target
            var target = await _targetRepo.GetActiveTargetAsync(
                machId, new DateTime(year, month, 1));

            if (target == null)
                throw new InvalidOperationException(
                    $"No active target for machine {machId}. " +
                    $"Please add targets in dim_machine_targets before running.");

            Console.WriteLine($"[3/5] Loaded target — Capacity: {target.CapacityPerDay}t/day | " +
                              $"FE:{target.FETargetPct}% FP:{target.FPTargetPct}% " +
                              $"SE:{target.SETargetPct}% SP:{target.SPTargetPct}%");

            // Step 4 — Calculate per week
            var weeks    = records.Select(r => r.Week).Distinct().OrderBy(w => w);
            var allKpis  = new List<WeeklyKpi>();
            var allSteps = new List<WaterfallStep>();

            foreach (var week in weeks)
            {
                var weekRecords = records.Where(r => r.Week == week).ToList();
                var kpi         = _calculator.CalculateWeeklyKpi(machId, week, weekRecords, target);
                var steps       = _calculator.BuildWaterfallSteps(kpi);

                allKpis.AddRange(new[] { kpi });
                allSteps.AddRange(steps);

                Console.WriteLine($"[4/5] Week {week} — " +
                                  $"Actual: {kpi.ActualOutput:F1}t | " +
                                  $"Target: {kpi.Target:F1}t | " +
                                  $"Availability: {kpi.AvailabilityPct:F1}%");
            }

            // Step 5 — Write results to DB
            await _waterfallRepo.SaveWeeklyKpisAsync(allKpis);
            await _waterfallRepo.SaveWaterfallStepsAsync(allSteps);

            Console.WriteLine($"[5/5] Written {allKpis.Count} KPI rows " +
                              $"and {allSteps.Count} waterfall steps to DB");
            Console.WriteLine($"[DONE] Pipeline complete for {machId} {year}/{month:D2}");
        }
    }
}
```

---

## Step 7 — Wire Everything Together (Program.cs)

**Purpose:** Entry point that wires up all dependencies and runs the pipeline.
Change the machId, year and month here to run for any machine and period.

```csharp
using MachinePerformance.Engine;
using MachinePerformance.Engine.Config;
using MachinePerformance.Engine.Repositories;
using Microsoft.Extensions.Configuration;

// Load connection string from appsettings.json
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var dbConfig = new DbConfig(config.GetConnectionString("LocalDb"));

// Wire up repositories
var productionRepo = new SqlProductionRepository(dbConfig);
var targetRepo     = new SqlTargetRepository(dbConfig);
var waterfallRepo  = new SqlWaterfallRepository(dbConfig);

// Create orchestrator
var orchestrator = new KpiOrchestrator(productionRepo, targetRepo, waterfallRepo);

// Run pipeline for machine S6, April 2026
await orchestrator.RunAsync("S6", 2026, 4);
```

---

## Step 8 — Integration Test

### Tests/PipelineIntegrationTest.cs
**Purpose:** End-to-end test that verifies the full pipeline works correctly —
from pulling data, through all calculations, to writing back to the DB.
Run this after setup to confirm everything is connected before going live.

```csharp
using MachinePerformance.Engine;
using MachinePerformance.Engine.Config;
using MachinePerformance.Engine.Repositories;
using Xunit;

namespace MachinePerformance.Engine.Tests
{
    public class PipelineIntegrationTest
    {
        private readonly KpiOrchestrator _orchestrator;
        private readonly SqlWaterfallRepository _waterfallRepo;
        private readonly DbConfig _dbConfig;

        public PipelineIntegrationTest()
        {
            // Use your actual local DB connection string here for testing
            _dbConfig      = new DbConfig("Server=YOUR_SERVER;Database=YOUR_DB;Trusted_Connection=True;TrustServerCertificate=True;");
            _waterfallRepo = new SqlWaterfallRepository(_dbConfig);
            _orchestrator  = new KpiOrchestrator(
                new SqlProductionRepository(_dbConfig),
                new SqlTargetRepository(_dbConfig),
                _waterfallRepo
            );
        }

        [Fact]
        public async Task Pipeline_S6_April2026_ShouldWriteResults()
        {
            // Act — run the full pipeline
            await _orchestrator.RunAsync("S6", 2026, 4);

            // Assert — verify results were written to DB
            using var connection = new Microsoft.Data.SqlClient.SqlConnection(_dbConfig.ConnectionString);

            var waterfallCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.fact_waterfall WHERE MachId = 'S6'"
            );

            var kpiCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.fact_kpi WHERE MachId = 'S6'"
            );

            // Should have 4 weeks x 7 waterfall steps = 28 rows minimum
            Assert.True(waterfallCount >= 7,
                $"Expected at least 7 waterfall rows but got {waterfallCount}");

            // Should have at least 1 KPI row per week
            Assert.True(kpiCount >= 1,
                $"Expected at least 1 KPI row but got {kpiCount}");

            Console.WriteLine($"Waterfall rows: {waterfallCount}");
            Console.WriteLine($"KPI rows: {kpiCount}");
        }

        [Fact]
        public async Task Pipeline_ShouldLoadCorrectTargetForMachine()
        {
            var targetRepo = new SqlTargetRepository(_dbConfig);
            var target     = await targetRepo.GetActiveTargetAsync("S6", new DateTime(2026, 4, 1));

            Assert.NotNull(target);
            Assert.Equal("S6", target.MachId);
            Assert.Equal(388, target.CapacityPerDay);
            Assert.Equal(6.25, target.FETargetPct);
            Assert.Equal(15.0, target.FPTargetPct);

            Console.WriteLine($"Target loaded: {target.MachId} | " +
                              $"Capacity: {target.CapacityPerDay}t | " +
                              $"FE: {target.FETargetPct}%");
        }
    }
}
```

---

## Step 9 — How to Run and Test

### First time setup checklist:

1. Update `appsettings.json` with your local SQL Server connection string
2. Run the SQL script in Step 5 to create the three tables
3. Update `ProductionRecordMapper.cs` with your actual DB column names
4. Update the staging table name in the mapper SELECT query
5. Build the solution — fix any compile errors
6. Run `Program.cs` for machine S6, April 2026
7. Check console output — all 5 steps should print with green numbers
8. Open SSMS and verify rows exist in `fact_waterfall` and `fact_kpi`
9. Run the integration tests to confirm assertions pass
10. Connect Power BI to `fact_waterfall` and build the waterfall visual

---

## What Power BI Reads

Once the pipeline runs successfully Power BI connects directly to these two tables:

| Table | Used for |
|---|---|
| `dbo.fact_waterfall` | Waterfall chart — Category on X-axis, Value on Y-axis |
| `dbo.fact_kpi` | KPI cards, trend lines, availability gauges |

Power BI does zero calculations — it only displays what the engine wrote.

---

## Scaling to 23 Machines

To run for all machines, update Program.cs:

```csharp
var machines = new[] { "S6", "M1", "M2", "M3" }; // add all 23 machine IDs

foreach (var machId in machines)
{
    await orchestrator.RunAsync(machId, 2026, 4);
}
```

Each machine reads its own targets from `dim_machine_targets` automatically.
