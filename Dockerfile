# 1. 使用 .NET 10.0 SDK 進行編譯
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 只複製並 restore API 專案
COPY *.csproj ./
RUN dotnet restore BadmintonApp.API.csproj

# 複製所有程式碼
COPY . ./

# 只 publish API 專案（避免觸發 BadmintonApp.Client.esproj 的編譯）
RUN dotnet publish BadmintonApp.API.csproj -c Release -o /app/out

# 2. 使用 .NET 10.0 ASP.NET Runtime 執行環境
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/out .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "BadmintonApp.API.dll"]