using BomLocalService.Services.Interfaces;
using BomLocalService.Services.Interfaces.Registration;
using BomLocalService.Services.Scraping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace BomLocalService.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures CORS with settings from configuration.
    /// Configuration values come from appsettings.json (defaults) and can be overridden via environment variables.
    /// Environment variables use double underscore for nested keys (e.g., CORS__ALLOWEDORIGINS).
    /// </summary>
    public static IServiceCollection AddCorsConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        var corsOrigins = configuration.GetValue<string>("Cors:AllowedOrigins")
            ?? throw new InvalidOperationException("Cors:AllowedOrigins configuration is required. Set it in appsettings.json or via CORS__ALLOWEDORIGINS environment variable.");
        var corsMethods = configuration.GetValue<string>("Cors:AllowedMethods")
            ?? throw new InvalidOperationException("Cors:AllowedMethods configuration is required. Set it in appsettings.json or via CORS__ALLOWEDMETHODS environment variable.");
        var corsHeaders = configuration.GetValue<string>("Cors:AllowedHeaders")
            ?? throw new InvalidOperationException("Cors:AllowedHeaders configuration is required. Set it in appsettings.json or via CORS__ALLOWEDHEADERS environment variable.");

        // For bool, check if the key exists in configuration (GetValue<bool> returns false if not found, which is ambiguous)
        var corsAllowCredentialsKey = configuration["Cors:AllowCredentials"];
        if (corsAllowCredentialsKey == null)
        {
            throw new InvalidOperationException("Cors:AllowCredentials configuration is required. Set it in appsettings.json or via CORS__ALLOWCREDENTIALS environment variable.");
        }
        var corsAllowCredentials = configuration.GetValue<bool>("Cors:AllowCredentials");

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (corsOrigins == "*")
                {
                    policy.AllowAnyOrigin();
                }
                else
                {
                    // Split comma-separated origins
                    var origins = corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    policy.WithOrigins(origins);
                }

                // Split comma-separated methods
                var methods = corsMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                policy.WithMethods(methods);

                // Split comma-separated headers or allow all
                if (corsHeaders == "*")
                {
                    policy.AllowAnyHeader();
                }
                else
                {
                    var headers = corsHeaders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    policy.WithHeaders(headers);
                }

                if (corsAllowCredentials)
                {
                    policy.AllowCredentials();
                }
            });
        });

        return services;
    }

    /// <summary>
    /// Scans the assembly for services implementing registration interfaces and registers them automatically.
    /// Services are registered as their concrete interface (that inherits from registration interface).
    /// Properly handles generic interfaces by using concrete interfaces that hide the generics.
    /// </summary>
    public static IServiceCollection ScanAndRegisterServices(this IServiceCollection services, Assembly assembly)
    {
        var registeredTypes = new HashSet<Type>(); // Track registered types to avoid duplicates

        // Get all types from the assembly with improved filtering
        var types = GetRegisterableTypes(assembly);

        // Register Singleton services
        RegisterServicesByLifetime<ISingletonService>(
            services, 
            types, 
            registeredTypes);

        // Register Scoped services
        RegisterServicesByLifetime<IScopedService>(
            services, 
            types, 
            registeredTypes);

        // Register Transient services
        RegisterServicesByLifetime<ITransientService>(
            services, 
            types, 
            registeredTypes);

        // Register Hosted Services
        RegisterHostedServices(services, types, registeredTypes);

        return services;
    }

    /// <summary>
    /// Gets all types that should be considered for registration.
    /// Filters out abstract classes, interfaces, generic type definitions, nested private types,
    /// compiler-generated types, and types from excluded namespaces.
    /// </summary>
    private static List<Type> GetRegisterableTypes(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => 
                // Must be a class (not interface, struct, enum, etc.)
                t.IsClass
                // Must be concrete (not abstract)
                && !t.IsAbstract
                // Must not be a generic type definition (but closed generics are OK)
                && !t.IsGenericTypeDefinition
                // Must be public (or nested public in a public type)
                && (t.IsPublic || (t.IsNestedPublic && t.DeclaringType?.IsPublic == true))
                // Must not be compiler-generated (e.g., async state machines, iterator classes)
                && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
                // Must not be a nested private/internal type
                && !(t.IsNested && !t.IsNestedPublic)
                // Exclude test namespaces if any (optional - adjust as needed)
                && !t.Namespace?.StartsWith("BomLocalService.Tests", StringComparison.Ordinal) == true
            )
            .ToList();
    }

    private static void RegisterServicesByLifetime<TRegistrationInterface>(
        IServiceCollection services,
        List<Type> types,
        HashSet<Type> registeredTypes)
        where TRegistrationInterface : class
    {
        foreach (var implementationType in types)
        {
            // Skip if already registered
            if (registeredTypes.Contains(implementationType))
                continue;

            // Get all interfaces implemented by this type
            var allInterfaces = implementationType.GetInterfaces().ToList();

            // Find interfaces that inherit from TRegistrationInterface (but not the registration interface itself)
            var registrationInterfaces = allInterfaces
                .Where(i => typeof(TRegistrationInterface).IsAssignableFrom(i) 
                         && i != typeof(TRegistrationInterface)
                         && !IsGenericTypeDefinition(i))
                .ToList();

            if (registrationInterfaces.Count == 0)
                continue;

            // For each registration interface, find the most specific concrete interface
            // Priority: concrete interfaces (like IRadarScrapingWorkflow) over generic interfaces (like IWorkflow<T>)
            foreach (var registrationInterface in registrationInterfaces)
            {
                // Skip if this is a generic interface definition (we prefer concrete interfaces)
                if (registrationInterface.IsGenericTypeDefinition)
                    continue;

                // Check if this interface is already registered to avoid duplicates
                var alreadyRegistered = services.Any(s => 
                    s.ServiceType == registrationInterface && 
                    s.ImplementationType == implementationType);

                if (alreadyRegistered)
                    continue;

                // Determine lifetime based on registration interface
                var lifetime = GetLifetime<TRegistrationInterface>();

                // Register the service by interface
                if (lifetime == ServiceLifetime.Singleton)
                {
                    services.AddSingleton(registrationInterface, implementationType);
                    // Also register as concrete type for direct resolution (e.g., for ScrapingStepRegistry)
                    services.AddSingleton(implementationType);
                }
                else if (lifetime == ServiceLifetime.Scoped)
                {
                    services.AddScoped(registrationInterface, implementationType);
                    services.AddScoped(implementationType);
                }
                else if (lifetime == ServiceLifetime.Transient)
                {
                    services.AddTransient(registrationInterface, implementationType);
                    services.AddTransient(implementationType);
                }

                registeredTypes.Add(implementationType);
            }
        }
    }

    private static bool IsGenericTypeDefinition(Type type)
    {
        return type.IsGenericTypeDefinition || 
               (type.IsGenericType && type.GetGenericTypeDefinition() == type);
    }

    private static ServiceLifetime GetLifetime<TRegistrationInterface>()
        where TRegistrationInterface : class
    {
        if (typeof(ISingletonService).IsAssignableFrom(typeof(TRegistrationInterface)))
            return ServiceLifetime.Singleton;
        if (typeof(IScopedService).IsAssignableFrom(typeof(TRegistrationInterface)))
            return ServiceLifetime.Scoped;
        if (typeof(ITransientService).IsAssignableFrom(typeof(TRegistrationInterface)))
            return ServiceLifetime.Transient;
        
        throw new InvalidOperationException($"Unknown registration interface: {typeof(TRegistrationInterface).Name}");
    }

    private static void RegisterHostedServices(
        IServiceCollection services,
        List<Type> types,
        HashSet<Type> registeredTypes)
    {
        foreach (var type in types)
        {
            // Skip if already registered
            if (registeredTypes.Contains(type))
                continue;

            // Check if it's a BackgroundService that implements IHostedServiceRegistration
            if (typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(type) &&
                typeof(IHostedServiceRegistration).IsAssignableFrom(type))
            {
                // Check if already registered
                var alreadyRegistered = services.Any(s => 
                    s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && 
                    s.ImplementationType == type);

                if (!alreadyRegistered)
                {
                    // Use AddHostedService via reflection - this is the recommended way
                    // It properly handles lifecycle management for hosted services
                    // AddHostedService<T> is generic, so we need to call it via reflection
                    var addHostedServiceMethod = typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions)
                        .GetMethod(nameof(Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions.AddHostedService), 
                            new[] { typeof(IServiceCollection) })!
                        .MakeGenericMethod(type);
                    addHostedServiceMethod.Invoke(null, new object[] { services });
                    registeredTypes.Add(type);
                }
            }
        }
    }
}

