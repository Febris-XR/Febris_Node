using AspNetCoreRateLimit;
using Febris.UserNode.Portal.IdentityPolicy;
using Febris.ModelLibrary.Models.UserModels;
using Febris.UserNode.Portal.BackgroundTasks;
using Febris.SharedServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Febris.UserNode.DataAccessLayer.DataContext;
using Febris.UserNode.DataAccessLayer;
using Febris.UserNode.Portal.LocalUtility;
using Febris.EnumLibrary;
using Serilog;
using Febris.ModelLibrary.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Febris.PrimaryLogicLayer.Logic.XApiLogic;
// NET8 Wave 4: deprecated Microsoft.Extensions.Caching.Redis package removed; StackExchangeRedis
// provides the same RedisCache/RedisCacheOptions type names.
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Authorization;
using Febris.PrimaryLogicLayer.Logic.UserLogic;
using Febris.UserNode.LogicLayer.Logic.DataLogic;
using Febris.UserNode.LogicLayer.Logic.UserLogic;
using Febris.PrimaryLogicLayer.Logic.DataLogic;
using Febris.UserNode.DataAccessLayer.Claims;
using Microsoft.AspNetCore.Authentication;
using Febris.UserNode.LogicLayer.Logic;
using Febris.UserNode.LogicLayer.Logic.HealthLogic;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Febris.UserNode.LogicLayer.Logic.AnalyticsLogic;

namespace Febris.UserNode.Portal
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            Smb.Configuration = configuration;
        }

        #region outside config calls
        public static class Smb
        {
            public static IConfiguration Configuration;
        }
        public IConfiguration Configuration { get; }
        #endregion


        private DirectoryInfo GetKyRingDirectoryInfo()
        {
            // MDM-B8 fix (net8 boot): the previous do/while(keyRingPath != null) spun FOREVER when the
            // configured key-ring directory did not already exist -- keyRingPath was read once and never
            // mutated, and the loop only returned on Exists==true, so a fresh deployment whose
            // AppKeys:KeyRingPath dir hadn't been created yet hung the host in ConfigureServices.
            // Replaced with an idempotent create-if-missing that fails fast on missing config or an
            // unwritable path. Old (infinite-loop) version:
            //   string keyRingPath = Configuration.GetSection("AppKeys").GetValue<string>("KeyRingPath");
            //   do { var d = new DirectoryInfo($"{keyRingPath}"); if (d.Exists) return d; }
            //   while (keyRingPath != null);
            //   throw new Exception($"key ring path not found");
            string keyRingPath = Configuration.GetSection("AppKeys").GetValue<string>("KeyRingPath");
            if (string.IsNullOrWhiteSpace(keyRingPath))
            {
                throw new InvalidOperationException(
                    "AppKeys:KeyRingPath is not configured -- the DataProtection key ring has no persistence " +
                    "location, so every auth cookie/ticket would be invalidated on each restart. Set it in " +
                    "appsettings or the environment.");
            }
            DirectoryInfo keyRingDirectoryInfo = new DirectoryInfo(keyRingPath);
            if (!keyRingDirectoryInfo.Exists)
            {
                keyRingDirectoryInfo.Create();   // idempotent; throws fast on an invalid/unwritable path
                keyRingDirectoryInfo.Refresh();
            }
            return keyRingDirectoryInfo;
        }

        #region This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {


            #region data protection keys persistance
            services.AddDataProtection()
                        .PersistKeysToFileSystem(GetKyRingDirectoryInfo())
                        .SetApplicationName("Febris.UserAuth");
            #endregion

            Log.Information("Key Persistance complete");

            // DI refactor activation: scoped per-tenant DbContexts +
            // convention-registered IXxxQueries, so the BLL's greedy DI ctors resolve with
            // DbContext-backed LOCAL query instances (incl. the node-local vocabulary stores)
            // instead of the legacy self-newing path. TryAddScoped keeps explicit registrations.
            services.AddFebrisUserNodeDataAccess(Configuration);

            // Storage seam LIVE: binds the "Storage" config section
            // (FileSystem default, legacy SmbClient:Path fallback) and registers IStorageProvider.
            // The portal's LocalSoftwarePackageLogic (downloads page + package archive) now
            // streams package bytes from the node's own artifact store through this seam, so its
            // greedy DI ctor must be satisfiable here too.
            Febris.SharedServices.Storage.FebrisStorageRegistration.AddFebrisStorage(services, Configuration);

            // Module ingest in the PORTAL, not just the API. This is the write path that stores a
            // module .zip through IStorageProvider, records its SHA-256 on a PackageArtifact row
            // and upserts the local Module catalog row. It was API-only, which meant the sole way
            // to add a module was a hand-authored multipart POST carrying a NodeAdmin token minted
            // from a route with no UI. A standalone node has to be able to add content from its own
            // portal, so the authoring form fronts this same logic rather than duplicating it.
            services.AddScoped<IPackageIngestLogic, PackageIngestLogic>();

            // ROADMAP 16: the feed-sync trigger moved from the API (NodeAdmin-token POST) to the
            // Portal's FeedSync form, so the Portal now resolves the sync logic too. Its *Queries
            // dependencies (IPackageFeedQueries and friends) come from the AddFebrisUserNodeDataAccess
            // convention sweep above, same as on the API host.
            services.AddScoped<IPackageFeedSyncLogic, PackageFeedSyncLogic>();

            // Companion to the DAL registration so StatementLogic's greedy DI ctor is fully
            // satisfiable (an unresolvable IStatementFileHandler makes MS.DI silently fall
            // back to the legacy self-newing ctor -- the exact degradation the DI seam must
            // not have). Non-*Queries, so the convention sweep does not cover it.
            services.AddScoped<Febris.SharedServices.IStatementFileHandler, Febris.SharedServices.StatementFileHandler>();

            // WidgetController's media loaders (images/badges/profile pictures/recordings) now take
            // IFileServerHandler through DI instead of self-newing it. Still the legacy handler on
            // purpose: the IStorageProvider swap for the media areas is blocked on the Phase 3
            // layout reconciliation documented on StorageKeys (Specific-rooted, mixed-case legacy
            // paths have no verified-clean key builders yet). Non-*Queries, so the convention
            // sweep does not cover it.
            services.AddScoped<Febris.SharedServices.IFileServerHandler, Febris.SharedServices.FileServerHandler>();

            #region database connection
            //************************************************************************************
            //databases using postgresql
            //************************************************************************************            
            services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(Configuration.GetConnectionString("UserDBConnection")));
            // NET8 Wave 4: dev-only EF diagnostics; replaces UseDatabaseErrorPage (removed in net6+).
            services.AddDatabaseDeveloperPageExceptionFilter();
            #endregion

            Log.Information("connection string Settings complete");

            #region caching
            services.AddSingleton<IDistributedUserCache>(x =>
            {
                var options = x.GetRequiredService<IOptions<RedisCacheOptions>>();
                //options.Value.Configuration = ...  set you server IP, etc
                options.Value.Configuration = StaticDetails.PassedBackConfig.GetSection("RedisConnectionStrings").GetValue<string>("AuthConnection");
                return new DistributedUserCache(options);
            });
            //services.AddSingleton<IDistributedLicenseCache>(x =>
            //{
            //    var options = x.GetRequiredService<IOptions<RedisCacheOptions>>();
            //    //options.Value.Configuration = ...  set you server IP, etc
            //    options.Value.Configuration = StaticDetails.PassedBackConfig.GetSection("RedisConnectionStrings").GetValue<string>("LicenseConnection");
            //    return new DistributedLicenseCache(options);
            //});
            // A-02 Stage 2: the revocation list the JWT middleware consults per request and
            // HardwareLogic publishes to on lock. Scoped, so it enters JwtHardwareMiddleware
            // through Invoke rather than its constructor.
            services.AddScoped<IHardwareRevocationList, HardwareRevocationList>();
            services.AddSingleton<IDistributedHardwareCache>(x =>
            {
                var options = x.GetRequiredService<IOptions<RedisCacheOptions>>();
                //options.Value.Configuration = ...  set you server IP, etc
                options.Value.Configuration = StaticDetails.PassedBackConfig.GetSection("RedisConnectionStrings").GetValue<string>("HardwareConnection");
                return new DistributedHardwareCache(options);
            });

            //no idea if this is needed
            services.AddResponseCaching(options =>
            {
                options.UseCaseSensitivePaths = true;
                options.MaximumBodySize = 1024;
            });
            #endregion

            Log.Information("cach adding complete");

            #region Form options
            services.Configure<FormOptions>(x =>
            {
                x.ValueLengthLimit = 5000; // Limit on individual form values
                x.MultipartBodyLengthLimit = Configuration.GetValue<long?>("UploadLimits:MaxMultipartBodyBytes") ?? 10737418240;// file-upload-hardening: env-tunable, defaults to prior 10 GiB value
                x.MultipartHeadersLengthLimit = 737280000; // Limit on form header size
            });
            #endregion

            Log.Information("Form options");

            #region Auth and Cookies
            #region cookie policies
            services.Configure<CookiePolicyOptions>(options =>
            {
                // CheckConsentNeeded (the ASP.NET Core 2.x template line) was removed with
                // _CookieConsentPartial.cshtml: the Portal writes no non-essential cookie, and the
                // template never shipped a UI that could grant consent (ROADMAP 17).
                //options.MinimumSameSitePolicy = SameSiteMode.Lax;// .None;
                options.MinimumSameSitePolicy = SameSiteMode.None;
                //options.Secure = CookieSecurePolicy.Always;
            });
            #endregion

            #region cookie Authorization
            services.AddAuthorization(options =>
            {
                options.AddPolicy("FebrisAuth",
                    builder => builder.RequireRole(
                        InstitutionUserAccountType.User.ToString(),
                        InstitutionUserAccountType.Admin.ToString(),
                        InstitutionUserAccountType.ITAdmin.ToString(),
                        InstitutionUserAccountType.Educator.ToString(),
                        InstitutionUserAccountType.UserParent.ToString(),
                        FebrisUserType.SuperAdmin.ToString()
                        ));
            });
            #endregion

            #region Redis Caching
            ///trying this so I can use more than one caching system
            services.AddSingleton<IDistributedUserCache>(x =>
            {
                var options = x.GetRequiredService<IOptions<RedisCacheOptions>>();
                //options.Value.Configuration = ...  set you server IP, etc
                options.Value.Configuration = StaticDetails.PassedBackConfig.GetSection("RedisConnectionStrings").GetValue<string>("AuthConnection");
                return new DistributedUserCache(options);
            });
            //register my custom store -- ONLY when Redis/Valkey is configured.
            // No Redis configured -> no server-side ticket store; the DataProtection-encrypted cookie
            // carries the ticket (single-instance zero-dependency default). Swaps by config.
            if (NodeSessionPolicy.UsesRedisSessionStore(Configuration))
            {
                services.AddSingleton<ITicketStore, RedisCacheTicketStore>();
            }
            #endregion

            #region cookie Authentication
            ///https://www.red-gate.com/simple-talk/development/dotnet-development/using-auth-cookies-in-asp-net-core/
            ///https://docs.microsoft.com/en-us/aspnet/core/security/cookie-sharing?view=aspnetcore-3.1
            // Identity policy gates: identity/auth/lifecycle policy is
            // operator-configurable from the "Identity" appsettings section (env-overridable via
            // Identity__Section__Key) with safe defaults that live in code, so a missing section keeps
            // the same behavior. Bound once here and applied below.
            IdentityPolicyOptions identityPolicy =
                Configuration.GetSection(IdentityPolicyOptions.SectionName).Get<IdentityPolicyOptions>()
                ?? new IdentityPolicyOptions();
            services.Configure<IdentityPolicyOptions>(Configuration.GetSection(IdentityPolicyOptions.SectionName));

            // Where the Software Repository pages send an operator when this node holds no local
            // copy of a client package. A node's catalogue starts empty and only fills by manual
            // upload or a feed sync, neither of which anything obliges a self-host operator to do,
            // so those pages were a dead end on every fresh deployment. Defaults resolve to the
            // project's public download page; blanking "ClientDownloads:BaseUrl" turns link-out
            // off entirely for an air-gapped site. A missing section keeps the default, matching
            // how every other options section here behaves.
            services.Configure<ClientDownloadOptions>(Configuration.GetSection(ClientDownloadOptions.SectionName));

            // Registration policy is resolved DB-FIRST (node initialization design 2026-08-18).
            // A stored NodeRegistrationConfig row (the portal's Registration page) governs when
            // present; otherwise the configured "Identity:Registration" section governs unchanged,
            // so a deployment that never opens the page behaves exactly as before. An UNREADABLE
            // store resolves AdminOnly rather than falling back to configuration -- see the
            // resolver's class doc for why that asymmetry is the point.
            //
            // Singleton, not scoped: IRegistrationPolicy is consumed synchronously (including from
            // a Razor inject on the login page), so the DB read is confined to a short-TTL refresh
            // exactly as HubFederationSettingsResolver does it. RegistrationPolicy itself is
            // unchanged and still makes every admission decision -- the resolver only decides WHICH
            // RegistrationOptions it gets.
            services.AddSingleton<NodeRegistrationPolicyResolver>();
            services.AddSingleton<IRegistrationPolicy>(provider =>
                provider.GetRequiredService<NodeRegistrationPolicyResolver>());
            services.AddSingleton<IRegistrationPolicyCache>(provider =>
                provider.GetRequiredService<NodeRegistrationPolicyResolver>());
            services.AddSingleton<ICsvUserImporter, CsvUserImporter>();

            // Operator-configurable transport security (SELF_HOSTING.md: TLS and reverse proxying). Safe defaults
            // preserve the prior production posture; a missing "Transport" section keeps them. The pipeline
            // middleware (HSTS/HTTPS-redirect/CORS/security-headers) is wired in Configure() from these.
            NodeTransportOptions transport =
                Configuration.GetSection(NodeTransportOptions.SectionName).Get<NodeTransportOptions>()
                ?? new NodeTransportOptions();
            services.Configure<NodeTransportOptions>(Configuration.GetSection(NodeTransportOptions.SectionName));
            services.AddHsts(o =>
            {
                o.MaxAge = TimeSpan.FromDays(transport.Hsts.MaxAgeDays);
                o.IncludeSubDomains = transport.Hsts.IncludeSubdomains;
                o.Preload = transport.Hsts.Preload;
            });
            // AccountLifecycle.PurgeAfterDays: daily hard-delete of soft-deleted accounts retained past the
            // cap. Fails safe (no-op) when PurgeAfterDays is unset (the default).
            services.AddScoped<ISoftDeletedUserPurger, SoftDeletedUserPurger>();
            services.AddHostedService<SoftDeletedUserPurgeService>();

            // AnalyticsRetention (T11): daily trim of the request-analytics tables, which grew one
            // row per HTTP request with no retention at all and hold per-request learner PII that is
            // rendered to Org Admins. Unlike the two purgers above this defaults ON, because here
            // the defect is KEEPING the data rather than deleting it (see finding H-26).
            //
            // Registered on the PORTAL ONLY. Both hosts write into the same analytics database, so
            // registering it on both would have two processes deleting from one table on overlapping
            // schedules. The host that owns the analytics screens is the host that bounds them.
            services.AddScoped<IAnalyticsRetentionReaper, AnalyticsRetentionReaper>();
            services.AddHostedService<Febris.UserNode.Portal.BackgroundTasks.AnalyticsRetentionService>();

            // PackageFeedSyncService was written, documented and never registered, so it had no
            // effect whatsoever. Its options were never bound either, meaning even an operator who
            // set PackageFeed:Url got nothing: no sync, and not even the "idle" line the service
            // logs to explain itself. The consequence is the one its own config comment predicts.
            // The manual upload path was removed, so a feed is the ONLY automatic way packages
            // reach a node, the Mobile Server fetches the Companion FROM the node, and a node with
            // an empty catalogue cannot serve headsets. Measured 2026-09-02, LocalSoftwarePackage
            // and PackageArtifact both held 0 rows and the Mobile Server got a 404 from
            // /api/CompanionApp/getlatestversion on every launch.
            //
            // Binding the options section is what makes the shipped blank Url meaningful. With no
            // Url the service still starts, logs that it is idle, and returns, so this does not
            // make a node reach out to anything it was not configured to trust.
            services.Configure<Febris.UserNode.Portal.BackgroundTasks.PackageFeedOptions>(
                Configuration.GetSection("PackageFeed"));
            services.AddHostedService<Febris.UserNode.Portal.BackgroundTasks.PackageFeedSyncService>();

            // Identity core policy (password/lockout/confirmed-email) is applied in a named,
            // [EnforcesGate]-annotated method so the coverage ratchet (IdentityGateCoverageTests) can
            // prove each knob is honored. Do not inline these copies back into a lambda -- attributes
            // cannot attach to a lambda, and the gate would read as unenforced.
            services.AddIdentity<LocalApplicationUser, ApplicationRole>(
                config => ApplyIdentityCorePolicy(config, identityPolicy))
              .AddEntityFrameworkStores<ApplicationDbContext>()
              .AddDefaultTokenProviders();
            //.AddDefaultUI();

            // the session store + cookie security swap by configuration.
            //  - Redis/Valkey configured -> server-side ticket store + HTTPS-strict cookie (the
            //    cookie is only a key; the ticket is shared across instances for HA).
            //  - Not configured -> the ticket lives in the encrypted cookie (no server store), and
            //    the cookie is relaxed so login works over plain-HTTP localhost with only a database.
            //    A non-Secure cookie MUST be Lax -- browsers reject SameSite=None without Secure.
            services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IServiceProvider>((options, serviceProvider) =>
            {
                options.Cookie.Name = "Febris.AuthCookie";
                options.Cookie.Path = "/";
                if (NodeSessionPolicy.UsesRedisSessionStore(Configuration))
                {
                    options.Cookie.SameSite = SameSiteMode.None;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.SessionStore = serviceProvider.GetRequiredService<ITicketStore>();
                }
                else
                {
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    // options.SessionStore intentionally left null -> ASP.NET stores the ticket in the cookie.
                }
            });
            #endregion

            #region External SSO providers (task #15 scaffolding 2026-05-20)
            // Config-driven external auth provider registration. An institution
            // that has its own SSO (Okta, Azure AD, Auth0, Google Workspace,
            // generic OpenID Connect) can plug it in by populating
            // appsettings.json -> ExternalAuthProviders rather than editing
            // this file. See ExternalAuthProviderRegistration.cs XML docs for
            // the enablement steps (NuGet adds + uncomment the AddXxx calls).
            //
            // The Register page is intentionally closed (per portal policy);
            // external SSO is the supported way for an institution's own
            // users to sign in without admin-issued invite tokens.
            services.AddExternalAuthProvidersFromConfig(Configuration);
            #endregion

            #region Application cookie
            services.ConfigureApplicationCookie(
                options => ApplyApplicationCookiePolicy(options, identityPolicy));

            #endregion

            #region SSO Authentication cookie

            //// configure authentication options
            //services.Configure<AuthenticationOptions>(options =>
            //{
            //    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            //    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            //    options.DefaultChallengeScheme = "SSO"; // use SSO middleware for authentication
            //});

            //// map SSO claims to Identity claims
            //services.Configure<ClaimsIdentityOptions>(options =>
            //{
            //    options.MapUniqueJsonKey("email", "email");
            //    options.MapUniqueJsonKey("name", "name");
            //    // additional claim mapping
            //});

            //// protect sensitive data
            //services.AddDataProtection();

            #endregion
            #endregion

            #region password recovery            
            services.AddTransient<IEmailSender, EmailService>(i =>
                new EmailService(Configuration));
            #endregion

            Log.Information("Cookie policies complete");

            #region set static objects for injectable values
            //This is used to route URLS for logins and what not from APP data
            //services.Configure<LocalUserAPIConfig>
            //    (Configuration.GetSection("ApiPaths"));
            //API for tile server
            // The commented-out services.Configure<GeoDataUrls> that sat here was half of a dead map
            // widget. Both halves went in ROADMAP 18, and the third piece, the Geocoder call on the
            // Location save path that wrote coordinates the removed map was the only reader of, is
            // now gone too. No GeoDataUrls key is read anywhere, so none ships.
            #endregion

            Log.Information("Configure Model setup Complete");

            #region MVC settings
            //************************************************************************************
            //added for 3.0 compatability            
            //adding this to hopefully fix Identity
            services.AddControllersWithViews();

            // Audit A-07 (2026-05-20): AddMvcOptions here modifies the SHARED
            // MvcOptions used by BOTH Razor Pages AND MVC controllers, so the
            // "FebrisAuth" policy gate applies globally to every endpoint in
            // this portal -- including controllers with no per-class [Authorize].
            // The "FebrisAuth" policy is defined ~145 lines above in the
            // "cookie Authorization" region (RequireRole: User, Educator, Admin,
            // ITAdmin, UserParent, SuperAdmin).
            // Any new controller automatically gets this gate; if a controller
            // must be anonymous (e.g., a public /health endpoint), it MUST be
            // explicitly decorated with [AllowAnonymous].
            services.AddRazorPages()
                .AddMvcOptions(options => options.Filters.Add(new AuthorizeFilter("FebrisAuth")));

            services.AddMvc(options =>
            {
                options.EnableEndpointRouting = false;
                //*********************************************************************
                //auto applying validation tokens everywhere - all actions and controllers - this could be a little redicouls 
                //*********************************************************************
                // ENABLED. This is the default-deny that makes every future POST safe without
                // anyone remembering an attribute. It was commented out with the note above
                // calling it "a little redicouls", which left exactly three POST actions
                // unprotected -- and they were the bulk user CREATE, CSV import and REMOVAL
                // endpoints, the highest-damage writes on the node.
                //
                // Safe to switch on here: the Portal is browser-only (no [ApiController], no
                // api/* routes, no machine-to-machine POST surface -- the launcher and mobile
                // clients talk to the separate API host). Razor Pages already auto-validate.
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());

                //*********************************************************************
                //require https*******************************************************************************************************************************************
                //*********************************************************************
                //options.Filters.Add(new RequireHttpsAttribute());
            })
                // NET8 Wave 4: SetCompatibilityVersion was a no-op since 3.0; removed.
                .AddNewtonsoftJson();

            // EXPLICIT PIN, not a behaviour change. AntiforgeryOptions.HeaderName ALREADY defaults
            // to "RequestVerificationToken" (verified against .NET 8, not assumed), so this line
            // sets the value it would have had anyway. It is kept because BulkUserProcessing.js
            // hard-codes that exact header name, and a silent framework default is a poor place
            // for a coupling the client depends on.
            //
            // An earlier version of this comment claimed the missing header config was why the
            // JS token sends were commented out. That was WRONG -- the header would always have
            // been read. The piece that was actually missing is the token HOLDER: the form in
            // Views/User/BulkCreatePartial.cshtml was commented out too, so the JS had nothing
            // to read and would have sent an empty header.
            services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

            services.AddControllers().AddNewtonsoftJson();
            //services.AddMvc().AddNewtonsoftJson();

            #endregion

            Log.Information("Compatibility Complete");

            #region Scoping/registering            

            #region Local
            // Duplicate of the Redis-Caching-region registration; both gated on config.
            if (NodeSessionPolicy.UsesRedisSessionStore(Configuration))
            {
                services.AddSingleton<ITicketStore, RedisCacheTicketStore>();
            }
            services.AddScoped<ILocalAnalyticsLogic, LocalAnalyticsLogic>();
            services.AddScoped<IUserAnalyticsLogic, UserAnalyticsLogic>();
            services.AddScoped<IAnalyticsLogic, AnalyticsLogic>();
            services.AddScoped<IModuleDownloadAnalyticsLogic, ModuleDownloadAnalyticsLogic>();
            services.AddScoped<IModuleUsageAnalyticsLogic, ModuleUsageAnalyticsLogic>();
            services.AddScoped<IWidgetLogic, WidgetLogic>();

            #region Data
            services.AddScoped<IMessageBoardLogic, MessageBoardLogic>();
            // Video ownership: backs the entitlement check on both WidgetController video loaders.
            services.AddScoped<Febris.UserNode.LogicLayer.Logic.DataLogic.IRecordingLogic, Febris.UserNode.LogicLayer.Logic.DataLogic.RecordingLogic>();
            services.AddScoped<ILocationLogic, LocationLogic>();
            services.AddScoped<ICohortLogic, CohortLogic>();
            services.AddScoped<ICohortMemberLogic, CohortMemberLogic>();
            services.AddScoped<IHardwareLogic, HardwareLogic>();
            services.AddScoped<IHardwareLinkedCohortLogic, HardwareLinkedCohortLogic>();

            #endregion

            #region User
            services.AddScoped<IUserLogic, UserLogic>();
            // Admin-only parent/guardian to student link management (FERPA).
            services.AddScoped<IParentLinkLogic, ParentLinkLogic>();

            // ROADMAP 16: the NodeAdmin token-mint surface that lived here (a lazy
            // IJwtSigningKeyProvider singleton plus INodeAdminAuthorization) is deleted. The
            // admin-only API writes the token existed to reach moved into this Portal behind
            // cookie auth, so the Portal no longer signs JWTs at all -- the signing key is the
            // API host's concern alone, and the ROADMAP 18 Development-waiver logging for it
            // lives in the API's Startup.

            //this is for making and using identity claims (Cookies)
            services.AddScoped<IUserClaimsPrincipalFactory<LocalApplicationUser>, SupplementalClaimFactory>();
            services.AddScoped<IClaimsTransformation, FebrisClaimsTransformer>();
            #endregion

            #region XApi
            services.AddScoped<IActorLogic, ActorLogic>();
            services.AddScoped<IAttachmentLogic, AttachmentLogic>();
            services.AddScoped<IAuthorityLogic, AuthorityLogic>();
            services.AddScoped<IContextLogic, ContextLogic>();
            services.AddScoped<IDefinitionLogic, DefinitionLogic>();
            services.AddScoped<IExtensionsLogic, ExtensionsLogic>();
            services.AddScoped<IResultLogic, ResultLogic>();
            services.AddScoped<IStatementLogic, StatementLogic>();
            services.AddScoped<IStatementVoidingLogic, StatementVoidingLogic>();
            services.AddScoped<IStatementDownloadLogic, StatementDownloadLogic>();
            #endregion

            #region Generic injections
            services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();

            //Allows logic layer to get cookies and PrincipalUser
            services.AddHttpContextAccessor();
            services.AddSingleton<IActionContextAccessor, ActionContextAccessor>();
            //services.AddScoped<ICookieHandler, CookieHandler>()
            //    .AddOptions();
            //services.AddScoped<IWidgetLogic, WidgetLogic>();

            #endregion

            #endregion

            #region Remote
            #region Data logic
            // IAdminMessageBoardLogic and IMessageLogic removed with the hub messaging teardown
            // (owner ruling 2026-08-01). Both were hub-backed behind a closed federation gate, so
            // they rendered permanently blank pages rather than failing. The LOCAL MessageBoard is
            // node-owned and stays.
            services.AddScoped<IHardwareLinkedModuleLogic, HardwareLinkedModuleLogic>();
            //services.AddScoped<IInvoiceLogic, InvoiceLogic>();
            services.AddScoped<IInstitutionLogic, InstitutionLogic>();
            services.AddScoped<IInstitutionSettingsLogic, InstitutionSettingsLogic>();
            services.AddScoped<ILocalSoftwarePackageLogic, LocalSoftwarePackageLogic>();
            services.AddScoped<ILiabilityWaiverLogic, LiabilityWaiverLogic>();
            services.AddScoped<IModuleLogic, ModuleLogic>();
            services.AddScoped<IModuleLinkedObjectLogic, ModuleLinkedObjectLogic>();
            //services.AddScoped<IPurchaseLogic, PurchaseLogic>();
            services.AddScoped<IXRHardwareModelLogic, XRHardwareModelLogic>();
            // Category / Industry / Focus / Tag registrations removed: marketplace-scoped taxonomy
            // (owner ruling 2026-08-01). ITagLogic was registered TWICE -- here and again in the
            // xAPI region -- so the second silently won. The MODELS remain in
            // shared/FebrisModelLibrary for a future marketplace; only node plumbing is gone.
            // Relocated out of #region Marketplace: TestUser is node-local and has nothing to do
            // with the marketplace. It sat inside that region, so removing the region as a block
            // would have killed TestUser DI silently -- a runtime resolution failure on first
            // request, not a compile error.

            #endregion

            // #region Purchasing removed entirely (owner ruling 2026-08-01): commerce/billing is a
            // permanently-closed hub capability (OSS_RELEASE_MAP 4.1.3) and no commerce model has
            // ever had a table on the node. Cohort access no longer derives from Purchase -- it is
            // curriculum-derived, see CohortLogic.GetCohortAccessList.

            // #region Marketplace removed entirely (owner ruling 2026-08-01): the marketplace is a
            // hub capability and no marketplace model has ever had a table on the node. The
            // ITestUserLogic registration that was trapped inside this region moved to
            // #region Data logic first -- deleting the block with it still here would have killed
            // TestUser DI at runtime rather than at compile time.

            #region XApi logic
            services.AddScoped<IObjectLogic, ObjectLogic>();
            services.AddScoped<IVerbLogic, VerbLogic>();
            services.AddScoped<IVersionLogic, VersionLogic>();
            #endregion

            #region User
            services.AddScoped<ILiabilityWaiverLogic, LiabilityWaiverLogic>();
            #endregion

            #region Add Auth policy
            //services.AddAuthorization(options =>
            //{
            //    options.AddPolicy("HasSignedPaperwork",
            //        policy => policy.Requirements.Add(new Microsoft.AspNetCore.Authorization.HasSignedPaperwork()));
            //});
            #endregion

            #endregion


            Log.Information("Scoping Complete");

            //services.AddTransient<ClaimsPrincipal>(s =>
            //    s.GetService<IHttpContextAccessor>().HttpContext.User);

            #endregion

            #region S-01 audit fix: per-IP rate limiting (AspNetCoreRateLimit)
            // Throttles anonymous /Identity/Account/Login traffic. Rules live
            // in appsettings.json under "IpRateLimiting" (5/15min on login,
            // 120/min default). In-memory counters; swap to a distributed
            // store if EndUserPortal scales beyond a single instance.
            services.AddMemoryCache();
            services.Configure<AspNetCoreRateLimit.IpRateLimitOptions>(Configuration.GetSection("IpRateLimiting"));
            services.AddSingleton<AspNetCoreRateLimit.IIpPolicyStore, AspNetCoreRateLimit.MemoryCacheIpPolicyStore>();
            services.AddSingleton<AspNetCoreRateLimit.IRateLimitCounterStore, AspNetCoreRateLimit.MemoryCacheRateLimitCounterStore>();
            services.AddSingleton<AspNetCoreRateLimit.IRateLimitConfiguration, AspNetCoreRateLimit.RateLimitConfiguration>();
            services.AddSingleton<AspNetCoreRateLimit.IProcessingStrategy, AspNetCoreRateLimit.AsyncKeyLockProcessingStrategy>();
            services.AddInMemoryRateLimiting();
            #endregion

            // Node health site: readiness checks for what THIS host owns.
            // Deliberately LAST in ConfigureServices -- the helper inspects the service
            // collection (ApplicationDbContext + the tenant contexts, IStorageProvider, the
            // Redis cache abstractions) to decide which checks exist, so every ownership
            // registration above must already be present.
            services.AddNodeHealthChecks(Configuration);

            // The admin status page's aggregation (health report + node identity + package
            // versions + storage usage + gate state). Greenfield node logic, DI-only per
            // (non-*Queries, so the convention sweep does not cover it).
            services.AddScoped<INodeStatusLogic, NodeStatusLogic>();

            // The operator's Hub Federation page: stored settings read/save (license
            // key encrypted at rest through the DAL's dedicated protector) + the gate-aware hub
            // reachability probe. Greenfield node logic, DI-only per 
            // (non-*Queries, so the convention sweep does not cover it).
            services.AddScoped<Febris.UserNode.LogicLayer.Logic.FederationLogic.IHubFederationSettingsLogic,
                Febris.UserNode.LogicLayer.Logic.FederationLogic.HubFederationSettingsLogic>();

            // Hub-pull sync: the page's "Sync now" pass -- pull-only enrichment of
            // the local vocabulary/catalog stores behind the gate. Greenfield node logic, DI-only
            // per (non-*Queries, so the convention sweep does not cover it).
            services.AddScoped<Febris.UserNode.LogicLayer.Logic.FederationLogic.IHubSyncLogic,
                Febris.UserNode.LogicLayer.Logic.FederationLogic.HubSyncLogic>();

            // The operator's Registration page: read/save the node's stored registration policy
            // (node initialization design 2026-08-18). Also the collaborator the registration
            // RESOLVER reaches for through a fresh scope on every TTL refresh, which is why its
            // absence is treated as a fault there rather than as "no policy stored". Greenfield
            // node logic, DI-only (non-*Queries, so the convention sweep does not cover it).
            services.AddScoped<Febris.UserNode.LogicLayer.Logic.IdentityLogic.INodeRegistrationSettingsLogic,
                Febris.UserNode.LogicLayer.Logic.IdentityLogic.NodeRegistrationSettingsLogic>();

            // Account invitations (invitation flow 2026-08-21): issue, list, revoke, validate and
            // consume. Deliberately NOT gated on the registration mode -- an invited person was
            // named by an operator entitled to create their account outright, so this is
            // admin-initiated creation rather than self-registration. Greenfield node logic,
            // DI-only (non-*Queries, so the convention sweep does not cover it).
            services.AddScoped<Febris.UserNode.LogicLayer.Logic.IdentityLogic.INodeInvitationLogic,
                Febris.UserNode.LogicLayer.Logic.IdentityLogic.NodeInvitationLogic>();

            // First-run claim (2026-08-21): issue and redeem the one-time setup token that creates
            // the node's first ITAdmin. Resolved by the STARTUP SEEDER as well as by the /setup
            // page, which is why its absence is logged as an error there rather than ignored -- a
            // node with no ITAdmin and no way to issue a token cannot be claimed at all.
            services.AddScoped<Febris.UserNode.LogicLayer.Logic.IdentityLogic.INodeSetupLogic,
                Febris.UserNode.LogicLayer.Logic.IdentityLogic.NodeSetupLogic>();
        }


        #endregion


        // ---- Identity-policy enforcement points (IdentityGateCoverageTests ratchet) --------------
        // These named methods copy operator-configured policy into the ASP.NET Identity / cookie
        // primitives that actually enforce it. Each [EnforcesGate] marks one leaf of
        // IdentityPolicyOptions as honored; the coverage test fails the build if a declared gate is
        // neither marked here nor listed in DeferredGates. Keep them as named methods -- attributes
        // cannot attach to the DI lambdas these are called from.

        [EnforcesGate("Password.RequiredLength")]
        [EnforcesGate("Password.RequireDigit")]
        [EnforcesGate("Password.RequireUppercase")]
        [EnforcesGate("Password.RequireLowercase")]
        [EnforcesGate("Password.RequireNonAlphanumeric")]
        [EnforcesGate("Password.RequiredUniqueChars")]
        [EnforcesGate("Lockout.MaxFailedAttempts")]
        [EnforcesGate("Lockout.LockoutMinutes")]
        [EnforcesGate("Lockout.EnabledForNewUsers")]
        [EnforcesGate("Registration.RequireConfirmedEmail")]
        private static void ApplyIdentityCorePolicy(IdentityOptions config, IdentityPolicyOptions identityPolicy)
        {
            // Password policy (safe defaults in "Identity:Password").
            config.Password.RequiredLength = identityPolicy.Password.RequiredLength;
            config.Password.RequireDigit = identityPolicy.Password.RequireDigit;
            config.Password.RequireUppercase = identityPolicy.Password.RequireUppercase;
            config.Password.RequireLowercase = identityPolicy.Password.RequireLowercase;
            config.Password.RequireNonAlphanumeric = identityPolicy.Password.RequireNonAlphanumeric;
            config.Password.RequiredUniqueChars = identityPolicy.Password.RequiredUniqueChars;

            // Lockout policy (Audit S-04) -- was hardcoded 5 / 15 min; same safe defaults, now configurable.
            config.Lockout.MaxFailedAccessAttempts = identityPolicy.Lockout.MaxFailedAttempts;
            config.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(identityPolicy.Lockout.LockoutMinutes);
            config.Lockout.AllowedForNewUsers = identityPolicy.Lockout.EnabledForNewUsers;

            // Require a confirmed email before sign-in. Admin/bulk-provisioned users are created
            // pre-confirmed (UserLogic), so this gates only self-registered accounts.
            config.SignIn.RequireConfirmedEmail = identityPolicy.Registration.RequireConfirmedEmail;
        }

        [EnforcesGate("Session.LifetimeMinutes")]
        [EnforcesGate("Session.Sliding")]
        [EnforcesGate("Session.AbsoluteTimeoutMinutes")]
        private static void ApplyApplicationCookiePolicy(CookieAuthenticationOptions options, IdentityPolicyOptions identityPolicy)
        {
            options.AccessDeniedPath = "/Identity/Account/AccessDenied";
            options.Cookie.Name = "Febris.AuthCookie";
            options.Cookie.HttpOnly = true;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(identityPolicy.Session.LifetimeMinutes);
            options.LoginPath = "/Identity/Account/Login";
            options.ReturnUrlParameter = CookieAuthenticationDefaults.ReturnUrlParameter;
            options.SlidingExpiration = identityPolicy.Session.Sliding;

            // Absolute session cap (Session.AbsoluteTimeoutMinutes): ExpireTimeSpan + SlidingExpiration only
            // bound IDLE time -- with sliding on, an active session renews forever. Stamp an absolute
            // deadline at real sign-in (AuthenticationProperties survive sliding renewals, unlike IssuedUtc)
            // and reject + sign out once it passes. A null timeout means no stamp and no enforcement (default).
            int? absoluteTimeout = identityPolicy.Session.AbsoluteTimeoutMinutes;
            options.Events ??= new CookieAuthenticationEvents();

            // CHAIN, do not replace: AddIdentity wires OnValidatePrincipal to SecurityStampValidator
            // (invalidates cookies on password/security-stamp change -- "sign out everywhere"). Overwriting
            // it would silently drop that. Run the prior handler first and only enforce our absolute cap if
            // it did not already reject the principal.
            Func<CookieValidatePrincipalContext, Task> priorValidate = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = async context =>
            {
                if (priorValidate != null)
                {
                    await priorValidate(context);
                }
                if (context.Principal == null)
                {
                    return; // already rejected upstream (e.g. security-stamp mismatch)
                }
                if (AbsoluteSessionTimeout.IsExpired(context.Properties, DateTimeOffset.UtcNow))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
            };

            Func<CookieSigningInContext, Task> priorSigningIn = options.Events.OnSigningIn;
            options.Events.OnSigningIn = async context =>
            {
                if (priorSigningIn != null)
                {
                    await priorSigningIn(context);
                }
                AbsoluteSessionTimeout.Stamp(context.Properties, absoluteTimeout, DateTimeOffset.UtcNow);
            };
        }


        #region This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // MED-6: surface any config value still left as a literal {Placeholder} in a deployed
            // environment (a missing ConfigMap/Secret injection). Logs by default; throws only when
            // ConfigValidation:FailFastOnUnresolvedPlaceholders=true. No-op in Development.
            var unresolvedPlaceholders = ConfigurationPlaceholderValidator.Validate(Configuration);
            if (unresolvedPlaceholders.Count > 0)
            {
                Log.Warning("Unresolved configuration placeholders (no deploy-time value injected): {Placeholders}", string.Join(", ", unresolvedPlaceholders));
            }

            // Operator-configurable transport security (Transport section; safe defaults preserve prior prod).
            NodeTransportOptions transport =
                Configuration.GetSection(NodeTransportOptions.SectionName).Get<NodeTransportOptions>()
                ?? new NodeTransportOptions();

            // MUST run first: behind a TLS-terminating reverse proxy (the primary self-host topology) the
            // request arrives as HTTP, so X-Forwarded-Proto must correct Request.Scheme/IsHttps BEFORE HSTS
            // (which skips emission when !IsHttps), HTTPS redirection, and auth. ASP.NET best practice.
            // Was a hard-coded options object with NO KnownProxies/KnownNetworks, so it kept the
            // framework's loopback-only default. Any proxy on another address -- the bundled Caddy
            // container, a Kubernetes ingress pod -- had its X-Forwarded-For DISCARDED, and every
            // caller resolved to the proxy's own address. That silently degraded analytics AND the
            // refresh-token IP binding at HardwareKeyAuthorization:133,:167, which became the same
            // constant for every device.
            //
            // Now operator-declared. With no ForwardedHeaders section present the behaviour is
            // identical to before, so this cannot change an existing deployment until opted into.
            Microsoft.AspNetCore.Builder.ForwardedHeadersOptions forwardedOptions =
                Febris.SharedServices.ForwardedHeadersConfiguration.Build(Configuration);
            if (forwardedOptions != null)
            {
                app.UseForwardedHeaders(forwardedOptions);
            }

            #region env specific
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                // NET8 Wave 4: UseDatabaseErrorPage removed in net6+; replaced by the filter below.
                //app.UseDatabaseErrorPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // HSTS (non-Development only). Config-driven via AddHsts(Transport:Hsts) in ConfigureServices;
                // the operator can disable it (e.g. when TLS terminates at a proxy that owns the header).
                if (transport.Hsts.Enabled)
                {
                    app.UseHsts();
                }
            }
            #endregion

            Log.Information("Environment Config Complete");


            #region What is this?
            // (UseForwardedHeaders moved to the top of Configure so it precedes HSTS -- see there.)

            //custom analytics
            app.UseLocalAnalytics();

            // The previous SECOND (NWebsec) UseHsts here was removed: HSTS is now the single config-driven
            // built-in above (Transport:Hsts), which also fixes it having run in Development.
            if (transport.SecurityHeaders.XXssProtection)
            {
                app.UseXXssProtection(options => options.EnabledWithBlockMode());
            }
            if (transport.SecurityHeaders.XContentTypeOptions)
            {
                app.UseXContentTypeOptions();
            }
            // X-Frame-Options (clickjacking), operator-configured via Transport:SecurityHeaders:XFrameOptions.
            // "Off" omits it; "Deny" blocks all framing; anything else (incl. "SameOrigin" or a typo) fails
            // safe to SameOrigin so protection is never silently lost.
            if (!string.Equals(transport.SecurityHeaders.XFrameOptions, "Off", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(transport.SecurityHeaders.XFrameOptions, "Deny", StringComparison.OrdinalIgnoreCase))
                {
                    app.UseXfo(o => o.Deny());
                }
                else
                {
                    app.UseXfo(o => o.SameOrigin());
                }
            }
            #endregion

            // Data seeding (roles + the bootstrap ITAdmin) moved to Program.Main: it now runs
            // awaited, once, BEFORE the host serves traffic. See SeedData.SeedAllDataAsync.

            Log.Information("Protection Middleware addition Complete");

            // Off by default (Transport:HttpsRedirection): self-host nodes usually terminate TLS at a proxy,
            // where an app-level redirect would loop. Operators fronting the app directly can enable it.
            if (transport.HttpsRedirection)
            {
                app.UseHttpsRedirection();
            }

            #region Logging and routing
            app.UseStaticFiles();

            app.UseSerilogRequestLogging();

            app.UseCors(builder =>
            {
                // Operator-configured origins (Transport:Cors:AllowedHosts) + localhost, replacing the
                // febr.is hardcoding so a self-host node can trust its OWN frontend. Empty list => same-origin
                // only. A specific-origin predicate (never "*") keeps AllowCredentials valid.
                builder.SetIsOriginAllowed(origin => NodeTransport.IsOriginAllowed(origin, transport.Cors.AllowedHosts));
                builder.AllowAnyMethod();
                builder.AllowAnyHeader();
                if (transport.Cors.AllowCredentials)
                {
                    builder.AllowCredentials();
                }
            });

            ///This is commented out in example
            app.UseResponseCaching();

            // S-01 audit fix: per-IP rate limit middleware. Placed before
            // UseRouting so throttled requests short-circuit prior to
            // endpoint resolution. Rules: appsettings.json -> IpRateLimiting.
            app.UseIpRateLimiting();

            app.UseRouting();
            #endregion


            #region Cookie policies
            app.UseCookiePolicy();

            app.UseAuthentication();

            app.UseAuthorization();

            // TwoFactor.Enforcement: after auth so HttpContext.User is populated. When 2FA is required,
            // an authenticated-but-unenrolled user is redirected to authenticator setup and blocked until
            // they enroll. No-op (pass-through) when Enforcement=Off (the default).
            app.UseTwoFactorEnrollmentGate();
            #endregion

            Log.Information("Other middleware policies Complete");

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");
                //endpoints.MapRazorPages();
                endpoints.MapRazorPages().RequireAuthorization();
                // Node health site: anonymous /health/live + /health/ready for
                // Docker/K8s probes (which cannot authenticate). Health endpoints are not MVC,
                // so the global "FebrisAuth" AuthorizeFilter does not apply. AllowAnonymous is
                // stamped explicitly in the mapper regardless.
                //
                // The body is TERSE unless this host is Development or the operator opts in with
                // HealthChecks:DetailedResponse. Anonymous plus enumerated dependencies is a
                // deployment inventory, and the previous 404 in the bundled Caddy config protected
                // only operators who ran that proxy.
                endpoints.MapNodeHealthEndpoints(
                    NodeHealthRegistration.ResolveDetailedResponse(Configuration, env.IsDevelopment()));
            });

            Log.Information("Routing Complete");
            //app.UseSession();
            //app.UseMvc(routes =>
            //{
            //    routes.MapRoute(
            //            name: "default",
            //            template: "{controller=Home}/{action=Index}/{id?}");
            //});
        }

        #endregion

        //private void CheckFebrisAuth(HttpContext httpContext, CookieOptions options)
        //{
        //    Log.Information("****CheckFebris Auth Started ****");
        //    if (options.SameSite == SameSiteMode.None)
        //    {
        //        var userAgent = httpContext.Request.Headers["Febris.Auth"].ToString();
        //        //if (SameSite.BrowserDetection.DisallowsSameSiteNone(userAgent))
        //        //{
        //        //    options.SameSite = SameSiteMode.Unspecified;
        //        //}
        //        Log.Information("***Febris.Auth cookie: " + userAgent + "****");
        //    }
        //}


        #region This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        //public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        //{
        //    if (env.IsDevelopment())
        //    {
        //        app.UseDeveloperExceptionPage();
        //        app.UseDatabaseErrorPage();
        //    }
        //    else
        //    {
        //        app.UseExceptionHandler("/Home/Error");
        //        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        //        app.UseHsts();
        //    }

        //    //SeedData.SeedAllData(app);

        //    #region What is this?
        //    //*********************************************************************
        //    //preventing cross site scripting 
        //    //*********************************************************************
        //    app.UseForwardedHeaders(new ForwardedHeadersOptions
        //    {
        //        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        //    });
        //    //custom analytics
        //    app.UseLocalAnalytics();

        //    //*********************************************************************
        //    //I am not sure what this is but it seems to also be in production area
        //    //*********************************************************************
        //    app.UseHsts(options => options.MaxAge(days: 365).IncludeSubdomains());
        //    //*********************************************************************
        //    //*********************************************************************
        //    app.UseXXssProtection(options => options.EnabledWithBlockMode());
        //    //*********************************************************************
        //    //*********************************************************************
        //    app.UseXContentTypeOptions();
        //    #endregion

        //    app.UseHttpsRedirection();
        //    app.UseStaticFiles();

        //    app.UseSerilogRequestLogging();

        //    app.UseCookiePolicy();

        //    app.UseCors();
        //    app.UseResponseCaching();

        //    app.UseRouting();

        //    app.UseAuthentication();
        //    app.UseAuthorization();

        //    app.UseEndpoints(endpoints =>
        //    {
        //        endpoints.MapControllerRoute(
        //            name: "default",
        //            pattern: "{controller=Home}/{action=Index}/{id?}");
        //        endpoints.MapRazorPages();
        //    });
        //}
        #endregion


    }
}
