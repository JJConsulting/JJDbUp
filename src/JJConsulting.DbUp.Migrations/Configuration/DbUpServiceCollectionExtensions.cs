using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JJConsulting.DbUp.Migrations.Configuration;

public static class DbUpServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDbUpMigrations(IConfiguration configuration)
        {
            services.AddOptions<DbUpOptions>().Bind(configuration.GetSection(DbUpOptions.SectionName));

            return services;
        }
    }
}
