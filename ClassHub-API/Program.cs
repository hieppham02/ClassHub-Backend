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
            builder.Services.AddHealthChecks();
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

            builder.WebHost.UseUrls("http://0.0.0.0:5146");

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
            app.MapHealthChecks("/health");

            app.Run();
        }
    }
}