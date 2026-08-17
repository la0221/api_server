# AIVision Code Analysis Summary

## Project Structure

| Project | Layer | Description | Source File Count |
|---------|-------|-------------|-------------------|
| AIVision.Domain | Domain | Core entities, value objects, enums, domain events | 18 |
| AIVision.Application | Application | Ports (interfaces), commands/handlers, services, configuration, contracts | 67 |
| AIVision.Infrastructure | Infrastructure | Device adapters, persistence, AI service clients, DI extensions | 67 |
| AIVision.InterfaceAdapters | Interface Adapters | DTO mappers between layers | 1 |
| AIVision.Api | API | ASP.NET Core Web API controllers | 3 |
| AIVision.Presentation.Wpf | Presentation | WPF MVVM UI (ViewModels, Views, Adapters, Services, Converters) | ~60 |

**Architecture**: Clean Architecture / Hexagonal (Ports & Adapters)
**Framework**: .NET 8, WPF (net8.0-windows)
**Key Libraries**: MediatR (CQRS), CommunityToolkit.Mvvm, Dapper, SixLabors.ImageSharp, Microsoft.Data.Sqlite, AForge.Video

---

## Devices Identified

| Device | Brand/Protocol | Port Interface | Implementation | Config Section |
|--------|---------------|---------------|----------------|----------------|
| PLC | Modbus TCP (FC01/02/03/05/06/15) | IPlcCommunicationPort, IPlcPort, IPlcSignalMapper, IPlcHandshakePort | ModbusTcpPlcAdapter, ModbusPlcPort, PlcSignalMapper, PlcHandshakeService | Devices:Plc:Connection, Devices:PlcSignalMap, Devices:Plc:Handshake |
| Camera (IDS) | IDS Peak SDK (line scan + area scan) | ICameraPort, ICameraDiscoveryPort, ICameraControlPort, ILineScanService | IdsCameraPort, IdsCameraDiscoveryAdapter, IdsCameraControlPort, LineScanService | Devices:Camera (Type=IdsPeak) |
| Camera (HikVision) | HikVision MVS SDK (GigE/USB) | ICameraPort, ICameraDiscoveryPort | HikCameraAdapter, HikDiscoveryAdapter | Devices:Camera (Type=Hik) |
| Camera (USB/Webcam) | AForge.Video DirectShow | ICameraPort, ICameraDiscoveryPort | AForgeCameraPort, AForgeCameraDiscovery | N/A (fallback) |
| Light (ASCII TCP) | LTS light controller, ASCII protocol, TCP Server | ILightPort | LtsAsciiLightPort | Devices:Light |
| Light (Serial RS232) | LTS light controller, RS232 serial | ILightPort | LtsSerialLightPort | Devices:LightSerial |
| Light (Modbus TCP) | LTS light controller, Modbus TCP | N/A (server helper) | LtsModbusTcpServer | N/A |
| AI (HTTP Generic) | Generic HTTP AI service | IAiInferencePort | HttpAiInferencePort | Devices:Ai |
| AI (AINAVI EdgeHub) | AINAVI EdgeHub platform | IAiInferencePort, IAiModelPort | AinaviAiInferencePort, AinaviAiModelPort | Ainavi |
| AI (Workflow) | AINAVI EdgeHub Workflow (multi-step) | IAiInferencePort, IWorkflowService | WorkflowAiInferencePort, EdgeHubWorkflowService | Workflow |
| AI (Switchable) | Runtime SingleModel/Workflow switch | IAiInferencePort | SwitchableAiInferencePort | N/A |

---

## Variability Summary

| Variability Point | Mechanism | Options |
|-------------------|-----------|---------|
| Camera driver | Config `Devices:Camera:Type` + DI switch in App.xaml.cs | Fake, Hik, IdsPeak, AForge (USB webcam) |
| PLC connection | Config `Devices:Plc:Type` + DI switch in App.xaml.cs | Fake, Modbus |
| Light controller | DI registration in ServiceCollectionExtensions | Fake, LtsAsciiLightPort (TCP), LtsSerialLightPort (RS232) |
| AI inference mode | Config `Workflow:Enabled` + SwitchableAiInferencePort | SingleModel (AINAVI), Workflow (multi-step), HTTP generic, Fake |
| Persistence | DI registration in App.xaml.cs | InMemory, SQLite (Dapper) |
| PLC signal mapping | Config `Devices:PlcSignalMap` (JSON) | Configurable signal names, addresses, areas, active levels, edge modes |
| PLC handshake error policy | Config `Devices:Plc:Handshake:ErrorPolicy` | TreatAsNg, Ignore, BlockAndAlarm |
| Defect filtering | Config in project.json | Size thresholds (small/medium/large/critical), distance-based clustering |
| Camera mode | AutoRunOptions.CameraMode | AreaScan, LineScan |
| Batch AI inference mode | Config `AiInference:Mode` | classification, segmentation |
| Light brightness control | Config `Devices:LightSerial:BrightnessControl` | Per-channel working/idle brightness |
| User auth | Config `Authentication` in appsettings.json | Operator, Engineer, Vendor roles |

---

## File-by-File Analysis

### Domain Layer (AIVision.Domain)

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Domain\Abstractions\Entity.cs | Base class | Entity\<TId\> | Generic entity base with typed Id property | Id (property) | None | N/A | N/A | 10 |
| Domain\Shared\IoSnapshot.cs | Record struct | IoSnapshot | PLC I/O read snapshot (InReady, OutCapture, OutResult, RawMask) | Constructor | None | PLC | N/A | 11 |
| Domain\Shared\IoCommand.cs | Record struct | IoCommand | PLC output command (CaptureStart, ResultOn) | Constructor | None | PLC | N/A | 15 |
| Domain\Shared\Prediction.cs | Record/class | Prediction, Detection, WorkflowDefect, ContourPoint | AI prediction result with label, confidence, detections, workflow defects | Properties | None | AI | N/A | 98 |
| Domain\Shared\ImageData.cs | Record struct | ImageData | Raw image data (Bytes, Width, Height, PixelFormat, Stride) | Constructor | None | Camera | N/A | 12 |
| Domain\AutoRun\AutoRunStatistics.cs | Class | AutoRunStatistics | Runtime statistics: yield, UPH, timing averages | Reset(), Initialize(), Update(), RecordError(), ResetConsecutiveErrors() | None | N/A | N/A | 132 |
| Domain\AutoRun\AutoRunEvents.cs | EventArgs | AutoRunStateChangedEventArgs, InspectionCompletedEventArgs, AutoRunErrorEventArgs, CaptureCompletedEventArgs, TriggerReceivedEventArgs, AutoRunErrorType | Auto run lifecycle events | Properties | None | N/A | N/A | 218 |
| Domain\AutoRun\AutoRunOptions.cs | Class | AutoRunOptions, CameraMode, LineScanSettings | Auto run configuration: camera mode, consecutive error limit, skip inference flag | Properties | None | N/A | AutoRun section | 89 |
| Domain\AutoRun\AutoRunState.cs | Enum | AutoRunState | State machine: Idle, Initializing, WaitingTrigger, Capturing, Inferring, Reporting | N/A | None | N/A | N/A | 38 |
| Domain\Plc\PlcSignalEnums.cs | Enums | PlcSignalDirection, PlcSignalArea, PlcEdgeMode, PlcActiveLevel | PLC signal configuration enums | N/A | None | PLC | N/A | 62 |
| Domain\Plc\ModbusAddressConverter.cs | Static class | ModbusAddressConverter | Absolute-address-based Modbus address conversion | ToModbusAddress(), GetArea() | None | PLC/Modbus | N/A | 63 |
| Domain\Plc\PlcSignalDefinition.cs | Class | PlcSignalDefinition | Signal definition: Name, Direction, Area, Address, ActiveLevel, EdgeMode | Properties, DisplayAddress | None | PLC | N/A | 50 |
| Domain\Plc\PlcAddressBaseMode.cs | Enum | PlcAddressBaseMode | ZeroBased / OneBased address mode | N/A | None | PLC | N/A | 19 |
| Domain\Plc\PlcHandshakeState.cs | Enums | PlcHandshakeState, PlcInspectionResult | Handshake state machine + inspection result | N/A | None | PLC | N/A | 41 |
| Domain\User\UserRole.cs | Enum | UserRole | Operator(3), Engineer(2), Vendor(1) - lower value = higher permission | N/A | None | N/A | Authentication | 17 |
| Domain\Entities\WorkOrder.cs | Entity | WorkOrder | Work order with Code, ProductName, ModelName, Status lifecycle | Start(), Complete(), Cancel() | Entity\<Guid\> | N/A | N/A | 84 |
| Domain\Entities\Inspection.cs | Entity | Inspection | Inspection result linked to WorkOrder | Properties | Entity\<Guid\> | N/A | N/A | 62 |
| Domain\Entities\Defect.cs | Entity/class | Defect, BoundingBox | Defect entity with type, confidence, bounding box | Properties | None | N/A | N/A | 70 |

### Application Layer (AIVision.Application)

#### Ports - Devices

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Ports\Devices\IPlcPort.cs | Interface | IPlcPort | Read/Write IoSnapshot/IoCommand | ReadAsync(), WriteAsync() | IoSnapshot, IoCommand | PLC | N/A | ~15 |
| Ports\Devices\ICameraPort.cs | Interface | ICameraPort | Camera lifecycle + capture | OpenAsync(), StartPreviewAsync(), StopPreviewAsync(), CaptureOnceAsync(), FrameReceived event | ImageData | Camera | N/A | ~30 |
| Ports\Devices\ICameraDiscoveryPort.cs | Interface | ICameraDiscoveryPort | List available cameras | ListAsync() | CameraDeviceVm | Camera | N/A | ~10 |
| Ports\Devices\ICameraControlPort.cs | Interface | ICameraControlPort | Camera parameter control (ExposureTime, Gain, Height, LineRate, ROI) | GetParametersAsync(), SetParameterAsync() | CameraParameterKind, CameraParameterDescriptor | Camera (IDS) | N/A | ~80 |
| Ports\Devices\ILightPort.cs | Interface | ILightPort | Light controller: intensity, turn on/off, device info, network config, work mode | SetIntensity(), Turn(), GetState(), GetDeviceInfo(), SetNetworkProfile(), SetWorkMode(), SetTriggerPolarity(), SetHeartbeat(), SetWorkingBrightness(), SetIdleBrightness() | None | Light (LTS) | N/A | 78 |
| Ports\Devices\IAiInferencePort.cs | Interface | IAiInferencePort | AI prediction | PredictAsync(), HealthCheckAsync(), IsConnected | Prediction, ImageData | AI | N/A | ~20 |
| Ports\Devices\IAiModelPort.cs | Interface | IAiModelPort | Load AI model | LoadAsync() | ModelLoadRequest, ModelLoadResult | AI (AINAVI) | N/A | ~15 |
| Ports\Devices\IWorkflowService.cs | Interface | IWorkflowService | Workflow lifecycle management | StartAsync(), LoadWorkflowAsync(), StopAsync(), GetStatusAsync(), HealthCheckAsync() | WorkflowStartResult | AI (Workflow) | N/A | 79 |
| Ports\Devices\IPlcSignalMapper.cs | Interface | IPlcSignalMapper | Signal name to Modbus address mapping | ReadSignalAsync(), WriteSignalAsync(), WriteMultipleSignalsAsync(), ReadAllSignalsAsync() | PlcSignalDefinition | PLC/Modbus | N/A | ~50 |
| Ports\Devices\IPlcCommunicationPort.cs | Interface | IPlcCommunicationPort | Low-level Modbus TCP operations | ConnectAsync(), DisconnectAsync(), ReadCoilsAsync(), WriteSingleCoilAsync(), WriteMultipleCoilsAsync(), ReadDiscreteInputsAsync(), ReadHoldingRegistersAsync(), WriteSingleRegisterAsync() | None | PLC/Modbus TCP | N/A | 55 |
| Ports\Devices\IPlcHandshakePort.cs | Interface | IPlcHandshakePort | PLC handshake state machine | StartAsync(), StopAsync(), ReportResultAsync(), NotifyCaptureCompleteAsync(), ResetAsync() + events | PlcHandshakeState, PlcInspectionResult | PLC | N/A | 87 |

#### Ports - Services

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Ports\Services\IAutoRunService.cs | Interface | IAutoRunService | Auto run lifecycle | StartAsync(), StopAsync(), Statistics, State + events | AutoRunStatistics, AutoRunState | N/A | N/A | ~40 |
| Ports\Services\IAuthService.cs | Interface | IAuthService | Authentication | Login(), Logout(), HasPermission(), CurrentRole, LoginStateChanged | UserRole | N/A | Authentication | 49 |
| Ports\Services\IModelConfigProvider.cs | Interface | IModelConfigProvider, ModelConfigInfo | Model config with EdgeHub settings, defect filtering params | GetModelConfig() | None | AI | N/A | 120 |
| Ports\Services\IDefectFilteringService.cs | Interface | IDefectFilteringService, DefectFilteringResult, DefectSizeCategory | Defect size/distance filtering | FilterDefects() | Prediction, WorkflowDefect | N/A | DefectFiltering | 125 |
| Ports\Services\ILineScanService.cs | Interface | ILineScanService | Line scan lifecycle: connect, configure, preview, capture | ConnectCameraAsync(), ConfigureAsync(), StartScanAsync(), StopScanAsync(), CaptureOnceAsync(), WaitForNextImageAsync() + events | LineScanRoiSettings, LineScanRoiBounds | Camera (IDS line scan) | N/A | 183 |
| Ports\Services\ILineScanSimulator.cs | Interface | ILineScanSimulator | Simulated line scan for testing | N/A | None | N/A | N/A | ~15 |
| Ports\Models\IModelDiscoveryService.cs | Interface | IModelDiscoveryService | Scan model folders | ScanModelsAsync() | DiscoveredModel | AI | ModelScan | ~15 |
| Ports\Models\DiscoveredModel.cs | Class | DiscoveredModel, AinaviModelType | Model info with Plugin type, InputShape, ClassMap | Properties | None | AI | N/A | 74 |

#### Ports - Persistence & History

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Ports\Persistence\IInspectionRepository.cs | Interface | IInspectionRepository | Inspection persistence | AddAsync(), GetStatisticsByWorkOrderIdAsync(), GetDefectStatisticsByWorkOrderIdAsync() | Inspection, InspectionStatistics | N/A | N/A | ~25 |
| Ports\Persistence\IWorkOrderRepository.cs | Interface | IWorkOrderRepository | Work order CRUD | GetByCodeAsync(), GetByIdAsync(), GetAllAsync(), AddAsync(), UpdateAsync(), DeleteAsync() | WorkOrder | N/A | N/A | ~30 |
| Ports\History\IInspectionHistoryQuery.cs | Interface | IInspectionHistoryQuery | Paged inspection history query with filters | QueryAsync() | InspectionHistoryDto, InspectionQueryFilter, PagedResult | N/A | N/A | 92 |
| Ports\ProductionStats\IProductionStatsQuery.cs | Interface | IProductionStatsQuery | Production statistics query | FindOrdersAsync() | WorkOrderSummaryDto | N/A | N/A | ~20 |
| Ports\ProductionStats\IProductionStatsConfigProvider.cs | Interface | IProductionStatsConfigProvider | Stats UI configuration | GetConfig() | ProductionStatsUiConfig | N/A | N/A | ~10 |

#### Ports - ImageBatch

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Ports\ImageBatch\IFolderPickerPort.cs | Interface | IFolderPickerPort | Folder selection dialog | PickFolderAsync() | None | N/A | N/A | ~10 |
| Ports\ImageBatch\IImageEnumeratorPort.cs | Interface | IImageEnumeratorPort | Enumerate images in folder | EnumerateAsync() | None | N/A | N/A | ~10 |
| Ports\ImageBatch\IImageLoaderPort.cs | Interface | IImageLoaderPort | Load image from file | LoadAsync() | ImageData | N/A | N/A | ~10 |
| Ports\ImageBatch\IOverlayRendererPort.cs | Interface | IOverlayRendererPort | Render AI overlay on image | RenderAsync() | None | N/A | N/A | ~10 |
| Ports\ImageBatch\IImageWriterPort.cs | Interface | IImageWriterPort | Save image to file | WriteAsync() | None | N/A | N/A | ~10 |
| Ports\ImageBatch\IAiInferencePort.cs | Interface | IAiInferencePort (ImageBatch) | Batch AI inference with AiResult, SegMask, DetectionResult | PredictAsync() | AiResult, SegMask, DetectionResult | AI | N/A | ~60 |

#### Commands & Handlers

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Inspection\Commands\StartInspectionCycleCommand.cs | MediatR Command | StartInspectionCycleCommand | Trigger single inspection cycle | WorkOrderId property | None | N/A | N/A | ~10 |
| Inspection\Commands\StartInspectionCycleCommandHandler.cs | MediatR Handler | StartInspectionCycleCommandHandler | PLC write -> Camera capture -> AI predict -> PLC result -> Save | Handle() | IPlcPort, ICameraPort, IAiInferencePort, IInspectionRepository | PLC, Camera, AI | N/A | 60 |
| Inspection\Commands\SwitchModelCommand.cs | MediatR Command+Handler | SwitchModelCommand, SwitchModelCommandHandler | Switch AI model via IAiModelPort | Handle() | IAiModelPort | AI (AINAVI) | N/A | 67 |

#### Application Services

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Services\InspectionImageService.cs | Service | InspectionImageService | Save inspection images to OK/NG folders with path traversal protection | SaveImageAsync(), SaveOverlayImageAsync() | IConfiguration | N/A | ImageSave | 286 |
| Services\IContourOverlayRenderer.cs | Interface | IContourOverlayRenderer | Draw defect contours on images | RenderOverlay() | ImageData, WorkflowDefect | N/A | N/A | ~15 |
| Services\IProjectInitializationService.cs | Interface+events | IProjectInitializationService, InitializationStatusChangedEventArgs | Parallel device initialization with status tracking | InitializeAsync(), Status events | N/A | All devices | N/A | 241 |
| Services\IProjectConfigService.cs | Interface | IProjectConfigService | project.json management | LoadAsync(), SaveAsync(), GetProjectList() | ProjectConfig | N/A | N/A | ~30 |
| Services\IOfflineInspectionService.cs | Interface | IOfflineInspectionService | Offline folder-based inspection | RunInspectionAsync() | None | AI | N/A | 91 |
| Services\WorkOrderManagementService.cs | Service | WorkOrderManagementService | Work order lifecycle, auto-generate codes | CreateAsync(), LoadAsync(), CompleteAsync() | IWorkOrderRepository, IInspectionRepository | N/A | N/A | 194 |
| Services\LineScanSimulator.cs | Service | LineScanSimulator | Line scan simulation from source image | StartAsync(), StopAsync() | ILineScanSimulator | Camera (simulated) | N/A | 331 |
| Services\LineScanImageBuilder.cs | Service | LineScanImageBuilder | Accumulate line data into complete image | AddLine(), Build() | ImageData | Camera (line scan) | N/A | 198 |

#### Configuration

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Configuration\ProjectConfig.cs | Class | ProjectConfig (+ sub-configs) | Project config with Camera, Light, PLC, AiModel, DefectFiltering sub-configs | Properties | None | All | project.json | 271 |
| Configuration\DefectFilteringOptions.cs | Class | DefectFilteringOptions | Pixel area/size mm2 thresholds for defect size categories | Properties | None | N/A | DefectFiltering | 80 |
| Configuration\WorkflowOptions.cs | Class | WorkflowOptions | EdgeHub Workflow config (Enabled, Host, Port, timeout, workflow path) | GetEdgeHubUrl(), GetWorkflowRunUrl() | None | AI (Workflow) | Workflow | 71 |
| Configuration\AiServiceOptions.cs | Class | AiServiceOptions | HTTP AI service config (BaseUrl, TimeoutMs, Type) | IsHttpEnabled | None | AI (HTTP) | Devices:Ai | 69 |
| Configuration\AinaviOptions.cs | Class | AinaviOptions | AINAVI EdgeHub config (Host, DefaultModelPort, ModelBasePath) | GetEdgeHubUrl(), GetModelPath() | None | AI (AINAVI) | Ainavi | 58 |
| Configuration\HikCameraOptions.cs | Class | HikCameraOptions | HikVision camera settings (Preferred, GigE, Options) | Properties | None | Camera (Hik) | Devices:Camera | 59 |
| Configuration\ProductionStatsUiConfig.cs | Class | ProductionStatsUiConfig | UI config for stats display | Properties | None | N/A | ProductionStats | 36 |
| Configuration\ModelScanOptions.cs | Class | ModelScanOptions | Model folder scanning paths | Properties | None | AI | ModelScan | 30 |
| Configuration\InferenceType.cs | Enum | InferenceType | SingleModel / Workflow | N/A | None | AI | N/A | 18 |

#### Contracts (DTOs)

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| Contracts\InspectionResultDto.cs | DTO | InspectionResultDto | Inspection result data transfer | ~15 |
| Contracts\DefectDto.cs | DTO | DefectDto | Defect data transfer | ~20 |
| Contracts\BoundingBoxDto.cs | DTO | BoundingBoxDto | Bounding box coordinates | ~10 |
| Contracts\Camera\CameraCaptureMessage.cs | Message | CameraCaptureMessage | Camera capture notification | ~10 |
| Contracts\Camera\BatchPreviewMessage.cs | Message | BatchPreviewMessage | Batch preview notification | ~10 |
| Contracts\WorkOrder\WorkOrderStatsDto.cs | DTO | WorkOrderStatsDto | Work order statistics | ~15 |
| Contracts\WorkOrder\WorkOrderSummaryDto.cs | DTO | WorkOrderSummaryDto | Work order summary | ~20 |
| Contracts\WorkOrder\WorkOrderChangedMessage.cs | Message | WorkOrderChangedMessage | Work order change notification | ~10 |
| Contracts\ModelLoadRequest.cs | Record | ModelLoadRequest | Model load request (uuid, path, port) | ~10 |
| Contracts\ModelLoadResult.cs | Record | ModelLoadResult | Model load result (success, message, port) | ~10 |
| ViewModels\Camera\CameraDeviceVm.cs | ViewModel | CameraDeviceVm | Camera device display model | ~10 |

### Infrastructure Layer (AIVision.Infrastructure)

#### PLC Devices

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Devices\Plc\ModbusPlcPort.cs | Adapter | ModbusPlcPort | IPlcPort impl via IPlcSignalMapper | ReadAsync(), WriteAsync() | IPlcSignalMapper | PLC/Modbus TCP | N/A | 97 |
| Devices\Plc\Modbus\PlcModbusTcpClient.cs | Client | PlcModbusTcpClient | Raw Modbus TCP client (FC01/02/03/05/06/15) with TIME_WAIT retry | ConnectAsync(), Disconnect(), ReadCoilsAsync(), WriteSingleCoilAsync(), WriteMultipleCoilsAsync(), ReadDiscreteInputsAsync(), ReadHoldingRegistersAsync(), WriteSingleRegisterAsync() | System.Net.Sockets | PLC/Modbus TCP | N/A | 552 |
| Devices\Plc\Communication\ModbusTcpPlcAdapter.cs | Adapter | ModbusTcpPlcAdapter | IPlcCommunicationPort impl with heartbeat, exponential backoff reconnect | ConnectAsync(), DisconnectAsync(), ReadCoilsAsync(), WriteSingleCoilAsync(), etc. | PlcModbusTcpClient, ExponentialBackoff | PLC/Modbus TCP | Devices:Plc:Connection | 471 |
| Devices\Plc\Communication\PlcConnectionOptions.cs | Config | PlcConnectionOptions | PLC connection config (IP, Port, UnitId, timeouts, heartbeat, reconnect) | Properties | None | PLC/Modbus TCP | Devices:Plc:Connection | 42 |
| Devices\Plc\Handshake\PlcHandshakeService.cs | Service | PlcHandshakeService | PLC handshake state machine: WaitForTrigger -> StartCapture -> RunningInspect -> SendResult -> WaitForPlcReset | StartAsync(), StopAsync(), ReportResultAsync(), NotifyCaptureCompleteAsync(), ResetAsync() | IPlcCommunicationPort, IPlcSignalMapper | PLC/Modbus TCP | Devices:Plc:Handshake | 917 |
| Devices\Plc\Handshake\PlcHandshakeOptions.cs | Config | PlcHandshakeOptions | Handshake config (signal names, timeouts, error policy) | Properties | None | PLC | Devices:Plc:Handshake | 39 |
| Devices\Plc\SignalMapping\PlcSignalMapper.cs | Adapter | PlcSignalMapper | Signal name to Modbus address mapping with batch write support | ReadSignalAsync(), WriteSignalAsync(), WriteMultipleSignalsAsync(), ReadAllSignalsAsync() | IPlcCommunicationPort, PlcSignalMapOptions | PLC/Modbus TCP | Devices:PlcSignalMap | 268 |
| Devices\Plc\SignalMapping\PlcSignalMapOptions.cs | Config | PlcSignalMapOptions | Signal mapping config with defaults (00001-00004, 10001) | CreateDefault() | PlcSignalDefinition | PLC | Devices:PlcSignalMap | 78 |
| Devices\Plc\DependencyInjection\PlcServiceExtensions.cs | DI Extension | PlcServiceExtensions | Register PLC services (Communication, SignalMapper, Handshake) | AddPlcServices(), AddPlcServicesWithDefaults() | All PLC types | PLC | Devices:Plc:* | 88 |

#### Camera Devices

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Devices\Camera\Ids\IdsCameraPort.cs | Adapter | IdsCameraPort | IDS Peak SDK camera: open, preview, capture, buffer management | OpenAsync(), StartPreviewAsync(), StopPreviewAsync(), CaptureOnceAsync() | peak.core SDK, IdsCameraControlPort | Camera/IDS Peak | Devices:Camera | 2306 |
| Devices\Camera\Ids\IdsCameraControlPort.cs | Adapter | IdsCameraControlPort | IDS camera parameter control via JSON config file | GetParametersAsync(), SetParameterAsync() | peak.core.nodes, JSON config | Camera/IDS Peak | Devices:Camera | 559 |
| Devices\Camera\Ids\IdsCameraDiscoveryAdapter.cs | Adapter | IdsCameraDiscoveryAdapter | IDS camera discovery with preferred order | ListAsync() | peak.core DeviceManager | Camera/IDS Peak | Devices:Camera | 143 |
| Devices\Camera\Ids\IdsCameraSettings.cs | Config | IdsCameraSettings | Camera settings DTO (exposure, gain, height, line rate, ROI) | Properties | None | Camera/IDS Peak | JSON file | 28 |
| Devices\Camera\Ids\IdsCameraSettingsChangedEventArgs.cs | EventArgs | IdsCameraSettingsChangedEventArgs | Settings change notification | Properties | IdsCameraSettings | Camera/IDS Peak | N/A | 17 |
| Devices\Camera\Ids\IdsCameraOptions.cs | Config | IdsCameraOptions | IDS camera DI options (SdkPath, ConfigPath, defaults) | Properties | None | Camera/IDS Peak | Devices:Camera | 53 |
| Devices\Camera\Ids\IdsPeakLibrary.cs | Static helper | IdsPeakLibrary | IDS Peak SDK initialization (DLL loading, PATH, SetDllDirectory) | EnsureInitialized(), Shutdown() | peak.Library, kernel32 P/Invoke | Camera/IDS Peak | N/A | 189 |
| Devices\Camera\Ids\LineScanService.cs | Service | LineScanService | IDS line scan: hardware accumulation mode, passive wait | ConnectCameraAsync(), ConfigureAsync(), StartScanAsync(), StopScanAsync(), CaptureOnceAsync(), WaitForNextImageAsync() | IdsCameraPort, ICameraDiscoveryPort | Camera/IDS Peak (line scan) | N/A | 652 |
| Devices\Camera\Hik\HikCameraAdapter.cs | Adapter | HikCameraAdapter | HikVision MVS SDK camera: open, preview, capture with BGR callback | OpenAsync(), StartPreviewAsync(), StopPreviewAsync(), CaptureOnceAsync() | MvCamCtrl.NET CCamera | Camera/HikVision MVS | Devices:Camera | 373 |
| Devices\Camera\Hik\HikDiscoveryAdapter.cs | Adapter | HikDiscoveryAdapter | HikVision camera discovery (GigE + USB) | ListAsync() | MvCamCtrl.NET CSystem | Camera/HikVision MVS | N/A | 83 |

#### Light Devices

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Devices\Light\LtsAsciiLightPort.cs | Adapter | LtsAsciiLightPort | LTS light controller via ASCII TCP Server (device dials in) | ConnectAsync(), SetIntensity(), Turn(), GetState(), GetDeviceInfo() | System.Net.Sockets TcpListener | Light/ASCII TCP | Devices:Light | 518 |
| Devices\Light\LtsSerialLightPort.cs | Adapter | LtsSerialLightPort | LTS light controller via RS232 serial with brightness control | ConnectAsync(), SetIntensity(), Turn(), GetState(), SetWorkingBrightness(), SetIdleBrightness() | System.IO.Ports SerialPort | Light/RS232 Serial | Devices:LightSerial | 557 |
| Devices\Light\LtsModbusTcpServer.cs | Server | LtsModbusTcpServer | Modbus TCP server for LTS light controller 3-way communication | StartAsync(), StopAsync() | System.Net.Sockets TcpListener | Light/Modbus TCP | N/A | 235 |
| Devices\Light\LightDeviceOptions.cs | Config | LightDeviceOptions | TCP light device config (ListenIp, ListenPort, ChannelCount) | Properties | None | Light/TCP | Devices:Light | 32 |
| Devices\Light\LightSerialDeviceOptions.cs | Config | LightSerialDeviceOptions, LightBrightnessControlOptions | Serial light config + auto brightness control (working/idle per channel) | GetChannelBrightness() | None | Light/RS232 | Devices:LightSerial | 79 |
| Devices\Light\Modbus\ModbusTcpClient.cs | Client | ModbusTcpClient | Minimal Modbus TCP client for light controller (FC03, FC06, FC10) | ReadHoldingRegistersAsync(), WriteSingleRegisterAsync(), WriteMultipleRegistersAsync() | System.Net.Sockets | Light/Modbus TCP | N/A | 234 |

#### Fake/Null Devices

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| Devices\FakePlcPort.cs | Fake | FakePlcPort | No-op PLC for development | 24 |
| Devices\FakeCameraPort.cs | Fake | FakeCameraPort | Generates random test images | 47 |
| Devices\FakeCameraDiscovery.cs | Fake | FakeCameraDiscovery | Returns single fake camera | 18 |
| Devices\FakeAiInferencePort.cs | Fake | FakeAiInferencePort | Returns random OK/NG predictions | 35 |
| Devices\FakeLightPort.cs | Fake | FakeLightPort | In-memory light state simulation | 96 |
| Devices\NullCameraControlPort.cs | Null | NullCameraControlPort | No-op camera control | 20 |

#### AI Service

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| AiService\HttpAiInferencePort.cs | Adapter | HttpAiInferencePort | Generic HTTP AI inference (base64 image upload) | PredictAsync(), HealthCheckAsync() | HttpClient | AI/HTTP | Devices:Ai | 220 |
| AiService\AinaviAiInferencePort.cs | Adapter | AinaviAiInferencePort | AINAVI EdgeHub inference (multipart/form-data upload) | PredictAsync(), HealthCheckAsync(), SetModelPort() | HttpClient | AI/AINAVI EdgeHub | Ainavi | 310 |
| AiService\AinaviAiModelPort.cs | Adapter | AinaviAiModelPort | AINAVI EdgeHub model loading | LoadAsync() | HttpClient | AI/AINAVI EdgeHub | Ainavi | 139 |
| AiService\WorkflowAiInferencePort.cs | Adapter | WorkflowAiInferencePort | AINAVI Workflow multi-step inference via /workflow/run API | PredictAsync(), HealthCheckAsync() | HttpClient, SixLabors.ImageSharp | AI/AINAVI Workflow | Workflow | 534 |
| AiService\EdgeHubWorkflowService.cs | Service | EdgeHubWorkflowService | Workflow service lifecycle (start/stop/load/status) | StartAsync(), LoadWorkflowAsync(), StopAsync(), GetStatusAsync(), HealthCheckAsync() | HttpClient | AI/AINAVI EdgeHub Workflow | Workflow | 372 |
| AiService\SwitchableAiInferencePort.cs | Decorator | SwitchableAiInferencePort | Runtime switch between SingleModel and Workflow inference | PredictAsync(), HealthCheckAsync(), SwitchTo() | AinaviAiInferencePort, WorkflowAiInferencePort | AI | N/A | 103 |
| AiService\AiInferenceRequestDto.cs | DTO | AiInferenceRequestDto | AI request payload (base64 image) | Properties | None | AI | N/A | 18 |
| AiService\AiInferenceResponseDto.cs | DTO | AiInferenceResponseDto, AiDetectionDto, BoundingBoxDto | AI response payload | Properties | None | AI | N/A | 35 |
| AiService\PredictResult.cs | Record | PredictResult | Inference log record | Properties | None | AI | N/A | 21 |
| AiService\IInferenceLogService.cs | Interface | IInferenceLogService | Inference logging | AppendLogAsync(), GetLogsAsync() | PredictResult | AI | N/A | 22 |
| AiService\JsonFileInferenceLogService.cs | Service | JsonFileInferenceLogService | JSON file-based inference logging | AppendLogAsync(), GetLogsAsync() | AinaviOptions | AI | Ainavi:LogPath | 118 |

#### Services

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Services\AutoRunService.cs | Service | AutoRunService | Main auto-run loop: PLC handshake -> LineScan capture -> AI inference -> Overlay -> Save -> PLC report | StartAsync(), StopAsync(), Statistics, State + events | IPlcHandshakePort, ILineScanService, IAiInferencePort, IInspectionImageService, IContourOverlayRenderer?, ILightPort?, IDefectFilteringService? | PLC, Camera, AI, Light | AutoRun | 1083 |
| Services\ConfigAuthService.cs | Service | ConfigAuthService | Config-based authentication from appsettings.json (plain text passwords) | Login(), Logout(), HasPermission() | IConfiguration | N/A | Authentication | 128 |
| Services\DefectFilteringService.cs | Service | DefectFilteringService | Defect size categorization (pixel area -> mm2) + distance-based clustering | FilterDefects() | DefectFilteringOptions | N/A | DefectFiltering | 332 |
| Services\ContourOverlayRenderer.cs | Service | ContourOverlayRenderer | Bresenham line drawing for defect contours on raw image bytes | RenderOverlay() | ImageData, WorkflowDefect | N/A | N/A | 315 |
| Services\ProjectInitializationService.cs | Service | ProjectInitializationService | Parallel device initialization (PLC, Camera, Light, AI) with status tracking | InitializeAsync() | IPlcCommunicationPort, ICameraPort, ILightPort, IAiInferencePort, IWorkflowService?, IAiModelPort? | All devices | ProjectConfig | 889 |
| Services\ProjectConfigService.cs | Service | ProjectConfigService | project.json file CRUD | LoadAsync(), SaveAsync(), GetProjectList() | None | N/A | N/A | 181 |
| Services\OfflineInspectionService.cs | Service | OfflineInspectionService | Offline folder-based AI inspection using SixLabors.ImageSharp | RunInspectionAsync() | IAiInferencePort, SixLabors.ImageSharp | AI | N/A | 211 |
| Services\LocalModelDiscoveryService.cs | Service | LocalModelDiscoveryService | Scan model folders for inference.json/info.json files | ScanModelsAsync() | ModelScanOptions | AI | ModelScan | 206 |

#### Persistence

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Persistence\InMemoryInspectionRepository.cs | Repository | InMemoryInspectionRepository | In-memory inspection storage | AddAsync(), GetStatisticsByWorkOrderIdAsync(), GetDefectStatisticsByWorkOrderIdAsync() | ConcurrentDictionary | N/A | N/A | 38 |
| Persistence\InMemoryWorkOrderRepository.cs | Repository | InMemoryWorkOrderRepository | In-memory work order storage | GetByCodeAsync(), GetByIdAsync(), GetAllAsync(), AddAsync(), UpdateAsync(), DeleteAsync() | ConcurrentDictionary | N/A | N/A | 52 |
| Persistence\SQLite\IDatabaseConnectionFactory.cs | Interface | IDatabaseConnectionFactory | DB connection factory | CreateConnection(), InitializeDatabaseAsync() | System.Data | N/A | N/A | 15 |
| Persistence\SQLite\SqliteDatabaseConnectionFactory.cs | Factory | SqliteDatabaseConnectionFactory | SQLite connection factory with pooling | CreateConnection(), InitializeDatabaseAsync() | Microsoft.Data.Sqlite | N/A | DB path | 177 |
| Persistence\SQLite\SqliteInspectionRepository.cs | Repository | SqliteInspectionRepository | SQLite inspection persistence with Dapper | AddAsync(), GetStatisticsByWorkOrderIdAsync(), GetDefectStatisticsByWorkOrderIdAsync() | Dapper, IDatabaseConnectionFactory | N/A | N/A | 203 |
| Persistence\SQLite\SqliteWorkOrderRepository.cs | Repository | SqliteWorkOrderRepository | SQLite work order persistence with Dapper | GetByCodeAsync(), GetByIdAsync(), GetAllAsync(), AddAsync(), UpdateAsync(), DeleteAsync() | Dapper, IDatabaseConnectionFactory | N/A | N/A | 189 |
| Persistence\SQLite\SqliteInspectionHistoryQuery.cs | Query | SqliteInspectionHistoryQuery | Paged inspection history query with dynamic SQL filters | QueryAsync() | Dapper, IDatabaseConnectionFactory | N/A | N/A | 230 |
| Persistence\SQLite\SqliteProductionStatsQuery.cs | Query | SqliteProductionStatsQuery | Production statistics aggregation query | FindOrdersAsync() | Dapper, IDatabaseConnectionFactory | N/A | N/A | 216 |

#### Adapters (Batch AI Inference)

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Config | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|--------|-------|
| Adapters\AiInference\HttpBatchInferencePort.cs | Adapter | HttpBatchInferencePort | HTTP batch inference (traditional + Workflow endpoints) | PredictAsync(), SetEndpoint() | HttpClient, AiSettings | AI/HTTP | AiInference | 438 |
| Adapters\AiInference\HttpClassificationInferencePort.cs | Adapter | HttpClassificationInferencePort | HTTP classification inference | PredictAsync() | HttpClient, AiSettings | AI/HTTP | AiInference | 139 |
| Adapters\AiInference\HttpSegmentationInferencePort.cs | Adapter | HttpSegmentationInferencePort | HTTP segmentation inference | PredictAsync() | HttpClient, AiSettings | AI/HTTP | AiInference | 113 |

#### Configuration & Validators

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| Configs\AiSettings.cs | Config | AiSettings, AiEndpoints, AiHttpSettings, AiUploadSettings, AiOverlaySettings | AI inference config for batch mode | 63 |
| Common\ExponentialBackoff.cs | Utility | ExponentialBackoff | Exponential backoff strategy for reconnection | 44 |
| ConfigurationValidators\PlcConnectionOptionsValidator.cs | Validator | PlcConnectionOptionsValidator | Validate PLC connection config (IP, port, timeouts) | 53 |
| ConfigurationValidators\AiServiceOptionsValidator.cs | Validator | AiServiceOptionsValidator | Validate AI service config (BaseUrl, timeout) | 48 |
| ConfigurationValidators\LightDeviceOptionsValidator.cs | Validator | LightDeviceOptionsValidator | Validate light device config (IP, port, channels) | 48 |

#### DI Extensions

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| DependencyInjection\ServiceCollectionExtensions.cs | DI Extension | ServiceCollectionExtensions | AddFakeInfrastructure, AddHikVisionCamera, AddIdsPeakCamera, AddLtsAsciiLightController, AddLtsSerialLightController, AddAutoRunService, AddProjectServices, AddDefectFilteringService, AddAuthService, AddAinaviServices, AddWorkflowServices, AddAiInferenceWithWorkflowSupport | 282 |

### Interface Adapters Layer (AIVision.InterfaceAdapters)

| File | Type | Class | Purpose | Public Methods | Dependencies | Lines |
|------|------|-------|---------|---------------|-------------|-------|
| Inspection\InspectionResultMapper.cs | Mapper | InspectionResultMapper, InspectionResultResponse | Map InspectionResultDto to API response | ToResponse() | InspectionResultDto | 12 |

### API Layer (AIVision.Api)

| File | Type | Class | Purpose | Public Methods | Dependencies | Device/Protocol | Lines |
|------|------|-------|---------|---------------|-------------|-----------------|-------|
| Program.cs | Entry point | N/A | ASP.NET Core Web API setup, DI, Swagger | N/A | MediatR, ServiceCollectionExtensions | N/A | 60 |
| Controllers\InspectionController.cs | Controller | InspectionController | POST /api/inspection/cycle - Run inspection cycle via MediatR | RunCycle() | ISender (MediatR) | N/A | 38 |
| Controllers\AinaviController.cs | Controller | AinaviController | POST /api/ainavi/open-model, POST /api/ainavi/predict, GET /api/ainavi/logs | OpenModel(), Predict(), GetLogs() | IAiModelPort, IAiInferencePort, IInferenceLogService | AI (AINAVI) | 184 |

### Presentation Layer (AIVision.Presentation.Wpf)

#### App Entry & Shell

| File | Type | Class | Purpose | Key Dependencies | Lines |
|------|------|-------|---------|-----------------|-------|
| App.xaml.cs | Application | App | WPF app entry, DI container configuration, device selection by config | Host, all DI registrations | ~400+ |
| ViewModels\ShellViewModel.cs | ViewModel | ShellViewModel | Main shell: Auto Run control, inspection display, navigation, work order, PLC status | ISender, IPlcCommunicationPort, IPlcHandshakePort, ICameraPort, IAiInferencePort, IAutoRunService, ILineScanService, IAuthService | ~1000+ |
| Views\ShellView.xaml.cs | Code-behind | ShellView | Main window code-behind | ShellViewModel | ~50 |

#### ViewModels

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| ViewModels\CameraViewModel.cs | ViewModel | CameraViewModel | Camera preview and capture control | ~200 |
| ViewModels\CameraTestViewModel.cs | ViewModel | CameraTestViewModel | Camera test/calibration | ~150 |
| ViewModels\Camera\CameraParameterViewModel.cs | ViewModel | CameraParameterViewModel | Camera parameter slider control | ~80 |
| ViewModels\LineScanViewModel.cs | ViewModel | LineScanViewModel | Line scan configuration and test | ~300 |
| ViewModels\IoPanelViewModel.cs | ViewModel | IoPanelViewModel | PLC I/O signal monitoring panel | ~200 |
| ViewModels\LightControlViewModel.cs | ViewModel | LightControlViewModel | Light controller (TCP) control | ~200 |
| ViewModels\LightSerialControlViewModel.cs | ViewModel | LightSerialControlViewModel | Light controller (Serial) control | ~200 |
| ViewModels\LightDeviceScanViewModel.cs | ViewModel | LightDeviceScanViewModel | Light device network scan | ~100 |
| ViewModels\ImageBatchViewModel.cs | ViewModel | ImageBatchViewModel | Batch image inspection | ~300 |
| ViewModels\BatchInferenceViewModel.cs | ViewModel | BatchInferenceViewModel | Batch AI inference with work order | ~300 |
| ViewModels\ModelSelectorViewModel.cs | ViewModel | ModelSelectorViewModel | AI model selection | ~150 |
| ViewModels\ModelSelectViewModel.cs | ViewModel | ModelSelectViewModel | Model selection dialog | ~100 |
| ViewModels\ModelEditViewModel.cs | ViewModel | ModelEditViewModel | Model configuration editing | ~150 |
| ViewModels\ModelManagementViewModel.cs | ViewModel | ModelManagementViewModel | Model management | ~200 |
| ViewModels\WorkOrderManagementViewModel.cs | ViewModel | WorkOrderManagementViewModel | Work order CRUD | ~200 |
| ViewModels\WorkOrderInputViewModel.cs | ViewModel | WorkOrderInputViewModel | Work order input dialog | ~100 |
| ViewModels\HistoryViewModel.cs | ViewModel | HistoryViewModel | Inspection history browsing | ~200 |
| ViewModels\ProductionStatsViewModel.cs | ViewModel | ProductionStatsViewModel | Production statistics display | ~200 |
| ViewModels\LoginViewModel.cs | ViewModel | LoginViewModel | User login | ~80 |
| ViewModels\OfflineTestViewModel.cs | ViewModel | OfflineTestViewModel | Offline inspection test | ~150 |
| ViewModels\ProjectSelectViewModel.cs | ViewModel | ProjectSelectViewModel | Project selection | ~100 |
| ViewModels\ProjectEditViewModel.cs | ViewModel | ProjectEditViewModel | Project configuration editing | ~150 |
| ViewModels\ProjectLoadingViewModel.cs | ViewModel | ProjectLoadingViewModel | Project initialization progress | ~100 |
| ViewModels\DefectStatViewModel.cs | ViewModel | DefectStatViewModel | Defect statistics display | ~50 |
| ViewModels\DefectRowViewModel.cs | ViewModel | DefectRowViewModel | Single defect display row | ~30 |
| ViewModels\DefectItemViewModel.cs | ViewModel | DefectItemViewModel | Defect item display | ~30 |
| ViewModels\SummaryFieldViewModel.cs | ViewModel | SummaryFieldViewModel | Summary field display | ~20 |
| ViewModels\ResultTypeItemViewModel.cs | ViewModel | ResultTypeItemViewModel | Result type display item | ~20 |

#### Views (XAML code-behind - minimal logic)

| File | Type | Lines |
|------|------|-------|
| Views\ShellView.xaml.cs | ShellView | ~50 |
| Views\CameraView.xaml.cs | CameraView | ~20 |
| Views\CameraTestView.xaml.cs | CameraTestView | ~20 |
| Views\LineScanView.xaml.cs | LineScanView | ~20 |
| Views\IoPanelView.xaml.cs | IoPanelView | ~20 |
| Views\LightControlView.xaml.cs | LightControlView | ~20 |
| Views\LightSerialControlView.xaml.cs | LightSerialControlView | ~20 |
| Views\LightDeviceScanView.xaml.cs | LightDeviceScanView | ~20 |
| Views\ImageBatchView.xaml.cs | ImageBatchView | ~20 |
| Views\BatchInferenceView.xaml.cs | BatchInferenceView | ~20 |
| Views\ModelSelectorView.xaml.cs | ModelSelectorView | ~20 |
| Views\ModelSelectView.xaml.cs | ModelSelectView | ~20 |
| Views\ModelEditView.xaml.cs | ModelEditView | ~20 |
| Views\ModelManagementView.xaml.cs | ModelManagementView | ~20 |
| Views\WorkOrderManagementView.xaml.cs | WorkOrderManagementView | ~20 |
| Views\WorkOrderInputView.xaml.cs | WorkOrderInputView | ~20 |
| Views\HistoryView.xaml.cs | HistoryView | ~20 |
| Views\ProductionStatsView.xaml.cs | ProductionStatsView | ~20 |
| Views\LoginView.xaml.cs | LoginView | ~20 |
| Views\OfflineTestView.xaml.cs | OfflineTestView | ~20 |
| Views\ProjectSelectWindow.xaml.cs | ProjectSelectWindow | ~20 |
| Views\ProjectEditWindow.xaml.cs | ProjectEditWindow | ~20 |
| Views\ProjectLoadingWindow.xaml.cs | ProjectLoadingWindow | ~20 |
| Views\SplashWindow.xaml.cs | SplashWindow | ~20 |

#### Adapters

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| Adapters\Camera\AForgeCameraPort.cs | Adapter | AForgeCameraPort | AForge.Video USB webcam adapter implementing ICameraPort | ~120 |
| Adapters\Camera\AForgeCameraDiscovery.cs | Adapter | AForgeCameraDiscovery | AForge DirectShow device enumeration | 24 |
| Adapters\ImageBatch\FolderPickerPort.cs | Adapter | FolderPickerPort | WPF folder picker dialog | ~30 |
| Adapters\ImageBatch\FileSystemImageEnumerator.cs | Adapter | FileSystemImageEnumerator | File system image file enumeration | ~40 |
| Adapters\ImageBatch\WpfImageLoader.cs | Adapter | WpfImageLoader | WPF BitmapSource to ImageData loader | ~50 |
| Adapters\ImageBatch\WpfImageWriter.cs | Adapter | WpfImageWriter | WPF image file writer | ~50 |
| Adapters\ImageBatch\NullOverlayRenderer.cs | Adapter | NullOverlayRenderer | No-op overlay renderer | ~15 |
| Adapters\ImageBatch\SegOverlayRenderer.cs | Adapter | SegOverlayRenderer | Segmentation overlay renderer | ~80 |
| Adapters\ProductionStats\FakeProductionStatsQuery.cs | Fake | FakeProductionStatsQuery | Fake production stats for development | ~30 |
| Adapters\ProductionStats\ProductionStatsConfigProvider.cs | Adapter | ProductionStatsConfigProvider | IProductionStatsConfigProvider from config | ~30 |

#### Services

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| Services\Navigation\INavigationService.cs | Interface | INavigationService | Window navigation | 20 |
| Services\Navigation\NavigationService.cs | Service | NavigationService | DI-based window creation and navigation | ~40 |
| Services\AinaviApiClient.cs | Service | AinaviApiClient | AINAVI API HTTP client helper | ~100 |
| Services\ModelConfigProviderAdapter.cs | Adapter | ModelConfigProviderAdapter | Adapts ModelConfigService to IModelConfigProvider | ~30 |
| Services\ModelConfigService.cs | Service | ModelConfigService | Model configuration management with auto-discovery | ~150 |
| Services\ProductionStats\IProductionStatsExportService.cs | Interface | IProductionStatsExportService | Export production stats | ~15 |
| Services\ProductionStats\ProductionStatsExportService.cs | Service | ProductionStatsExportService | CSV/Excel export of production statistics | ~100 |

#### Converters & Utilities

| File | Type | Class | Purpose | Lines |
|------|------|-------|---------|-------|
| Converters\BooleanToVisibilityConverter.cs | Converter | BooleanToVisibilityConverter | bool -> Visibility | ~20 |
| Converters\ModelTypeConverter.cs | Converter | ModelTypeConverter | Model type display | ~20 |
| Converters\PageIndexToDisplayConverter.cs | Converter | PageIndexToDisplayConverter | Page index display | ~15 |
| Converters\PageIndexToBoolConverter.cs | Converter | PageIndexToBoolConverter | Page navigation | ~15 |
| Converters\BooleanToTitleConverter.cs | Converter | BooleanToTitleConverter | Bool to title text | ~15 |
| Converters\NullToVisibilityConverter.cs | Converter | NullToVisibilityConverter | null -> Collapsed | ~15 |
| Converters\NgToColorConverter.cs | Converter | NgToColorConverter | NG result -> red color | ~15 |
| Converters\CanGoNextPageConverter.cs | Converter | CanGoNextPageConverter | Page navigation enabled | ~15 |
| Converters\LightControlConverters.cs | Converter | LightControlConverters | Light control value conversion | ~30 |
| Converters\InverseBooleanConverter.cs | Converter | InverseBooleanConverter | Invert boolean | ~15 |
| Utilities\ObjectPathResolver.cs | Utility | ObjectPathResolver | Resolve nested object properties by path | ~50 |
| Utilities\BitmapSourceFactory.cs | Utility | BitmapSourceFactory | Create WPF BitmapSource from raw bytes | ~50 |
| AssemblyInfo.cs | Metadata | N/A | Assembly metadata | ~5 |
