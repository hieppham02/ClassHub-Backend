# Deploy ClassHub API to Render

## Backend repository layout
This Dockerfile expects the repository root to contain ClassHub-API/ClassHub-API.csproj.
For the existing standalone backend repository:
- Language: Docker
- Root Directory: empty
- Dockerfile Path: ClassHub-API/Dockerfile
- Docker Build Context Directory: .
- Docker Command: empty (use ENTRYPOINT)
- Instance: Free for demonstration
- Health Check Path: /health

If using a monorepo containing Backend/ClassHub-API, set Root Directory to Backend/ClassHub-API and keep the other paths above relative to that directory.

## Environment
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:10000
ConnectionStrings__DefaultConnection: MySQL .NET connection string for Aiven/defaultdb with TLS.
JwtSettings__SecretKey: configured signing secret, at least 32 bytes.
JwtSettings__Issuer=ClassHubAPI
JwtSettings__Audience=ClassHubWeb
Cors__AllowedOrigins__0: exact frontend origin without trailing slash.
MqttSettings__BrokerHost: existing broker hostname, no scheme.
MqttSettings__Port=8883
MqttSettings__Username and MqttSettings__Password: existing broker credentials.
MqttSettings__ClientId=classhub-render-backend

Use different MQTT client IDs for local and cloud backend instances.
Do not run local and cloud backend workers against the same cabinet simultaneously during cutover; they can process the same commands/results.
Do not copy User Secrets or SQL dumps into the image.
The container does not create or migrate database tables.

## Verify after deploy
GET /health should return Healthy (process liveness only).
GET /health/ready also checks configured dependencies; investigate a non-success status.
Swagger is enabled only in Development, so /swagger is not a production verification URL.
Render Free may sleep and pause MQTT/background tasks. Files written in the container are not persistent.
Aiven database rows persist independently. Camera LAN access requires a separate network solution.
Frontend build settings use VITE_API_URL=https://<backend>/api and VITE_HUB_URL=https://<backend>.

## Verification limits
Docker CLI is not installed on the preparation machine; Docker image has not been built or run locally.
No deployment or server restart is performed by changing these files.
