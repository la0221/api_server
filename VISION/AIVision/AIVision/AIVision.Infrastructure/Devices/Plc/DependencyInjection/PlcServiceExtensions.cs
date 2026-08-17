using AIVision.Application.Ports.Devices;
using AIVision.Infrastructure.Devices.Plc.Communication;
using AIVision.Infrastructure.Devices.Plc.Handshake;
using AIVision.Infrastructure.Devices.Plc.SignalMapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIVision.Infrastructure.Devices.Plc.DependencyInjection;

/// <summary>
/// PLC 服務 DI 擴展方法
/// </summary>
public static class PlcServiceExtensions
{
    /// <summary>
    /// 註冊 PLC 相關服務
    /// </summary>
    public static IServiceCollection AddPlcServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 連線設定
        services.Configure<PlcConnectionOptions>(
            configuration.GetSection(PlcConnectionOptions.SectionName));

        // 訊號映射設定
        services.Configure<PlcSignalMapOptions>(
            configuration.GetSection(PlcSignalMapOptions.SectionName));

        // 交握設定
        services.Configure<PlcHandshakeOptions>(
            configuration.GetSection(PlcHandshakeOptions.SectionName));

        // 通訊層
        services.AddSingleton<IPlcCommunicationPort, ModbusTcpPlcAdapter>();

        // 訊號映射層
        services.AddSingleton<IPlcSignalMapper, PlcSignalMapper>();

        // 交握服務
        services.AddSingleton<IPlcHandshakePort, PlcHandshakeService>();

        return services;
    }

    /// <summary>
    /// 註冊 PLC 相關服務（使用預設設定）
    /// </summary>
    public static IServiceCollection AddPlcServicesWithDefaults(this IServiceCollection services)
    {
        // 使用預設連線設定
        services.Configure<PlcConnectionOptions>(options =>
        {
            options.Ip = "127.0.0.1";
            options.Port = 502;
            options.UnitId = 1;
            options.PollIntervalMs = 50;
        });

        // 使用預設訊號映射
        services.Configure<PlcSignalMapOptions>(options =>
        {
            var defaults = PlcSignalMapOptions.CreateDefault();
            options.Signals = defaults.Signals;
        });

        // 使用預設交握設定
        services.Configure<PlcHandshakeOptions>(options =>
        {
            options.CaptureTimeoutMs = 15000;
            options.InferenceTimeoutMs = 10000;
            options.MinTriggerIntervalMs = 200;
            options.AutoClearByPc = true;
            options.ErrorPolicy = "TreatAsNg";
        });

        // 通訊層
        services.AddSingleton<IPlcCommunicationPort, ModbusTcpPlcAdapter>();

        // 訊號映射層
        services.AddSingleton<IPlcSignalMapper, PlcSignalMapper>();

        // 交握服務
        services.AddSingleton<IPlcHandshakePort, PlcHandshakeService>();

        return services;
    }
}
