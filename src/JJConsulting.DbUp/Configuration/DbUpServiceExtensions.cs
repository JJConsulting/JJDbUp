using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.CompilerServices;

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
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void MigrateDatabase()
        {
            var migrator = serviceProvider.GetRequiredService<DatabaseMigrator>();
            migrator.Migrate(Assembly.GetCallingAssembly());
        }
    }

}
