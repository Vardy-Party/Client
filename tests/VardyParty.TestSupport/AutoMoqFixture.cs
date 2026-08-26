using System;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using AutoFixture.Kernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace VardyParty.TestSupport;

/// <summary>
/// AutoFixture + AutoMoq specimen factory. Tests should <c>Build</c>/<c>Create</c>/<c>GetMock</c>
/// collaborators in the test method rather than hand-construct graphs.
/// </summary>
public static class AutoMoqFixture
{
    public static IFixture Create()
    {
        var fixture = new Fixture();
        fixture.Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList()
            .ForEach(b => fixture.Behaviors.Remove(b));
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());
        fixture.Customizations.Insert(0, new NullLoggerSpecimenBuilder());
        return fixture;
    }

    /// <summary>
    /// Frozen AutoMoq mock for <typeparamref name="T"/>. Same instance is injected when the SUT is created.
    /// </summary>
    public static Mock<T> GetMock<T>(this IFixture fixture) where T : class
        => fixture.Freeze<Mock<T>>();

    private sealed class NullLoggerSpecimenBuilder : ISpecimenBuilder
    {
        public object Create(object request, ISpecimenContext context)
        {
            if (request is not Type type)
                return new NoSpecimen();

            if (type == typeof(ILogger))
                return NullLogger.Instance;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>))
            {
                var loggerType = typeof(NullLogger<>).MakeGenericType(type.GenericTypeArguments);
                return Activator.CreateInstance(loggerType)!;
            }

            return new NoSpecimen();
        }
    }
}
