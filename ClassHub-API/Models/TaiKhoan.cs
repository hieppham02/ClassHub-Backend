
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("tai_khoan")]
public partial class TaiKhoan
{
    [Key]
    public string ma_sv { get; set; } = null!;

    public string email { get; set; } = null!;

    public string ho_ten { get; set; } = null!;

    public string mat_khau { get; set; } = null!;

    public string? sdt { get; set; }

    public string vai_tro { get; set; } = null!;

    public string? ten_lop { get; set; }

    [InverseProperty("MaSvNavigation")]
    public virtual ICollection<PhieuMuon> PhieuMuons { get; set; } = new List<PhieuMuon>();
}
