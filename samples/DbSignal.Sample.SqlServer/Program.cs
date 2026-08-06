using DbSignal;
using DbSignal.SqlServer;
using Microsoft.Data.SqlClient;

// The SQL Server twin of the SQLite sample — with one large difference you can see in the
// output: this one names the rows.
//
//   dotnet run --project samples/DbSignal.Sample.SqlServer
//
// Leave it running, connect to the printed server in SSMS / Azure Data Studio / sqlcmd,
// and INSERT, UPDATE or DELETE. The console reports which rows changed and how.
//
//   --simulate                  write to the database from a second connection
//   --connection "<string>"     use a real server instead of LocalDB

const string DatabaseName = "DbSignalDemo";

var serverConnectionString = ArgValue("--connection")
    ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

var simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);

var dbConnectionString = new SqlConnectionStringBuilder(serverConnectionString)
{
    InitialCatalog = DatabaseName,
}.ConnectionString;

Console.WriteLine("DbSignal — SQL Server sample");
Console.WriteLine(new string('─', 72));

try
{
    await EnsureDatabaseAndTableAsync(serverConnectionString, dbConnectionString);
}
catch (SqlException ex)
{
    Console.WriteLine($"Could not reach SQL Server.{Environment.NewLine}  {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Pass a server explicitly with:");
    Console.WriteLine("  dotnet run --project samples/DbSignal.Sample.SqlServer -- --connection \"Server=.;Integrated Security=true;TrustServerCertificate=true\"");
    return 1;
}

var builder = SqlServerFeed.For(dbConnectionString)
                           .Watch("dbo.Products")
                           .PollEvery(TimeSpan.FromMilliseconds(250));

// Change Tracking is DDL, so the library never enables it behind your back.
var provisioner = builder.Provisioner();
if (!await provisioner.IsProvisionedAsync())
{
    Console.WriteLine("Change Tracking is not enabled yet. Enabling it now:");
    Console.WriteLine();
    Console.WriteLine(provisioner.GetScript());
    await provisioner.EnsureAsync();
    Console.WriteLine("Enabled.");
    Console.WriteLine(new string('─', 72));
}

await using var feed = builder.Build();

var serverForSsms = new SqlConnectionStringBuilder(serverConnectionString).DataSource;

Console.WriteLine($"Server   : {serverForSsms}");
Console.WriteLine($"Database : {DatabaseName}");
Console.WriteLine($"Table    : dbo.Products");
Console.WriteLine($"Provider : {feed.ProviderName}");
Console.WriteLine($"Detail   : {feed.Capabilities.Detail}  (this engine names the changed rows)");
Console.WriteLine($"Durable  : {feed.Capabilities.DurableAcrossRestart}  (a checkpoint survives a restart)");
Console.WriteLine(new string('─', 72));

if (!simulate)
{
    Console.WriteLine("Connect in SSMS / Azure Data Studio to the server above, then run:");
    Console.WriteLine();
    Console.WriteLine($"    USE {DatabaseName};");
    Console.WriteLine("    INSERT INTO dbo.Products (Name) VALUES ('Hello from SSMS');");
    Console.WriteLine("    UPDATE dbo.Products SET Name = 'Renamed' WHERE Id = 1;");
    Console.WriteLine("    DELETE FROM dbo.Products WHERE Id = 1;");
    Console.WriteLine();
    Console.WriteLine("No sqlcmd or SSMS handy? From another terminal:");
    Console.WriteLine($"    dotnet run --project samples/DbSignal.Sample.SqlServer -- --simulate");
}
else
{
    Console.WriteLine("Simulating an external writer every 2s (insert, update, delete in turn).");
}

Console.WriteLine("Ctrl+C to stop.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (simulate)
{
    _ = SimulateExternalWriterAsync(dbConnectionString, cts.Token);
}

var seen = 0;
await foreach (var batch in feed.ReadAsync(Checkpoint.Now, cts.Token))
{
    seen++;
    Console.WriteLine($"[{batch.ObservedUtc.ToLocalTime():HH:mm:ss}] change #{seen} — version {batch.Position}");

    foreach (var table in batch.Tables)
    {
        Console.WriteLine($"          {table.QualifiedName}");
        foreach (var key in table.Keys)
        {
            var id = string.Join(", ", key.Values);
            // Unlike SQLite, we know exactly which row — so re-read only that one.
            // A delete has no row left to read, and that is fine: we still have its key.
            var name = key.Kind == ChangeKind.Delete
                ? "(row deleted)"
                : await ReadProductNameAsync(dbConnectionString, id, cts.Token);

            Console.WriteLine($"            {Symbol(key.Kind),-2} {key.Kind,-7} Id={id,-5} {name}");
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Stopped after detecting {seen} change(s).");
return 0;

static string Symbol(ChangeKind kind) => kind switch
{
    ChangeKind.Insert => "+",
    ChangeKind.Update => "~",
    ChangeKind.Delete => "-",
    _ => "?",
};

string? ArgValue(string name)
{
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static async Task EnsureDatabaseAndTableAsync(string serverConnectionString, string dbConnectionString)
{
    await using (var server = new SqlConnection(serverConnectionString))
    {
        await server.OpenAsync();
        await using var create = server.CreateCommand();
        create.CommandText =
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];";
        await create.ExecuteNonQueryAsync();
    }

    await using var database = new SqlConnection(dbConnectionString);
    await database.OpenAsync();
    await using var table = database.CreateCommand();
    table.CommandText =
        "IF OBJECT_ID(N'dbo.Products') IS NULL " +
        "CREATE TABLE dbo.Products (Id INT IDENTITY(1,1) PRIMARY KEY, Name NVARCHAR(200) NOT NULL);";
    await table.ExecuteNonQueryAsync();
}

static async Task<string> ReadProductNameAsync(string connectionString, string id, CancellationToken ct)
{
    try
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM dbo.Products WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", int.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
        var result = await command.ExecuteScalarAsync(ct);
        return result as string ?? "(not found)";
    }
    catch (Exception)
    {
        return "(unavailable)";
    }
}

// Stands in for the ERP job, the script, or the person in SSMS. A separate connection,
// cycling through all three operations so the output shows each kind.
static async Task SimulateExternalWriterAsync(string connectionString, CancellationToken ct)
{
    var step = 0;

    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();

            switch (step % 3)
            {
                case 0:
                    command.CommandText = "INSERT INTO dbo.Products (Name) VALUES (@name);";
                    command.Parameters.AddWithValue("@name", $"Widget {DateTime.Now:HH:mm:ss}");
                    break;

                case 1:
                    command.CommandText =
                        "UPDATE dbo.Products SET Name = Name + ' (edited)' " +
                        "WHERE Id = (SELECT MAX(Id) FROM dbo.Products);";
                    break;

                default:
                    command.CommandText =
                        "DELETE FROM dbo.Products WHERE Id = (SELECT MIN(Id) FROM dbo.Products);";
                    break;
            }

            await command.ExecuteNonQueryAsync(ct);
            step++;
        }
        catch (SqlException)
        {
            // The demo table may be empty on the first update/delete pass.
            step++;
        }
    }
}
