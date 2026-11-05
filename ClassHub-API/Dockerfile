# Build context: backend repository root (contains ClassHub-API/).
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ClassHub-API/ClassHub-API.csproj", "ClassHub-API/"]
RUN dotnet restore "ClassHub-API/ClassHub-API.csproj"
COPY ClassHub-API/ ClassHub-API/
RUN dotnet publish "ClassHub-API/ClassHub-API.csproj" \
    -c "$BUILD_CONFIGURATION" -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ClassHub-API.dll"]
