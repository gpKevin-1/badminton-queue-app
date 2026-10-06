namespace BadmintonApp.API.Models
{
    public class Court
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<Player> Players { get; set; } = new List<Player>();
    }
}
