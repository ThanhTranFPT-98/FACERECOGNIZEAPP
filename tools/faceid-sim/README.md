# FaceID Simulator: thiết bị nhận diện khuôn mặt giả lập

Đóng vai **một máy FaceID gắn ở cổng** (thay cho Face-S cho tới khi có SDK thật). Simulator tự lưu khuôn mặt đã đăng ký theo **PIN**, tự nhận diện, rồi chỉ gửi **kết quả** cho trạm: PIN, điểm và ảnh chụp. Trạm và Cloud không biết đây là thiết bị giả.

Chạy trên laptop (WSL2 hoặc Linux). Không vào gói cài trạm.

```text
Webcam (trình duyệt Windows) ─► Simulator :8095 ─► Trạm :5085 ─► FPT Cloud
  khung hình JPEG               SCRFD + ArcFace      receiver/faceid-sim   events, ảnh,
                                bộ nhớ PIN            → PIN→PersonId        heartbeat
                                1 sự kiện / lượt      → làn PED-IN (FaceOnly)
                                                      → tín hiệu mở (giả lập)
```

**Vì sao trình duyệt giữ webcam?** WSL2 không thấy webcam của laptop. Trình duyệt trên Windows mở camera (`getUserMedia` được phép trên `localhost`) rồi gửi khung hình vào WSL qua `localhost:8095`.

## Thư mục

| Đường dẫn | Nội dung |
|---|---|
| `faceid_sim/engine.py` | Phát hiện mặt (SCRFD 2.5g) và trích đặc trưng (ArcFace r50). **Dùng lại code và trọng số của `AiService/`**, không chép |
| `faceid_sim/gallery.py` | "Bộ nhớ thiết bị": PIN → tên và các vector. Lưu ở `~/.local/share/dsa-faceid-sim`. Đây là dữ liệu sinh trắc học, không đưa lên git hay Cloud |
| `faceid_sim/debounce.py` | Mỗi lượt người một sự kiện: cùng kết quả trong N khung hình liên tiếp, rồi nghỉ theo từng PIN |
| `faceid_sim/device.py` | Bản tin gửi trạm, hàng đợi gửi theo thứ tự có thử lại, heartbeat 30 giây |
| `faceid_sim/server.py`, `static/index.html` | API và giao diện: camera trực tiếp, đăng ký mặt, nhật ký sự kiện |
| `station/faceid-sim.profile.json` | Hồ sơ thiết bị cho trạm (CR-015): cách đọc bản tin của simulator |
| `station/setup_station.py` | Cấu hình trạm qua API: hồ sơ, làn `PED-IN`, camera `FACESIM-01` |
| `station/tram-wsl.sh` | Chạy trạm trong WSL2, nối lên Cloud, tự tạo `Receiver:Key` |

## Bản tin gửi trạm

`POST {trạm}/api/v1/parking/receiver/faceid-sim?key=<Receiver:Key>`

```json
{ "event": "verify", "eventId": "9f2c…", "device": { "sn": "FACESIM-01", "model": "DSA FaceID Simulator" },
  "channel": "1", "pin": "0007", "score": 73.4, "time": "2026-11-02T22:10:05+07:00", "photo": "<base64 JPEG>" }
```

- Người lạ: `"pin": "STRANGER"`. Mã này không được ánh xạ, nên trạm tạo sự kiện chờ bảo vệ duyệt kèm cảnh báo `UNKNOWN_PERSON:faceid-sim/STRANGER`.
- Heartbeat: `{"event":"heartbeat","device":{"sn":…}}`. Hồ sơ bỏ qua bản tin này nhưng vẫn cập nhật "thiết bị còn sống", nên Cloud thấy camera Online.

## Chạy demo

Cần: WSL2 Ubuntu, `python3-venv`, `.NET 8 SDK` trong WSL, trọng số model trong `AiService/*/weights/`, webcam.

1. **Cloud** (Dashboard): khu vực, trạm `CUALO-GC` (lấy khoá trạm), người với **ID số** và FaceId `faceid-sim` / `<PIN>`.
2. **Trạm** (WSL, terminal 1): `bash tools/faceid-sim/station/tram-wsl.sh`
3. **Cấu hình trạm** (WSL, terminal 2): chạy lệnh `setup_station.py` mà script ở bước 2 in ra.
4. **Simulator** (WSL, terminal 2): `FACESIM_RECEIVER_KEY=$(cat ~/.local/share/dsa-tram/receiver-key) bash tools/faceid-sim/run.sh`
5. Mở **http://localhost:8095** trên Windows, cho phép camera, đăng ký mặt với PIN đã khai trên Cloud, rồi đứng trước camera.

## Cấu hình (biến môi trường)

| Biến | Mặc định | Ý nghĩa |
|---|---|---|
| `FACESIM_STATION_URL` | `http://localhost:5085` | Địa chỉ trạm |
| `FACESIM_RECEIVER_KEY` | | `Receiver:Key` của trạm (từ 32 ký tự) |
| `FACESIM_DEVICE_ID` / `FACESIM_CHANNEL` | `FACESIM-01` / `1` | Phải khớp camera trên trạm (`ExternalDevice`, `ExternalChannel`) |
| `FACESIM_PROFILE` | `faceid-sim` | Mã hồ sơ = `Source` của FaceId trên Cloud |
| `FACESIM_MATCH_THRESHOLD` | `0.5` | Ngưỡng cosine để coi là người quen (giống AiService, chưa đánh giá tại hiện trường) |
| `FACESIM_STABLE_FRAMES` / `FACESIM_COOLDOWN` | `3` / `10` | Số khung hình liên tiếp trước khi gửi / số giây nghỉ cho cùng một PIN |
| `FACESIM_DATA` | `~/.local/share/dsa-faceid-sim` | Bộ nhớ khuôn mặt |
| `FACESIM_PORT` | `8095` | Cổng giao diện |

## Test

```bash
bash tools/faceid-sim/run.sh test      # không cần model: debounce, gallery, bản tin, khớp hồ sơ
```

## Thay bằng thiết bị thật

| Thay | Giữ nguyên |
|---|---|
| Simulator → SDK connector (`docs/DEVICE_INPUT.md` §6), hoặc thiết bị POST thẳng tới receiver | Code trạm, chính sách làn, Cloud |
| Hồ sơ `faceid-sim` → hồ sơ của hãng (ví dụ `zk-face-s`), dựng từ **bản tin thật** | Người và lịch sử trên Cloud |
| FaceId trên Cloud: nhập hàng loạt `zk-face-s/<PIN>` | ID người |

## Giới hạn

- **Không chống giả mạo**: ảnh trên điện thoại vẫn có thể qua. Chỉ dùng để demo.
- Ngưỡng và điểm tin cậy chưa được đánh giá tại hiện trường. `setup_station.py` đặt `TrustedConfidence=1.0` và `FaceThreshold=0.55` **chỉ cho demo**.
- Làn giả lập chỉ phát **tín hiệu mở** (lệnh `Simulated`). Muốn ghi nhận "đã qua" (để Cloud nhận `passage` và `presence`) thì bấm **Mô phỏng xe qua** ở bảng lệnh trên UI trạm (trang Bãi xe).
