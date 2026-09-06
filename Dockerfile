# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/FamilyDashboard.Web/FamilyDashboard.Web.csproj FamilyDashboard.Web/
RUN dotnet restore FamilyDashboard.Web/FamilyDashboard.Web.csproj

COPY src/FamilyDashboard.Web/ FamilyDashboard.Web/
RUN dotnet publish FamilyDashboard.Web/FamilyDashboard.Web.csproj -c Release -o /app --no-restore

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root user for defense in depth on a home-network appliance.
RUN useradd -m dashboard
USER dashboard

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV DataDirectory=/data
ENV PhotoDirectory=/photos

EXPOSE 8080
ENTRYPOINT ["dotnet", "FamilyDashboard.Web.dll"]
