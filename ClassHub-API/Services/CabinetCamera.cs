using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ClassHub_API.Services;

public record CabinetPhoto(byte[] Jpeg, long Sequence);

// The configured URL is controlled by the server administrator, never an API caller.
public class CabinetCamera(IConfiguration config)
{
    public const int MaxPacketBytes = 640 * 480 * 2 + 16;
    private readonly ConcurrentDictionary<string, (string Token, DateTime Until)> sessions = new();
    private readonly ConcurrentDictionary<string, byte> viewers = new();

    public bool Configured(string room) => Uri.TryCreate(config[$"ReturnVerification:Cameras:{room}:WebSocketUrl"],UriKind.Absolute,out var u)
        && (u.Scheme=="ws" || u.Scheme=="wss") && u.AbsolutePath=="/ws" && string.IsNullOrEmpty(u.UserInfo) && string.IsNullOrEmpty(u.Query);

    public async IAsyncEnumerable<CabinetPhoto> Stream(string room,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        if(!Configured(room))throw new InvalidDataException("Camera chưa được kết nối với máy chủ.");
        if(!viewers.TryAdd(room,0))throw new InvalidDataException("Camera đang được sử dụng. Hãy thử lại sau.");
        try {
            var url=new Uri(config[$"ReturnVerification:Cameras:{room}:WebSocketUrl"]!);
            using var socket=new ClientWebSocket();
            using(var connect=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                connect.CancelAfter(TimeSpan.FromSeconds(12));
                var auth=await Session(room,url,connect.Token);
                await socket.ConnectAsync(url,connect.Token);
                if(auth!=null)await Send(socket,"AUTH "+auth,connect.Token);
                await Send(socket,"FORMAT JPEG",connect.Token);
            }
            try {
                var buffer=new byte[16384];
                while(!ct.IsCancellationRequested) {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    await Send(socket,"NEXT",timeout.Token);
                    CabinetPhoto? photo=null;
                    for(var n=0;n<8 && photo==null;n++) {
                        using var packet=new MemoryStream();
                        WebSocketReceiveResult part;
                        do {
                            part=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token);
                            if(part.MessageType==WebSocketMessageType.Close)throw new InvalidDataException("Camera đã ngắt kết nối.");
                            if(packet.Length+part.Count>MaxPacketBytes)throw new InvalidDataException("Ảnh camera vượt dung lượng cho phép.");
                            packet.Write(buffer,0,part.Count);
                        }while(!part.EndOfMessage);
                        if(part.MessageType==WebSocketMessageType.Binary)photo=Decode(packet.ToArray());
                    }
                    if(photo==null)throw new InvalidDataException("Không nhận được ảnh camera.");
                    yield return photo;
                }
            } finally {
                using var close=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try {await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"viewer closed",close.Token);}
                catch {socket.Abort();}
            }
        } finally {viewers.TryRemove(room,out _);}
    }

    static Task Send(ClientWebSocket socket,string command,CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(command)),WebSocketMessageType.Text,true,ct);


    private async Task<string?> Session(string room, Uri cameraUrl, CancellationToken ct)
    {
        // Open consoles are the default; enable only for an older secured cabinet.
        if (!config.GetValue<bool>($"ReturnVerification:Cameras:{room}:RequireAuthentication")) return null;
        var password = config[$"ReturnVerification:Cameras:{room}:AdminPassword"];
        if (string.IsNullOrEmpty(password)) throw new InvalidDataException("Thiếu mật khẩu camera đã bật xác thực.");
        var key = room + cameraUrl.AbsoluteUri;
        if (sessions.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow) return cached.Token;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 4096 };
        var login = new UriBuilder(cameraUrl) { Scheme = cameraUrl.Scheme == "wss" ? "https" : "http", Path = "/auth/login", Query = "" };
        using var response = await client.PostAsJsonAsync(login.Uri, new { password }, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("Camera yêu cầu mật khẩu quản trị hợp lệ hoặc đang giới hạn đăng nhập.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = json.RootElement.GetProperty("token").GetString();
        if (token == null || token.Length != 32 || token.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
            throw new InvalidDataException("Camera trả thông tin đăng nhập không hợp lệ.");
        sessions[key] = (token, DateTime.UtcNow.AddMinutes(25));
        return token;
    }

    public static CabinetPhoto Decode(byte[] packet)
    {
        if (packet.Length < 20 || packet.Length > MaxPacketBytes ||
            !packet.AsSpan(0, 4).SequenceEqual("OVF1"u8) || packet[14] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)) != 640 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)) != 480 ||
            packet[16] != 0xff || packet[17] != 0xd8 || packet[^2] != 0xff || packet[^1] != 0xd9)
            throw new InvalidDataException("Camera chưa trả ảnh JPEG VGA hợp lệ. Hãy chụp lại.");
        return new(packet[16..], BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
    }

    public async Task<CabinetPhoto> Capture(string room, CancellationToken cancellation) {
        if(!viewers.TryAdd(room,0))throw new InvalidDataException("Camera đang được sử dụng. Hãy đóng trình xem và thử lại.");
        try {return await CaptureOne(room,cancellation);}
        finally {viewers.TryRemove(room,out _);}
    }

    private async Task<CabinetPhoto> CaptureOne(string room, CancellationToken cancellation)
    {
        var address = config[$"ReturnVerification:Cameras:{room}:WebSocketUrl"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) ||
            (url.Scheme != "ws" && url.Scheme != "wss") || url.AbsolutePath != "/ws" ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query))
            throw new InvalidDataException("Chưa cấu hình camera hộc này trên máy chủ.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var auth = await Session(room, url, timeout.Token);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(url, timeout.Token);
        if (auth != null) await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("AUTH " + auth)),
            WebSocketMessageType.Text, true, timeout.Token);
        // New connection + NEXT asks the camera task for a new frame. No cached browser photo.
        foreach (var command in new[] { "FORMAT JPEG", "NEXT" })
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(command)),
                WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[16384];
        for (var message = 0; message < 8; message++)
        {
            using var packet = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                try { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); }
                catch { sessions.TryRemove(room + url.AbsoluteUri, out _); throw; }
                if (part.MessageType == WebSocketMessageType.Close)
                {
                    sessions.TryRemove(room + url.AbsoluteUri, out _);
                    throw new InvalidDataException("Camera đang bận, phiên đăng nhập hết hạn hoặc đã ngắt kết nối. Kiểm tra mật khẩu và thử chụp lại.");
                }
                if (packet.Length + part.Count > MaxPacketBytes)
                    throw new InvalidDataException("Ảnh camera vượt dung lượng cho phép.");
                packet.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            if (part.MessageType != WebSocketMessageType.Binary) continue; // initial STATE message
            var photo = Decode(packet.ToArray());
            // Complete the closing handshake so ESP32 promptly releases its single viewer slot.
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "captured", closeTimeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { socket.Abort(); }
            return photo;
        }
        throw new InvalidDataException("Không nhận được ảnh mới từ camera.");
    }
}
