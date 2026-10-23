using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JJConsulting.DbUp.Configuration;

public static class DbUpServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDbUpMigrations(IConfiguration configuration)
        {
            services.AddSingleton<DatabaseMigrator>();
            services.AddOptions<DbUpOptions>().Bind(configuration.GetSection(DbUpOptions.SectionName));

            return services;
        }
    }

    extension(IServiceProvider serviceProvider)
    {
        public void MigrateDatabase()
        {
            var migrator = serviceProvider.GetRequiredService<DatabaseMigrator>();
            migrator.Migrate();
        }
    }

}
