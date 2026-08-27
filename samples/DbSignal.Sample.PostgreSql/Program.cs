using DbSignal;
using DbSignal.PostgreSql;
using Npgsql;

// The third sample, and the one that shows something neither of the others can: the row's
// values BEFORE and AFTER the change — from an application that did not make it.
//
//   dotnet run --project samples/DbSignal.Sample.PostgreSql
//
// Leave it running, connect with psql / pgAdmin / DBeaver, and UPDATE a row. The console
// prints what it used to say and what it says now.
//
//   --simulate                  write to the database from a second connection
//   --connection "<string>"     use a real server instead of localhost
//
// Requires wal_level = logical, which needs a server restart to change. The sample says so
// plainly rather than hanging on a stream that will never carry anything.

const string DatabaseName = "dbsignal_demo";

var serverConnectionString = ArgValue("--connection")
    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres";

var simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);

var dbConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString)
{
    Database = DatabaseName,
}.ConnectionString;

Console.WriteLine("DbSignal — PostgreSQL sample");
Console.WriteLine(new string('─', 72));

try
{
    await EnsureDatabaseAndTableAsync(serverConnectionString, dbConnectionString);
}
catch (NpgsqlException ex)
{
    Console.WriteLine($"Could not reach PostgreSQL.{Environment.NewLine}  {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Pass a server explicitly with:");
    Console.WriteLine("  dotnet run --project samples/DbSignal.Sample.PostgreSql -- --connection \"Host=localhost;Username=postgres;Password=secret;Database=postgres\"");
    return 1;
}

var builder = PostgreSqlFeed.For(dbConnectionString).Watch("public.products");

// A publication, a replica identity and a replication slot are all DDL, so the library
// never creates them behind your back.
var provisioner = builder.Provisioner();
if (!await provisioner.IsProvisionedAsync())
{
    Console.WriteLine("Logical replication is not set up for this table yet:");
    Console.WriteLine();
    Console.WriteLine(provisioner.GetScript());

    try
    {
        await provisioner.EnsureAsync();
        Console.WriteLine("Done.");
    }
    catch (ProvisioningRequiredException ex)
    {
        // wal_level is the one thing a running server cannot be talked into.
        Console.WriteLine(ex.Message);
        return 1;
    }

    Console.WriteLine(new string('─', 72));
}

await using var feed = builder.Build();

Console.WriteLine($"Server   : {new NpgsqlConnectionStringBuilder(serverConnectionString).Host}");
Console.WriteLine($"Database : {DatabaseName}");
Console.WriteLine($"Table    : public.products");
Console.WriteLine($"Provider : {feed.ProviderName}");
Console.WriteLine($"Detail   : {feed.Capabilities.Detail}  (this engine carries the values, not just the keys)");
Console.WriteLine($"Downtime : {feed.Capabilities.SurvivesDowntime}  (the slot holds the WAL while you are away)");
Console.WriteLine(new string('─', 72));

if (!simulate)
{
    Console.WriteLine("Connect with psql / pgAdmin / DBeaver, then run:");
    Console.WriteLine();
    Console.WriteLine("    INSERT INTO public.products (name) VALUES ('Hello from psql');");
    Console.WriteLine("    UPDATE public.products SET name = 'Renamed' WHERE id = 1;");
    Console.WriteLine("    DELETE FROM public.products WHERE id = 1;");
    Console.WriteLine();
    Console.WriteLine("No client handy? From another terminal:");
    Console.WriteLine("    dotnet run --project samples/DbSignal.Sample.PostgreSql -- --simulate");
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

try
{
    await foreach (var batch in feed.ReadAsync(Checkpoint.Now, cts.Token))
    {
        seen++;

        // One batch is one transaction. Three inserts committed together print together.
        Console.WriteLine($"[{batch.ObservedUtc.ToLocalTime():HH:mm:ss}] transaction #{seen} — LSN {batch.Position}");

        foreach (var table in batch.Tables)
        {
            Console.WriteLine($"          {table.QualifiedName}");

            foreach (var row in table.Rows)
            {
                // No re-read needed: unlike the other two providers, the values are here.
                // The old row is available even for a DELETE, where there is nothing left
                // in the table to go and look at.
                var before = Describe(row.Before);
                var after = Describe(row.After);

                Console.WriteLine($"            {Symbol(row.Kind),-2} {row.Kind,-7} {before} -> {after}");
            }
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}

Console.WriteLine();
Console.WriteLine($"Stopped after detecting {seen} transaction(s).");
Console.WriteLine();
Console.WriteLine("The slot is still holding WAL for this consumer. Drop it when you are done:");
Console.WriteLine("    SELECT pg_drop_replication_slot('dbsignal_slot');");
return 0;

static string Describe(IReadOnlyDictionary<string, object?>? row) =>
    row is null
        ? "(none)"
        : $"[{string.Join(", ", row.Select(pair => $"{pair.Key}={pair.Value}"))}]";

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
    await using (var server = new NpgsqlConnection(serverConnectionString))
    {
        await server.OpenAsync();

        await using var exists = server.CreateCommand();
        exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name;";
        _ = exists.Parameters.AddWithValue("@name", DatabaseName);

        if (await exists.ExecuteScalarAsync() is null)
        {
            // CREATE DATABASE cannot be parameterised, and the name is a constant above.
            await using var create = server.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{DatabaseName}\";";
            _ = await create.ExecuteNonQueryAsync();
        }
    }

    await using var database = new NpgsqlConnection(dbConnectionString);
    await database.OpenAsync();
    await using var table = database.CreateCommand();
    table.CommandText =
        "CREATE TABLE IF NOT EXISTS public.products " +
        "(id SERIAL PRIMARY KEY, name TEXT NOT NULL);";
    _ = await table.ExecuteNonQueryAsync();
}

// Stands in for the ERP job, the script, or the person in psql. A separate connection,
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
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();

            switch (step % 3)
            {
                case 0:
                    command.CommandText = "INSERT INTO public.products (name) VALUES (@name);";
                    _ = command.Parameters.AddWithValue("@name", $"Widget {DateTime.Now:HH:mm:ss}");
                    break;

                case 1:
                    command.CommandText =
                        "UPDATE public.products SET name = name || ' (edited)' " +
                        "WHERE id = (SELECT MAX(id) FROM public.products);";
                    break;

                default:
                    command.CommandText =
                        "DELETE FROM public.products WHERE id = (SELECT MIN(id) FROM public.products);";
                    break;
            }

            _ = await command.ExecuteNonQueryAsync(ct);
            step++;
        }
        catch (NpgsqlException)
        {
            // The demo table may be empty on the first update/delete pass.
            step++;
        }
    }
}
