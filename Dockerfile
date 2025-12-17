# Base image for Azure Functions .NET Isolated Worker  
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated9.0 AS base  
WORKDIR /home/site/wwwroot  
  
# Change to port 80 so it matches Kubernetes service definition  
EXPOSE 80  
  
# Build stage  
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build  
ARG BUILD_CONFIGURATION=Release  
WORKDIR /src  
COPY ["MailNotificationFunctionApp.csproj", "./"]  
RUN dotnet restore "./MailNotificationFunctionApp.csproj"  
COPY . .  
RUN dotnet build "./MailNotificationFunctionApp.csproj" -c $BUILD_CONFIGURATION -o /app/build  
  
# Publish stage  
FROM build AS publish  
ARG BUILD_CONFIGURATION=Release  
RUN dotnet publish "./MailNotificationFunctionApp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false  
  
# Final runtime image  
FROM base AS final  
WORKDIR /home/site/wwwroot  
COPY --from=publish /app/publish .  
  
# Environment variables — runtime config comes from Kubernetes ConfigMaps/Secrets  
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \  
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \  
    ASPNETCORE_URLS=http://+:80