using ClassHub_API.Data;
using ClassHub_API.Hubs;
using ClassHub_API.Interfaces;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace ClassHub_API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var secretsFile = Environment.GetEnvironmentVariable("CLASSHUB_SECRETS_FILE");
            if (!string.IsNullOrEmpty(secretsFile)) {
                if (!Path.IsPathFullyQualified(secretsFile)) throw new InvalidOperationException("CLASSHUB_SECRETS_FILE must be absolute.");
                builder.Configuration.AddJsonFile(secretsFile, optional:false, reloadOnChange:false)
                    .AddEnvironmentVariables().AddCommandLine(args);
            }
            var logPath = builder.Configuration["Logging:FileDirectory"];
            if (!string.IsNullOrWhiteSpace(logPath)) builder.Logging.AddProvider(new RollingFileLogger(logPath, builder.Configuration));

            string connectionString =
                builder.Configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "Thiếu ConnectionStrings:DefaultConnection trong User Secrets.");

            string jwtSecret =
                builder.Configuration["JwtSettings:SecretKey"]
                ?? throw new InvalidOperationException(
                    "Thiếu JwtSettings:SecretKey trong User Secrets.");

            string jwtIssuer =
                builder.Configuration["JwtSettings:Issuer"]
                ?? throw new InvalidOperationException(
                    "Thiếu JwtSettings:Issuer trong appsettings.json.");

            string jwtAudience =
                builder.Configuration["JwtSettings:Audience"]
                ?? throw new InvalidOperationException(
                    "Thiếu JwtSettings:Audience trong appsettings.json.");

            if (Encoding.UTF8.GetByteCount(jwtSecret) < 32)
            {
                throw new InvalidOperationException("JwtSettings:SecretKey phải có ít nhất 32 byte.");
            }

            string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddProblemDetails();
            builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("dependencies", timeout:TimeSpan.FromSeconds(5), tags:new[] { "ready" });
            builder.Services.AddSignalR();

            builder.Services.AddDbContext<AppDbContext>(options =>
            {
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
            });

            builder.Services.AddScoped<ITokenService, TokenService>();
            builder.Services.AddSingleton<IMqttService, MqttService>();
            builder.Services.AddHostedService<BackgroundServices>();

            builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;

                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters =
                    new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,

                        ValidIssuer = jwtIssuer,
                        ValidAudience = jwtAudience,

                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),

                        ClockSkew = TimeSpan.FromMinutes(1)
                    };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        string accessToken =
                            context.Request.Query["access_token"].ToString();

                        PathString requestPath = context.HttpContext.Request.Path;

                        if (!string.IsNullOrWhiteSpace(accessToken) && requestPath.StartsWithSegments("/hub/cabinet"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

            builder.Services.AddScoped<QrAccessService>();
            builder.Services.AddSingleton<CabinetCamera>();
            builder.Services.AddHttpClient("return-vision").ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddScoped<ReturnInspectionService>();
            builder.Services.AddScoped<BatteryAssessmentService>();
            builder.Services.AddSingleton<RfidEnrollmentTokens>();
            builder.Services.AddSingleton<BatteryForecastModel>();
            builder.Services.AddSingleton<IotAnomalyModel>();
            builder.Services.AddHostedService<ReturnInspectionWorker>();
            builder.Services.AddHostedService<OperationRulesWorker>();
            builder.Services.AddAuthorization();

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("FrontendPolicy", policy =>
                {
                    if (allowedOrigins.Length > 0)
                    {
                        policy
                            .WithOrigins(allowedOrigins)
                            .AllowAnyHeader()
                            .AllowAnyMethod()
                            .AllowCredentials();

                        return;
                    }

                    if (builder.Environment.IsDevelopment())
                    {
                        policy
                            .SetIsOriginAllowed(_ => true)
                            .AllowAnyHeader()
                            .AllowAnyMethod()
                            .AllowCredentials();

                        return;
                    }

                    throw new InvalidOperationException(
                        "Phải cấu hình Cors:AllowedOrigins "
                        + "khi chạy ngoài môi trường Development.");
                });
            });

            if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://0.0.0.0:5146");

            var app = builder.Build();

            app.UseExceptionHandler();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseCors("FrontendPolicy");

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();
            app.MapHub<CabinetHub>("/hub/cabinet");
            app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
            app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

            app.Run();
        }
    }
}
