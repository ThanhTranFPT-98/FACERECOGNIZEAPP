# PPE AI Box (CR-030)

Phần mềm nhận diện trang bị bảo hộ. Chạy trên AI Box ngoài hiện trường; trên laptop thì chạy trong
container và lấy hình từ webcam qua trình duyệt (container không thấy webcam).

Nó **không** quyết định mở barrier. Nó chỉ báo cáo *trong vùng này đang có ai, họ đội mũ hay không*.
Trạm là nơi ra quyết định (`PpeRules`, luật theo làn, CR-018).

## Luồng

```
camera ──► phát hiện người ──► phán quyết PPE ──► bám vết (trackId) ──► lọc theo vùng ROI
                                                                              │
   trạm ◄── POST /api/v1/parking/receiver/ppe?key=… ◄── bản tin ppe/1 ◄───────┘
     │
     └──► GET /api/v1/parking/ai-config?device=…&key=…  (vùng ROI do người vận hành vẽ trên trạm)
```

Bản tin là **trạng thái**, không phải sự kiện: cứ mỗi giây gửi một bản, kể cả khi không có ai. Bản tin
cũ không gửi lại — một tấm ảnh hai phút trước còn tệ hơn im lặng — và chính nhịp gửi đều này giữ cho
trạm thấy camera còn sống.

## Ba chế độ (`PPEBOX_MODE`)

| Chế độ | Ai tìm ra người | Ai quyết định mũ/áo | Dùng khi |
| --- | --- | --- | --- |
| `face` (mặc định) | SCRFD của AiService (một khuôn mặt = một người) | Bảng điều khiển trên trang web | Diễn thử: người thật, khung thật, trackId thật, chỉ phán quyết là bấm tay |
| `model` | Mô hình ONNX trong `PPEBOX_MODEL` | Chính mô hình đó | Ngoài hiện trường |
| `manual` | Không có camera | Bảng điều khiển | Không có webcam, hoặc dựng kịch bản cố định |

Chế độ `face` ước lượng khung người quanh khuôn mặt (người lớn đứng ≈ 3 đầu ngang, 7 đầu dọc), đủ để
thử vùng ROI và việc bám vết mà không cần ai đội mũ bảo hộ trong phòng làm việc.

## Chế độ `model`

Mỗi bộ dữ liệu PPE công khai đặt tên lớp một kiểu, nên việc *lớp nào nghĩa là gì* là **cấu hình**:

```
PPEBOX_MODEL=/models/ppe.onnx
PPEBOX_CLASSES=person,helmet,no-helmet,vest,no-vest     # bỏ trống nếu mô hình tự khai tên lớp
PPEBOX_ITEM_CLASSES=helmet:helmet,no-helmet:!helmet,vest:vest,no-vest:!vest
```

Dấu `!` nghĩa là lớp đó **chứng minh món đồ bị thiếu**. Một khung "no-helmet" là bằng chứng đầu trần;
còn việc không thấy khung "helmet" nào thì không phải bằng chứng gì cả. Khi hai bên mâu thuẫn, phán
quyết thiếu thắng.

Một món PPE chỉ tính cho người nào nếu khung của nó nằm trong khung người đó ít nhất 60% — mũ của
người bên cạnh không phải mũ của họ.

## Vùng ROI

Người vận hành vẽ vùng trên **phần mềm trạm** (Cài đặt làn → camera → *Vẽ vùng ROI*), không vẽ ở đây:
ngoài hiện trường AI Box không có màn hình. Box hỏi trạm mỗi 30 giây, nên sửa vùng là có hiệu lực ngay,
không cần khởi động lại.

Chưa vẽ vùng nào thì cả khung hình là một vùng — đúng cách trạm hiểu một camera canh đúng một làn.
Người thuộc vùng nào là tính theo **chỗ họ đứng** (giữa chân), không theo đầu: người nghiêng qua vạch
vẫn thuộc bên của mình.

## Chạy

```bash
# trong lab (cùng docker-compose với trạm và FaceID)
make lab-up                 # trang điều khiển: http://localhost:8096

# hoặc chạy thẳng trong WSL
PPEBOX_RECEIVER_KEY=$(cat ~/.local/share/dsa-tram/receiver-key) ./run.sh
./run.sh test               # 21 unit test, không cần camera hay mạng
```

## Biến môi trường chính

| Biến | Mặc định | Nghĩa |
| --- | --- | --- |
| `PPEBOX_DEVICE_ID` | `PPEBOX-01` | Khớp với `Camera.ExternalDevice` trên trạm |
| `PPEBOX_STATION_URL` | `http://localhost:5085` | Trạm |
| `PPEBOX_RECEIVER_KEY` | — | `Receiver:Key` của trạm (≥ 32 ký tự) |
| `PPEBOX_ITEMS` | `helmet,vest` | Những món box này quan sát |
| `PPEBOX_SEND_INTERVAL` | `1.0` | Giây giữa hai bản tin |
| `PPEBOX_ROI` | *(rỗng)* | Mã ROI dùng khi trạm chưa khai báo vùng nào |
| `PPEBOX_ROI_REFRESH` | `30` | Giây giữa hai lần hỏi lại vùng ROI |
| `PPEBOX_TRACK_TTL` | `3.0` | Giây không thấy thì quên một track |

## Ghi chú kỹ thuật

- Không dùng torch. SCRFD của AiService có `import torch` ở đầu file nhưng chỉ dùng trong
  `detect_tracking()` — thứ ta không gọi — nên `vision.py` nạp một module torch rỗng thay thế. Nhờ đó
  image của AI Box nhẹ hơn khoảng 800 MB.
- Trọng số mô hình nằm trong `AiService/`, gắn vào container ở chế độ chỉ đọc, không nhúng vào image.
- `trackId` không bao giờ được dùng lại: trạm kiểm tra lại track trước khi mở barrier, nên một mã nhảy
  từ người này sang người khác còn nguy hiểm hơn là không có mã.
