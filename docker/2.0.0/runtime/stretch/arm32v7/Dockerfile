FROM microsoft/dotnet:2.0.0-runtime-stretch-arm32v7

COPY ["azure-functions-runtime", "/azure-functions-runtime"]

ENV AzureWebJobsScriptRoot=/app

EXPOSE 80

CMD ["dotnet", "/azure-functions-runtime/Microsoft.Azure.WebJobs.Script.WebHost.dll"]
