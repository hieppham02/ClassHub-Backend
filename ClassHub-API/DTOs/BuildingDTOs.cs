namespace ClassHub_API.DTOs
{
    public class CreateBuildingDTO
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public int Floors { get; set; } = 5;
        public string? Description { get; set; }
    }

    public class UpdateBuildingDTO
    {
        public string Name { get; set; } = null!;
        public int Floors { get; set; } = 5;
        public string? Description { get; set; }
    }

    public class CreateRoomDTO
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string BuildingId { get; set; } = null!;
        public int Floor { get; set; } = 1;
        public int Capacity { get; set; } = 70;
        public string? Status { get; set; } = "HOAT_DONG";
    }

    public class UpdateRoomDTO
    {
        public string Name { get; set; } = null!;
        public string BuildingId { get; set; } = null!;
        public int Floor { get; set; } = 1;
        public int Capacity { get; set; } = 70;
        public string? Status { get; set; }
    }
}