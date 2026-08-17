using AIVision.Application.Contracts;
using MediatR;

namespace AIVision.Application.Inspection.Commands;

/// <summary>
/// 切換 AI 模型命令。
/// </summary>
/// <param name="ModelName">模型名稱</param>
/// <param name="Port">模型埠號</param>
public sealed record SwitchModelCommand(string ModelName, int Port) : IRequest<ModelLoadResult>;

