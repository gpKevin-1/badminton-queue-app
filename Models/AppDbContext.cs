using Microsoft.EntityFrameworkCore;

namespace BadmintonApp.API.Models
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        // 對應 SQL Server 中名稱為 Players 的資料表
        public DbSet<Player> Players { get; set; }
    }
}