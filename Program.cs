using System.Data.Common;
using Dapper;
using MySqlConnector;
using Npgsql;

// Toda la configuración llega por VARIABLES DE ENTORNO (en ECS Express: "Environment variables").
//   DB_ENGINE     mysql | postgres                       (por defecto: postgres)
//   DB_HOST       endpoint de la base                    (obligatoria)
//   DB_PORT       puerto                                 (por defecto: 3306 mysql / 5432 postgres)
//   DB_USER       usuario de la base                     (obligatoria)
//   DB_PASSWORD   contraseña                             (obligatoria)
//   DB_NAME       nombre de la base                      (por defecto: el mismo valor de DB_USER)
//   DB_MAX_POOL   conexiones máximas abiertas por la API (por defecto: 2)
//   CORS_ORIGINS  URL(s) del front separadas por coma    (por defecto: cualquier origen)
//
// Si falta una variable, la API igual arranca (para que el despliegue de ECS no se quede trabado)
// y responde en "/" y en /api/* indicando qué variable falta.

var builder = WebApplication.CreateBuilder(args);

var db = DbConfig.FromEnvironment();
builder.Services.AddSingleton(db);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = Environment.GetEnvironmentVariable("CORS_ORIGINS");
    if (string.IsNullOrWhiteSpace(origins) || origins.Trim() == "*")
        p.AllowAnyOrigin();
    else
        p.WithOrigins(origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    p.AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();
app.UseCors();

if (db.Errors.Count > 0)
    app.Logger.LogError("Configuración incompleta: {Errores}", string.Join(" | ", db.Errors));
else
    app.Logger.LogInformation("Configuración: motor={Motor} host={Host}:{Puerto} base={Base} usuario={Usuario} pool={Pool}",
        db.Engine, db.Host, db.Port, db.Database, db.User, db.MaxPool);

// Health check para ECS Express Mode (no toca la BD: Aurora puede estar pausada).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Resumen de la configuración recibida (nunca muestra la contraseña).
app.MapGet("/", () => Results.Ok(new
{
    servicio = "productos-api",
    configuracion = new
    {
        motor = db.Engine, host = db.Host, puerto = db.Port, baseDeDatos = db.Database, usuario = db.User,
        contrasena = string.IsNullOrEmpty(db.Password) ? "FALTA" : "definida", poolMaximo = db.MaxPool,
    },
    errores = db.Errors,
    endpoints = new[] { "GET /api/productos", "GET /api/productos/{id}", "POST /api/productos" },
}));

app.MapGet("/api/productos", (DbConfig cfg) => Safe(cfg, async () =>
{
    await using var cn = await cfg.OpenAsync();
    var items = await cn.QueryAsync<Producto>(
        "SELECT id AS Id, nombre AS Nombre, precio AS Precio, stock AS Stock, creado_en AS CreadoEn FROM productos ORDER BY id DESC");
    return Results.Ok(items);
}));

app.MapGet("/api/productos/{id:int}", (int id, DbConfig cfg) => Safe(cfg, async () =>
{
    await using var cn = await cfg.OpenAsync();
    var item = await cn.QuerySingleOrDefaultAsync<Producto>(
        "SELECT id AS Id, nombre AS Nombre, precio AS Precio, stock AS Stock, creado_en AS CreadoEn FROM productos WHERE id = @id", new { id });
    return item is null ? Results.NotFound(new { error = "Producto no encontrado" }) : Results.Ok(item);
}));

app.MapPost("/api/productos", (NuevoProducto body, DbConfig cfg) => Safe(cfg, async () =>
{
    var nombre = body.Nombre?.Trim() ?? "";
    if (nombre.Length is 0 or > 120) return Results.BadRequest(new { error = "El nombre es obligatorio (máx. 120 caracteres)." });
    if (body.Precio < 0) return Results.BadRequest(new { error = "El precio no puede ser negativo." });
    if (body.Stock < 0) return Results.BadRequest(new { error = "El stock no puede ser negativo." });

    await using var cn = await cfg.OpenAsync();
    var sql = cfg.Engine == "mysql"
        ? "INSERT INTO productos (nombre, precio, stock) VALUES (@nombre, @precio, @stock); SELECT LAST_INSERT_ID();"
        : "INSERT INTO productos (nombre, precio, stock) VALUES (@nombre, @precio, @stock) RETURNING id;";
    var id = await cn.ExecuteScalarAsync<long>(sql, new { nombre, precio = body.Precio, stock = body.Stock });
    var creado = await cn.QuerySingleAsync<Producto>(
        "SELECT id AS Id, nombre AS Nombre, precio AS Precio, stock AS Stock, creado_en AS CreadoEn FROM productos WHERE id = @id", new { id });
    return Results.Created($"/api/productos/{id}", creado);
}));

// Prepara la tabla al arrancar. Si falla (BD pausada o mal configurada), la API igual arranca
// y reintenta en el primer request, para que ECS no reinicie la tarea en bucle.
if (db.Errors.Count == 0)
    _ = Task.Run(async () =>
    {
        try { await db.EnsureSchemaAsync(app.Logger); app.Logger.LogInformation("Esquema listo ({Engine})", db.Engine); }
        catch (Exception e) { app.Logger.LogWarning("No se pudo preparar el esquema todavía; se reintentará: {Error}", e.Message); }
    });

app.Run();

// Devuelve errores de configuración o de conexión como JSON legible (y los deja en los logs de ECS).
async Task<IResult> Safe(DbConfig cfg, Func<Task<IResult>> action)
{
    if (cfg.Errors.Count > 0)
        return Results.Json(new { error = "Configuración incompleta de la API", detalle = cfg.Errors }, statusCode: 500);
    try { return await action(); }
    catch (DbException e)
    {
        app.Logger.LogError("Error de base de datos: {Error}", e.Message);
        return Results.Json(new { error = "No se pudo completar la operación en la base de datos", detalle = e.Message }, statusCode: 503);
    }
}

record Producto(long Id, string Nombre, decimal Precio, int Stock, DateTime CreadoEn);
record NuevoProducto(string? Nombre, decimal Precio, int Stock);

sealed class DbConfig
{
    public string Engine { get; private init; } = "postgres";
    public string Host { get; private init; } = "";
    public int Port { get; private init; }
    public string Database { get; private init; } = "";
    public string User { get; private init; } = "";
    public string Password { get; private init; } = "";
    public int MaxPool { get; private init; } = 2;
    public List<string> Errors { get; } = new();
    private volatile bool _schemaReady;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static DbConfig FromEnvironment()
    {
        static string Env(string k) => (Environment.GetEnvironmentVariable(k) ?? "").Trim();
        var engine = Env("DB_ENGINE").ToLowerInvariant();
        if (engine is "" or "postgresql" or "pg") engine = "postgres";
        var user = Env("DB_USER");
        var cfg = new DbConfig
        {
            Engine = engine,
            Host = Env("DB_HOST"),
            Port = int.TryParse(Env("DB_PORT"), out var p) ? p : engine == "mysql" ? 3306 : 5432,
            User = user,
            Password = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "",
            Database = Env("DB_NAME") is { Length: > 0 } n ? n : user,   // convención: base = usuario
            MaxPool = int.TryParse(Env("DB_MAX_POOL"), out var mp) && mp > 0 ? mp : 2,
        };
        if (engine is not ("postgres" or "mysql")) cfg.Errors.Add($"DB_ENGINE debe ser 'mysql' o 'postgres' (se recibió '{engine}')");
        if (cfg.Host.Length == 0) cfg.Errors.Add("Falta la variable DB_HOST");
        if (cfg.User.Length == 0) cfg.Errors.Add("Falta la variable DB_USER");
        if (cfg.Password.Length == 0) cfg.Errors.Add("Falta la variable DB_PASSWORD");
        return cfg;
    }

    // Aurora Serverless v2 con mínimo 0 ACU tarda unos segundos en despertar: timeout amplio.
    // Pool chico y conexiones inactivas que se cierran en 60 s: hay cientos de alumnos en el mismo clúster.
    private string ConnectionString(string database) => Engine == "mysql"
        ? new MySqlConnectionStringBuilder { Server = Host, Port = (uint)Port, Database = database, UserID = User, Password = Password, SslMode = MySqlSslMode.Required,
            ConnectionTimeout = 60, MaximumPoolSize = (uint)MaxPool, ConnectionIdleTimeout = 60 }.ConnectionString
        : new NpgsqlConnectionStringBuilder { Host = Host, Port = Port, Database = database, Username = User, Password = Password, SslMode = SslMode.Require,
            Timeout = 60, MaxPoolSize = MaxPool, ConnectionIdleLifetime = 60 }.ConnectionString;

    private async Task<DbConnection> RawOpenAsync(string database)
    {
        DbConnection cn = Engine == "mysql" ? new MySqlConnection(ConnectionString(database)) : new NpgsqlConnection(ConnectionString(database));
        await cn.OpenAsync();
        return cn;
    }

    public async Task<DbConnection> OpenAsync()
    {
        if (!_schemaReady) await EnsureSchemaAsync(null);
        return await RawOpenAsync(Database);
    }

    public async Task EnsureSchemaAsync(ILogger? log)
    {
        if (_schemaReady) return;
        await _lock.WaitAsync();
        try
        {
            if (_schemaReady) return;
            // Intenta crear la base si no existe. Si el usuario no tiene permiso (lo normal cuando el
            // administrador ya la creó), se ignora y se sigue con la tabla.
            try
            {
                if (Engine == "mysql")
                {
                    await using var admin = await RawOpenAsync("");
                    await admin.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{Database.Replace("`", "")}`");
                }
                else
                {
                    await using var admin = await RawOpenAsync("postgres");
                    var exists = await admin.ExecuteScalarAsync<int?>("SELECT 1 FROM pg_database WHERE datname = @d", new { d = Database });
                    if (exists is null) await admin.ExecuteAsync($"CREATE DATABASE \"{Database.Replace("\"", "")}\"");
                }
            }
            catch (DbException e) { log?.LogInformation("No se creó la base (puede que ya exista o no haya permiso): {Error}", e.Message); }

            await using var cn = await RawOpenAsync(Database);
            await cn.ExecuteAsync(Engine == "mysql"
                ? """
                  CREATE TABLE IF NOT EXISTS productos (
                    id BIGINT AUTO_INCREMENT PRIMARY KEY,
                    nombre VARCHAR(120) NOT NULL,
                    precio DECIMAL(10,2) NOT NULL,
                    stock INT NOT NULL DEFAULT 0,
                    creado_en DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                  )
                  """
                : """
                  CREATE TABLE IF NOT EXISTS productos (
                    id BIGSERIAL PRIMARY KEY,
                    nombre VARCHAR(120) NOT NULL,
                    precio NUMERIC(10,2) NOT NULL,
                    stock INT NOT NULL DEFAULT 0,
                    creado_en TIMESTAMP NOT NULL DEFAULT now()
                  )
                  """);
            _schemaReady = true;
        }
        finally { _lock.Release(); }
    }
}
