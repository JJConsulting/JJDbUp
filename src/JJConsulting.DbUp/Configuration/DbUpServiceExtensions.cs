using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JJConsulting.DbUp.Configuration;

public static class DbUpServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDbUpMigrations()
        {
            services.AddSingleton<DatabaseMigrator>();
            services.AddOptions<DbUpOptions>().BindConfiguration(DbUpOptions.SectionName);
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
