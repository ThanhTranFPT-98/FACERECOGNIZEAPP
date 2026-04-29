# Face Recognition System - FaceRecognizeApp

A real-time face recognition and management system consisting of a React frontend, an ASP.NET Core backend, and a Python-based AI service for computer vision tasks.

## System Architecture

The project is divided into three main components:

- **`MyfirstASP_NET`**: ASP.NET Core Web API backend.
  - Handles business logic, employee/stranger management, and access logs.
  - Integrates with SQL Server for data persistence.
  - Uses SignalR for real-time notifications to the frontend.
  - Acts as a bridge between the AI service and the UI.
- **`AiService`**: Python FastAPI service.
  - Performs heavy computer vision tasks: Face Detection (SCRFD), Tracking (ByteTrack), and Recognition (ArcFace).
  - Provides REST endpoints for embedding extraction and frame-by-frame recognition.
  - Can run as a standalone camera worker or process frames sent from the backend.
- **`ui`**: React (Vite) frontend.
  - Dashboard for system overview.
  - Real-time monitor for camera feeds and recognition events.
  - Management interfaces for Employees, Strangers, Access Logs, and System Alerts.

## Key Features

- **Real-time Face Detection & Tracking**: High-performance face detection using SCRFD and consistent tracking across frames with ByteTrack.
- **Face Recognition**: Accurate identification using ArcFace models, distinguishing between registered employees and unknown strangers.
- **Employee Management**: Enroll new people, update details, and manage face embeddings.
- **Access Logging**: Automatic logging of recognized individuals and strangers with timestamped entries and captured images.
- **Real-time Alerts**: Instant notifications when unauthorized or unknown people are detected.
- **System Monitoring**: Live dashboard showing access statistics and system health.

## Prerequisites

- **Backend**: .NET SDK 8, SQL Server
- **AI Service**: Python 3.9+ (3.10 recommended), CUDA (optional for GPU acceleration)
- **Frontend**: Node.js (v18+) & npm
- **OS**: Windows (optimized for PowerShell and `.bat` startup scripts)

## Getting Started

### 1. AI Service Setup (Weights & Environment)

The AI service requires pre-trained model weights which are not included in the repository due to size.

| Model File | Location | Description |
|------------|----------|-------------|
| `arcface_r50.pth` | `AiService/face_recognition/arcface/weights/` | ArcFace Recognition model |
| `scrfd_2.5g_bnkps.onnx` | `AiService/face_detection/scrfd/weights/` | SCRFD Detection model |

**Install Dependencies:**
```powershell
cd AiService
python -m venv face_detection_env
.\face_detection_env\Scripts\Activate.ps1
pip install -r requirements.txt
```

### 2. Database Configuration

The ASP.NET backend uses SQL Server. Configure your connection string using `dotnet user-secrets`:

```powershell
cd MyfirstASP_NET
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=YOUR_SERVER;Initial Catalog=FaceRecognitionDB;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
dotnet ef database update
```

### 3. Running the Application

Use the provided startup scripts in the root directory:

- **Start all services**: Run `.\start-all.bat` (opens 3 windows for Backend, AI, and UI).
- **Stop all services**: Run `.\stop-all.bat`.

**Access Points:**
- **Web UI**: `http://localhost:5173`
- **Backend API**: `http://localhost:5085`
- **Swagger UI**: `http://localhost:5085/swagger`
- **Python AI Service**: `http://localhost:8000`

## Development Notes

- **Model Loading**: The Python AI service takes 30-60 seconds to initialize models on startup. Wait for the "Uvicorn running" message before testing recognition.
- **Camera Access**: Ensure the browser has permission to access the camera when using the Monitor page.
- **Datasets**: Face images are stored locally in `AiService/datasets/` and are not committed to git. Use `add_persons.py` to enroll new faces into the feature database.

## Technologies Used

- **Frontend**: React, Vite, Tailwind CSS, SignalR Client
- **Backend**: ASP.NET Core 8, Entity Framework Core, SQL Server
- **AI/ML**: FastAPI, PyTorch, OpenCV, InsightFace (ArcFace, SCRFD), ByteTrack
