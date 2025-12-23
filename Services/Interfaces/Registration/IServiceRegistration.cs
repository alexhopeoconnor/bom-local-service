namespace BomLocalService.Services.Interfaces.Registration;

/// <summary>
/// Marker interface for services that should be registered as Singleton
/// </summary>
public interface ISingletonService
{
}

/// <summary>
/// Marker interface for services that should be registered as Scoped
/// </summary>
public interface IScopedService
{
}

/// <summary>
/// Marker interface for services that should be registered as Transient
/// </summary>
public interface ITransientService
{
}

/// <summary>
/// Marker interface for hosted services (BackgroundService implementations)
/// </summary>
public interface IHostedServiceRegistration
{
}

