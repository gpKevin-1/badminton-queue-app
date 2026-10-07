using BadmintonApp.API.Models;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);

// --- 1. 讀取 SQL Server 連線字串 ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION_STRING");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddControllers();

// --- 2. CORS 跨域政策設定 ---
builder.Services.AddCors(options =>
{
    // 方案 A：開發階段建議（允許動態通配本地任意 Port + 你的 Vercel 網址）
    options.AddPolicy("AllowVueApp", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
            // 允許所有 localhost (不限 Port) 或你的 Vercel 正式網域
            new Uri(origin).Host == "localhost" || origin == "https://badminton-app.vercel.app"
        )
        .AllowAnyHeader()
        .AllowAnyMethod();
    });

    /* 方案 B：如果不限定來源，可以直接開放全域 AllowAnyOrigin（常用於 public API）
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
    */
});

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// --- 3. Middlewares 中間件順序 ---
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Badminton API v1");
    c.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();

// 💡 關鍵修正：明確加入 UseRouting，並把 UseCors 緊接在後
app.UseRouting();

app.UseCors("AllowVueApp");

app.UseAuthorization();

app.MapControllers();

app.Run();