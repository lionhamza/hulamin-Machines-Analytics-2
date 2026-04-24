public class DateDimension
{
    public DateTime date { get; set; }
    public int day { get; set; }
    public int week { get; set; }
    public string month { get; set; }=null!;
    public string quarter { get; set; }=null!;
    public int year { get; set; }
    public string shift { get; set; }=null!;
}