using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
namespace ClassHub_API.Services;

public record RfidSelection(int CabinetId,string RoomId,string Uid,string BootId,long Sequence,long ExpiresAt);
public record RfidLatestScan(string Uid,string SelectionToken,DateTime ScannedAtUtc,DateTime ExpiresAtUtc);

public class RfidEnrollmentTokens(IConfiguration config) {
    byte[] Key => Encoding.UTF8.GetBytes(config["JwtSettings:SecretKey"] ?? throw new InvalidOperationException("JWT signing key unavailable."));
    const string Purpose="ClassHub-RFID-enrollment-v1:";
    public static DateTime? ScanTime(CabinetHealthView? health,DateTime now) {
        var p=health?.Metrics;
        if(p==null || health!.AgeSeconds>45 || health.Quality!="CLOCK_SYNCED" || p.MeasuredAtUtc==null ||
            !RfidSession.ValidUid(p.LastRfidUid) || p.RfidScanSequence is not (>0 and <=uint.MaxValue) ||
            p.LastRfidScanAgeMs is not (>=0 and <=120000))return null;
        var scanned=DateTime.SpecifyKind(p.MeasuredAtUtc.Value,DateTimeKind.Utc).AddMilliseconds(-p.LastRfidScanAgeMs.Value);
        return scanned>now.AddSeconds(5) || scanned<=now.AddSeconds(-120)?null:scanned;
    }
    public RfidLatestScan? Issue(int cabinetId,string room,CabinetHealthView? health,DateTime now) {
        var scanned=ScanTime(health,now);if(scanned==null)return null;
        var p=health!.Metrics;var expires=scanned.Value.AddSeconds(120);
        var selection=new RfidSelection(cabinetId,room,p.LastRfidUid!,p.BootId!,p.RfidScanSequence!.Value,
            new DateTimeOffset(expires).ToUnixTimeSeconds());
        var body=WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(selection));
        var signature=HMACSHA256.HashData(Key,Encoding.UTF8.GetBytes(Purpose+body));
        return new(selection.Uid,body+"."+WebEncoders.Base64UrlEncode(signature),scanned.Value,expires);
    }
    public RfidSelection? Read(string? token,int cabinetId,string room,DateTime now) {
        if(token==null || token.Length>2048)return null;
        var parts=token.Split('.');if(parts.Length!=2)return null;
        try {
            var expected=HMACSHA256.HashData(Key,Encoding.UTF8.GetBytes(Purpose+parts[0]));
            var signature=WebEncoders.Base64UrlDecode(parts[1]);
            if(signature.Length!=expected.Length || !CryptographicOperations.FixedTimeEquals(signature,expected))return null;
            var value=JsonSerializer.Deserialize<RfidSelection>(WebEncoders.Base64UrlDecode(parts[0]));
            if(value==null || value.CabinetId!=cabinetId || value.RoomId!=room || !RfidSession.ValidUid(value.Uid) ||
                value.Sequence is <=0 or >uint.MaxValue || value.ExpiresAt<=new DateTimeOffset(now).ToUnixTimeSeconds() ||
                value.ExpiresAt>new DateTimeOffset(now.AddSeconds(125)).ToUnixTimeSeconds())return null;
            return value;
        }catch(Exception ex)when(ex is FormatException or JsonException or ArgumentException){return null;}
    }
}
