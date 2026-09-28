# Kế hoạch tích hợp camera bên thứ ba và vận hành thực tế

Ngày: 23/09/2026. Trạng thái: bản kế hoạch để rà soát; chưa triển khai chức năng mới.

## 1. Mục tiêu và phạm vi

Tiếp nhận sự kiện camera, hiểu đúng dữ liệu nhà cung cấp, quản lý ảnh và kết quả nhận diện, đối chiếu quyền ra/vào, tích hợp quy trình bảo vệ và barrier hiện có. Tách kết quả nhận diện khỏi quyền mở cổng để một gói tin camera không trở thành lệnh điều khiển trực tiếp.

Phạm vi chắc chắn: xử lý định dạng trong tệp `../../tcp`, kiến trúc mở rộng cho camera khác, lưu vết và thử nghiệm tại hiện trường. OCR dự phòng là nhánh đề xuất, cần người dùng xác nhận. Số camera/làn, tải và thời gian triển khai chưa chốt. Không bổ sung tính phí gửi xe trong kế hoạch này.

## 2. Kết quả khảo sát tệp tcp

Tệp chứa log HTTP/1.1 chạy trên TCP: `POST /api`, cổng đích 2123, JSON UTF-8 và Content-Length. Vì vậy đề xuất dùng HTTP receiver của ASP.NET Core để nhận định dạng này; không tự viết bộ tách gói TCP. Nếu hãng khác sử dụng giao thức TCP riêng, bổ sung adapter riêng sau khi có đặc tả.

Đã phân tích 43 đối tượng JSON trong log: 36 chỉ chứa nhóm alarm, 6 chỉ chứa nhóm AI, 1 chứa cả hai. Có 15 bản tin với alarm rỗng; 22 ảnh alarm; 7 phần tử PlateInfo, mỗi phần tử có PlateImg và BgImg. Kiểm tra giải mã Base64 và cấu trúc ảnh thành công với cả 36 ảnh. Đây là kiểm tra tệp mẫu, chưa chứng minh độ chính xác nhận diện hoặc khả năng vận hành camera thật.

| Trường quan sát | Cách xử lý đề xuất | Điều chưa được phép suy diễn |
|---|---|---|
| dev_net_info: device_name, mac, ip, phy, ChannelName | Tra cấu hình thiết bị và giữ metadata gốc | IP/MAC do payload khai báo không phải bằng chứng xác thực |
| alarm_list[].channel_alarm[]: int_alarm, int_alarm_ex, int_subtype=lpd | Ghi sự kiện alarm; xử lý cả bản tin có AI và alarm | Chưa khẳng định mã lpd có nghĩa chính thức gì nếu thiếu tài liệu hãng |
| take_alarm_snap và alarm_snap_data[].img_id | Đối chiếu trong ngữ cảnh thiết bị, kênh và bản tin; kiểm tra thiếu ảnh | Không dùng img_id như ID duy nhất toàn hệ thống |
| alarm_snap_data[].img_data, img_format, img_encode | Giải mã, kiểm tra ảnh, lưu bằng chứng | Ảnh alarm không đồng nghĩa đã có biển số hợp lệ |
| ai_snap_picture.PlateInfo[] | Tạo một observation cho từng phần tử | Không chỉ đọc phần tử đầu tiên |
| SnapId | Giữ nguyên; đưa vào trường ứng viên biển số để khảo sát | Mẫu có 35A39957, 35E00429, 45E00423 nhưng chưa xác nhận đây là biển số chính thức; không dùng làm ID sự kiện |
| PlateImg / BgImg | Lưu hai vai trò ảnh riêng | Trong mẫu kích thước lần lượt 256×256 và 1920×1088; không hard-code kích thước cho mọi camera |
| ImageAllInfo | Base64 giải ra dữ liệu nhị phân; lưu có giới hạn và phiên bản adapter | Không phải JSON/chuỗi UTF-8; không đoán offset. Cả 7 mẫu có chuỗi SnapId bên trong, nhưng điều này chưa xác nhận schema |
| StartTime / EndTime / time / img_time | Lưu giá trị gốc và thời điểm server nhận; chuẩn hóa bằng quy tắc hãng đã xác minh | time có dạng Z+00:00 bất thường; chưa tự gán đơn vị/epoch cho img_time |
| subscribe_id / data_pos / GrpId / Id | Giữ để nghiên cứu tương quan và chống gửi lại | GrpId lặp lại, Id rỗng trong mẫu; chưa biết phạm vi duy nhất hoặc quy tắc reset của bộ đếm |
| Type, Sex, PlateColor, CarBrand, CarType | Lưu metadata cần thiết; bảng mã theo firmware | Không coi Sex hoặc giá trị số khác là confidence |
| Owner, Phone, IdCode, Domicile… | Chỉ đưa vào dữ liệu nghiệp vụ nếu có nhu cầu rõ ràng | Không tự ghi đè chủ xe đã đăng ký từ thông tin camera |

Mẫu không có trường confidence rõ ràng. Bản tin alarm rỗng được ghi nhận riêng, không tự biến thành lượt xe hoặc heartbeat chứng minh thiết bị an toàn. Không suy ra chiều vào/ra từ CH1: chiều lấy từ cấu hình camera–kênh–làn.

## 3. Kiến trúc đề xuất

```text
Camera → HTTP receiver → hộp thư bền vững → adapter theo hãng/firmware
                                             ↓
                                 observation chuẩn + ảnh bằng chứng
                                             ↓
                          xác thực kết quả / OCR tùy chọn / bảo vệ duyệt
                                             ↓
                         ParkingService → chính sách ra/vào → barrier bridge
                                             ↓                       ↓
                              UI, lịch sử, cảnh báo         cảm biến xác nhận xe qua
```

Hộp thư bền vững lưu bản tin trước khi phản hồi thành công cho camera; worker xử lý sau để OCR hoặc nghiệp vụ chậm không kéo dài kết nối camera. Phản hồi phải đúng status/body và timeout hãng yêu cầu, chưa mặc định dùng 202 hay 200 khi chưa kiểm chứng. HTTP ACK chỉ xác nhận tiếp nhận, không phải cấp quyền mở cổng.

Khởi đầu có thể dùng bảng inbox trong database và worker nền, giảm số thành phần vận hành. Broker riêng chỉ được cân nhắc sau khi biết tải và yêu cầu nhiều máy chủ. Giới hạn hiện có là một API process cho mỗi site/database, khóa điều khiển trong process; không chạy nhiều replica trước khi thiết kế lại đồng thời và khóa phân tán.

## 4. Các giai đoạn và điều kiện hoàn thành

### Giai đoạn 0 — Chốt hợp đồng camera và yêu cầu vận hành

- Thu thập hãng, model, firmware, tài liệu callback/SDK; xác nhận SnapId, ImageAllInfo, timestamp, mã enum, ACK, retry, reset counter và khả năng xác thực/TLS.
- Xác nhận gửi chuỗi biển số, gửi ảnh, hay cả hai; số camera/làn; loại xe; lưu lượng cực đại; thời gian phản hồi mong muốn; quy tắc ra/vào; yêu cầu lưu trữ.
- Thu thêm dữ liệu có biển số đối chứng: ngày/đêm, xe máy/ô tô, biển mờ, không đọc được, camera reconnect và restart.
- Đầu ra: bảng mapping có phiên bản, cấu hình camera–kênh–làn và tiêu chí nghiệm thu định lượng được thống nhất. Những mục chưa biết phải đánh dấu Unknown.

### Giai đoạn 1 — Tiếp nhận và lưu bền vững

- Bổ sung receiver tương thích POST /api; cấu hình cổng 2123 khi triển khai yêu cầu. Cô lập endpoint camera khỏi phiên đăng nhập của người dùng.
- Kiểm tra nguồn theo khả năng thiết bị: thông tin xác thực nếu hỗ trợ; với thiết bị hạn chế, bố trí gateway/mạng camera riêng và quy tắc truy cập. Không tin MAC/IP trong JSON.
- Giới hạn request, độ sâu JSON, số ảnh/phần tử, thời gian đọc và tốc độ theo tải đã đo; không ghi toàn bộ Base64 vào application log.
- Lưu inbox với device, receivedAtUtc, payload hash, trạng thái, số lần thử, lỗi và phiên bản parser; chỉ ACK thành công sau khi dữ liệu cần thiết đã được lưu bền vững.
- Có xử lý khi database/disk đầy: không xác nhận đã nhận thành công nếu chưa lưu; kiểm chứng retry thực tế của camera, không hứa không mất sự kiện khi thiết bị không hỗ trợ gửi lại.
- Nghiệm thu: gửi lại mẫu, phân mảnh HTTP body, nhiều request trên một kết nối, mất kết nối, request lớn, JSON lỗi và service restart; bản tin hợp lệ đã ACK vẫn có thể được xử lý sau restart.

### Giai đoạn 2 — Adapter, ảnh và dữ liệu chuẩn

- Tạo adapter T1550FAI-Z theo schema đã xác nhận, cùng interface cho các hãng tiếp theo. Tách nhận giao thức khỏi ánh xạ dữ liệu.
- Xử lý độc lập alarm, AI snapshot và trường hợp cùng xuất hiện; không dùng if/else làm mất một nhánh. Alarm rỗng có trạng thái riêng.
- Base64: kiểm tra kích thước trước/sau giải mã, chữ ký định dạng, kích thước pixel và khả năng đọc ảnh. Tên ảnh do server sinh; ảnh lỗi được ghi lỗi có thể tra cứu, không làm worker ngừng chạy.
- Chuẩn hóa observation: eventId nội bộ, deviceId, laneId, channel, source, rawPlate, normalizedPlate, confidence nullable, confidenceSource, rawTimestamp, occurredAtUtc nullable, receivedAtUtc, timeQuality, evidenceIds, adapterVersion và rawMessageId.
- Chống trùng ở hai cấp: cùng bản tin được gửi lại và nhiều quan sát thuộc cùng một lượt xe. Giữ log gốc; không gộp chỉ theo biển số hoặc chỉ theo hash ảnh vì xe có thể quay lại thật.
- Khóa chống trùng phụ thuộc device/session và bộ đếm đã được xác minh; giới hạn cửa sổ thời gian, xử lý reset/reboot. Correlation kết hợp kênh, thiết bị và thời gian; thiếu căn cứ thì giữ riêng để duyệt.
- Không ghép ảnh từ camera/làn khác hoặc chỉ dựa vào timestamp gần nhau. Nếu ảnh tới muộn, có thời hạn chờ và trạng thái thiếu bằng chứng.
- Nghiệm thu: 43 mẫu đều có kết quả xử lý rõ ràng; 7 PlateInfo và 22 ảnh alarm được bảo toàn; kiểm thử thêm nhiều phần tử, thiếu trường, sai kiểu, trường lạ, ảnh lỗi, thứ tự đảo và counter reset.

### Giai đoạn 3 — Nhận diện và xác minh biển số

- Nhánh A: dùng kết quả camera khi hợp đồng trường biển số đã xác minh; giữ cả chuỗi gốc và chuỗi chuẩn hóa để truy vết.
- Nhánh B, chờ xác nhận phạm vi: OCR từ PlateImg; nếu cần dùng BgImg thì phải định vị biển số trước khi đọc. Tái sử dụng dịch vụ Python hiện có sau khi đánh giá trên dữ liệu thực.
- Không tự sửa O thành 0 hoặc I thành 1 một cách mù quáng. Định dạng hợp lệ là một phép kiểm tra, không chứng minh biển số đúng.
- Thiếu confidence giữ null; không tự gán 0.99 hoặc 1.0. Khi nối vào contract hiện tại dùng double, phải bổ sung biểu diễn chất lượng/nguồn hoặc buộc ManualReview, tránh biến unknown thành đã được chứng nhận.
- Camera và OCR bất đồng, biển thiếu ký tự, nhiều xe/biển, ảnh mờ hoặc metadata chưa hiểu: chuyển bảo vệ duyệt. Không chọn kết quả theo score cao hơn khi hai score chưa được hiệu chuẩn cùng thang.
- Kết quả không có score chỉ được tự động dùng sau khi có chính sách nguồn nhận diện được phê duyệt dựa trên kiểm thử hiện trường; không chỉ bật cờ rồi coi là đủ tin cậy.
- Nghiệm thu: bộ ảnh có nhãn độc lập với bộ tinh chỉnh; đo độ đúng toàn chuỗi biển, tỷ lệ không đọc được, tỷ lệ duyệt tay và nhận sai dẫn đến cho phép. Phân tách ngày/đêm, loại xe; ngưỡng thông qua do bên vận hành chốt.

### Giai đoạn 4 — Nghiệp vụ, UI và barrier

- Đưa observation qua ParkingService/AccessPolicy hiện có, tái sử dụng xe đăng ký, khách, blocklist và cơ chế duyệt. Tránh viết bộ quyền thứ hai riêng cho camera hãng.
- UI hiển thị ảnh biển/ảnh toàn cảnh, nguồn kết quả, chất lượng chưa biết, trạng thái xử lý và lý do quyết định; cho bảo vệ sửa/duyệt có lịch sử trước–sau và người thao tác.
- Sự kiện cũ, sự kiện replay và retry không được tạo quyền mở cổng mới. Dùng thời hạn quyết định và khóa duy nhất liên kết observation–decision–command; cần kiểm chứng đồng thời ở database.
- Chỉ yêu cầu mở khi đúng làn, quyền còn hiệu lực và điều kiện cảm biến đáp ứng chính sách. Gửi lệnh có tính idempotent; timeout không rõ kết quả phải đối soát, không phát lệnh mù.
- Xác nhận lệnh đã nhận, barrier đã mở và xe đã đi qua là ba trạng thái riêng. Phiên gửi xe/số xe trong bãi cập nhật theo passage; điều khiển đóng và chống kẹt thuộc bộ điều khiển an toàn thực tế.
- Nghiệm thu: một lượt được chấp nhận không phát sinh lệnh trùng; ManualReview/Denied không mở tự động; sự kiện cũ, sensor stale, mất phản hồi và xe chưa qua đều có hành vi xác định.

### Giai đoạn 5 — Vận hành, khôi phục và pilot

- Quản lý cấu hình camera/firmware/làn; giám sát lần cuối nhận tin, lỗi parser, hàng đợi, tốc độ, độ trễ, tỷ lệ ảnh lỗi, duyệt tay và tình trạng barrier.
- Camera im lặng có thể do không có xe; trạng thái offline cần heartbeat hoặc phép kiểm tra sức khỏe được hãng hỗ trợ, không suy ra chỉ từ thiếu sự kiện nhận diện.
- Retry có giới hạn; lỗi dài hạn vào danh sách xử lý lại. Replay phục vụ khôi phục/kiểm toán mặc định không điều khiển barrier.
- Lưu ảnh ngoài hàng metadata chính; cấu hình thời hạn giữ và quyền xem/tải. Phối hợp backup database, bằng chứng, cấu hình và khóa; kiểm tra khôi phục và tham chiếu ảnh bị thiếu.
- Chạy shadow mode trước: nhận và quyết định nhưng không điều khiển. Sau khi đối chiếu thực tế, thử một làn có giám sát, rồi mở rộng theo kết quả nghiệm thu.
- Kiểm thử mất mạng, mất điện, restart giữa lưu tin và xử lý, disk đầy, camera đổi IP/firmware, tải cao, trùng/đảo tin, sai đồng hồ, lỗi OCR và bộ điều khiển.
- Có người chịu trách nhiệm xử lý cảnh báo, hướng dẫn bảo vệ khi mất hệ thống, và rollback bằng tắt AutoOpen/chuyển quy trình vận hành đã thống nhất; không mặc định đóng cứng mọi cổng.
- Nghiệm thu: đủ tiêu chí độ chính xác/độ trễ/tải đã chốt, diễn tập restore đạt yêu cầu, chạy pilot và kiểm chứng phần cứng tại hiện trường. Test phần mềm không thay thế nghiệm thu camera/barrier thật.

## 5. Liên hệ với mã nguồn hiện có

| Thành phần | Thay đổi dự kiến |
|---|---|
| DSAVehicle/ParkingCore/ParkingController.cs | Giữ API capture/capture-burst hiện có; thêm receiver đúng định dạng JSON hãng qua controller riêng |
| DSAVehicle/ParkingCore/ParkingService.cs | Tái sử dụng Observe và quy trình ra/vào; thêm đường nhận observation camera đã chuẩn hóa và kiểm tra độ mới/nguồn |
| DSAVehicle/ParkingCore/Domain.cs và migrations | Bổ sung CameraDevice, CameraInbox, CameraObservation, EvidenceAsset và ràng buộc chống xử lý trùng; tên cuối cùng chốt lúc thiết kế |
| DSAVehicle/ParkingCore/Security.cs, Setup.cs | Đăng ký worker/adapter và cơ chế xác thực tương thích camera, không làm yếu API thiết bị đang có |
| AiService/plate_service.py | Nhánh OCR dự phòng/đối chứng nếu được chọn; hiệu chuẩn mô hình với dữ liệu hiện trường |
| ui/src/pages/Parking | Màn hình camera, trạng thái inbox, ảnh bằng chứng và duyệt kết quả |
| tests và docs | Fixture đã loại dữ liệu không cần thiết, replay, kiểm thử lỗi, runbook camera và pilot |

Mã hiện tại có đường upload ảnh và OCR, chưa thấy receiver cho JSON đúng mẫu tcp trong các controller ParkingCore đã khảo sát. Có sẵn chính sách, review, barrier và phiên xe để nối vào. Tài liệu hiện tại ghi rõ chưa nghiệm thu camera, OCR và phần cứng thật; kế hoạch này không coi các phần đó đã hoàn tất.

## 6. Thứ tự thực hiện và quyết định còn mở

Thứ tự phụ thuộc: 0 → 1 → 2 → 3 → 4 → 5. Đầu ra chạy được đầu tiên là camera thật → lưu bền vững → giải mã → hiển thị ảnh và ứng viên biển số trong shadow mode. Chỉ mở tự động sau khi hoàn thành kiểm chứng nhận diện, chính sách và phần cứng.

Chưa ấn định lịch theo tuần hoặc mua hạ tầng khi chưa biết nhân lực, số thiết bị, tải và khả năng nhận tài liệu hãng.

Các câu hỏi cần chốt:

1. Nhận chuỗi biển số có sẵn hay cần cả OCR ảnh dự phòng?
2. Hãng/model/firmware thực tế, tài liệu giao thức, số camera và số làn vào/ra?
3. Chính sách cho phép tự động, mục tiêu độ trễ/lưu lượng và thời hạn giữ dữ liệu?

## 7. Kiểm tra hiểu biết theo từng bước

Chưa đánh dấu người dùng đã hiểu nếu chưa có câu trả lời. Bắt đầu bằng việc người dùng mô tả luồng camera → hệ thống → barrier, sau đó kiểm tra từng giai đoạn:

- Vì sao nhận được HTTP POST chưa đủ để kết luận đọc đúng biển số?
- Vì sao SnapId cần xác minh và thiếu confidence không thể tự coi là 100%?
- Vì sao lưu tin trước ACK và chống trùng vẫn cần thiết khi camera gửi lại?
- Vì sao mở barrier thành công chưa có nghĩa xe đã vào bãi?

Checklist xuyên suốt nằm ở `../../LEARNING_CHECKLIST.md`.
