namespace Subscriptions.Api.Domain;

public static class AuthRules
{
    public static DateTimeOffset LoginFailureCutoff(DateTimeOffset now) => now.AddMinutes(-15);

    public static bool IsLoginBlocked(long recentFailures) => recentFailures >= 5;

    public static DateTimeOffset SessionExpiresAt(DateTimeOffset now) => now.AddMinutes(30);

    public static DateTimeOffset ResetExpiresAt(DateTimeOffset now) => now.AddMinutes(15);

    public static bool IsTokenActive(long expiresAtUnixSeconds, DateTimeOffset now) =>
        expiresAtUnixSeconds > now.ToUnixTimeSeconds();
}
