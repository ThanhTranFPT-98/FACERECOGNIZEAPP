# M1 · Identity: FaceID và biển số

Module này trả lời câu hỏi **"đây là ai, xe nào?"** tại một làn. Nó nhận bản tin từ camera biển số và thiết bị khuôn mặt (Face-S), rồi đối chiếu với danh sách người/xe.

- **Đầu vào:** HTTP receiver (`/api/v1/parking/receiver/{adapter}`, cổng receiver `host:port/api`) hoặc SDK connector gọi `InboxWriter.Store`.
- **Đầu ra duy nhất cho M3:** `Contracts/Recognition.cs` (biển, độ tin cậy biển, PersonId, độ tin cậy mặt, nguồn).
- **Không bao giờ** mở cổng. Kết quả của M1 chỉ là bằng chứng; M3 (`ParkingService.Ingest/Observe` → `Open`) mới quyết định.

## Đường đi của một bản tin

```text
thiết bị ─► ① Inbox/      lưu CameraInbox TRƯỚC khi ACK (chống trùng theo SHA-256)
          ─► ② Adapters/   dịch bản tin của từng hãng → ExternalObservation
                           (code: t1550, generic · hoặc hồ sơ thiết bị JSON/XML trong Profiles/)
          ─► ③ Resolve/    mã của thiết bị (PIN Face-S) → PersonId qua PersonIdentity
          ─► Recognition ─► M3
   Plates/  luật biển số VN + mẫu biển theo site (logic thuần, không I/O)
   Master/  người được phép, xe đăng ký, khách — danh sách để đối chiếu
```

## Thư mục

| Thư mục | Chứa gì |
| --- | --- |
| `Inbox/` | `ReceiverController`, `ReceiverOptions`, `CameraInbox` (entity), `InboxWriter`, `InboxSignal`, `CameraInboxWorker` |
| `Adapters/` | `ICameraAdapter` + `ExternalObservation`, `T1550Adapter`, `GenericAdapter`, `CameraAdapters`, `DeviceAdapterResolver`, `PayloadFormat` (JSON/XML) |
| `Adapters/Profiles/` | Hồ sơ thiết bị (CR-015): `DeviceProfile`, `ProfileAdapter`, `ProfilePath`, `ProfileCache`, `DeviceProfilesController` |
| `Resolve/` | `PersonIdentity`, `PersonIdentitiesController` |
| `Plates/` | `VietnamPlate`, `PlateRules` |
| `Master/` | `AuthorizedPerson`, `PeopleController`, `RegisteredVehicle` + `VehiclePerson`, `Visit`; `MasterSync` (kéo danh sách từ FPT Cloud khi `Sync:MasterSource = Cloud`, CR-017), `LocalMasterOnly` (khoá sửa tại trạm khi đó) |

Chi tiết cho người tích hợp thiết bị: [`docs/DEVICE_INPUT.md`](../../../../docs/DEVICE_INPUT.md).

## Quy ước

- Namespace vẫn là `DSAVehicle.ParkingCore` (giai đoạn 1 chỉ dời file). Đổi namespace ở giai đoạn 2.
- Mọi entity vẫn nằm trong **một** `ParkingDb`. Đổi entity thì thêm migration cho **cả** `Migrations/` và `SqlServerMigrations/`.
- Commit bắt đầu bằng `m1:`. PR sửa `Contracts/Recognition.cs` cần người phụ trách M3 duyệt.

## Ranh giới còn rò (sửa ở bước sau, có test riêng)

1. `Inbox/CameraInboxWorker.cs` vẫn gọi thẳng `ParkingService.Ingest` (M3) và tự xử lý nhánh PPE (`PpeMessage`, `PpeCache` — M2). Cần tách bước ③ ra `Resolve/` và đưa nhánh PPE về `Modules/Ppe/`.
2. `ParkingService` (M3) tự gom kết quả nhiều khung hình thành `Recognition` ("burst consensus") — về bản chất là việc của M1.

## Test

```powershell
dotnet run --project tests/ParkingCore.Tests      # receiver, adapter, profile, identity, plate
dotnet run --project tests/ParkingSimulation.Tests
```
