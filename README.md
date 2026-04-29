# FaceRecognizeApp

Đây là project demo nhận diện khuôn mặt theo thời gian thực, gồm 3 phần chạy cùng nhau:

- `MyfirstASP_NET`: backend ASP.NET Core, xử lý API, lưu dữ liệu và realtime SignalR
- `AiService`: Python FastAPI dùng để detect/recognize khuôn mặt
- `ui`: giao diện React để xem dashboard, monitor camera và quản lý dữ liệu

## Demo gồm những gì

Sau khi chạy thành công, bạn có thể:

- mở giao diện web để xem dashboard
- vào màn hình monitor để bật camera và gửi frame nhận diện
- quản lý `Employees` và `Strangers`
- xem `Access Logs` và `Alerts`

## Yêu cầu trước khi chạy

Máy local cần có:

- .NET SDK 8
- Node.js + npm
- Python 3.9+ (khuyến nghị 3.10)
- SQL Server
- PowerShell trên Windows

Project hiện đang chạy local với các cổng:

- Backend: `http://localhost:5085`
- AI service: `http://localhost:8000`
- UI: `http://localhost:5173`

## Setup lần đầu

Clone repo về xong, làm 3 bước dưới đây trước khi chạy:

### 1. Tải model weights cho AI service

Một số file model **không được commit lên repo** (quá lớn / không thuộc bản quyền project), bạn cần tự tải:

| File | Đường dẫn đặt vào | Ghi chú |
|------|---------------------|---------|
| `arcface_r50.pth` (~167 MB) | `AiService/face_recognition/arcface/weights/` | Model recognition (ArcFace R50) |
| `scrfd_2.5g_bnkps.onnx` (~3 MB) | `AiService/face_detection/scrfd/weights/` | Model detection (SCRFD) |

Bạn có thể tìm các model trên trang gốc của ArcFace / SCRFD (InsightFace) hoặc dùng nguồn nội bộ của team. Sau khi tải đủ, kiểm tra lại bằng lệnh:

```powershell
ls .\AiService\face_recognition\arcface\weights\arcface_r50.pth
ls .\AiService\face_detection\scrfd\weights\scrfd_2.5g_bnkps.onnx
```

### 2. Chuẩn bị thư mục dataset

Thư mục `AiService/datasets/` chứa ảnh khuôn mặt thật → KHÔNG commit lên repo. Sau khi clone, tạo cấu trúc trống:

```
AiService/datasets/
├── backup/          # ảnh gốc của từng người (mỗi người 1 folder)
├── data/            # ảnh đã align để train
├── face_features/   # vector đặc trưng (sinh tự động)
└── new_persons/     # ảnh mới chờ enroll
```

Có thể tạo nhanh bằng:

```powershell
mkdir AiService\datasets\backup, AiService\datasets\data, AiService\datasets\face_features, AiService\datasets\new_persons
```

Để enroll người mới, đặt ảnh vào `new_persons/<tên>/` rồi chạy:

```powershell
cd AiService
python add_persons.py
```

### 3. Cấu hình kết nối database

File `MyfirstASP_NET/appsettings.json` chỉ chứa **connection string mẫu** với placeholder `YOUR_SERVER`, `YOUR_USER`, `YOUR_PASSWORD`. Thông tin thật **không nên commit**. Có 2 cách config local:

#### Cách A — `dotnet user-secrets` (khuyến nghị cho dev)

```powershell
cd MyfirstASP_NET
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=YOUR_HOST\YOUR_INSTANCE;Initial Catalog=DSA_FaceRecognition_r;User Id=sa;Password=YOUR_PASSWORD;Integrated Security=False;TrustServerCertificate=True;"
```

#### Cách B — biến môi trường (dùng cho production / CI)

```powershell
$env:ConnectionStrings__DefaultConnection = "Data Source=...;Initial Catalog=...;User Id=...;Password=...;TrustServerCertificate=True;"
```

Sau khi config xong, chạy migration:

```powershell
cd MyfirstASP_NET
dotnet ef database update
```

## Cách chạy demo nhanh

Tại thư mục gốc project, chạy:

```bat
.\start-all.bat
```

Script này sẽ tự mở 3 cửa sổ:

1. backend ASP.NET
2. Python AI service
3. React UI

Sau khi chạy:

- mở UI tại `http://localhost:5173`
- backend API tại `http://localhost:5085`
- swagger tại `http://localhost:5085/swagger`

## Lưu ý khi demo

- AI service cần khoảng `30-60 giây` để load model
- chỉ nên test nhận diện sau khi cửa sổ Python báo Uvicorn đã chạy xong
- monitor cần quyền truy cập camera từ trình duyệt

## Cách dừng demo

Chạy:

```bat
.\stop-all.bat
```

## Nếu muốn chạy thủ công

### Backend

```powershell
cd .\MyfirstASP_NET
dotnet run
```

### AI service

```powershell
cd .\AiService
.\face_detection_env\Scripts\Activate.ps1
$env:PYTHONPATH='.'
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

Lần đầu chạy cần tạo virtual env và cài requirements:

```powershell
cd .\AiService
python -m venv face_detection_env
.\face_detection_env\Scripts\Activate.ps1
pip install -r requirements.txt
```

### UI

```powershell
cd .\ui
npm install
npm run dev
```

## Một số lỗi hay gặp

### UI mở được nhưng không gọi được API

Kiểm tra backend có đang chạy ở `http://localhost:5085` hay không.

### Monitor không nhận diện

Kiểm tra:

- Python AI service đã chạy chưa
- model đã load xong chưa (file `.pth` và `.onnx` đã đặt đúng chỗ)
- camera đã được cấp quyền chưa

### Lỗi database

- Kiểm tra SQL Server có đang chạy không
- Kiểm tra connection string đã set qua `user-secrets` hoặc env var (xem mục Setup → bước 3)
- Đã chạy `dotnet ef database update` chưa
