FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 複製 csproj 檔並還原套件
COPY *.csproj ./
RUN dotnet restore

# 複製所有程式碼並發布 Release
COPY . ./
RUN dotnet publish -c Release -o /app/out

# 使用 ASP.NET Runtime 執行環境
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/out .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "BadmintonApp.API.dll"]