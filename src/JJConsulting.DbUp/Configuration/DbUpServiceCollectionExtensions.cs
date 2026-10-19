using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JJConsulting.DbUp.Configuration;

public static class DbUpServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDbUpMigrations()
        {
            services.AddOptions<DbUpOptions>();

            return services;
        }

        public IServiceCollection AddDbUpMigrations(Action<DbUpOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(configureOptions);

            services.Configure(configureOptions);

            return services;
        }

        public IServiceCollection AddDbUpMigrations(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<DbUpOptions>().Bind(configuration.GetSection(DbUpOptions.SectionName));

            return services;
        }
    }
}
