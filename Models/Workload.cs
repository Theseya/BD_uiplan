namespace WebApplication1.Models
{
    public class Workload
    {
        public int Id { get; set; }
        public string WorkType { get; set; } = string.Empty;
        public string TeacherName { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string ModuleSemester { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
    }
}