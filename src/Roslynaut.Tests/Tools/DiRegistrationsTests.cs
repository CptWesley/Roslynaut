using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>di_registrations, against stand-ins for Microsoft.Extensions.DependencyInjection's registration methods.</summary>
public class DiRegistrationsTests
{
    private static readonly Microsoft.CodeAnalysis.Solution Shop = TestSolution.Create("""
        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }
            public static class ServiceCollectionServiceExtensions
            {
                public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, System.Func<System.IServiceProvider, TService> implementationFactory) => services;
                public static IServiceCollection AddTransient(this IServiceCollection services, System.Type serviceType, System.Type implementationType) => services;
                public static IServiceCollection AddKeyedSingleton<TService, TImplementation>(this IServiceCollection services, object? serviceKey) where TImplementation : TService => services;
            }
        }
        namespace Microsoft.Extensions.DependencyInjection.Extensions
        {
            public static class ServiceCollectionDescriptorExtensions
            {
                public static void TryAddSingleton<TService, TImplementation>(this Microsoft.Extensions.DependencyInjection.IServiceCollection services) where TImplementation : TService { }
            }
        }
        namespace Shop
        {
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;

            public interface IStore { }
            public class SqlStore : IStore { }
            public class MemoryStore : IStore { }
            public interface IClock { }
            public class Clock : IClock { }
            public class Cache { public Cache(IStore store) { } }
            public class Orders { public Orders(IClock clock, System.Collections.Generic.IEnumerable<IStore> stores) { } }

            public static class Setup
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddScoped<IStore, SqlStore>();
                    services.TryAddSingleton<IStore, MemoryStore>();
                    services.AddSingleton<Cache, Cache>();
                    services.AddSingleton<IClock>(provider => new Clock());
                    services.AddTransient(typeof(Orders), typeof(Orders));
                    services.AddKeyedSingleton<IStore, MemoryStore>("memory");
                }
            }
        }
        """);

    [Test]
    public async Task Registrations_PerService_WithWinnerConsumersAndCaptives()
    {
        var services = await DiRegistrationsTool.FindAsync(Shop, CancellationToken.None);

        services.Select(service => service.Service).Should().Equal("Shop.Cache", "Shop.IClock", "Shop.IStore", "Shop.Orders");
        var store = services.Single(service => service.Service == "Shop.IStore");
        store.Registrations.Select(registration => (registration.Implementation, registration.Lifetime, registration.Method, registration.Key, registration.Wins)).Should().Equal(
            ("Shop.SqlStore", "Scoped", "AddScoped", null, true),
            ("Shop.MemoryStore", "Singleton", "TryAddSingleton", null, false),
            ("Shop.MemoryStore", "Singleton", "AddKeyedSingleton", "memory", true));
        store.InjectedInto.Select(constructor => constructor.Container).Should().BeEquivalentTo("Shop.Cache", "Shop.Orders");
        store.Problems.Should().ContainSingle().Which.Should().Contain("Scoped Shop.IStore is injected into Shop.Cache, a Singleton");

        services.Single(service => service.Service == "Shop.IClock").Registrations.Single().Implementation.Should().Match("factory at line *, returning Shop.Clock");
        services.Single(service => service.Service == "Shop.Orders").Registrations.Single().Should().Match<DiRegistrationsTool.Registration>(registration =>
            registration.Implementation == "Shop.Orders" && registration.Lifetime == "Transient");
    }
}
