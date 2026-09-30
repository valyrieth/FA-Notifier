FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY FaNotify.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "FaNotify.dll"]