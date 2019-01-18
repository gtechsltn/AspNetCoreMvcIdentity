﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Funq;
using MyApp.Data;
using MyApp.Models;
using MyApp.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Twitter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyApp.ServiceInterface;
using ServiceStack;
using ServiceStack.Auth;
using ServiceStack.Configuration;
using ServiceStack.Data;
using ServiceStack.Host.NetCore;
using ServiceStack.OrmLite;
using ServiceStack.Web;

namespace MyApp
{
    /// <summary>
    /// To create Identity SQL Server database, change "ConnectionStrings" in appsettings.json
    ///   $ dotnet ef migrations add CreateMyAppIdentitySchema
    ///   $ dotnet ef database update
    /// </summary>
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection")));

            services.AddIdentity<ApplicationUser, IdentityRole>(options => {
                    options.User.AllowedUserNameCharacters = null;
                })
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();
            
            services.AddAuthentication(IISDefaults.AuthenticationScheme)
                .AddTwitter(options =>
                {
                    options.ConsumerKey = Configuration["oauth.twitter.ConsumerKey"];
                    options.ConsumerSecret = Configuration["oauth.twitter.ConsumerSecret"];
                    options.SaveTokens = true;
                    options.RetrieveUserDetails = true;
                })
                .AddFacebook(options => {
                    options.AppId = Configuration["oauth.facebook.AppId"];
                    options.AppSecret = Configuration["oauth.facebook.AppSecret"];
                    options.SaveTokens = true;
                    options.Scope.Clear();
                    Configuration.GetSection("oauth.facebook.Permissions").GetChildren()
                        .Each(x => options.Scope.Add(x.Value));
                })
                .AddGoogle(options => {
                    options.ClientId = Configuration["oauth.google.ConsumerKey"];
                    options.ClientSecret = Configuration["oauth.google.ConsumerSecret"];
                    options.SaveTokens = true;
                })
                .AddMicrosoftAccount(options => {
                    options.ClientId = Configuration["oauth.microsoftgraph.AppId"];
                    options.ClientSecret = Configuration["oauth.microsoftgraph.AppSecret"];
                    options.SaveTokens = true;
                });
            
            services.Configure<ForwardedHeadersOptions>(options => {
                //https://github.com/aspnet/IISIntegration/issues/140#issuecomment-215135928
                options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            });
            
            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = false;
                options.Password.RequiredUniqueChars = 6;

                // Lockout settings
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(30);
                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.AllowedForNewUsers = true;

                // User settings
                options.User.RequireUniqueEmail = true;
            });

            services.ConfigureApplicationCookie(options =>
            {
                // Cookie settings
                options.Cookie.HttpOnly = true;
                options.Cookie.Expiration = TimeSpan.FromDays(150);
                // If the LoginPath isn't set, ASP.NET Core defaults 
                // the path to /Account/Login.
                options.LoginPath = "/Account/Login";
                // If the AccessDeniedPath isn't set, ASP.NET Core defaults 
                // the path to /Account/AccessDenied.
                options.AccessDeniedPath = "/Account/AccessDenied";
                options.SlidingExpiration = true;
            });

            // Add application services.
            services.AddTransient<IEmailSender, EmailSender>();

            // Populate ApplicationUser with Auth Info
            services.AddTransient<IExternalLoginAuthInfoProvider>(c => 
                new ExternalLoginAuthInfoProvider(Configuration));
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            app.UseStaticFiles();
            app.UseCookiePolicy();
            app.UseAuthentication();
            
            app.UseServiceStack(new AppHost
            {
                AppSettings = new NetCoreAppSettings(Configuration)
            });

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
            });
            
            AddSeedUsers(app).Wait();
        }
        
        private async Task AddSeedUsers(IApplicationBuilder app)
        {
            var scopeFactory = app.ApplicationServices.GetRequiredService<IServiceScopeFactory>();

            using (var scope = scopeFactory.CreateScope())
            {
                //initializing custom roles 
                var RoleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var UserManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                string[] roleNames = { "Admin", "Manager" };

                void assertResult(IdentityResult result)
                {
                    if (!result.Succeeded)
                        throw new Exception(result.Errors.First().Description);
                }

                foreach (var roleName in roleNames)
                {
                    var roleExist = await RoleManager.RoleExistsAsync(roleName);
                    if (!roleExist)
                    {
                        //create the roles and seed them to the database: Question 1
                        assertResult(await RoleManager.CreateAsync(new IdentityRole(roleName)));
                    }
                }
            
                var testUser = await UserManager.FindByEmailAsync("user@gmail.com");
                if (testUser == null)
                {
                    assertResult(await UserManager.CreateAsync(new ApplicationUser {
                        DisplayName = "Test User",
                        Email = "user@gmail.com",
                        UserName = "user@gmail.com",
                        FirstName = "Test",
                        LastName = "User",
                    }, "p@55wOrd"));
                }

                var managerUser = await UserManager.FindByEmailAsync("manager@gmail.com");
                if (managerUser == null)
                {
                    assertResult(await UserManager.CreateAsync(new ApplicationUser {
                        DisplayName = "Test Manager",
                        Email = "manager@gmail.com",
                        UserName = "manager@gmail.com",
                        FirstName = "Test",
                        LastName = "Manager",
                    }, "p@55wOrd"));
                    
                    managerUser = await UserManager.FindByEmailAsync("manager@gmail.com");
                    assertResult(await UserManager.AddToRoleAsync(managerUser, "Manager"));
                }

                var adminUser = await UserManager.FindByEmailAsync("admin@gmail.com");
                if (adminUser == null)
                {
                    assertResult(await UserManager.CreateAsync(new ApplicationUser {
                        DisplayName = "Admin User",
                        Email = "admin@gmail.com",
                        UserName = "admin@gmail.com",
                        FirstName = "Admin",
                        LastName = "User",
                    }, "p@55wOrd"));
                    
                    adminUser = await UserManager.FindByEmailAsync("admin@gmail.com");
                    assertResult(await UserManager.AddToRoleAsync(adminUser, RoleNames.Admin));
                }
            }
        }
        
    }
    
    public class AppHost : AppHostBase
    {
        public AppHost() : base("MyApp", typeof(MyServices).Assembly) { }

        // Configure your AppHost with the necessary configuration and dependencies your App needs
        public override void Configure(Container container)
        {
            SetConfig(new HostConfig
            {
                DebugMode = AppSettings.Get(nameof(HostConfig.DebugMode), false),
                AdminAuthSecret = "adm1nSecret",
            });

            // TODO: Replace OAuth App settings in: appsettings.Development.json
            Plugins.Add(new AuthFeature(() => new CustomUserSession(), 
                new IAuthProvider[] {
                    new NetCoreIdentityAuthProvider(AppSettings) // Adapter to enable ServiceStack Auth in MVC
                    {
                        AdminRoles = { "Manager" }, // Automatically Assign additional roles to Admin Users
                        PopulateSessionFilter = (session, principal, req) => 
                        {
                            //Example of populating ServiceStack Session Roles + Custom Info from EF Identity DB
                            var userManager = req.TryResolve<UserManager<ApplicationUser>>();
                            var user = userManager.FindByIdAsync(session.Id).Result;
                            var roles = userManager.GetRolesAsync(user).Result;
                            session.Roles = roles.ToList();
                            session.Email = session.Email ?? user.Email;
                            session.FirstName = session.FirstName ?? user.FirstName;
                            session.LastName = session.LastName ?? user.LastName;
                            session.DisplayName = session.DisplayName ?? user.DisplayName;
                            session.ProfileUrl = user.ProfileUrl ?? AuthMetadataProvider.DefaultNoProfileImgUrl;
                        }
                    }, 
                }));
        }
    }
    
    // Add any additional metadata properties you want to store in the Users Typed Session
    public class CustomUserSession : AuthUserSession
    {
    }
}
