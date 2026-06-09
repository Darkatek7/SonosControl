FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV DataProtection__KeysDirectory=/app/DataProtectionKeys
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg python3 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["SonosControl.Web/SonosControl.Web.csproj", "SonosControl.Web/"]
COPY ["SonosControl.DAL/SonosControl.DAL.csproj", "SonosControl.DAL/"]
COPY ["SonosControl.Tests/SonosControl.Tests.csproj", "SonosControl.Tests/"]
RUN dotnet restore "SonosControl.Web/SonosControl.Web.csproj"
COPY . .
WORKDIR "/src/SonosControl.Web"
RUN dotnet build "SonosControl.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SonosControl.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/Data /app/DataProtectionKeys /app/artifacts/youtube-audio
ENTRYPOINT ["dotnet", "SonosControl.Web.dll"]
