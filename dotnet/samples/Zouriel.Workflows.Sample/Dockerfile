# The sample app in playground mode, as deployed behind zouriel.com/uilib/workflow-api.
# Build from the dotnet/ folder: docker build -f samples/Zouriel.Workflows.Sample/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish samples/Zouriel.Workflows.Sample -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    Playground__Enabled=true \
    Playground__Directory=/tmp/playground \
    DOTNET_gcServer=0
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Zouriel.Workflows.Sample.dll"]
