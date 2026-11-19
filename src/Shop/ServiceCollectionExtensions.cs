using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Shop;

public enum DispatchMode
{
    BeforeCommit,
    AfterCommit,
    Outbox
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddShop(this IServiceCollection services, string connectionString, DispatchMode mode)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DomainEventDispatcher>();
        services.AddScoped<IDomainEventHandler, ReserveStockHandler>();
        services.AddScoped<IDomainEventHandler, SendConfirmationEmailHandler>();

        switch (mode)
        {
            case DispatchMode.BeforeCommit:
                services.AddScoped<ISaveChangesInterceptor, BeforeCommitDispatchInterceptor>();
                break;
            case DispatchMode.AfterCommit:
                services.AddScoped<ISaveChangesInterceptor, AfterCommitDispatchInterceptor>();
                break;
            case DispatchMode.Outbox:
                services.AddScoped<ISaveChangesInterceptor, OutboxInterceptor>();
                services.AddSingleton<OutboxProcessor>();
                services.AddHostedService<OutboxBackgroundService>();
                break;
        }

        services.AddDbContext<ShopDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .AddInterceptors(sp.GetServices<ISaveChangesInterceptor>()));

        return services;
    }
}
