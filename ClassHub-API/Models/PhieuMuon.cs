using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("phieu_muon")]
public partial class PhieuMuon
{
    [Key]
    public int id { get; set; }

    public string? ma_sv { get; set; }
    public string? ma_phong { get; set; }
    public DateOnly? ngay_muon { get; set; }
    public int? ca_muon { get; set; }
    public string? trang_thai { get; set; }
    public DateTime thoi_gian_tao { get; set; }
    public string? otp { get; set; }
    public DateTime? thoi_gian_tra { get; set; }
    public string? ma_sv_tra { get; set; }
    public string? ma_sv_uy_quyen { get; set; }
    public int? is_cabinet_open { get; set; }

    [ForeignKey("ma_phong")]
    public virtual PhongHoc? MaPhongNavigation { get; set; }

    [ForeignKey("ma_sv")]
    [InverseProperty("PhieuMuons")]
    public virtual TaiKhoan? MaSvNavigation { get; set; }

    [ForeignKey("ma_sv_tra")]
    public virtual TaiKhoan? MaSvTraNavigation { get; set; }

    [ForeignKey("ma_sv_uy_quyen")]
    public virtual TaiKhoan? MaSvUyQuyenNavigation { get; set; }
}