using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("thiet_bi")]
public partial class ThietBi
{
    [Key]
    public int id { get; set; }

    public string? ten_thiet_bi { get; set; }

    public int? so_luong { get; set; }

    public string? ma_phong { get; set; }

    public virtual PhongHoc? MaPhongNavigation { get; set; }
}
