public class Production
{
    public int id { get; set; }
    public DateTime date { get; set; }
    public string machine_id { get; set; }=null!;

    public decimal fe { get; set; }
    public decimal fp { get; set; }
    public decimal pa { get; set; }
    public decimal se { get; set; }
    public decimal sp { get; set; }

    public decimal base_capacity { get; set; }
    public decimal output { get; set; }
    public decimal cycle_time { get; set; }
}