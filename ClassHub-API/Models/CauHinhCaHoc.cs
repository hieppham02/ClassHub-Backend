using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("class_slot")]
public partial class CauHinhCaHoc
{
    [Key]
    [Column("id")]
    public int ca_id { get; set; }

    [Column("name")]
    public string ten_ca { get; set; } = null!;

    [Column("start_time")]
    public TimeSpan gio_bat_dau { get; set; }

    [Column("end_time")]
    public TimeSpan gio_ket_thuc { get; set; }

    [Column("note")]
    public string? ghi_chu { get; set; }
}