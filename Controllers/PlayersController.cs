using BadmintonApp.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BadmintonApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlayersController : ControllerBase
    {
        private readonly AppDbContext _context;

        // 透過建構子注入 AppDbContext
        public PlayersController(AppDbContext context)
        {
            _context = context;
        }

        // GET: api/players (取得所有佇列球員)
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Player>>> GetPlayers()
        {
            var players = await _context.Players.ToListAsync();
            return Ok(players);
        }

        // POST: api/players (新增球員)
        [HttpPost]
        public async Task<ActionResult<Player>> CreatePlayer([FromBody] Player newPlayer)
        {
            // 若未傳入 ID，則自動計算遞增 ID
            if (string.IsNullOrEmpty(newPlayer.Id))
            {
                var maxId = await _context.Players
                    .Select(p => p.Id)
                    .ToListAsync();

                int nextId = maxId.Select(id => int.TryParse(id, out var parsed) ? parsed : 0)
                                  .DefaultIfEmpty(0)
                                  .Max() + 1;

                newPlayer.Id = nextId.ToString();
            }

            _context.Players.Add(newPlayer);
            await _context.SaveChangesAsync(); // 寫入資料庫

            return CreatedAtAction(nameof(GetPlayers), new { id = newPlayer.Id }, newPlayer);
        }

        // PUT: api/players/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdatePlayer(string id, [FromBody] Player updatedPlayer)
        {
            if (id != updatedPlayer.Id)
            {
                return BadRequest("URL 中的 ID 與物件 ID 不一致。");
            }

            var player = await _context.Players.FindAsync(id);
            if (player == null)
            {
                return NotFound("找不到該名球員。");
            }

            // 更新資料
            player.Name = updatedPlayer.Name;
            player.Gender = updatedPlayer.Gender;
            player.Level = updatedPlayer.Level;
            player.PlayedCount = updatedPlayer.PlayedCount;
            player.Status = updatedPlayer.Status;

            await _context.SaveChangesAsync();

            return NoContent(); // 回傳 204 代表更新成功且不需回傳資料體
        }
    }
}