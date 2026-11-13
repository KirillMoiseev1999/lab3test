using NUnit.Framework;
using Subscriptions.Api.Domain;

namespace Subscriptions.Tests;

public class AuthRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    [TestCase(0, false)]
    [TestCase(4, false)]
    [TestCase(5, true)]
    [TestCase(6, true)]
    public void LoginBlockStartsAtFiveRecentFailures(long failures, bool blocked) =>
        Assert.That(AuthRules.IsLoginBlocked(failures), Is.EqualTo(blocked));

    [Test]
    public void LoginWindowLooksBackFifteenMinutes() =>
        Assert.That(AuthRules.LoginFailureCutoff(Now),
            Is.EqualTo(new DateTimeOffset(2026, 11, 10, 8, 45, 0, TimeSpan.Zero)));

    [Test]
    public void SessionsAndResetTokensHaveDifferentLifetimes()
    {
        Assert.That(AuthRules.SessionExpiresAt(Now),
            Is.EqualTo(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero)));
        Assert.That(AuthRules.ResetExpiresAt(Now),
            Is.EqualTo(new DateTimeOffset(2026, 11, 10, 9, 15, 0, TimeSpan.Zero)));
    }

    [TestCase(-1, false)]
    [TestCase(0, false)]
    [TestCase(1, true)]
    public void TokenBecomesInvalidAtExpiration(int secondsRemaining, bool active) =>
        Assert.That(AuthRules.IsTokenActive(Now.ToUnixTimeSeconds() + secondsRemaining, Now),
            Is.EqualTo(active));
}
