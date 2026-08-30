using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("phong_hoc")]
public partial class PhongHoc
{
    [Key]
    public string ma_phong { get; set; } = null!;

    public string ten_phong { get; set; } = null!;

    public int? suc_chua { get; set; }

    public string? trang_thai { get; set; }

    public string? ma_toa_nha { get; set; }

    public virtual ToaNha? MaToaNhaNavigation { get; set; }

    public virtual ICollection<PhieuMuon> PhieuMuons { get; set; } = new List<PhieuMuon>();

    public virtual ICollection<ThietBi> ThietBis { get; set; } = new List<ThietBi>();
}
