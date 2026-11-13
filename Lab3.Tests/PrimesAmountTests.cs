using System;
using System.Collections.Concurrent;
using System.Linq;
using Moq;
using NUnit.Framework;
using async_tests;

namespace Lab3.Tests;

[TestFixture]
public class PrimesAmountTests
{
    [Test]
    public void CountPrimesAmount_ReturnsPrimes_AndClearsCacheAfterChecks()
    {
        var calls = new ConcurrentQueue<int>();
        int callsAtCleanup = -1;

        var service = new Mock<PrimeService>(MockBehavior.Strict);

        service.Setup(s => s.CheckIsPrime(It.IsAny<int>()))
            .Callback<int>(number => calls.Enqueue(number))
            .Returns<int>(number => number is 2 or 3 or 5 or 7);

        service.Setup(s => s.ClearCache())
            .Callback(() => callsAtCleanup = calls.Count);

        var sut = new PrimesAmount(10);

        var result = sut.CountPrimesAmount(service.Object);

        Assert.Multiple(() =>
        {
            // Порядок элементов при параллельном выполнении не фиксирован.
            Assert.That(result.ToArray(),
                Is.EquivalentTo(new[] { 2, 3, 5, 7 }));

            // Каждое число полуинтервала [2, 10) проверено ровно один раз.
            Assert.That(calls.ToArray(),
                Is.EquivalentTo(Enumerable.Range(2, 8)));

            // Очистка выполнена после завершения всех проверок.
            Assert.That(callsAtCleanup, Is.EqualTo(8));
        });

        service.Verify(s => s.ClearCache(), Times.Once());
    }

    [TestCase(1, 0)]
    [TestCase(2, 1)]
    public void CountPrimesAmount_EmptyRange_ClearsCacheAccordingToBorder(
    int border, int expectedCleanupCalls)
    {
        var service = new Mock<PrimeService>(MockBehavior.Strict);
        service.Setup(s => s.ClearCache());

        var sut = new PrimesAmount(border);

        var result = sut.CountPrimesAmount(service.Object);

        Assert.That(result, Is.Empty);

        service.Verify(s => s.CheckIsPrime(It.IsAny<int>()),
            Times.Never());

        service.Verify(s => s.ClearCache(),
            Times.Exactly(expectedCleanupCalls));

        service.VerifyNoOtherCalls();
    }

    [Test]
    public void CountPrimesAmount_CheckThrows_ReturnsEmptyAndClearsCache()
    {
        var service = new Mock<PrimeService>(MockBehavior.Strict);

        service.Setup(s => s.CheckIsPrime(It.IsAny<int>()))
            .Throws(new InvalidOperationException("Ошибка проверки"));

        service.Setup(s => s.ClearCache());

        var sut = new PrimesAmount(10);

        var result = sut.CountPrimesAmount(service.Object);

        Assert.That(result, Is.Empty);
        service.Verify(s => s.CheckIsPrime(It.IsAny<int>()),
            Times.AtLeastOnce());
        service.Verify(s => s.ClearCache(), Times.Once());
    }

    [Test]
    public void CountPrimesAmount_ClearCacheThrows_PropagatesException()
    {
        var service = new Mock<PrimeService>(MockBehavior.Strict);

        service.Setup(s => s.CheckIsPrime(It.IsAny<int>()))
            .Returns(false);

        service.Setup(s => s.ClearCache())
            .Throws(new InvalidOperationException("Ошибка очистки"));

        var sut = new PrimesAmount(10);

        Assert.Throws<InvalidOperationException>(() =>
            sut.CountPrimesAmount(service.Object));

        service.Verify(s => s.ClearCache(), Times.Once());
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Constructor_InvalidBorder_Throws(int border)
    {
        Assert.Throws<ArgumentException>(() => new PrimesAmount(border));
    }
}