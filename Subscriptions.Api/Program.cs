using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.Data.Sqlite;
using Subscriptions.Api.Domain;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var databasePath = Path.Combine(
    app.Environment.ContentRootPath, "subscriptions.db");

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    ForeignKeys = true
}.ToString();

using (var connection = new SqliteConnection(connectionString))
{
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY NOT NULL,
            email TEXT NOT NULL,
            normalized_email TEXT NOT NULL UNIQUE,
            password_hash TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS subscriptions (
            id TEXT PRIMARY KEY NOT NULL,
            owner_id TEXT NOT NULL REFERENCES users(id),
            name TEXT NOT NULL,
            category TEXT NOT NULL,
            price_minor INTEGER NOT NULL CHECK (price_minor > 0),
            currency TEXT NOT NULL CHECK (currency IN ('RUB', 'USD', 'EUR')),
            frequency TEXT NOT NULL CHECK (frequency IN ('monthly', 'yearly')),
            next_payment_date TEXT NOT NULL,
            billing_day INTEGER NOT NULL CHECK (billing_day BETWEEN 1 AND 31),
            billing_month INTEGER NOT NULL CHECK (billing_month BETWEEN 1 AND 12),
            status TEXT NOT NULL CHECK (status IN ('active', 'paused', 'archived'))
        );
        CREATE TABLE IF NOT EXISTS sessions (
            token_hash TEXT PRIMARY KEY NOT NULL,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            expires_at_utc INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS login_failures (
            normalized_email TEXT NOT NULL,
            occurred_at_utc INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_login_failures_email_time
            ON login_failures (normalized_email, occurred_at_utc);

        CREATE TABLE IF NOT EXISTS password_resets (
            token_hash TEXT PRIMARY KEY NOT NULL,
            user_id TEXT NOT NULL UNIQUE REFERENCES users(id) ON DELETE CASCADE,
            expires_at_utc INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS payments (
            id TEXT PRIMARY KEY NOT NULL,
            subscription_id TEXT NOT NULL REFERENCES subscriptions(id),
            owner_id TEXT NOT NULL REFERENCES users(id),
            amount_minor INTEGER NOT NULL CHECK (amount_minor > 0),
            currency TEXT NOT NULL CHECK (currency IN ('RUB', 'USD', 'EUR')),
            paid_on TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            created_at_utc INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_payments_owner_key_time
            ON payments (owner_id, idempotency_key, created_at_utc);
        """;

    command.ExecuteNonQuery();

}

app.MapGet("/api/v1/health/ready", async () =>
{
    try
    {
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync();

        return Results.Ok(new { status = "ready" });
    }
    catch (SqliteException ex)
    {
        app.Logger.LogWarning(ex, "SQLite readiness check failed");

        return Results.Json(
            new { status = "not_ready", reason = "database_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status503ServiceUnavailable);



var passwordHasher = new PasswordHasher<string>();

//===================================================================== РЕГИСТРАЦИЯ
app.MapPost("/api/v1/auth/register",
    async (RegisterRequest request, HttpContext context) =>
    {
        var email = request.Email?.Trim() ?? "";
        var password = request.Password;

        if (email.Length > 254 ||
            !MailAddress.TryCreate(email, out var address) ||
            address.Address != email ||
            password is null ||
            password.Length is < 12 or > 128)
        {
            return Results.BadRequest(new
            {
                code = "validation_error",
                message = "Укажи корректный email и пароль длиной от 12 до 128 символов.",
                requestId = context.TraceIdentifier
            });
        }

        var id = Guid.NewGuid().ToString("D");
        var passwordHash = passwordHasher.HashPassword(id, password);

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(context.RequestAborted);

        using var command = connection.CreateCommand();
        command.CommandText = """
        INSERT INTO users (id, email, normalized_email, password_hash)
        VALUES ($id, $email, $normalizedEmail, $passwordHash)
        ON CONFLICT (normalized_email) DO NOTHING;
        """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$normalizedEmail", email.ToLowerInvariant());
        command.Parameters.AddWithValue("$passwordHash", passwordHash);

        var inserted = await command.ExecuteNonQueryAsync(context.RequestAborted);
        if (inserted == 0)
        {
            return Results.Conflict(new
            {
                code = "email_already_registered",
                message = "Пользователь с таким email уже зарегистрирован.",
                requestId = context.TraceIdentifier
            });
        }

        return Results.Json(new { id, email },
            statusCode: StatusCodes.Status201Created);
    })
.Produces(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status409Conflict);

//===================================================================== ВХОД
app.MapPost("/api/v1/auth/login",
    (RegisterRequest request, HttpContext context) =>
    {
        var email = request.Email?.Trim() ?? "";
        var password = request.Password;

        IResult Error(int status, string code, string message) =>
            Results.Json(new
            {
                code,
                message,
                requestId = context.TraceIdentifier
            }, statusCode: status);

        if (email.Length > 254 ||
            !MailAddress.TryCreate(email, out var address) ||
            address.Address != email ||
            password is null ||
            password.Length is < 12 or > 128)
        {
            return Error(400, "validation_error",
                "Укажи корректный email и пароль длиной от 12 до 128 символов.");
        }

        var normalizedEmail = email.ToLowerInvariant();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = """
        SELECT COUNT(*) FROM login_failures
        WHERE normalized_email = $email AND occurred_at_utc > $cutoff;
        """;
        count.Parameters.AddWithValue("$email", normalizedEmail);
        count.Parameters.AddWithValue("$cutoff", AuthRules.LoginFailureCutoff(now).ToUnixTimeSeconds());

        if (AuthRules.IsLoginBlocked(Convert.ToInt64(count.ExecuteScalar())))
        {
            return Error(429, "too_many_login_attempts",
                "Слишком много попыток входа. Попробуй позже.");
        }

        string? userId = null;
        string? passwordHash = null;

        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
            SELECT id, password_hash FROM users WHERE normalized_email = $email;
            """;
            lookup.Parameters.AddWithValue("$email", normalizedEmail);

            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                userId = reader.GetString(0);
                passwordHash = reader.GetString(1);
            }
        }

        if (userId is null || passwordHash is null ||
            passwordHasher.VerifyHashedPassword(userId, passwordHash, password)
                == PasswordVerificationResult.Failed)
        {
            using var failure = connection.CreateCommand();
            failure.Transaction = transaction;
            failure.CommandText = """
            INSERT INTO login_failures (normalized_email, occurred_at_utc)
            VALUES ($email, $now);
            """;
            failure.Parameters.AddWithValue("$email", normalizedEmail);
            failure.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            failure.ExecuteNonQuery();

            transaction.Commit();
            return Error(401, "invalid_credentials", "Неверный email или пароль.");
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token)));
        var expiresAt = AuthRules.SessionExpiresAt(now);

        using var session = connection.CreateCommand();
        session.Transaction = transaction;
        session.CommandText = """
        INSERT INTO sessions (token_hash, user_id, expires_at_utc)
        VALUES ($hash, $userId, $expiresAt);
        """;
        session.Parameters.AddWithValue("$hash", tokenHash);
        session.Parameters.AddWithValue("$userId", userId);
        session.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeSeconds());
        session.ExecuteNonQuery();

        transaction.Commit();
        return Results.Ok(new { accessToken = token, tokenType = "Bearer", expiresAt });
    })
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status429TooManyRequests);


//===================================================================== ВЫХОД
app.MapPost("/api/v1/auth/logout", (HttpContext context) =>
{
    var tokenHash = GetBearerTokenHash(context);

    if (tokenHash is null)
    {
        return InvalidSession(context);
    }

    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = """
        DELETE FROM sessions
        WHERE token_hash = $hash AND expires_at_utc > $now;
        """;
    command.Parameters.AddWithValue("$hash", tokenHash);
    command.Parameters.AddWithValue("$now",
        DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    if (command.ExecuteNonQuery() == 0)
    {
        return InvalidSession(context);
    }

    return Results.NoContent();
})
.Produces(StatusCodes.Status204NoContent)
.Produces(StatusCodes.Status401Unauthorized);

static string? GetBearerTokenHash(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();

    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var token = authorization["Bearer ".Length..].Trim();
    if (token.Length != 64 || !token.All(Uri.IsHexDigit))
    {
        return null;
    }

    return Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(token)));
}

static IResult InvalidSession(HttpContext context)
{
    context.Response.Headers.WWWAuthenticate = "Bearer";

    return Results.Json(new
    {
        code = "invalid_session",
        message = "Для запроса нужен действующий токен.",
        requestId = context.TraceIdentifier
    }, statusCode: StatusCodes.Status401Unauthorized);
}

//===================================================================== ПОДПИСКИ
app.MapGet("/api/v1/subscriptions",
    (HttpContext context, string? status, string? category,
        string? page, string? pageSize) =>
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var userId = GetSessionUserId(connection, context);
        if (userId is null)
        {
            return InvalidSession(context);
        }

        if (!int.TryParse(page ?? "1", out var pageNumber) ||
            pageNumber < 1 ||
            !int.TryParse(pageSize ?? "20", out var size) ||
            size is < 1 or > 100 ||
            (status is not null && status is not ("active" or "paused" or "archived")))
        {
            return Results.BadRequest(new
            {
                code = "validation_error",
                message = "Проверь status, page и pageSize.",
                requestId = context.TraceIdentifier
            });
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT id, name, category, price_minor, currency,
               frequency, next_payment_date, billing_day, status
        FROM subscriptions
        WHERE owner_id = $ownerId
          AND ($status IS NULL OR status = $status)
          AND ($category IS NULL OR category = $category)
        ORDER BY next_payment_date, id
        LIMIT $limit OFFSET $offset;
        """;
        command.Parameters.AddWithValue("$ownerId", userId);
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", size);
        command.Parameters.AddWithValue("$offset", (pageNumber - 1L) * size);

        var items = new List<object>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            items.Add(new
            {
                id = reader.GetString(0),
                name = reader.GetString(1),
                category = reader.GetString(2),
                price = (reader.GetInt64(3) / 100m).ToString(
                    "F2", System.Globalization.CultureInfo.InvariantCulture),
                currency = reader.GetString(4),
                frequency = reader.GetString(5),
                nextPaymentDate = reader.GetString(6),
                billingDay = reader.GetInt32(7),
                status = reader.GetString(8)
            });
        }

        return Results.Ok(new { items });
    })
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

static string? GetSessionUserId(SqliteConnection connection, HttpContext context)
{
    var tokenHash = GetBearerTokenHash(context);
    if (tokenHash is null)
    {
        return null;
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT user_id, expires_at_utc FROM sessions WHERE token_hash = $hash;
        """;
    command.Parameters.AddWithValue("$hash", tokenHash);
    using var reader = command.ExecuteReader();
    return reader.Read() && AuthRules.IsTokenActive(reader.GetInt64(1), DateTimeOffset.UtcNow)
        ? reader.GetString(0)
        : null;
}

//===================================================================== ПОДПИСКИ

app.MapPost("/api/v1/subscriptions",
    (CreateSubscriptionRequest request, HttpContext context) =>
        SaveSubscription(null, request, context))
    .Produces(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized);

app.MapPut("/api/v1/subscriptions/{id}",
    (string id, CreateSubscriptionRequest request, HttpContext context) =>
        SaveSubscription(id, request, context))
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status404NotFound);

IResult SaveSubscription(
    string? subscriptionId, CreateSubscriptionRequest request, HttpContext context)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var userId = GetSessionUserId(connection, context);
    if (userId is null)
    {
        return InvalidSession(context);
    }

    var name = request.Name?.Trim() ?? "";
    var category = request.Category?.Trim() ?? "";
    var priceText = request.Price ?? "";

    if (name.Length is < 1 or > 100 ||
        category.Length is < 1 or > 50 ||
        !InputRules.TryReadPositiveMoney(priceText, out var price) ||
        request.Currency is not ("RUB" or "USD" or "EUR") ||
        request.Frequency is not ("monthly" or "yearly") ||
        request.Status is not ("active" or "paused" or "archived") ||
        !DateOnly.TryParseExact(request.NextPaymentDate, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var nextDate))
    {
        return Results.BadRequest(new
        {
            code = "validation_error",
            message = "Проверь поля подписки, формат цены и дату платежа.",
            requestId = context.TraceIdentifier
        });
    }

    var id = subscriptionId ?? Guid.NewGuid().ToString("D");
    var nextPaymentDate = nextDate.ToString(
        "yyyy-MM-dd", CultureInfo.InvariantCulture);

    using var command = connection.CreateCommand();
    command.CommandText = subscriptionId is null
        ? """
          INSERT INTO subscriptions (
              id, owner_id, name, category, price_minor, currency,
              frequency, next_payment_date, billing_day, billing_month, status
          ) VALUES (
              $id, $ownerId, $name, $category, $priceMinor, $currency,
              $frequency, $nextDate, $billingDay, $billingMonth, $status
          );
          """
        : """
          UPDATE subscriptions
          SET name = $name, category = $category, price_minor = $priceMinor,
              currency = $currency, frequency = $frequency,
              next_payment_date = $nextDate, billing_day = $billingDay,
              billing_month = $billingMonth, status = $status
          WHERE id = $id AND owner_id = $ownerId;
          """;

    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$ownerId", userId);
    command.Parameters.AddWithValue("$name", name);
    command.Parameters.AddWithValue("$category", category);
    command.Parameters.AddWithValue("$priceMinor", (long)(price * 100m));
    command.Parameters.AddWithValue("$currency", request.Currency);
    command.Parameters.AddWithValue("$frequency", request.Frequency);
    command.Parameters.AddWithValue("$nextDate", nextPaymentDate);
    command.Parameters.AddWithValue("$billingDay", nextDate.Day);
    command.Parameters.AddWithValue("$billingMonth", nextDate.Month);
    command.Parameters.AddWithValue("$status", request.Status);
    if (command.ExecuteNonQuery() == 0)
    {
        return Results.NotFound(new
        {
            code = "subscription_not_found",
            message = "Подписка не найдена.",
            requestId = context.TraceIdentifier
        });
    }

    return Results.Json(new
    {
        id,
        name,
        category,
        price = price.ToString("F2", CultureInfo.InvariantCulture),
        currency = request.Currency,
        frequency = request.Frequency,
        nextPaymentDate,
        billingDay = nextDate.Day,
        status = request.Status
    }, statusCode: subscriptionId is null
        ? StatusCodes.Status201Created
        : StatusCodes.Status200OK);
}

//===================================================================== ПОЛУЧЕНИЕ ПОДПИСКИ ПО ID
app.MapGet("/api/v1/subscriptions/{id}", (string id, HttpContext context) =>
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var userId = GetSessionUserId(connection, context);
    if (userId is null)
    {
        return InvalidSession(context);
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT id, name, category, price_minor, currency,
               frequency, next_payment_date, billing_day, status
        FROM subscriptions
        WHERE id = $id AND owner_id = $ownerId;
        """;
    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$ownerId", userId);

    using var reader = command.ExecuteReader();
    if (!reader.Read())
    {
        return Results.NotFound(new
        {
            code = "subscription_not_found",
            message = "Подписка не найдена.",
            requestId = context.TraceIdentifier
        });
    }

    return Results.Ok(new
    {
        id = reader.GetString(0),
        name = reader.GetString(1),
        category = reader.GetString(2),
        price = (reader.GetInt64(3) / 100m).ToString(
            "F2", CultureInfo.InvariantCulture),
        currency = reader.GetString(4),
        frequency = reader.GetString(5),
        nextPaymentDate = reader.GetString(6),
        billingDay = reader.GetInt32(7),
        status = reader.GetString(8)
    });
})
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);



app.MapDelete("/api/v1/subscriptions/{id}", (string id, HttpContext context) =>
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var userId = GetSessionUserId(connection, context);
    if (userId is null)
    {
        return InvalidSession(context);
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE subscriptions
        SET status = 'archived'
        WHERE id = $id AND owner_id = $ownerId;
        """;
    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$ownerId", userId);

    if (command.ExecuteNonQuery() == 0)
    {
        return Results.NotFound(new
        {
            code = "subscription_not_found",
            message = "Подписка не найдена.",
            requestId = context.TraceIdentifier
        });
    }

    return Results.NoContent();
})
.Produces(StatusCodes.Status204NoContent)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

app.MapPost("/api/v1/subscriptions/{id}/payments",
    (string id, CreatePaymentRequest request, HttpContext context) =>
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var userId = GetSessionUserId(connection, context);
        if (userId is null)
        {
            return InvalidSession(context);
        }

        IResult Error(int status, string code, string message) =>
            Results.Json(new { code, message, requestId = context.TraceIdentifier },
                statusCode: status);

        var keys = context.Request.Headers["Idempotency-Key"];
        var key = keys.ToString().Trim();
        var amountText = request.Amount ?? "";
        if (keys.Count != 1 || key.Length is < 1 or > 128 ||
            !InputRules.TryReadPositiveMoney(amountText, out var amount) ||
            request.Currency is not ("RUB" or "USD" or "EUR") ||
            !DateOnly.TryParseExact(request.PaidOn, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var paidDate) ||
            paidDate > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            return Error(400, "validation_error",
                "Проверь сумму, валюту, дату платежа и заголовок Idempotency-Key.");
        }

        var paidOn = paidDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var amountMinor = (long)(amount * 100m);
        IResult CreatedPayment(string paymentId) => Results.Json(new
        {
            id = paymentId,
            subscriptionId = id,
            amount = amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency,
            paidOn
        }, statusCode: StatusCodes.Status201Created);

        // Serialize the lookup and insertion to prevent duplicate concurrent writes.
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow;
        using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = """
            SELECT id, subscription_id, amount_minor, currency, paid_on
            FROM payments
            WHERE owner_id = $ownerId AND idempotency_key = $key
              AND created_at_utc > $cutoff
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;
            previous.Parameters.AddWithValue("$ownerId", userId);
            previous.Parameters.AddWithValue("$key", key);
            previous.Parameters.AddWithValue("$cutoff", now.AddHours(-24).ToUnixTimeSeconds());

            using var reader = previous.ExecuteReader();
            if (reader.Read())
            {
                var previousPayment = new PaymentDetails(
                    reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4));
                var requestedPayment = new PaymentDetails(id, amountMinor, request.Currency, paidOn);
                if (!PaymentRules.IsSamePayment(previousPayment, requestedPayment))
                {
                    return Error(409, "idempotency_conflict",
                        "Этот ключ уже использован для другого платежа.");
                }

                return CreatedPayment(reader.GetString(0));
            }
        }

        using var subscription = connection.CreateCommand();
        subscription.Transaction = transaction;
        subscription.CommandText = """
        SELECT currency FROM subscriptions WHERE id = $id AND owner_id = $ownerId;
        """;
        subscription.Parameters.AddWithValue("$id", id);
        subscription.Parameters.AddWithValue("$ownerId", userId);
        if (subscription.ExecuteScalar() is not string currency)
        {
            return Error(404, "subscription_not_found", "Подписка не найдена.");
        }

        if (currency != request.Currency)
        {
            return Error(400, "validation_error", "Валюта платежа должна совпадать с валютой подписки.");
        }

        var paymentId = Guid.NewGuid().ToString("D");
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
        INSERT INTO payments (
            id, subscription_id, owner_id, amount_minor, currency, paid_on,
            idempotency_key, created_at_utc
        ) VALUES ($id, $subscriptionId, $ownerId, $amount, $currency,
            $paidOn, $key, $now);
        """;
        insert.Parameters.AddWithValue("$id", paymentId);
        insert.Parameters.AddWithValue("$subscriptionId", id);
        insert.Parameters.AddWithValue("$ownerId", userId);
        insert.Parameters.AddWithValue("$amount", amountMinor);
        insert.Parameters.AddWithValue("$currency", currency);
        insert.Parameters.AddWithValue("$paidOn", paidOn);
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        insert.ExecuteNonQuery();

        transaction.Commit();
        return CreatedPayment(paymentId);
    })
.Produces(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict);

app.MapGet("/api/v1/analytics/expenses",
    (string? from, string? to, HttpContext context) =>
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var userId = GetSessionUserId(connection, context);
        if (userId is null)
        {
            return InvalidSession(context);
        }

        const string dateFormat = "yyyy-MM-dd";
        if (!InputRules.TryReadPeriod(from, to, out var start, out var end))
        {
            return Results.BadRequest(new
            {
                code = "validation_error",
                message = "Укажи даты from и to в формате yyyy-MM-dd. Дата from должна быть раньше to.",
                requestId = context.TraceIdentifier
            });
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT s.category, p.currency, SUM(p.amount_minor)
        FROM payments AS p
        JOIN subscriptions AS s ON s.id = p.subscription_id AND s.owner_id = p.owner_id
        WHERE p.owner_id = $ownerId AND p.paid_on >= $from AND p.paid_on < $to
        GROUP BY s.category, p.currency
        ORDER BY s.category, p.currency;
        """;
        command.Parameters.AddWithValue("$ownerId", userId);
        command.Parameters.AddWithValue("$from", start.ToString(dateFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", end.ToString(dateFormat, CultureInfo.InvariantCulture));

        var categoryTotals = new List<(string Category, string Currency, decimal Amount)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            categoryTotals.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2) / 100m));
        }

        return Results.Ok(new
        {
            byCurrency = PaymentRules.TotalByCurrency(
                categoryTotals.Select(row => (row.Currency, row.Amount))),
            byCategory = categoryTotals.Select(row => new
            {
                category = row.Category,
                currency = row.Currency,
                amount = row.Amount.ToString("F2", CultureInfo.InvariantCulture)
            })
        });
    })
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/api/v1/calendar", (string? from, string? to, HttpContext context) =>
    GetPlannedPayments(from, to, context, forecast: false))
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/api/v1/analytics/forecast", (string? from, string? to, HttpContext context) =>
    GetPlannedPayments(from, to, context, forecast: true))
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized);

IResult GetPlannedPayments(string? from, string? to, HttpContext context, bool forecast)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var userId = GetSessionUserId(connection, context);
    if (userId is null)
    {
        return InvalidSession(context);
    }

    const string dateFormat = "yyyy-MM-dd";
    if (!InputRules.TryReadPeriod(from, to, out var start, out var end, maxDays: 366))
    {
        return Results.BadRequest(new
        {
            code = "validation_error",
            message = "Укажи период в формате yyyy-MM-dd длительностью от 1 до 366 дней.",
            requestId = context.TraceIdentifier
        });
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT id, name, price_minor, currency, frequency,
               next_payment_date, billing_day, billing_month
        FROM subscriptions
        WHERE owner_id = $ownerId AND status = 'active' AND next_payment_date < $to;
        """;
    command.Parameters.AddWithValue("$ownerId", userId);
    command.Parameters.AddWithValue("$to", end.ToString(dateFormat, CultureInfo.InvariantCulture));

    var payments = new List<PlannedPayment>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        var id = reader.GetString(0);
        var name = reader.GetString(1);
        var amount = reader.GetInt64(2) / 100m;
        var currency = reader.GetString(3);
        var yearly = reader.GetString(4) == "yearly";
        var first = DateOnly.ParseExact(reader.GetString(5), dateFormat, CultureInfo.InvariantCulture);

        foreach (var date in PaymentSchedule.Calculate(first, reader.GetInt32(6),
            reader.GetInt32(7), yearly, start, end))
        {
            payments.Add(new PlannedPayment(id, name, date, amount, currency));
        }
    }

    if (forecast)
    {
        return Results.Ok(new
        {
            byCurrency = PaymentRules.TotalByCurrency(
                payments.Select(payment => (payment.Currency, payment.Amount)))
        });
    }

    return Results.Ok(new
    {
        items = payments.OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.SubscriptionId, StringComparer.Ordinal)
            .Select(payment => new
            {
                payment.SubscriptionId,
                payment.Name,
                date = payment.Date.ToString(dateFormat, CultureInfo.InvariantCulture),
                amount = payment.Amount.ToString("F2", CultureInfo.InvariantCulture),
                payment.Currency
            })
    });
}

app.MapGet("/api/v1/payments",
    (string? from, string? to, string? subscriptionId, string? page,
        string? pageSize, HttpContext context) =>
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var userId = GetSessionUserId(connection, context);
        if (userId is null)
        {
            return InvalidSession(context);
        }

        const string dateFormat = "yyyy-MM-dd";
        if (!InputRules.TryReadPeriod(from, to, out var start, out var end) ||
            !int.TryParse(page ?? "1", out var pageNumber) || pageNumber < 1 ||
            !int.TryParse(pageSize ?? "20", out var size) || size is < 1 or > 100)
        {
            return Results.BadRequest(new
            {
                code = "validation_error",
                message = "Проверь даты from и to, номер страницы и размер страницы.",
                requestId = context.TraceIdentifier
            });
        }

        if (subscriptionId is not null)
        {
            using var subscription = connection.CreateCommand();
            subscription.CommandText = """
            SELECT 1 FROM subscriptions WHERE id = $id AND owner_id = $ownerId;
            """;
            subscription.Parameters.AddWithValue("$id", subscriptionId);
            subscription.Parameters.AddWithValue("$ownerId", userId);
            if (subscription.ExecuteScalar() is null)
            {
                return Results.NotFound(new
                {
                    code = "subscription_not_found",
                    message = "Подписка не найдена.",
                    requestId = context.TraceIdentifier
                });
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT id, subscription_id, amount_minor, currency, paid_on
        FROM payments
        WHERE owner_id = $ownerId AND paid_on >= $from AND paid_on < $to
          AND ($subscriptionId IS NULL OR subscription_id = $subscriptionId)
        ORDER BY paid_on DESC, id
        LIMIT $size OFFSET $offset;
        """;
        command.Parameters.AddWithValue("$ownerId", userId);
        command.Parameters.AddWithValue("$from", start.ToString(dateFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", end.ToString(dateFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$subscriptionId", (object?)subscriptionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$offset", (pageNumber - 1L) * size);

        var items = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new
            {
                id = reader.GetString(0),
                subscriptionId = reader.GetString(1),
                amount = (reader.GetInt64(2) / 100m).ToString("F2", CultureInfo.InvariantCulture),
                currency = reader.GetString(3),
                paidOn = reader.GetString(4)
            });
        }

        return Results.Ok(new { items });
    })
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

app.MapPost("/api/v1/auth/password-reset/request", (ResetRequest request, HttpContext context) =>
{
    var email = request.Email?.Trim() ?? "";
    if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
        return ResetError(context, "Укажи корректный email.");

    if (!app.Environment.IsDevelopment())
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var transaction = connection.BeginTransaction(deferred: false);
    using var lookup = connection.CreateCommand();
    lookup.Transaction = transaction;
    lookup.CommandText = "SELECT id FROM users WHERE normalized_email = $email;";
    lookup.Parameters.AddWithValue("$email", email.ToLowerInvariant());
    if (lookup.ExecuteScalar() is string userId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO password_resets (token_hash, user_id, expires_at_utc)
            VALUES ($hash, $userId, $expires)
            ON CONFLICT (user_id) DO UPDATE SET token_hash = excluded.token_hash,
                expires_at_utc = excluded.expires_at_utc;
            """;
        save.Parameters.AddWithValue("$hash", HashResetToken(token));
        save.Parameters.AddWithValue("$userId", userId);
        save.Parameters.AddWithValue("$expires", AuthRules.ResetExpiresAt(DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        save.ExecuteNonQuery();

        var pickup = Path.Combine(app.Environment.ContentRootPath, "dev-mail");
        Directory.CreateDirectory(pickup);
        File.WriteAllText(Path.Combine(pickup, $"{userId}.txt"),
            $"To: {email}{Environment.NewLine}Reset token: {token}{Environment.NewLine}");
    }
    transaction.Commit();
    return Results.Accepted();
})
.Produces(202).Produces(400).Produces(503);

app.MapPost("/api/v1/auth/password-reset/confirm", (ResetConfirm request, HttpContext context) =>
{
    var token = request.Token;
    var newPassword = request.NewPassword;
    if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit) ||
        newPassword is null || newPassword.Length is < 12 or > 128)
        return ResetError(context, "Проверь токен и длину нового пароля.");

    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var transaction = connection.BeginTransaction(deferred: false);
    using var consume = connection.CreateCommand();
    consume.Transaction = transaction;
    consume.CommandText = """
        DELETE FROM password_resets WHERE token_hash = $hash AND expires_at_utc > $now
        RETURNING user_id;
        """;
    consume.Parameters.AddWithValue("$hash", HashResetToken(token));
    consume.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    if (consume.ExecuteScalar() is not string userId)
        return ResetError(context, "Токен недействителен или истёк.");

    using var update = connection.CreateCommand();
    update.Transaction = transaction;
    update.CommandText = """
        UPDATE users SET password_hash = $passwordHash WHERE id = $userId;
        DELETE FROM sessions WHERE user_id = $userId;
        DELETE FROM login_failures WHERE normalized_email =
            (SELECT normalized_email FROM users WHERE id = $userId);
        """;
    update.Parameters.AddWithValue("$passwordHash", passwordHasher.HashPassword(userId, newPassword));
    update.Parameters.AddWithValue("$userId", userId);
    update.ExecuteNonQuery();
    transaction.Commit();
    return Results.NoContent();
})
.Produces(204).Produces(400);

static string HashResetToken(string token) =>
    Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

static IResult ResetError(HttpContext context, string message) =>
    Results.BadRequest(new { code = "validation_error", message, requestId = context.TraceIdentifier });

app.Run();

record RegisterRequest(string? Email, string? Password);
record ResetRequest(string? Email);
record ResetConfirm(string? Token, string? NewPassword);
record CreatePaymentRequest(string? Amount, string? Currency, string? PaidOn);
record PlannedPayment(string SubscriptionId, string Name, DateOnly Date, decimal Amount, string Currency);
record CreateSubscriptionRequest(
    string? Name,
    string? Category,
    string? Price,
    string? Currency,
    string? Frequency,
    string? NextPaymentDate,
    string? Status);
