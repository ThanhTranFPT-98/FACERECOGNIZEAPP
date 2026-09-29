# Đầu vào thiết bị linh hoạt: hồ sơ thiết bị, ánh xạ danh tính, SDK connector

Ngày: 29/09/2026 · CR-015 · Mã nguồn: `DSAVehicle/ParkingCore/DeviceProfiles.cs`, `Receiver.cs` · UI: tab **Nguồn thiết bị**.

Tài liệu này dành cho người tích hợp thiết bị mới (camera biển số, thiết bị nhận diện khuôn mặt) và người viết SDK connector.

## 1. Bốn lớp

```text
Lớp 1  TRUYỀN TẢI   HTTP receiver (URL có key) · cổng receiver (POST host:port/api) · SDK connector trong tiến trình
                    └────────────── tất cả đi qua InboxWriter: lưu CameraInbox TRƯỚC khi xác nhận ──────────────┘
Lớp 2  GIẢI MÃ      adapter viết bằng code (t1550, generic, ppe)  |  HỒ SƠ THIẾT BỊ (ánh xạ JSON/XML, sửa trên UI)
Lớp 3  HỢP ĐỒNG     ExternalObservation: deviceId, channel, plate, personId / externalPersonId, confidence, capturedUtc, ảnh
                    externalPersonId → PersonId qua bảng PersonIdentity
Lớp 4  NGHIỆP VỤ    ParkingService.Ingest → AccessPolicy → (tuỳ chọn) tự mở. KHÔNG đổi khi đổi thiết bị.
```

Thiết bị không bao giờ ra lệnh mở barrier. Mọi kết quả chỉ là bằng chứng đưa vào `ParkingService.Ingest`. Khi camera chưa có `TrustedConfidence` (mặc định), mọi kết quả đều vào hàng chờ bảo vệ duyệt.

## 2. Chọn cách tích hợp

| Thiết bị mới | Cách làm | Cần code? |
|---|---|---|
| Gửi JSON hoặc XML qua HTTP POST | Hồ sơ thiết bị (mục 3) | Không |
| Chỉ POST được tới `host:port/api` | Hồ sơ + thêm cổng vào `Receiver:Ports` | Không |
| Có SDK riêng / giao thức nhị phân / cần kéo (poll) dữ liệu | SDK connector (mục 6) → JSON → hồ sơ hoặc adapter `generic` | Có (connector) |
| Gửi `multipart/form-data` (JSON/XML kèm file ảnh) | Chưa hỗ trợ; viết connector hoặc adapter code | Có |
| Ảnh gửi dưới dạng URL | Không tải về (nguy cơ SSRF, chặn luồng); cần connector tải ảnh rồi gửi base64 | Có |

## 3. Ngôn ngữ hồ sơ (profile)

Một hồ sơ là một đối tượng JSON. Được phép có chú thích `//` và dấu phẩy thừa.

```jsonc
{
  "match":   [ { "path": "eventType", "equals": "ANPR" } ],   // tuỳ chọn: bản tin không khớp → bỏ qua (Ignored)
  "records": "data.results[*]",                                 // tuỳ chọn: mảng chứa các kết quả
  "personId": "identity",                                       // tuỳ chọn: "identity" (mặc định) | "direct"
  "fields": { "deviceId": "...", "plate": "...", ... }          // bắt buộc
}
```

### 3.1 Đường dẫn (path)

| Cú pháp | Ý nghĩa |
|---|---|
| `a.b.c` | thuộc tính lồng nhau |
| `a[0]` | phần tử đầu tiên của mảng |
| `a[*]` | mọi phần tử; **chỉ dùng trong `records`** |
| `$.a.b` | tính từ **gốc bản tin** thay vì từ bản ghi hiện tại (ví dụ MAC nằm ngoài mảng kết quả) |
| `@id` | thuộc tính XML `id` |

Để chịu được XML, path được xử lý "dễ dãi": bước thuộc tính gặp mảng thì áp dụng cho từng phần tử; `[0]` trên một đối tượng đơn lẻ trả về chính đối tượng đó. Nhờ vậy một hay nhiều phần tử `<ANPR>` đều đọc được bằng cùng một path.

Không có `records`: gốc là mảng thì mỗi phần tử là một bản ghi; gốc là đối tượng thì cả bản tin là một bản ghi.

### 3.2 Các trường (fields)

| Trường | Bắt buộc | Ghi chú |
|---|---|---|
| `deviceId` | có | Tự chuẩn hoá (bỏ ký tự không phải chữ/số, viết hoa), khớp với `Camera.ExternalDevice`. Nên dùng path `$.` để heartbeat cũng cập nhật trạng thái "còn sống" |
| `channel` | | Khớp với `Camera.ExternalChannel` (camera để trống kênh thì khớp mọi kênh) |
| `plate` | một trong hai | Chuỗi gốc; worker tự chuẩn hoá theo luật biển số của site |
| `personId` | một trong hai | Mã người trên thiết bị. `identity`: tra bảng PersonIdentity. `direct`: số nguyên dương được coi là PersonId của hệ thống |
| `confidence` | | Thang 0–1 sau khi chia cho `scale`. Ngoài 0–1 thì bị coi là **không hợp lệ** → bảo vệ duyệt |
| `capturedUtc` | | Xem `time`. Lệch quá 24 giờ so với lúc nhận thì bị bỏ qua |
| `plateImage`, `overviewImage` | | base64, có thể kèm tiền tố `data:image/jpeg;base64,` |
| `rawKey` | | Khoá chống trùng trong bản tin; hệ thống tự thêm số thứ tự bản ghi |

Mỗi trường là **một chuỗi path**, hoặc một đối tượng quy tắc:

| Khoá | Ví dụ | Ý nghĩa |
|---|---|---|
| `path` | `"plateNo"` hoặc `["plateNo","License"]` | Path đầu tiên có giá trị sẽ được dùng (xử lý khác biệt giữa các firmware) |
| `const` | `"CH1"` | Giá trị cố định |
| `default` | `"1"` | Dùng khi mọi path đều rỗng |
| `transform` | `["trim","upper"]` | Lần lượt: `trim`, `upper`, `lower`, `alnum` (chỉ giữ chữ/số), `digits` |
| `map` | `{"unknown":"", "1":"Car"}` | Bảng tra sau transform; giá trị không có trong bảng thì giữ nguyên |
| `scale` | `100` | Chỉ cho `confidence`: điểm 0–100 → 0–1 |
| `time` | `"unixSeconds"` | `unixSeconds`, `unixMillis`, `iso` (mặc định), hoặc mẫu .NET như `"yyyy-MM-dd HH:mm:ss"` |
| `timeZone` | `"+07:00"` | Múi giờ khi chuỗi thời gian không mang offset. Không khai báo thì lấy theo giờ máy trạm |
| `image` | `"base64"` | Hiện chỉ hỗ trợ base64 |

### 3.3 XML

XML được chuyển thành cây giống JSON trước khi áp dụng hồ sơ:
- giữ tên phần tử gốc (`EventNotificationAlert.ANPR.licensePlate`);
- bỏ namespace;
- thuộc tính thành `@tên`;
- phần tử lặp lại thành mảng;
- chữ nằm cạnh thuộc tính thành `#text`.

DTD và entity ngoài bị từ chối (chống XXE). Độ sâu tối đa 32 cấp.

### 3.4 Giới hạn

- Tối đa 200 bản ghi mỗi bản tin.
- Định nghĩa hồ sơ tối đa 20 000 ký tự.
- Body tối đa 16 MB.
- Bản tin giống hệt nhau (cùng SHA-256) trong vòng 1 giờ chỉ được lưu một lần.

## 4. Ví dụ

**T1550FAI-Z** (tương đương adapter có sẵn `t1550`; nút *Nạp ví dụ T1550* trên UI):

```json
{
  "records": "data.ai_snap_picture.PlateInfo[*]",
  "fields": {
    "deviceId": "$.data.dev_net_info[0].mac",
    "channel": "StrChn",
    "plate": "SnapId",
    "capturedUtc": { "path": "StartTime", "time": "unixSeconds" },
    "plateImage": { "path": "PlateImg", "image": "base64" },
    "overviewImage": { "path": "BgImg", "image": "base64" },
    "rawKey": "GrpId"
  }
}
```

**Máy nhận diện khuôn mặt gửi JSON** (điểm 0–100, giờ địa phương, mã nhân viên riêng):

```json
{
  "match": [{ "path": "event", "equals": "verify" }],
  "fields": {
    "deviceId": "$.device.sn",
    "personId": { "path": ["userCode", "pin"], "transform": ["trim", "upper"] },
    "confidence": { "path": "score", "scale": 100 },
    "capturedUtc": { "path": "time", "time": "yyyy-MM-dd HH:mm:ss", "timeZone": "+07:00" },
    "overviewImage": "photo"
  }
}
```

**Camera biển số gửi XML kiểu ISAPI** (định dạng `xml`):

```json
{
  "match": [{ "path": "EventNotificationAlert.eventType", "equals": "ANPR" }],
  "records": "EventNotificationAlert.ANPR[*]",
  "fields": {
    "deviceId": "$.EventNotificationAlert.macAddress",
    "channel": "$.EventNotificationAlert.channelID",
    "plate": "licensePlate",
    "confidence": { "path": "confidenceLevel", "scale": 100 },
    "capturedUtc": { "path": "$.EventNotificationAlert.dateTime", "time": "iso" }
  }
}
```

Các ví dụ trên đều được kiểm tra trong `tests/ParkingCore.Tests`. Tên trường của thiết bị thật phải lấy từ **bản tin thật hoặc tài liệu hãng**, không được đoán.

## 5. Quy trình thêm một thiết bị mới

1. Lấy **bản tin thật** của thiết bị: bắt gói tin, xem log, hoặc lấy từ tài liệu hãng. Nên có cả bản tin heartbeat, bản tin có kết quả, và bản tin không đọc được.
2. Tab *Nguồn thiết bị* → viết ánh xạ → dán bản tin vào ô mẫu → **Thử với bản tin mẫu**. Sửa đến khi hết lỗi và cảnh báo.
3. **Lưu hồ sơ.** Mỗi lần lưu tăng phiên bản (`AdapterVersion` trên từng bản tin trong inbox ghi lại phiên bản đã dùng).
4. Cấu hình thiết bị gửi tới `http://<trạm>/api/v1/parking/receiver/<mã hồ sơ>?key=<Receiver:Key>`. Nếu thiết bị chỉ POST được tới `host:port/api`, thêm `Receiver__Ports=<cổng>=<mã hồ sơ>`, thêm cổng vào `--urls` và `Receiver__AllowedIps`, và chỉ mở cổng đó trên firewall về phía mạng thiết bị.
5. Tab *Camera & video*: khai báo camera với `AI device ID` = mã thiết bị, `Channel`, `Lane`. Để trống *Trusted confidence*.
6. Thiết bị khuôn mặt: nhập bảng **Mã trên thiết bị → người**. Mã nào chưa ánh xạ sẽ sinh cảnh báo `UNKNOWN_PERSON:<nguồn>/<mã>`.
7. Chạy làn ở **shadow mode**, đối chiếu kết quả thực tế. Chỉ đặt `TrustedConfidence` sau khi đã đánh giá tại hiện trường.

## 6. SDK connector (phần bạn tự code)

Connector là một `BackgroundService` trong API. Nó nói chuyện với thiết bị bằng SDK của hãng, chuyển mỗi sự kiện thành **JSON bất kỳ**, rồi gọi `InboxWriter.Store`. Từ đó trở đi, sự kiện đi đúng đường ống như bản tin HTTP: worker, hồ sơ/adapter, ánh xạ danh tính, chính sách.

Có hai cách tạo JSON:
- **(a) Theo định dạng của adapter `generic`:** `{"deviceId","channel","plate"?,"personId"?,"confidence"?,"capturedUtc"?,"plateImage"?,"overviewImage"?,"id"?}` → `Store("generic", …)`. Không cần hồ sơ.
- **(b) Giữ nguyên cấu trúc gần với SDK** → viết một hồ sơ để ánh xạ. Đổi ý nghĩa trường về sau thì không phải build lại.

```csharp
// DSAVehicle/ParkingCore/Connectors/MyVendorConnector.cs  (mẫu — thay phần SDK bằng SDK thật)
using System.Text.Json;
using System.Threading.Channels;

namespace DSAVehicle.ParkingCore.Connectors;

public sealed class MyVendorConnector(IServiceScopeFactory scopes, IConfiguration config, ILogger<MyVendorConnector> logger) : BackgroundService
{
    // Callback của SDK chạy trên luồng của SDK: chỉ ghi vào hàng đợi, không làm việc với database trong callback.
    private readonly Channel<string> events = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var adapter = config["Connectors:MyVendor:Adapter"] ?? "myvendor";   // mã hồ sơ, hoặc "generic"
        var device = config["Connectors:MyVendor:Address"] ?? "";
        _ = Task.Run(() => ConnectLoop(device, stop), stop);
        await foreach (var json in events.Reader.ReadAllAsync(stop))
        {
            using var scope = scopes.CreateScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<InboxWriter>().Store(adapter, json, "sdk:" + device, stop);
            // Stored / Duplicate = đã lưu bền vững → lúc này mới được ACK với SDK (nếu SDK có cơ chế xác nhận).
            if (outcome is InboxOutcome.UnknownAdapter or InboxOutcome.BadBody) logger.LogError("MyVendor event refused: {Outcome} (adapter {Adapter})", outcome, adapter);
        }
    }

    private async Task ConnectLoop(string device, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // var session = MyVendorSdk.Login(device, user, password);
                // session.OnPlate += e => events.Writer.TryWrite(JsonSerializer.Serialize(new {
                //     deviceId = e.Serial, channel = e.Channel.ToString(), plate = e.PlateText,
                //     eventId = e.Id, time = e.Time,                     // bắt buộc có id/thời gian: bản tin giống hệt nhau bị gộp
                //     plateImage = Convert.ToBase64String(e.PlateJpeg) }));
                // await session.WaitUntilDisconnectedAsync(stop);
                await Task.Delay(Timeout.Infinite, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "MyVendor connection lost, retrying"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stop);   // kết nối lại có giãn cách, không vòng lặp nóng
        }
    }
}
```

Đăng ký trong `ParkingSetup.AddParking` (`Setup.cs`), **tắt mặc định** theo quy tắc của repo:

```csharp
if (builder.Configuration.GetValue<bool>("Connectors:MyVendor:Enabled")) builder.Services.AddHostedService<Connectors.MyVendorConnector>();
```

Quy tắc cho connector:
- Không bao giờ gọi `Open` hay điều khiển barrier. Chỉ gọi `InboxWriter.Store`.
- Chỉ ACK với thiết bị/SDK **sau khi** `Store` trả về `Stored` hoặc `Duplicate`.
- Mỗi sự kiện phải có mã hoặc thời gian riêng trong JSON. Nếu không, hai sự kiện giống hệt nhau trong vòng 1 giờ sẽ bị gộp làm một.
- Tạo scope mới cho mỗi sự kiện, vì `InboxWriter` và `ParkingDb` là scoped.
- Mật khẩu thiết bị đặt trong cấu hình máy trạm hoặc biến môi trường, không commit vào repo.
- SDK chỉ chạy trên Windows x86/x64: kiểm tra kiến trúc tiến trình của API. Khi SDK không chạy được trong API, tách connector thành một tiến trình riêng gửi HTTP tới receiver (phương án D, gateway) — đường ống phía API giữ nguyên.

## 7. Ánh xạ danh tính (PersonIdentity)

- Khoá: (nguồn = mã hồ sơ, mã trên thiết bị) → PersonId. Mã được so khớp sau khi áp dụng transform của hồ sơ, nên hãy chuẩn hoá bằng `trim`/`upper` ngay trong hồ sơ.
- **Thay thiết bị:** tạo hồ sơ mới, rồi *Nhập hàng loạt* danh sách `mã thiết bị,PersonId` xuất ra từ phần mềm của hãng. Không phải tạo lại người, không mất lịch sử.
- Xoá hồ sơ **không** xoá ánh xạ; tạo lại hồ sơ cùng mã thì dùng lại được.
- Mã chưa ánh xạ không bao giờ được dùng để cho qua. Sự kiện vẫn được tạo (không có người) để bảo vệ duyệt, và sinh một cảnh báo cho mỗi mã.

## 8. Chưa làm / giới hạn đã biết

- PPE vẫn dùng adapter code `ppe` (định dạng riêng, dữ liệu đưa vào `PpeCache`, không tạo sự kiện).
- Không hỗ trợ `multipart/form-data` và tải ảnh từ URL.
- Chưa có connector MQTT hoặc HTTP long-poll dựng sẵn. Nếu thiết bị cần, viết connector theo mục 6.
- Hồ sơ đúng cú pháp **không** chứng minh ánh xạ đúng nghĩa. Luôn thử bằng bản tin thật và chạy shadow mode trước.
