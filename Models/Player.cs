namespace BadmintonApp.API.Models
{
    public class Player
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Gender { get; set; } // "M" 或 "F"
        public int PlayedCount { get; set; } // 已打場數
        public string? Level { get; set; }  // "初"、"中"、"高"
        public string? Status { get; set; }  // "1" 表示上場排隊中，"0" 表示休息請假中
    }
}
