using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("cau_hinh_ca_hoc")]
public partial class CauHinhCaHoc
{
    [Key]
    [Column("ca_id")]
    public int ca_id { get; set; }

    [Column("ten_ca")]
    public string ten_ca { get; set; } = null!;

    [Column("gio_bat_dau")]
    public TimeSpan gio_bat_dau { get; set; }

    [Column("gio_ket_thuc")]
    public TimeSpan gio_ket_thuc { get; set; }

    [Column("ghi_chu")]
    public string? ghi_chu { get; set; }
}