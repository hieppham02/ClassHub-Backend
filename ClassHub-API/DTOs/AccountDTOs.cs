namespace ClassHub_API.DTOs
{
    public class CreateAccountDTO
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string Email { get; set; } = null!;
        public string? Password { get; set; }
        public string? Phone { get; set; }
        public string Role { get; set; } = "Sinh viên";
        public string? ClassOrDept { get; set; }
    }

    public class UpdateAccountDTO
    {
        public string Name { get; set; } = null!;
        public string Email { get; set; } = null!;
        public string? Phone { get; set; }
        public string Role { get; set; } = "Sinh viên";
        public string? ClassOrDept { get; set; }
        public string? NewPassword { get; set; }
    }
}