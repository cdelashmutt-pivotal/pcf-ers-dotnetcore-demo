using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Articulate;
using CloudPlatformDemo;
using CloudPlatformDemo.Models;
using CloudPlatformDemo.Repositories;
using CloudPlatformDemo.Utils;
using CloudPlatformDemo.Workaround;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Azure.SpringCloud.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Steeltoe.Common;
using Steeltoe.Common.Discovery;
using Steeltoe.Common.Hosting;
using Steeltoe.Common.Logging;
// using Steeltoe.Common.Http.Discovery;
using Steeltoe.Configuration.CloudFoundry;
using Steeltoe.Configuration.CloudFoundry.ServiceBinding;
using Steeltoe.Configuration.ConfigServer;
using Steeltoe.Configuration.Kubernetes.ServiceBinding;
using Steeltoe.Configuration.Placeholder;
using Steeltoe.Configuration.RandomValue;
using Steeltoe.Connectors.EntityFrameworkCore;
using Steeltoe.Connectors.PostgreSql;
using Steeltoe.Discovery;
using Steeltoe.Connectors.EntityFrameworkCore.MySql;
using Steeltoe.Connectors.EntityFrameworkCore.SqlServer;
using Steeltoe.Connectors.EntityFrameworkCore.PostgreSql;
using Steeltoe.Connectors.MySql;
using Steeltoe.Connectors.SqlServer;
// using Steeltoe.Discovery.Client;
using Steeltoe.Discovery.Configuration;
using Steeltoe.Management.Endpoint;
using Steeltoe.Management.Tracing;
using Steeltoe.Security.Authentication.CloudFoundry;
using Steeltoe.Discovery.Eureka;
using Steeltoe.Discovery.Eureka.Configuration;
using Steeltoe.Discovery.HttpClients;
using Steeltoe.Management.Endpoint.Health;
using Steeltoe.Management.Task;
using ConfigurationBuilderExtensions = Steeltoe.Configuration.CloudFoundry.ServiceBinding.ConfigurationBuilderExtensions;
using LocalCertificateWriter = CloudPlatformDemo.LocalCerts.LocalCertificateWriter;

var logger = LoggerFactory.Create(c => c
    .AddSimpleConsole(f => f
        .SingleLine = true))
    .CreateLogger<Program>();
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

builder.Configuration
    .AddYamlFile("appsettings.yaml", false, true)
    .AddYamlFile($"appsettings.{builder.Environment.EnvironmentName}.yaml", true, true)
    .AddProfiles();
if (Platform2.IsTanzuApplicationPlatform)
{
    logger.LogInformation("Adding Tanzu Application Platform bits");
    builder.Configuration.AddKubernetesServiceBindings();
}
if (Platform2.IsAzureSpringApps)
{
    logger.LogInformation("Adding ASA bits");
    builder.AddAzureSpringApp();
    builder.Services.AddEurekaDiscoveryClient();

}
if (Platform2.IsKubernetes)
{
    logger.LogInformation("Adding Kubernetes bits");
    services.ConfigureOptions(typeof(KubernetesServiceConfigureOptions));
}
if (Platform.IsCloudFoundry)
{
    logger.LogInformation("Adding Cloud Foundry bits");
    builder.Services.RegisterCloudFoundryApplicationInstanceInfo();
    if (Environment.GetEnvironmentVariable("VCAP_SERVICES") != null)
    {
        builder.Configuration.AddCloudFoundryServiceBindings();
    }
    else
    {
        // in local development, it's easier to manage values normally supplied by vcap_services env var in a yaml file
        builder.Configuration.AddCloudFoundryServiceBindings(_ => false, new YamlServiceBindingsReader("vcap_services.yaml"), BootstrapLoggerFactory.Default);
    }

    builder.Configuration
        .AddCloudFoundry()
        .AddCloudFoundryContainerIdentity();

    services.AddCloudFoundryContainerIdentity();
    services.AddAuthentication((options) =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CloudFoundryDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.AccessDeniedPath = new PathString("/Home/AccessDenied");
        })
        .AddCloudFoundryOAuth(builder.Configuration)
        .AddCloudFoundryIdentityCertificate();

    services.AddAuthorization(cfg =>
    {
        cfg.AddPolicy(SecurityPolicy.LoggedIn, policy => policy
            .AddAuthenticationSchemes(CloudFoundryDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser());
        cfg.AddPolicy(CloudFoundryDefaults.SameOrganizationAuthorizationPolicy, policy =>
        {
            policy.AuthenticationSchemes.Add(CertificateAuthenticationDefaults.AuthenticationScheme);
            policy.SameOrg();
        });
        cfg.AddPolicy(CloudFoundryDefaults.SameSpaceAuthorizationPolicy, policy =>
        {
            policy.AuthenticationSchemes.Add(CertificateAuthenticationDefaults.AuthenticationScheme);
            policy.SameSpace();
        });
    });
    services.ConfigureOptions(typeof(CloudFoundryServiceConfigureOptions));
}
else
{
    builder.Services.RegisterDefaultApplicationInstanceInfo();
}

builder.Configuration
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var envInfo = new EnvironmentInfo(builder.Configuration);
builder.Services.AddSingleton(envInfo);
if (envInfo.IsConfigServerBound)
{
    builder.Configuration
        .AddConfigServer()
        // readadd profiles and env vars on top of values from config server as they may alter selection of which keys to use
        .AddProfiles() 
        .AddEnvironmentVariables();
}
else
{
    logger.LogWarning("Config server not being used as no binding information was present");
}


builder.Configuration
    .AddCommandLine(args)
    .AddInMemoryCollection(new Dictionary<string, string> // used to demonstrate how random and placeholder provider is used
    {
        { "MyConnectionString", "Server=${SqlHost};Database=${SqlDatabase};User Id=${SqlUser};Password=${SqlPassword};" },
        { "AppInstanceId", "${random:value}" }
    })
    .AddRandomValueSource()
    .AddPlaceholderResolver();


builder.AddAllActuators();
services.AddDistributedTracing();

//services.AddSpringBootAdminClient(); 

services.ConfigureCloudFoundryOptions(builder.Configuration);
services.AddScoped<AppEnv>();
services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
services.AddConfigServerHealthContributor();

if (envInfo.IsMySqlBound)
{
    builder.AddMySql();
    services.AddDbContext<AttendeeContext>((serviceProvider, db) => db.UseMySql(serviceProvider));
    logger.LogInformation("Database Provider: MySQL");
}
else if (envInfo.IsSqlServerBound)
{
    services.AddSqlServer(builder.Configuration);
    services.AddDbContext<AttendeeContext>((serviceProvider, db) => db.UseSqlServer(serviceProvider));
    logger.LogInformation("Database Provider: SQL Server");
}
else
{
    var dbFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "../tmp/users.db");
    services.AddDbContext<AttendeeContext>(db => db.UseSqlite($"DataSource={dbFile}"));
    logger.LogInformation("Database Provider: SQLite");
        
}

services.AddTask<MigrateDbContextTask<AttendeeContext>>(ServiceLifetime.Scoped);
var httpClientBuilder = services.AddHttpClient(Options.DefaultName);

if (envInfo.IsEurekaBound)
{
    logger.LogInformation("Adding Eureka");
    builder.Services.AddEurekaDiscoveryClient();
    httpClientBuilder.AddServiceDiscovery();
    if (Platform2.IsAzureSpringApps)
    {
        logger.LogInformation("Configuring eureka to use client side certs with ASA");
        services.AddTransient<ClientCertificateHttpHandler2>();
        services.AddHttpClient("Eureka").ConfigurePrimaryHttpMessageHandler<ClientCertificateHttpHandler2>();
    }
    else
    {
        services.AddHttpClient("Eureka").ConfigurePrimaryHttpMessageHandler(_ => new HttpClientHandler());
    }
}

else
{
    logger.LogWarning("Service discovery (Eureka) integration disabled as no binding information was found. Discovery client will fall back to preconfigured " +
                      "services found under 'Discovery:Services' configuration key");
    services.AddConfigurationDiscoveryClient();
    services.AddSingleton<IDiscoveryClient, ConfigurationDiscoveryClient>();
}


// services.PostConfigure<CertificateOptions>(opt =>
// {
//     // if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) && opt.Certificate != null)
//     // {
//         try
//         {
//             // work around bug when running on Windows
//             opt.Certificate = new X509Certificate2(config.GetValue<string>("certificate"), (string)null, X509KeyStorageFlags.Exportable);
//             //opt.Certificate = new X509Certificate2(opt.Certificate.Export(X509ContentType.Pkcs12));
//         }
//         catch (Exception)
//         {
//             // ignored
//         }
//     // }
// });


    

// if (builder.Environment.IsDevelopment())
// {
//     services.AddTransient<SimulatedClientCertInHeaderHttpHandler>();
//     httpClientBuilder.ConfigurePrimaryHttpMessageHandler<SimulatedClientCertInHeaderHttpHandler>();
// }




services
    .AddControllersWithViews()
    .AddRazorRuntimeCompilation();

var app = builder.Build();


app.UseForwardedHeaders();
app.UseCookiePolicy(); 
app.UseDeveloperExceptionPage();
app.UseStaticFiles();
app.UseAzureSpringApps();
app.UseRouting();
app.UseCloudFoundryCertificateAuth();
// app.UseAuthentication();
// app.UseAuthorization();

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");
app.EnsureMigrationOfContext<AttendeeContext>();

var configValues = builder.Configuration.AsEnumerable().ToList();
app.Run();
