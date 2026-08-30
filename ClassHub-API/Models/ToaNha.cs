using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models;

[Table("toa_nha")]
public partial class ToaNha
{
    [Key]
    public string ma_toa_nha { get; set; } = null!;

    public string ten_toa_nha { get; set; } = null!;

    public virtual ICollection<PhongHoc> PhongHocs { get; set; } = new List<PhongHoc>();
}
