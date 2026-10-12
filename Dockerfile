# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-nanoserver-ltsc2022 AS build
WORKDIR /src
COPY NuGet.config ./
COPY Sluice.Portal.sln ./
COPY Sluice.Portal/Sluice.Portal.csproj Sluice.Portal/
RUN dotnet restore Sluice.Portal/Sluice.Portal.csproj
COPY Sluice.Portal/ Sluice.Portal/
# Guía de usuario (Markdown): la referencia el .csproj como Content (..\docs\guia-usuario)
# y se empaqueta para servirla desde /ayuda. Debe estar en el contexto antes del publish.
COPY docs/ docs/
RUN dotnet publish Sluice.Portal/Sluice.Portal.csproj -c Release -r win-x64 -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-windowsservercore-ltsc2022 AS runtime
SHELL ["powershell", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]
WORKDIR /app

# Microsoft ODBC Driver 18 for SQL Server (required by Microsoft.Data.SqlClient)
RUN Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=2249004' -OutFile 'msodbcsql.msi'; \
    Start-Process msiexec.exe -Wait -ArgumentList '/i', 'msodbcsql.msi', '/quiet', 'IACCEPTMSODBCSQLLICENSETERMS=YES'; \
    Remove-Item msodbcsql.msi

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:58472
EXPOSE 58472
HEALTHCHECK CMD powershell -Command "try { (Invoke-WebRequest -Uri http://localhost:58472/health -UseBasicParsing).StatusCode -eq 200 } catch { exit 1 }"
ENTRYPOINT ["dotnet", "Sluice.Portal.dll"]