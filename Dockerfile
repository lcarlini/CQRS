FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Commerce.Cqrs.slnx Directory.Build.props ./
COPY src/Commerce.Cqrs.Api/*.csproj src/Commerce.Cqrs.Api/
COPY src/Commerce.Cqrs.Application/*.csproj src/Commerce.Cqrs.Application/
COPY src/Commerce.Cqrs.Domain/*.csproj src/Commerce.Cqrs.Domain/
COPY src/Commerce.Cqrs.Infrastructure/*.csproj src/Commerce.Cqrs.Infrastructure/
RUN dotnet restore src/Commerce.Cqrs.Api/Commerce.Cqrs.Api.csproj

COPY src/ src/
RUN dotnet publish src/Commerce.Cqrs.Api/Commerce.Cqrs.Api.csproj \
    --configuration Release --output /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password --gecos "" --uid 10001 appuser
COPY --from=build /app .
USER appuser
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Commerce.Cqrs.Api.dll"]
