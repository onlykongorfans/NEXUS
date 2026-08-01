namespace DAWNBRINGER.WebPortal.UI;

public class DAWNBRINGER
{
    public static void Main(string[] arguments)
    {
        // Create The Application Builder
        WebApplicationBuilder builder = WebApplication.CreateBuilder(arguments);

        // Enable Static Web Assets, When Running From Source (e.g. Using The Aspire Orchestrator) In Non-Development Environments
        // In Development This Is Automatic; In Published Output The Physical Files Are Present; This Covers Everything In Between
        builder.WebHost.UseStaticWebAssets();

        // Add Aspire Service Defaults
        builder.AddServiceDefaults();

        // Add Serilog Logging
        builder.AddSerilogLogging();

        // Add Razor Components With Interactive Server Rendering
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Add Response Compression For Smaller Payloads
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
        });

        // Add MudBlazor Component Library Services
        builder.Services.AddMudServices(configuration =>
        {
            configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            configuration.SnackbarConfiguration.PreventDuplicates = true;
            configuration.SnackbarConfiguration.ShowCloseIcon = true;
            configuration.SnackbarConfiguration.VisibleStateDuration = 5000;
            configuration.SnackbarConfiguration.HideTransitionDuration = 300;
            configuration.SnackbarConfiguration.ShowTransitionDuration = 300;
            configuration.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
        });

        // Configure Named HTTP Client For The Web Portal API
        builder.Services.AddHttpClient(PortalAuthenticationService.HTTPClientName, client =>
        {
            // The Base Address Is Resolved By Aspire Service Discovery At Runtime
            client.BaseAddress = new Uri("https+http://web-portal-api");
        });

        // Add Authentication And Authorization Services
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();

        // Replace The Default Authorization Middleware Result Handler With A Custom One That Supports Blazor's Navigation Manager
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, BlazorAuthorisationMiddlewareResultHandler>();

        // Register Authentication State Provider And Authentication Service
        builder.Services.AddScoped<PortalAuthenticationService>();
        builder.Services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<PortalAuthenticationService>());

        // Add Cascading Authentication State For The Entire Application
        builder.Services.AddCascadingAuthenticationState();

        // Configure Forwarded Headers For Reverse Proxy Support
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            string proxy = Environment.GetEnvironmentVariable("INFRASTRUCTURE_GATEWAY") ?? throw new NullReferenceException("Infrastructure Gateway Is NULL");

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            IPAddress[] proxyResolvedAddresses;

            try { proxyResolvedAddresses = Dns.GetHostAddresses(proxy); }
            catch (Exception exception) { throw new InvalidOperationException($@"Failed To Resolve Proxy Host ""{proxy}""", exception); }

            foreach (IPAddress proxyResolvedAddress in proxyResolvedAddresses)
                if (proxyResolvedAddress.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    options.KnownProxies.Add(proxyResolvedAddress);

            // Only Trust The Last Forwarded Header In The Chain
            options.ForwardLimit = 1;
        });

        // Build The Application
        WebApplication application = builder.Build();

        // Honour The Original HTTPS Scheme And Client Address Supplied By The Trusted Fronting Gateway Before Applying HTTPS Redirects
        application.UseForwardedHeaders();

        // Configure Development-Specific Middleware
        if (application.Environment.IsDevelopment())
        {
            // Show Detailed Error Pages In Development
            application.UseDeveloperExceptionPage();
        }

        else
        {
            // Use Global Exception Handler In Production
            application.UseExceptionHandler("/error");
        }

        // Enforce HTTPS With Strict Transport Security
        application.UseHsts();

        // Automatically Redirect HTTP Requests To HTTPS
        application.UseHttpsRedirection();

        // Compress Responses To Reduce Payload Sizes
        application.UseResponseCompression();

        application.UseAntiforgery();
        application.MapStaticAssets();

        // Map Razor Components With Interactive Server Render Mode
        application.MapRazorComponents<Components.Application>().AddInteractiveServerRenderMode();

        // Map Aspire Default Health Check Endpoints
        application.MapDefaultEndpoints();

        // Run The Application
        application.Run();
    }
}
