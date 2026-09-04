using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClassHub_API.Models
{
    [Table("toa_nha")]
    public class ToaNha
    {
        [Key]
        [Column("ma_toa_nha")]
        public string ma_toa_nha { get; set; } = null!;

        [Column("ten_toa_nha")]
        public string ten_toa_nha { get; set; } = null!;

        [Column("so_tang")]
        public int? so_tang { get; set; } = 5;

        [Column("mo_ta")]
        public string? mo_ta { get; set; }

        public virtual ICollection<PhongHoc> PhongHocs { get; set; } = new List<PhongHoc>();
    }
}