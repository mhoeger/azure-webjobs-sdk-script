FROM microsoft/dotnet:2.0.0-runtime-stretch-arm32v7

ENV PublishWithAspNetCoreTargetManifest false
RUN git clone https://github.com/Azure/azure-webjobs-sdk-script --depth 1 \
    cd azure-webjobs-sdk-script \
    dotnet build WebJobs.Script.sln; \
    dotnet publish src\WebJobs.Script.WebHost\WebJobs.Script.WebHost.csproj --output /azure-functions-runtime \
    cd .. \
    rm -rf azure-webjobs-sdk-script

ENV AzureWebJobsScriptRoot=/app

CMD ["dotnet", "/app/Microsoft.Azure.WebJobs.Script.WebHost.dll"]
