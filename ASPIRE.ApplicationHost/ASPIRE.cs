namespace ASPIRE.ApplicationHost;

public class ASPIRE
{
    public static void Main(string[] arguments)
    {
        // Create Distributed Application Builder
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(arguments);

        // Get Configuration From Environment Variables And User Secrets
        IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddUserSecrets<ASPIRE>(optional: true).Build();

        // Get Chat Server Configuration
        IConfigurationSection chatServerConfiguration = builder.Configuration.GetRequiredSection("ChatServer");
        string chatServerHost = chatServerConfiguration.GetValue<string?>("Host") ?? throw new NullReferenceException("Chat Server Host Is NULL");
        string chatServerClientHost = chatServerConfiguration.GetValue<string?>("ClientHost") ?? chatServerHost;
        int chatServerClientConnectionsPort = chatServerConfiguration.GetValue<int?>("ClientPort") ?? throw new NullReferenceException("Chat Server Client Connections Port Is NULL");
        int chatServerMatchServerConnectionsPort = chatServerConfiguration.GetValue<int?>("MatchServerPort") ?? throw new NullReferenceException("Chat Server Match Server Connections Port Is NULL");
        int chatServerMatchServerManagerConnectionsPort = chatServerConfiguration.GetValue<int?>("MatchServerManagerPort") ?? throw new NullReferenceException("Chat Server Match Server Manager Connections Port Is NULL");

        // Get Infrastructure Configuration
        IConfigurationSection infrastructureConfiguration = builder.Configuration.GetRequiredSection("Infrastructure");
        string gateway = infrastructureConfiguration.GetValue<string?>("Gateway") ?? throw new NullReferenceException("Infrastructure Gateway Is NULL");

        // Use Distinct Persistent Stores For Development And Production So Runtime State Cannot Leak Between Environments
        string databaseName = builder.Environment.IsProduction() ? "production" : "development";

        // Set Distributed Cache Password Parameter Name And Environment Variable Name
        const string distributedCachePasswordParameterName = "distributed-cache-password";
        const string distributedCachePasswordEnvironmentVariableName = "DISTRIBUTED_CACHE_PASSWORD";

        // Explicit Runtime Environment Variables Override Development User Secrets During Production Deployment
        string? resolvedDistributedCachePassword = configuration[distributedCachePasswordEnvironmentVariableName] ?? configuration[$"Parameters:{distributedCachePasswordParameterName}"];

        // Populate Distributed Cache Password If Available In User Secrets Or Environment Variables
        IResourceBuilder<ParameterResource> distributedCachePassword = resolvedDistributedCachePassword is not null
            ? builder.AddParameter(distributedCachePasswordParameterName, resolvedDistributedCachePassword, secret: true)
            : builder.AddParameter(distributedCachePasswordParameterName, secret: true);

        // Add Distributed Cache Resource
        IResourceBuilder<ValkeyResource> distributedCache = builder.AddValkey("distributed-cache", password: distributedCachePassword)
            .WithImageTag("latest").WithImageSHA256("8e8d64b405ce18f41b8e5ee20aa4687a8ed0022d1298f2ce31cdcf3a76e09411") // Pin The Tested Valkey 9.1.0 Image
            .WithLifetime(ContainerLifetime.Persistent).WithDataVolume($"distributed-cache-data-{databaseName}"); // Persist Cached Data In An Environment-Specific Docker-Managed Data Volume

        // Create Resource Relationship After Parent Resource Is Defined
        distributedCachePassword
            .WithDescription("Distributed Cache Password") // Add Description To Parameter Resource
            .WithParentRelationship(distributedCache); // Set Distributed Cache As Parent Resource

        // Add Distributed Cache Dashboard Resource
        // Redis Insight Is Used Rather Than The Valkey-Native Valkey Admin Because It Pre-Configures Its Connection From "RI_REDIS_*" Environment Variables, Whereas Valkey Admin Cannot Pre-Configure A Connection For A Standalone (Non-Cluster) Node (TODO: Revisit If Valkey Admin Adds Standalone Pre-Configuration)
        builder.AddContainer("distributed-cache-dashboard", "redis/redisinsight")
            .WithImageTag("latest").WithImageSHA256("aa21bbd198455b4ad964f76782db951155aa0d712321f599972d1525f031f0e6") // Pin The Image Verified During Production Preparation
            .WithLifetime(ContainerLifetime.Persistent)
            .WithHttpEndpoint(targetPort: 5540, name: "http") // Default Redis Insight Web UI Port
            .WithEnvironment("RI_ACCEPT_TERMS_AND_CONDITIONS", "true") // Automatically Accept Terms And Conditions: https://redis.io/docs/latest/operate/redisinsight/configuration/
            .WithEnvironment("RI_REDIS_ALIAS0", "Distributed Cache") // Pre-Configured Connection Alias
            .WithEnvironment("RI_REDIS_PASSWORD0", distributedCachePassword) // Pre-Configured Connection Password
            .WithEnvironment(context => // Point The Pre-Configured Connection At The Distributed Cache Endpoint Within The Container Network
            {
                EndpointReference distributedCacheEndpoint = distributedCache.Resource.PrimaryEndpoint;

                context.EnvironmentVariables["RI_REDIS_HOST0"] = distributedCacheEndpoint.Property(EndpointProperty.Host);
                context.EnvironmentVariables["RI_REDIS_PORT0"] = distributedCacheEndpoint.Property(EndpointProperty.TargetPort);
            })
            .WaitFor(distributedCache) // Wait For The Distributed Cache To Start
            .WithParentRelationship(distributedCache); // Set Distributed Cache As Parent Resource

        // Set Database Password Parameter Name And Environment Variable Name
        const string databasePasswordParameterName = "database-password";
        const string databasePasswordEnvironmentVariableName = "DATABASE_PASSWORD";

        // Explicit Runtime Environment Variables Override Development User Secrets During Production Deployment
        string? resolvedDatabasePassword = configuration[databasePasswordEnvironmentVariableName] ?? configuration[$"Parameters:{databasePasswordParameterName}"];

        // Populate Database Password If Available In User Secrets Or Environment Variables
        IResourceBuilder<ParameterResource> databasePassword = resolvedDatabasePassword is not null
            ? builder.AddParameter(databasePasswordParameterName, resolvedDatabasePassword, secret: true)
            : builder.AddParameter(databasePasswordParameterName, secret: true);

        // Using The Default SQL Server Port Allows The Database To Be Accessible With Just The Host Name/Address And No Port Number (e.g. 127.0.0.1)
        // If The SQL Server Resource Does Not Define A Port, Then It Will Be Randomly Assigned One, And The Connection Address Format Will Be {HOST},{PORT} (e.g. 127.0.0.1,51433)
        // While Aspire's Service Orchestration Is Not Running, The Port To Connect Directly To The Running SQL Server Container Can Be Found In Docker (e.g. "docker container list")
        const int databasePort = 1433;

        // Add SQL Server Resource (SQL Server 2025 Requires A Container-Native Data Directory, A Linux-To-Windows Bind Mount Causes I/O Failures During Startup, So A Data Volume Is Required)
        IResourceBuilder<SqlServerServerResource> databaseServer = builder.AddSqlServer("database-server", password: databasePassword, port: databasePort)
            .WithImageTag("latest").WithImageSHA256("86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a") // Pin The Tested SQL Server Image
            .WithLifetime(ContainerLifetime.Persistent).WithDataVolume($"database-server-data-{databaseName}") // Persist SQL Server Data As Docker-Managed Data Volume
            .WithEnvironment("ACCEPT_EULA", "Y").WithEnvironment("MSSQL_PID", "Developer"); // SQL Server Image Information: https://mcr.microsoft.com/en-gb/artifact/mar/mssql/server/about

        // Create Resource Relationship After Parent Resource Is Defined
        databasePassword
            .WithDescription("Database Password") // Add Description To Parameter Resource
            .WithParentRelationship(databaseServer); // Set Database Server As Parent Resource

        // Add SQL Server Database Resource
        IResourceBuilder<SqlServerDatabaseResource> database = databaseServer.AddDatabase("database", databaseName)
            .WithParentRelationship(databaseServer); // Set Database Server As Parent Resource

        // Add Structured Log Server Resource
        IResourceBuilder<SeqResource> logServer = builder.AddSeq("log-server")
            .WithImageTag("latest").WithImageSHA256("868a12e93ec0b8c993767a7dd4cd6c8ebc441511c79e4cfac66c911f62d4db65") // Pin The Image Verified During Production Preparation
            .WithLifetime(ContainerLifetime.Persistent).WithDataVolume($"log-server-data-{databaseName}") // Persist Ingested Logs In An Environment-Specific Docker-Managed Data Volume
            .WithEnvironment("SEQ_DIAGNOSTICS_INTERNALLOGGINGLEVEL", "Warning") // Quieten Seq's Own Internal Maintenance Logging, Which By Default Goes Into STDERR At Information Level
            .WithEnvironment("ACCEPT_EULA", "Y"); // Automatically Accept End User License Agreement: https://datalust.co/docs/environment-variables

        // In Non-Development Environments, Protect The Log Server Dashboard With An Administrator Password
        if (builder.Environment.IsDevelopment() is false)
        {
            // The Default Seq Administrator User Name
            const string logServerFirstRunAdministratorUserName = "admin";

            // Resolve The Initial Seq Administrator Password From A Protected Parameter Rather Than Tracked Configuration
            const string logServerFirstRunAdministratorPasswordParameterName = "log-server-administrator-password";
            const string logServerFirstRunAdministratorPasswordEnvironmentVariableName = "LOG_SERVER_ADMINISTRATOR_PASSWORD";

            string? resolvedLogServerFirstRunAdministratorPassword =
                configuration[logServerFirstRunAdministratorPasswordEnvironmentVariableName]
                ?? configuration[$"Parameters:{logServerFirstRunAdministratorPasswordParameterName}"];

            IResourceBuilder<ParameterResource> logServerFirstRunAdministratorPassword =
                resolvedLogServerFirstRunAdministratorPassword is not null
                    ? builder.AddParameter(logServerFirstRunAdministratorPasswordParameterName, resolvedLogServerFirstRunAdministratorPassword, secret: true)
                    : builder.AddParameter(logServerFirstRunAdministratorPasswordParameterName, secret: true);

            logServer
                .WithEnvironment("SEQ_FIRSTRUN_ADMINUSERNAME", logServerFirstRunAdministratorUserName) // Set The Initial Administrator User Name On The Log Server Resource
                .WithEnvironment("SEQ_FIRSTRUN_ADMINPASSWORD", logServerFirstRunAdministratorPassword); // Set The Initial Administrator Password On The Log Server Resource

            logServerFirstRunAdministratorPassword
                .WithDescription("Log Server Administrator Password")
                .WithParentRelationship(logServer);
        }

        // Add Database Project
        IResourceBuilder<ProjectResource> databaseContext = builder.AddProject<MERRICK>("database-context", builder.Environment.IsProduction() ? "MERRICK.DatabaseContext Production" : "MERRICK.DatabaseContext Development")
            .WithReference(database, connectionName: "MERRICK").WaitFor(database) // Connect To SQL Server Database And Wait For It To Start
            .WithReference(logServer) // Connect To Structured Log Server
            .WithParentRelationship(databaseServer) // Set Database Server As Parent Resource
            .WithEnvironment("INFRASTRUCTURE_GATEWAY", gateway);

        // Enable Entity Framework Core Commands Which Resolve The Database Connection String Through The Aspire Application Host
        databaseContext.AddEFMigrations("database-migrations", "MERRICK.DatabaseContext.Persistence.MerrickContext")
            .WithReference(database, connectionName: "MERRICK").WaitFor(database) // Supply The Resolved Connection String Under The Name The Database Context Expects, And Wait For The Database To Start
            .WithParentRelationship(databaseContext); // Set Database Context As Parent Resource

        // Add Master Server Project
        builder.AddProject<KONGOR>("master-server", builder.Environment.IsProduction() ? "KONGOR.MasterServer Production" : "KONGOR.MasterServer Development")
            .WithReference(database, connectionName: "MERRICK").WaitFor(database) // Connect To SQL Server Database And Wait For It To Start
            .WaitFor(databaseContext) // Wait For Database Initialization And Production Credential Rotation To Complete
            .WithReference(distributedCache, connectionName: "DISTRIBUTED-CACHE").WaitFor(distributedCache) // Connect To Distributed Cache And Wait For It To Start
            .WithReference(logServer) // Connect To Structured Log Server
            .WithEnvironment("CHAT_SERVER_HOST", chatServerHost)
            .WithEnvironment("CHAT_SERVER_CLIENT_HOST", chatServerClientHost)
            .WithEnvironment("CHAT_SERVER_PORT_CLIENT", chatServerClientConnectionsPort.ToString())
            .WithEnvironment("CHAT_SERVER_PORT_MATCH_SERVER", chatServerMatchServerConnectionsPort.ToString())
            .WithEnvironment("CHAT_SERVER_PORT_MATCH_SERVER_MANAGER", chatServerMatchServerManagerConnectionsPort.ToString())
            .WithEnvironment("INFRASTRUCTURE_GATEWAY", gateway);

        // Add Chat Server Project
        builder.AddProject<TRANSMUTANSTEIN>("chat-server", builder.Environment.IsProduction() ? "TRANSMUTANSTEIN.ChatServer Production" : "TRANSMUTANSTEIN.ChatServer Development")
            .WithReference(database, connectionName: "MERRICK").WaitFor(database) // Connect To SQL Server Database And Wait For It To Start
            .WithReference(distributedCache, connectionName: "DISTRIBUTED-CACHE").WaitFor(distributedCache) // Connect To Distributed Cache And Wait For It To Start
            .WithReference(logServer) // Connect To Structured Log Server
            .WithEnvironment("CHAT_SERVER_HOST", chatServerHost)
            .WithEnvironment("CHAT_SERVER_PORT_CLIENT", chatServerClientConnectionsPort.ToString())
            .WithEnvironment("CHAT_SERVER_PORT_MATCH_SERVER", chatServerMatchServerConnectionsPort.ToString())
            .WithEnvironment("CHAT_SERVER_PORT_MATCH_SERVER_MANAGER", chatServerMatchServerManagerConnectionsPort.ToString())
            .WithEnvironment("INFRASTRUCTURE_GATEWAY", gateway);

        // Add Web Portal API Project
        IResourceBuilder<ProjectResource> webPortalAPI = builder.AddProject<ZORGATH>("web-portal-api", builder.Environment.IsProduction() ? "ZORGATH.WebPortal.API Production" : "ZORGATH.WebPortal.API Development")
            .WithReference(database, connectionName: "MERRICK").WaitFor(database) // Connect To SQL Server Database And Wait For It To Start
            .WaitFor(databaseContext) // Wait For Database Initialization And Production Credential Rotation To Complete
            .WithReference(logServer) // Connect To Structured Log Server
            .WithEnvironment("INFRASTRUCTURE_GATEWAY", gateway);

        // Add Local STMP Server In Development
        if (builder.Environment.IsDevelopment())
        {
            IResourceBuilder<ContainerResource> smtpServer = builder.AddContainer("smtp-server", "axllent/mailpit")
                .WithImageTag("latest").WithImageSHA256("5a49a77c5bdbe7c5474450b4f46348d09949df3695257729c93a30369382d4f6") // Pin The Tested Mailpit 1.30.4 Image
                .WithLifetime(ContainerLifetime.Persistent)
                .WithEndpoint(port: 1025, targetPort: 1025, name: "smtp", scheme: "tcp") // Default SMTP Port
                .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "http"); // Default Web UI Port

            webPortalAPI.WaitFor(smtpServer);
        }

        // Populate AWS SES Configuration In Staging/Production/etc.
        else
        {
            // Rotate The Publicly-Known Built-In Credentials Before Production Accepts Traffic
            const string builtInAdministratorEmailAddress = "kongor@kongor.fans";
            const string builtInOperatorEmailAddress = "operator@kongor.fans";
            const string builtInModeratorEmailAddress = "moderator@kongor.fans";

            const string builtInAdministratorPasswordParameterName = "built-in-administrator-password";
            const string builtInAdministratorPasswordEnvironmentVariableName = "BUILT_IN_ADMINISTRATOR_PASSWORD";
            IResourceBuilder<ParameterResource> builtInAdministratorPassword = AddSecretParameter(
                builder,
                configuration,
                builtInAdministratorPasswordParameterName,
                builtInAdministratorPasswordEnvironmentVariableName);

            const string builtInOperatorPasswordParameterName = "built-in-operator-password";
            const string builtInOperatorPasswordEnvironmentVariableName = "BUILT_IN_OPERATOR_PASSWORD";
            IResourceBuilder<ParameterResource> builtInOperatorPassword = AddSecretParameter(
                builder,
                configuration,
                builtInOperatorPasswordParameterName,
                builtInOperatorPasswordEnvironmentVariableName);

            const string builtInModeratorPasswordParameterName = "built-in-moderator-password";
            const string builtInModeratorPasswordEnvironmentVariableName = "BUILT_IN_MODERATOR_PASSWORD";
            IResourceBuilder<ParameterResource> builtInModeratorPassword = AddSecretParameter(
                builder,
                configuration,
                builtInModeratorPasswordParameterName,
                builtInModeratorPasswordEnvironmentVariableName);

            databaseContext
                .WithEnvironment("BUILT_IN_ADMINISTRATOR_EMAIL", builtInAdministratorEmailAddress)
                .WithEnvironment(builtInAdministratorPasswordEnvironmentVariableName, builtInAdministratorPassword)
                .WithEnvironment("BUILT_IN_OPERATOR_EMAIL", builtInOperatorEmailAddress)
                .WithEnvironment(builtInOperatorPasswordEnvironmentVariableName, builtInOperatorPassword)
                .WithEnvironment("BUILT_IN_MODERATOR_EMAIL", builtInModeratorEmailAddress)
                .WithEnvironment(builtInModeratorPasswordEnvironmentVariableName, builtInModeratorPassword);

            builtInAdministratorPassword.WithDescription("Built-In Administrator Production Password").WithParentRelationship(databaseContext);
            builtInOperatorPassword.WithDescription("Built-In Operator Production Password").WithParentRelationship(databaseContext);
            builtInModeratorPassword.WithDescription("Built-In Moderator Production Password").WithParentRelationship(databaseContext);

            // Set JWT Signing Key Parameter Name And Environment Variable Name
            const string jwtSigningKeyParameterName = "jwt-signing-key";
            const string jwtSigningKeyEnvironmentVariableName = "JWT_SIGNING_KEY";

            // Explicit Runtime Environment Variables Override Development User Secrets During Production Deployment
            string? resolvedJWTSigningKey = configuration[jwtSigningKeyEnvironmentVariableName] ?? configuration[$"Parameters:{jwtSigningKeyParameterName}"];

            // Populate JWT Signing Key If Available In User Secrets Or Environment Variables
            IResourceBuilder<ParameterResource> jwtSigningKey = resolvedJWTSigningKey is not null
                ? builder.AddParameter(jwtSigningKeyParameterName, resolvedJWTSigningKey, secret: true)
                : builder.AddParameter(jwtSigningKeyParameterName, secret: true);

            // Pass The Production JWT Signing Key To The Web Portal API Without Storing It In Tracked Configuration
            webPortalAPI.WithEnvironment("Operational__JWT__SigningKey", jwtSigningKey);

            // Create Resource Relationship After Parent Resource Is Defined
            jwtSigningKey.WithDescription("Web Portal JWT Signing Key").WithParentRelationship(webPortalAPI);

            // Set SMTP Host Parameter Name And Environment Variable Name
            const string smtpHostParameterName = "smtp-host";
            const string smtpHostEnvironmentVariableName = "SMTP_HOST";

            // Explicit Runtime Environment Variables Override Development User Secrets During Production Deployment
            string? resolvedSMTPHost = configuration[smtpHostEnvironmentVariableName] ?? configuration[$"Parameters:{smtpHostParameterName}"];

            // Populate SMTP Host If Available In User Secrets Or Environment Variables
            IResourceBuilder<ParameterResource> smtpHost = resolvedSMTPHost is not null
                ? builder.AddParameter(smtpHostParameterName, resolvedSMTPHost)
                : builder.AddParameter(smtpHostParameterName);

            // Set SMTP Port Parameter Name And Environment Variable Name
            const string smtpPortParameterName = "smtp-port";
            const string smtpPortEnvironmentVariableName = "SMTP_PORT";

            // Explicit Runtime Environment Variables Override Development User Secrets During Production Deployment
            string? resolvedSMTPPort = configuration[smtpPortEnvironmentVariableName] ?? configuration[$"Parameters:{smtpPortParameterName}"];

            // Populate SMTP Port If Available In User Secrets Or Environment Variables
            IResourceBuilder<ParameterResource> smtpPort = resolvedSMTPPort is not null
                ? builder.AddParameter(smtpPortParameterName, resolvedSMTPPort)
                : builder.AddParameter(smtpPortParameterName);

            // Set SMTP Username Parameter Name And Environment Variable Name
            const string smtpUsernameParameterName = "smtp-username";
            const string smtpUsernameEnvironmentVariableName = "SMTP_USERNAME";

            // Explicit Runtime Environment Variables Override Development User Secrets During Production Deployment
            string? resolvedSMTPUsername = configuration[smtpUsernameEnvironmentVariableName] ?? configuration[$"Parameters:{smtpUsernameParameterName}"];

            // Populate SMTP Username If Available In User Secrets Or Environment Variables
            IResourceBuilder<ParameterResource> smtpUsername = resolvedSMTPUsername is not null
                ? builder.AddParameter(smtpUsernameParameterName, resolvedSMTPUsername, secret: true)
                : builder.AddParameter(smtpUsernameParameterName, secret: true);

            // Set SMTP Password Parameter Name And Environment Variable Name
            const string smtpPasswordParameterName = "smtp-password";
            const string smtpPasswordEnvironmentVariableName = "SMTP_PASSWORD";

            // Explicit Runtime Environment Variables Override Development User Secrets During Production Deployment
            string? resolvedSMTPPassword = configuration[smtpPasswordEnvironmentVariableName] ?? configuration[$"Parameters:{smtpPasswordParameterName}"];

            // Populate SMTP Password If Available In User Secrets Or Environment Variables
            IResourceBuilder<ParameterResource> smtpPassword = resolvedSMTPPassword is not null
                ? builder.AddParameter(smtpPasswordParameterName, resolvedSMTPPassword, secret: true)
                : builder.AddParameter(smtpPasswordParameterName, secret: true);

            // Pass SMTP Configuration To Web Portal API
            webPortalAPI
                .WithEnvironment("SMTP_HOST", smtpHost)
                .WithEnvironment("SMTP_PORT", smtpPort)
                .WithEnvironment("SMTP_USERNAME", smtpUsername)
                .WithEnvironment("SMTP_PASSWORD", smtpPassword);

            // Create Resource Relationships After Parent Resource Is Defined
            smtpHost.WithDescription("SMTP Host").WithParentRelationship(webPortalAPI);
            smtpPort.WithDescription("SMTP Port").WithParentRelationship(webPortalAPI);
            smtpUsername.WithDescription("SMTP Username").WithParentRelationship(webPortalAPI);
            smtpPassword.WithDescription("SMTP Password").WithParentRelationship(webPortalAPI);
        }

        // Add Web Portal UI Project
        # pragma warning disable ASPIREBROWSERLOGS001
        builder.AddProject<DAWNBRINGER>("web-portal-ui", builder.Environment.IsProduction() ? "DAWNBRINGER.WebPortal.UI Production" : "DAWNBRINGER.WebPortal.UI Development")
            .WithReference(webPortalAPI).WaitFor(webPortalAPI) // Connect To Web Portal API And Wait For It To Start
            .WithReference(logServer) // Connect To Structured Log Server
            .WithEnvironment("INFRASTRUCTURE_GATEWAY", gateway)
            .WithBrowserLogs(userDataMode: BrowserUserDataMode.Isolated); // Experimental Extension; Surfaces Web Browser Logs In The Aspire Dashboard
        # pragma warning restore ASPIREBROWSERLOGS001

        // Start Orchestrating Distributed Application
        builder.Build().Run();
    }

    private static IResourceBuilder<ParameterResource> AddSecretParameter(
        IDistributedApplicationBuilder builder,
        IConfiguration configuration,
        string parameterName,
        string environmentVariableName)
    {
        string? resolvedValue = configuration[environmentVariableName] ?? configuration[$"Parameters:{parameterName}"];

        return resolvedValue is not null
            ? builder.AddParameter(parameterName, resolvedValue, secret: true)
            : builder.AddParameter(parameterName, secret: true);
    }
}
